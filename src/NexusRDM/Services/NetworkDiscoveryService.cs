using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusRDM.Core.Models;
using NexusRDM.Data.Context;
using NexusRDM.ViewModels;

namespace NexusRDM.Services;

/// <summary>
/// Sweeps a user-supplied IPv4 subnet for SSH (22) and RDP (3389)
/// listeners by TCP-connect, then inserts hits as
/// <see cref="ConnectionProfile"/> rows under a fixed "Discovered"
/// group. Bounded concurrency keeps the scan from saturating the
/// host's socket pool.
///
/// Design choices:
///   - Bounded by <see cref="MaxAddresses"/> (65,536 = a /16). Any
///     larger and we throw — saves the user from a typo'd /8 that'd
///     queue 16M probes.
///   - 256 simultaneous connects with a 400 ms per-probe timeout. On
///     a typical LAN this finds everything in well under a minute on
///     a /24, ~3 minutes on a /16.
///   - Dedup by exact host:port match against the existing
///     connections table, so re-scanning never produces duplicates.
///   - One running scan at a time. Calling <see cref="ScanAsync"/>
///     while a scan is in flight cancels the previous one.
/// </summary>
public sealed class NetworkDiscoveryService : IDisposable
{
    private const int MaxAddresses = 65536; // /16
    private const int Concurrency  = 256;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(400);

    private static readonly int[] Ports = { 22, 3389 };
    /// <summary>Name of the auto-created group hosting scan results.
    /// Public so the connections tree can mark it as undeletable
    /// (only the discovery toggle should manage it).</summary>
    public const string DiscoveredGroupName = "Discovered";

    private readonly IServiceProvider _services;
    private CancellationTokenSource?  _activeScan;
    private Timer?                    _timer;

    public event EventHandler<DiscoveryResult>?   ScanCompleted;
    public event EventHandler<DiscoveryProgress>? Progress;

    public bool IsScanning => _activeScan is { IsCancellationRequested: false };

    public NetworkDiscoveryService(IServiceProvider services)
    {
        _services = services;
        SettingsStore.DiscoverySettingsChanged += (_, _) => RestartTimer();
        RestartTimer();
    }

    /// <summary>Re-arms the periodic scan based on current settings.
    /// Idempotent — the existing timer is disposed and (when enabled)
    /// a fresh one starts at the new interval. When the user flips
    /// the feature off we also tear down the auto-created
    /// <c>Discovered</c> group so the tree doesn't keep an empty
    /// folder around. Connections inside fall to the top level via
    /// the FK's <c>SetNull</c> behavior; deletion of those is left to
    /// the user.</summary>
    public void RestartTimer()
    {
        _timer?.Dispose();
        _timer = null;

        if (!SettingsStore.ReadDiscoveryEnabled())
        {
            _ = RemoveDiscoveredGroupAsync();
            return;
        }

        var interval = TimeSpan.FromMinutes(SettingsStore.ReadDiscoveryIntervalMinutes());
        // Defer the first tick a few seconds so app launch isn't bogged
        // down by an immediate sweep.
        _timer = new Timer(async _ =>
        {
            try { await ScanAsync(SettingsStore.ReadDiscoverySubnet()); }
            catch { /* surfaced via ScanCompleted */ }
        }, null, TimeSpan.FromSeconds(15), interval);
    }

