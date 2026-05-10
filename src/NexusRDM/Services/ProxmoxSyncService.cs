using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Core.Proxmox;
using NexusRDM.Data.Context;

namespace NexusRDM.Services;

/// <summary>
/// Pulls VMs from a registered <see cref="ProxmoxSource"/> and projects
/// them into managed <see cref="ConnectionProfile"/> rows under the
/// source's root group. The cluster is the source of truth: VMs that
/// disappear from <c>cluster/resources</c> are <b>hard-deleted</b>,
/// taking their vault credentials with them.
///
/// Lives in the WinUI project (rather than Core) because the sync uses
/// <see cref="NexusDbContext"/> directly — a full-pass diff fits more
/// naturally as a single SaveChanges than as repository round-trips.
/// </summary>
public sealed class ProxmoxSyncService
{
    private readonly IServiceProvider _services;

    public ProxmoxSyncService(IServiceProvider services) => _services = services;

    /// <summary>Fired after a successful sync so the connections tree
    /// can refresh. Carries the source id; subscribers can decide
    /// whether to fully reload or do a delta.</summary>
    public event EventHandler<Guid>? SourceSynced;

    /// <summary>Last-known power state per managed connection.
    /// Populated on every sync from <c>cluster/resources.status</c>;
    /// <see cref="ConnectionTreeNode"/> seeds itself from this so the
    /// tree's power glyph survives reloads. In-memory only — no DB
    /// migration needed since the cluster is the source of truth and
    /// the next sync (or app start) re-fetches.</summary>
    private readonly Dictionary<Guid, (ProxmoxPowerState State, DateTime UpdatedAtUtc)> _powerStates = new();
    private readonly object _powerLock = new();

    public ProxmoxPowerState GetPowerState(Guid connectionId)
        => GetPowerStateInfo(connectionId).State;

    /// <summary>Like <see cref="GetPowerState"/> but also returns the
    /// timestamp of the last sync that wrote the value. Callers
    /// (ConnectionTreeNode) use this to mark the row's icon as
    /// "stale" when the cache hasn't been refreshed in a while.</summary>
    public (ProxmoxPowerState State, DateTime? UpdatedAtUtc) GetPowerStateInfo(Guid connectionId)
    {
        lock (_powerLock)
        {
            if (_powerStates.TryGetValue(connectionId, out var v))
                return (v.State, v.UpdatedAtUtc);
            return (ProxmoxPowerState.Unknown, null);
        }
    }

    private void SetPowerState(Guid connectionId, string? rawStatus)
    {
        var state = (rawStatus ?? "").ToLowerInvariant() switch
        {
            "running" => ProxmoxPowerState.Running,
            "stopped" => ProxmoxPowerState.Stopped,
            "paused"  => ProxmoxPowerState.Paused,
            _         => ProxmoxPowerState.Unknown,
        };
        lock (_powerLock) _powerStates[connectionId] = (state, DateTime.UtcNow);
    }

    public async Task<SyncResult> SyncAsync(Guid sourceId, CancellationToken ct = default)
    {
        // While demo mode is on, every sync is a no-op — the demo
        // tree is fake-fixed and we don't want background sync
        // mutating real DB rows the user can't see.
        var demoCheck = _services.GetService(typeof(NexusRDM.Services.DemoModeService))
            as NexusRDM.Services.DemoModeService;
        if (demoCheck?.IsActive == true) return SyncResult.SkippedReason("demo mode");

        using var scope = _services.CreateScope();
        var sp      = scope.ServiceProvider;
        var db      = sp.GetRequiredService<NexusDbContext>();
        var vault   = sp.GetRequiredService<ICredentialVault>();
        var factory = sp.GetRequiredService<IProxmoxClientFactory>();

        var source = await db.ProxmoxSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null) throw new InvalidOperationException($"Proxmox source {sourceId} not found.");
        if (!source.IsEnabled) return SyncResult.SkippedReason("disabled");

        // Make sure we have a root group. Created lazily so deleting
        // and re-adding the source produces a fresh group rather than
        // resurrecting a stale one.
        if (source.RootGroupId is null
         || !await db.Groups.AnyAsync(g => g.Id == source.RootGroupId, ct))
        {
            var group = new Group { Id = Guid.NewGuid(), Name = source.Name };
            db.Groups.Add(group);
            source.RootGroupId = group.Id;
        }