    /// <summary>Wipe every connection inside the Discovered folder
    /// but leave the folder in place so the next scan can repopulate.
    /// Vault credentials for those rows are deleted in the same pass.
    /// Returns the number of rows removed (0 if the folder didn't
    /// exist or was empty).</summary>
    public async Task<int> ClearDiscoveredAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var db    = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var vault = scope.ServiceProvider.GetRequiredService<NexusRDM.Core.Interfaces.ICredentialVault>();
            var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == DiscoveredGroupName);
            if (group is null) return 0;

            var rows = await db.Connections.Where(c => c.GroupId == group.Id).ToListAsync();
            foreach (var c in rows)
            {
                if (!string.IsNullOrEmpty(c.CredentialKey))
                    try { vault.Delete(c.CredentialKey); } catch { /* not in vault */ }
            }
            db.Connections.RemoveRange(rows);
            await db.SaveChangesAsync();

            // Surface via ScanCompleted so the connections tree
            // diff-merges the now-empty folder.
            ScanCompleted?.Invoke(this, new DiscoveryResult(0, 0, 0, "cleared"));
            return rows.Count;
        }
        catch { return 0; }
    }

    private async Task RemoveDiscoveredGroupAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var db    = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var vault = scope.ServiceProvider.GetRequiredService<NexusRDM.Core.Interfaces.ICredentialVault>();
            var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == DiscoveredGroupName);
            if (group is null) return;

            // Discovery OWNS the connections inside — turning the
            // feature off should wipe its surface area entirely, not
            // leave a swarm of orphaned auto-discovered rows pinned
            // to the top level. Vault credentials get cleaned up in
            // the same pass; anything we can't delete from the vault
            // is logged-and-ignored (orphaning a credential is annoying
            // but not corruption).
            var rows = await db.Connections.Where(c => c.GroupId == group.Id).ToListAsync();
            foreach (var c in rows)
            {
                if (!string.IsNullOrEmpty(c.CredentialKey))
                    try { vault.Delete(c.CredentialKey); } catch { /* not in vault */ }
            }
            db.Connections.RemoveRange(rows);
            db.Groups.Remove(group);
            await db.SaveChangesAsync();

            // Re-fire ScanCompleted so the connections tree reloads —
            // ConnectionsViewModel listens to that event and the
            // disappearance of the group is what we want to surface.
            // The synthetic result has Note="cleanup" so it's distinct
            // from a real scan in subscriber logging.
            ScanCompleted?.Invoke(this, new DiscoveryResult(0, 0, 0, "cleanup"));
        }
        catch { /* best effort — non-fatal during shutdown */ }
    }

    /// <summary>Run a one-shot scan of <paramref name="cidr"/>. Returns
    /// counts via <see cref="DiscoveryResult"/>; subscribers also get
    /// progress callbacks during the sweep. Cancels any in-flight scan
    /// first so the user can't queue them up by hammering "Scan now".
    ///
    /// The whole body runs on a thread-pool thread via <see cref="Task.Run"/>
    /// so the UI dispatcher never blocks on ParseCidr (which can be
    /// large for /16 subnets) or on the synchronous portion of the
    /// per-probe queue loop (first 256 iterations don't yield because
    /// the semaphore has slots available).</summary>
    public Task<DiscoveryResult> ScanAsync(string cidr, CancellationToken external = default)
        => Task.Run(() => ScanCoreAsync(cidr, external));

    private async Task<DiscoveryResult> ScanCoreAsync(string cidr, CancellationToken external)
    {
        _activeScan?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _activeScan = cts;
        var ct = cts.Token;

        try
        {
            var addresses = ParseCidr(cidr).ToList();
            if (addresses.Count > MaxAddresses)
                throw new InvalidOperationException(
                    $"Subnet too large ({addresses.Count} addresses). Cap is {MaxAddresses} (/16).");

            int probed = 0;
            int found  = 0;
            var hits   = new ConcurrentBag<DiscoveredHost>();

            using var sem = new SemaphoreSlim(Concurrency);
            var probes = new List<Task>(addresses.Count * Ports.Length);

            foreach (var ip in addresses)
            foreach (var port in Ports)
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                probes.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (await ProbeAsync(ip, port, ct).ConfigureAwait(false))
                        {
                            hits.Add(new DiscoveredHost(ip, port));
                            Interlocked.Increment(ref found);
                        }
                        var p = Interlocked.Increment(ref probed);
                        // Throttle progress firing to ~every 100 probes so
                        // we don't drown the UI thread.
                        if (p % 100 == 0)
                            Progress?.Invoke(this, new DiscoveryProgress(p, addresses.Count * Ports.Length, found));
                    }
                    finally { sem.Release(); }
                }, ct));
            }

            await Task.WhenAll(probes).ConfigureAwait(false);

            int inserted = await PersistResultsAsync(hits, ct).ConfigureAwait(false);
            var result = new DiscoveryResult(
                Probed:   addresses.Count * Ports.Length,
                Found:    found,
                Inserted: inserted,
                Error:    null);

            // Audit log: one entry per scan summarising the result.
            try
            {
                using var scope = _services.CreateScope();
                var audit = scope.ServiceProvider.GetRequiredService<NexusRDM.Core.Interfaces.IAuditRepository>();
                await audit.LogAsync(new NexusRDM.Core.Models.AuditEntry
                {
                    ConnectionId = Guid.Empty,
                    DisplayName  = "Network discovery",
                    Action       = NexusRDM.Core.Models.AuditAction.Synced,
                    Detail       = $"{cidr}: probed {result.Probed}, found {result.Found}, added {result.Inserted}",
                }, ct).ConfigureAwait(false);
            }
            catch { /* non-fatal */ }

            ScanCompleted?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            var r = new DiscoveryResult(0, 0, 0, "cancelled");
            ScanCompleted?.Invoke(this, r);
            return r;
        }
        catch (Exception ex)
        {
            var r = new DiscoveryResult(0, 0, 0, ex.Message);
            ScanCompleted?.Invoke(this, r);
            return r;
        }
    }

    private static async Task<bool> ProbeAsync(IPAddress ip, int port, CancellationToken ct)
    {
        // Per-probe linked CTS so a hung connect can't outlive the
        // ProbeTimeout and stall the whole scan.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var sock = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
            return sock.Connected;
        }
        catch { return false; }
    }

    private async Task<int> PersistResultsAsync(IEnumerable<DiscoveredHost> hits, CancellationToken ct)
    {
        var list = hits.ToList();
        if (list.Count == 0) return 0;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();

        var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == DiscoveredGroupName, ct);
        if (group is null)
        {
            group = new Group { Id = Guid.NewGuid(), Name = DiscoveredGroupName };
            db.Groups.Add(group);
            await db.SaveChangesAsync(ct);
        }

        // Dedup against the entire connections table, not just this
        // group — we don't want to surface a duplicate row when the
        // user already has the host registered manually under another
        // group.
        var existingKeys = await db.Connections
            .Select(c => new { c.Host, c.Port })
            .ToListAsync(ct);
        var keys = new HashSet<string>(
            existingKeys.Select(e => $"{e.Host}:{e.Port}"),
            StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (var h in list)
        {
            var hostStr = h.Address.ToString();
            var key     = $"{hostStr}:{h.Port}";
            if (!keys.Add(key)) continue;

            var name = SettingsStore.ReadDiscoveryReverseDns()
                ? await ReverseLookupAsync(h.Address, SettingsStore.ReadDiscoveryShortHostname(), ct)
                : null;
            db.Connections.Add(new ConnectionProfile
            {
                Id          = Guid.NewGuid(),
                DisplayName = string.IsNullOrEmpty(name) ? hostStr : $"{name} ({hostStr})",
                Host        = hostStr,
                Port        = h.Port,
                Protocol    = h.Port == 3389 ? ConnectionProtocol.Rdp : ConnectionProtocol.Ssh,
                GroupId     = group.Id,
                Tags        = "auto-discovered",
            });
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>Best-effort reverse DNS. Bounded with a 1-second timeout
    /// — most LANs will answer instantly or not at all, and we don't
    /// want a slow resolver to hold up persistence of the rest of the
    /// hits.</summary>
    private static async Task<string?> ReverseLookupAsync(IPAddress ip, bool shortHostname, CancellationToken ct)
    {
        // Dns.GetHostEntryAsync(IPAddress) doesn't accept a
        // CancellationToken — the OS resolver has its own ~5s timeout.
        // We race the lookup against a 1-second delay so a single slow
        // PTR doesn't drag out persistence of the rest of the hits.
        try
        {
            var lookup  = Dns.GetHostEntryAsync(ip);
            var timeout = Task.Delay(TimeSpan.FromSeconds(1), ct);
            var winner  = await Task.WhenAny(lookup, timeout).ConfigureAwait(false);
            if (winner != lookup) return null;

            var entry = await lookup.ConfigureAwait(false);
            var name  = entry.HostName;
            if (string.IsNullOrEmpty(name) || name == ip.ToString()) return null;

            if (shortHostname)
            {
                var dot = name.IndexOf('.');
                if (dot > 0) name = name.Substring(0, dot);
            }
            return name;
        }
        catch { return null; }
    }

    /// <summary>Enumerate every IPv4 address in <paramref name="cidr"/>,
    /// inclusive of the network and broadcast addresses. Throws
    /// <see cref="FormatException"/> on garbage input — the UI surfaces
    /// the message verbatim.
    ///
    /// Discovery only accepts <c>/24</c> subnets: it's the home/SMB
    /// LAN size that finishes a sweep in seconds, and it sidesteps
    /// the operational footguns of bigger ranges (slow scans, accidental
    /// hits on infrastructure subnets, alarms from network monitors).
    /// Larger or smaller prefixes are rejected up-front.</summary>
    public static IEnumerable<IPAddress> ParseCidr(string cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
            throw new FormatException("Subnet is empty.");

        var parts = cidr.Split('/');
        if (parts.Length != 2)
            throw new FormatException($"Expected CIDR like '192.168.1.0/24', got '{cidr}'.");

        if (!IPAddress.TryParse(parts[0], out var baseIp)
         || baseIp.AddressFamily != AddressFamily.InterNetwork)
            throw new FormatException($"'{parts[0]}' isn't a valid IPv4 address.");

        if (!int.TryParse(parts[1], out var prefix))
            throw new FormatException($"Prefix must be /24, got '/{parts[1]}'.");
        if (prefix != 24)
            throw new FormatException(
                $"Only /24 subnets are supported (got /{prefix}). " +
                "Pick a single LAN segment like 192.168.1.0/24.");

        // Big-endian conversion: 192.168.1.0 → 0xC0A80100
        var bytes = baseIp.GetAddressBytes();
        uint ipNum = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16)
                   | ((uint)bytes[2] << 8)  |  (uint)bytes[3];
        uint mask    = prefix == 32 ? 0xFFFFFFFF : ~((1u << (32 - prefix)) - 1);
        uint network = ipNum & mask;
        uint last    = prefix == 32 ? network : (network | ~mask);

        for (uint cur = network; ; cur++)
        {
            yield return new IPAddress(new[]
            {
                (byte)((cur >> 24) & 0xFF),
                (byte)((cur >> 16) & 0xFF),
                (byte)((cur >>  8) & 0xFF),
                (byte) (cur        & 0xFF),
            });
            if (cur == last) break;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _activeScan?.Cancel();
        _activeScan?.Dispose();
    }
}

public readonly record struct DiscoveredHost(IPAddress Address, int Port);

public readonly record struct DiscoveryResult(int Probed, int Found, int Inserted, string? Error)
{
    public bool IsSuccess => Error is null;
    public override string ToString() =>
        Error is { Length: > 0 } ? $"({Error})"
        : $"probed {Probed}, found {Found}, added {Inserted}";
}

public readonly record struct DiscoveryProgress(int Probed, int Total, int Found);