        // The client stays alive for the whole sync — we use it for
        // cluster/resources, then per-VM agent + config calls. Disposed
        // at the end of SyncAsync regardless of which branch we exit.
        var client = factory.Create(source);
        IReadOnlyList<ProxmoxClusterResource> resources;
        try { resources = await client.GetClusterResourcesAsync(ct); }
        catch (Exception ex)
        {
            source.LastSyncError = ex.Message;
            source.LastSyncUtc   = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            (client as IDisposable)?.Dispose();
            throw;
        }

        var existing = await db.Connections
            .Where(c => c.ExternalSourceId == sourceId)
            .ToListAsync(ct);
        var existingByExtId = existing.ToDictionary(c => c.ExternalId ?? "", c => c);

        var seen     = new HashSet<string>(StringComparer.Ordinal);
        var inserted = 0;
        var updated  = 0;
        var deleted  = 0;
        var skipped  = 0;

        // Per-sync DNS cache. We try to upgrade hostnames → IPs
        // opportunistically (cheap per-row reliability win — IP doesn't
        // depend on the user's DNS server staying available); caching
        // ensures we do at most one lookup per unique hostname per sync.
        // Null entries record negative resolutions so we don't retry.
        var dnsCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in resources)
        {
            var extId = ExternalIdOf(r);
            if (extId is null) { skipped++; continue; }
            seen.Add(extId);

            var directives = ParseTags(r.Tags);
            if (directives.Skip) { skipped++; continue; }

            // Fetch per-VM config + agent IPs. We do this BEFORE
            // resolving protocol/host so the OS hint (`ostype`) and
            // discovered IP feed both decisions. Both calls fall back
            // silently — discovery is best-effort, never fatal.
            var (discoveredIp, ostype) = await DiscoverVmDetailsAsync(client, r, ct);

            existingByExtId.TryGetValue(extId, out var row);
            var isUpdate = row is not null;
            // Probe the actual ports when we're inserting a new row and
            // can reach the guest. The TCP probe is far more reliable
            // than ostype guessing — a Linux VM running an RDP server
            // (xrdp) or a Windows host behind sshd both come out right.
            // We skip the probe when:
            //   - The user pinned the protocol via #nexus:rdp/ssh/console
            //   - We have no IP (probe would just time out twice)
            //   - The row already exists (Protocol is user-editable on
            //     managed rows; we don't clobber on update)
            ConnectionProtocol? probed = null;
            var hasTagPin = directives.ForceRdp || directives.ForceSsh || directives.ForceConsole;
            // Setting gate: users can flip the probe off in
            // Settings → Proxmox sources to avoid touching guest
            // sockets (e.g. on audited networks). When off the sync
            // falls through to ResolveProtocol's ostype heuristic.
            if (!isUpdate && !hasTagPin && !string.IsNullOrEmpty(discoveredIp)
             && NexusRDM.ViewModels.SettingsStore.ReadProxmoxProbeProtocol())
                probed = await ProbeProtocolAsync(discoveredIp!, ostype, ct);

            var protocol = probed ?? ResolveProtocol(directives, source.DefaultProtocol, r.Tags, ostype);
            var host     = await ResolveHostAsync(directives, r, discoveredIp, dnsCache, ct);
            var port     = directives.Port ?? ConnectionProfile.DefaultPort(protocol);
            var user     = directives.User ?? source.DefaultUsername;
            var name     = string.IsNullOrWhiteSpace(r.Name) ? $"vm-{r.Vmid}" : r.Name!;

            if (isUpdate && row is not null)
            {
                // Cluster-owned columns get overwritten on every sync.
                // Protocol + Port are explicitly user-editable on
                // managed rows: our auto-pick (probe / ostype) is a
                // best guess, so once the user changes it in the
                // editor we never clobber their choice. Other
                // user-modified fields (CredentialKey, IconGlyph,
                // IconColorHex, RdpSettingsJson, SshSettingsJson, Tags)
                // are similarly preserved.
                row.DisplayName = name;
                row.Host        = host;
                row.GroupId     = source.RootGroupId;
                row.IsManaged   = true;
                SetPowerState(row.Id, r.Status);
                updated++;
            }
            else
            {
                var newProfile = new ConnectionProfile
                {
                    Id               = Guid.NewGuid(),
                    DisplayName      = name,
                    Host             = host,
                    Port             = port,
                    Protocol         = protocol,
                    GroupId          = source.RootGroupId,
                    ExternalSourceId = sourceId,
                    ExternalId       = extId,
                    IsManaged        = true,
                    Tags             = r.Tags ?? string.Empty,
                };
                db.Connections.Add(newProfile);
                SetPowerState(newProfile.Id, r.Status);
                inserted++;
            }
        }

        // Hard-delete rows whose ExternalId is no longer in the feed.
        foreach (var stale in existing.Where(c => !seen.Contains(c.ExternalId ?? "")))
        {
            if (!string.IsNullOrEmpty(stale.CredentialKey))
                try { vault.Delete(stale.CredentialKey); } catch { /* not in vault */ }
            db.Connections.Remove(stale);
            deleted++;
        }

        source.LastSyncUtc   = DateTime.UtcNow;
        source.LastSyncError = null;

        await db.SaveChangesAsync(ct);
        (client as IDisposable)?.Dispose();

        // Audit-log the sync. ConnectionId stays Guid.Empty since
        // a sync isn't bound to a single connection — DisplayName +
        // Detail carry the source label and the per-action counts.
        try
        {
            var audit = sp.GetRequiredService<IAuditRepository>();
            await audit.LogAsync(new AuditEntry
            {
                ConnectionId = Guid.Empty,
                DisplayName  = source.Name,
                Action       = AuditAction.Synced,
                Detail       = $"Proxmox sync: +{inserted} ✎{updated} −{deleted} ⤼{skipped}",
            }, ct);
        }
        catch { /* audit failure shouldn't undo a successful sync */ }

        SourceSynced?.Invoke(this, sourceId);
        return new SyncResult(inserted, updated, deleted, skipped);
    }

    public async Task<IReadOnlyList<(Guid Id, SyncResult Result, Exception? Error)>>
        SyncAllAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var ids = await db.ProxmoxSources.Where(s => s.IsEnabled).Select(s => s.Id).ToListAsync(ct);

        var results = new List<(Guid, SyncResult, Exception?)>();
        foreach (var id in ids)
        {
            try { results.Add((id, await SyncAsync(id, ct), null)); }
            catch (Exception ex) { results.Add((id, SyncResult.Failed, ex)); }
        }
        return results;
    }

    // ── tag + heuristic helpers ──────────────────────────────────────────

    /// <summary><c>"{node}/{type}/{vmid}"</c> — stable across renames so
    /// the diff matches by identity, not name.</summary>
    private static string? ExternalIdOf(ProxmoxClusterResource r) =>
        string.IsNullOrEmpty(r.Node) || string.IsNullOrEmpty(r.Type)
            ? null
            : $"{r.Node}/{r.Type}/{r.Vmid}";

    internal static ConnectionProtocol ResolveProtocol(
        TagDirectives d, ProxmoxDefaultProtocol fallback, string? rawTags, string? ostype)
    {
        if (d.ForceRdp) return ConnectionProtocol.Rdp;
        if (d.ForceSsh) return ConnectionProtocol.Ssh;
        // Console-tagged VMs land on SSH for now; once the noVNC
        // protocol is wired up (rollout step 8) this branch flips.
        if (d.ForceConsole) return ConnectionProtocol.Ssh;

        return fallback switch
        {
            ProxmoxDefaultProtocol.Rdp     => ConnectionProtocol.Rdp,
            ProxmoxDefaultProtocol.Ssh     => ConnectionProtocol.Ssh,
            ProxmoxDefaultProtocol.Console => ConnectionProtocol.Ssh, // see above
            _ /* Auto */ => HeuristicProtocol(rawTags, ostype),
        };
    }

    /// <summary>Concurrently TCP-connect to ports 22 and 3389 on
    /// <paramref name="ip"/>. The probe is the most reliable protocol
    /// signal we have — a Windows host running OpenSSH or a Linux VM
    /// running xrdp would both come out wrong with ostype alone. When:
    ///   - only one port is open → use it
    ///   - both are open → defer to ostype (Windows = RDP, else SSH);
    ///     no ostype = SSH wins (covers more setups)
    ///   - neither is open → return null so the caller falls back to
    ///     the heuristic
    /// Each probe has a ~600 ms ceiling. Total wall time per VM is
    /// dominated by the slower of the two — typically 600 ms when one
    /// port is closed, near-zero when both respond.</summary>
    private static async Task<ConnectionProtocol?> ProbeProtocolAsync(
        string ip, string? ostype, CancellationToken ct)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr))
            return null;

        var sshTask = TryConnectAsync(addr, 22,   ct);
        var rdpTask = TryConnectAsync(addr, 3389, ct);
        await Task.WhenAll(sshTask, rdpTask).ConfigureAwait(false);

        var sshOpen = sshTask.Result;
        var rdpOpen = rdpTask.Result;

        if (sshOpen && !rdpOpen) return ConnectionProtocol.Ssh;
        if (rdpOpen && !sshOpen) return ConnectionProtocol.Rdp;
        if (sshOpen && rdpOpen)
        {
            var os = (ostype ?? "").ToLowerInvariant();
            if (os.StartsWith("win") || os.StartsWith("wxp") || os.StartsWith("w2k"))
                return ConnectionProtocol.Rdp;
            return ConnectionProtocol.Ssh;
        }
        return null;
    }

    private static async Task<bool> TryConnectAsync(
        System.Net.IPAddress ip, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(600));
        try
        {
            using var sock = new System.Net.Sockets.Socket(
                ip.AddressFamily,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            await sock.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
            return sock.Connected;
        }
        catch { return false; }
    }

    /// <summary>Picks the protocol when the source is set to Auto. The
    /// authoritative signal is <paramref name="ostype"/> from the VM's
    /// own config (<c>win10/win11/wxp...</c> for Windows, <c>l26/...</c>
    /// for Linux). Tags are only consulted when ostype is missing — for
    /// LXC containers, where ostype is "alpine"/"debian"/etc. and isn't
    /// "win"-prefixed, we still land on SSH naturally.</summary>
    private static ConnectionProtocol HeuristicProtocol(string? tags, string? ostype)
    {
        var os = (ostype ?? "").ToLowerInvariant();
        if (os.StartsWith("win") || os.StartsWith("wxp") || os.StartsWith("w2k"))
            return ConnectionProtocol.Rdp;

        if (!string.IsNullOrEmpty(os))
            return ConnectionProtocol.Ssh; // Linux / *BSD / Solaris / etc.

        // Last resort — no ostype available. Fall back to tag scan.
        var t = (tags ?? "").ToLowerInvariant();
        if (t.Contains("win") || t.Contains("rdp")) return ConnectionProtocol.Rdp;
        return ConnectionProtocol.Ssh;
    }

    /// <summary>Pure priority logic — user tag override, then
    /// discovered IP, then VM name. Kept as a sync helper for unit
    /// tests and for callers that don't want network I/O.</summary>
    internal static string ResolveHost(
        TagDirectives d, ProxmoxClusterResource r, string? discoveredIp)
    {
        if (!string.IsNullOrEmpty(d.Host))     return d.Host!;
        if (!string.IsNullOrEmpty(discoveredIp)) return discoveredIp!;
        return string.IsNullOrEmpty(r.Name) ? $"vm-{r.Vmid}" : r.Name!;
    }

    /// <summary>Same priority order as <see cref="ResolveHost"/> but
    /// opportunistically DNS-resolves hostnames into IPs when the
    /// resolved value would otherwise be a name. Discovered-IP paths
    /// pass through unchanged (already an IP). DNS errors / timeouts
    /// fall back to the original name — never fatal.</summary>
    internal static async Task<string> ResolveHostAsync(
        TagDirectives d, ProxmoxClusterResource r, string? discoveredIp,
        IDictionary<string, string?> dnsCache, CancellationToken ct)
    {
        var raw = ResolveHost(d, r, discoveredIp);

        // Discovered IP path? Already an IP; no upgrade needed.
        bool wasDiscoveredIpPath =
            string.IsNullOrEmpty(d.Host) && !string.IsNullOrEmpty(discoveredIp);
        if (wasDiscoveredIpPath) return raw;

        return await UpgradeToIpAsync(raw, dnsCache, ct);
    }

    /// <summary>If <paramref name="hostOrIp"/> already parses as an IP,
    /// return it unchanged. Otherwise attempt a DNS lookup (cached,
    /// 2-second timeout) and return the resolved IP — preferring IPv4
    /// when available — or the original name if resolution fails.
    /// Errors are swallowed: a missing PTR / NXDOMAIN / network blip
    /// shouldn't fail the sync; the connection will just fall back to
    /// using the hostname at connect time, exactly as before.</summary>
    private static async Task<string> UpgradeToIpAsync(
        string hostOrIp, IDictionary<string, string?> cache, CancellationToken ct)
    {
        if (System.Net.IPAddress.TryParse(hostOrIp, out _)) return hostOrIp;
        if (cache.TryGetValue(hostOrIp, out var cached))
            return cached ?? hostOrIp;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var addrs = await System.Net.Dns.GetHostAddressesAsync(hostOrIp, cts.Token);
            // Prefer IPv4 — it's both more universal across hosts and
            // it's what most homelab Proxmox networks use. Fall back
            // to IPv6 if that's all the resolver returned.
            var ipv4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            var pick = (ipv4 ?? addrs.FirstOrDefault())?.ToString();
            cache[hostOrIp] = pick;
            return pick ?? hostOrIp;
        }
        catch
        {
            cache[hostOrIp] = null; // negative cache so we don't retry every row
            return hostOrIp;
        }
    }

    /// <summary>Best-effort IP + ostype discovery. Returns
    /// <c>(null, null)</c> if everything we tried fails — sync is
    /// expected to keep going either way.
    ///
    /// Strategy:
    ///   - QEMU running: try qemu-guest-agent first (gives the real IP
    ///     the guest currently holds). Always read config for ostype.
    ///   - LXC: agent doesn't exist; parse <c>net0</c>'s <c>ip=</c> from
    ///     the container config. Static IPs surface; <c>ip=dhcp</c> gives
    ///     up and falls back to the VM name.
    ///   - Stopped VMs / containers: skip the agent call (it'd fail
    ///     anyway), still try config so ostype-driven protocol works.
    /// </summary>
    private static async Task<(string? Ip, string? OsType)> DiscoverVmDetailsAsync(
        IProxmoxClient client, ProxmoxClusterResource r, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.Node) || string.IsNullOrEmpty(r.Type))
            return (null, null);

        string? ip = null;
        string? osType = null;

        var config = await client.TryGetVmConfigAsync(r.Node!, r.Type!, r.Vmid, ct);
        if (config is not null)
        {
            osType = config.OsType;
            if (r.Type == "lxc") ip = TryParseLxcStaticIp(config);
        }

        // QEMU + running: ask the agent. Even if config gave us nothing,
        // the agent is the canonical IP source for VMs.
        if (ip is null && r.Type == "qemu" && string.Equals(r.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            var agent = await client.TryGetQemuAgentNetworkAsync(r.Node!, r.Vmid, ct);
            if (agent?.Result is { Count: > 0 } interfaces)
                ip = PickBestIp(interfaces);
        }

        return (ip, osType);
    }

    /// <summary>Walks <c>net0..net3</c> for an entry of the shape
    /// <c>"name=eth0,...,ip=10.0.0.5/24,..."</c> and returns the
    /// stripped IP. Returns null for DHCP-configured nics.</summary>
    internal static string? TryParseLxcStaticIp(ProxmoxVmConfig config)
    {
        foreach (var raw in new[] { config.Net0, config.Net1, config.Net2, config.Net3 })
        {
            if (string.IsNullOrEmpty(raw)) continue;
            foreach (var part in raw.Split(','))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2 || !string.Equals(kv[0], "ip", StringComparison.OrdinalIgnoreCase)) continue;
                var v = kv[1].Trim();
                if (string.IsNullOrEmpty(v) || v.Equals("dhcp", StringComparison.OrdinalIgnoreCase)) continue;
                // Strip CIDR mask if present.
                var slash = v.IndexOf('/');
                return slash > 0 ? v.Substring(0, slash) : v;
            }
        }
        return null;
    }

    /// <summary>Picks the best routable IP from a guest-agent
    /// interfaces dump. Preference order:
    ///   1. Non-loopback, non-link-local IPv4
    ///   2. Non-loopback, non-link-local IPv6
    /// Filters out <c>lo</c>, <c>docker*</c>, <c>br-*</c>, <c>veth*</c>,
    /// and <c>tun*</c> by interface name so the user's Docker bridge
    /// or VPN address doesn't take precedence over the primary NIC.</summary>
    internal static string? PickBestIp(IEnumerable<ProxmoxAgentInterface> interfaces)
    {
        var primary = interfaces.Where(IsPrimaryInterface).ToList();
        var pool    = primary.Count > 0 ? primary : interfaces.ToList();

        // First pass: routable IPv4
        foreach (var i in pool)
            foreach (var a in i.IpAddresses ?? new())
                if (string.Equals(a.Type, "ipv4", StringComparison.OrdinalIgnoreCase)
                 && IsRoutable(a.Address))
                    return a.Address;

        // Second pass: routable IPv6 (skip link-local fe80::)
        foreach (var i in pool)
            foreach (var a in i.IpAddresses ?? new())
                if (string.Equals(a.Type, "ipv6", StringComparison.OrdinalIgnoreCase)
                 && IsRoutable(a.Address))
                    return a.Address;

        return null;
    }

    private static bool IsPrimaryInterface(ProxmoxAgentInterface i)
    {
        var n = (i.Name ?? "").ToLowerInvariant();
        if (n is "lo" or "lo0") return false;
        if (n.StartsWith("docker") || n.StartsWith("br-")
         || n.StartsWith("veth")   || n.StartsWith("tun")
         || n.StartsWith("tap")    || n.StartsWith("vmnet")) return false;
        return true;
    }

    private static bool IsRoutable(string? addr)
    {
        if (string.IsNullOrEmpty(addr)) return false;
        if (addr == "127.0.0.1" || addr == "::1") return false;
        if (addr.StartsWith("169.254.", StringComparison.Ordinal)) return false; // IPv4 link-local
        if (addr.StartsWith("fe80::",   StringComparison.OrdinalIgnoreCase)) return false; // IPv6 link-local
        return true;
    }

    internal static TagDirectives ParseTags(string? tags)
    {
        var d = new TagDirectives();
        if (string.IsNullOrEmpty(tags)) return d;

        // Proxmox separates with semicolons; some clusters use commas.
        foreach (var raw in tags.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (!t.StartsWith("nexus:", StringComparison.OrdinalIgnoreCase)) continue;
            var body = t.Substring("nexus:".Length);

            switch (body.ToLowerInvariant())
            {
                case "rdp":     d.ForceRdp     = true; continue;
                case "ssh":     d.ForceSsh     = true; continue;
                case "console": d.ForceConsole = true; continue;
                case "skip":    d.Skip         = true; continue;
            }

            var eq = body.IndexOf('=');
            if (eq <= 0) continue;
            var key = body.Substring(0, eq).ToLowerInvariant();
            var val = body.Substring(eq + 1);

            switch (key)
            {
                case "user": d.User = val; break;
                case "host": d.Host = val; break;
                case "port": if (int.TryParse(val, out var p)) d.Port = p; break;
            }
        }

        return d;
    }

    internal sealed class TagDirectives
    {
        public bool    ForceRdp;
        public bool    ForceSsh;
        public bool    ForceConsole;
        public bool    Skip;
        public string? User;
        public string? Host;
        public int?    Port;
    }
}

public readonly record struct SyncResult(int Inserted, int Updated, int Deleted, int Skipped, string? Note = null)
{
    public static SyncResult Failed                    => new(0, 0, 0, 0, "failed");
    public static SyncResult SkippedReason(string why) => new(0, 0, 0, 0, why);
    public bool IsSuccess => Note is null or "" or "disabled";

    public override string ToString() =>
        Note is { Length: > 0 }
            ? $"({Note})"
            : $"+{Inserted} ✎{Updated} −{Deleted} ⤼{Skipped}";
}

/// <summary>VM power state from Proxmox's <c>cluster/resources.status</c>.
/// Surfaced as a colored glyph in the connections tree when the user
/// has enabled the option in Settings → Proxmox sources.</summary>
public enum ProxmoxPowerState
{
    /// <summary>Never synced or status field was empty/unknown.</summary>
    Unknown = 0,
    Running = 1,
    Stopped = 2,
    Paused  = 3,
}
