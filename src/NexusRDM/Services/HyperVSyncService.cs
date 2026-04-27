using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Data.Context;
using NexusRDM.ViewModels;

namespace NexusRDM.Services;

/// <summary>
/// Mirrors <see cref="ProxmoxSyncService"/> for the local Hyper-V
/// host. Pulls VMs via <see cref="HyperVClient"/>, projects each one
/// as a managed <see cref="ConnectionProfile"/> under the auto-managed
/// <c>HyperVGroupName</c> group, and hard-deletes rows whose VM is no
/// longer present.
///
/// No external source row in the DB — Hyper-V is always localhost in
/// this build, and shoehorning a <c>ProxmoxSource</c>-style record
/// would just be ceremony. We discriminate Hyper-V rows by their
/// <c>ExternalId</c> prefix (<c>"hyperv:{vmid}"</c>) and by their
/// group membership.
/// </summary>
public sealed class HyperVSyncService : IDisposable
{
    /// <summary>Auto-managed group VMs land under. Public so the
    /// connections tree marks it as undeletable + AUTO-badged.</summary>
    public const string HyperVGroupName = "Hyper-V";

    private const string ExternalIdPrefix = "hyperv:";

    private readonly IServiceProvider _services;
    private Timer? _timer;

    public event EventHandler<HyperVSyncResult>? SyncCompleted;

    public HyperVSyncService(IServiceProvider services)
    {
        _services = services;
        SettingsStore.HyperVSettingsChanged += (_, _) => RestartTimer();
        RestartTimer();
    }

    public void RestartTimer()
    {
        _timer?.Dispose();
        _timer = null;

        if (!SettingsStore.ReadHyperVEnabled())
        {
            // Disabled → wipe the auto-managed surface area, same
            // contract as discovery's RemoveDiscoveredGroupAsync.
            _ = RemoveHyperVGroupAsync();
            return;
        }

        var interval = TimeSpan.FromMinutes(SettingsStore.ReadHyperVSyncIntervalMinutes());
        _timer = new Timer(async _ =>
        {
            try { await SyncAsync(); }
            catch { /* surfaced via SyncCompleted */ }
        }, null, TimeSpan.FromSeconds(15), interval);
    }

    public async Task<HyperVSyncResult> SyncAsync(CancellationToken ct = default)
    {
        try
        {
            var client = new HyperVClient();
            var vms = await client.ListVmsAsync(ct).ConfigureAwait(false);

            using var scope = _services.CreateScope();
            var db    = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();

            // Ensure the Hyper-V group exists.
            var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == HyperVGroupName, ct);
            if (group is null)
            {
                group = new Group { Id = Guid.NewGuid(), Name = HyperVGroupName };
                db.Groups.Add(group);
                await db.SaveChangesAsync(ct);
            }

            // Existing managed rows for Hyper-V, indexed by ExternalId.
            var existing = await db.Connections
                .Where(c => c.GroupId == group.Id && c.ExternalId != null && c.ExternalId.StartsWith(ExternalIdPrefix))
                .ToListAsync(ct);
            var byExtId = existing.ToDictionary(c => c.ExternalId!, c => c);

            int inserted = 0, updated = 0, deleted = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var vm in vms)
            {
                if (string.IsNullOrEmpty(vm.Id)) continue;
                var extId = ExternalIdPrefix + vm.Id;
                seen.Add(extId);

                var host = !string.IsNullOrEmpty(vm.Ip) ? vm.Ip! : vm.Name;
                var name = string.IsNullOrWhiteSpace(vm.Name) ? $"vm-{vm.Id}" : vm.Name;

                if (byExtId.TryGetValue(extId, out var row))
                {
                    // Same contract as Proxmox: cluster-owned columns
                    // overwrite; user-edited Protocol/Port stay sticky.
                    row.DisplayName = name;
                    row.Host        = host;
                    row.GroupId     = group.Id;
                    row.IsManaged   = true;
                    updated++;
                }
                else
                {
                    var protocol = !string.IsNullOrEmpty(vm.Ip) && SettingsStore.ReadHyperVProbeProtocol()
                        ? await ProbeProtocolAsync(vm.Ip!, ct).ConfigureAwait(false) ?? ConnectionProtocol.Rdp
                        : ConnectionProtocol.Rdp; // sensible default for Hyper-V (mostly Windows VMs)

                    db.Connections.Add(new ConnectionProfile
                    {
                        Id          = Guid.NewGuid(),
                        DisplayName = name,
                        Host        = host,
                        Port        = ConnectionProfile.DefaultPort(protocol),
                        Protocol    = protocol,
                        GroupId     = group.Id,
                        ExternalId  = extId,
                        IsManaged   = true,
                        Tags        = "hyperv",
                    });
                    inserted++;
                }
            }

            // Hard-delete rows that no longer correspond to a live VM.
            foreach (var stale in existing.Where(c => !seen.Contains(c.ExternalId ?? "")))
            {
                if (!string.IsNullOrEmpty(stale.CredentialKey))
                    try { vault.Delete(stale.CredentialKey); } catch { /* not in vault */ }
                db.Connections.Remove(stale);
                deleted++;
            }

            await db.SaveChangesAsync(ct);

            var result = new HyperVSyncResult(inserted, updated, deleted, null);
            SyncCompleted?.Invoke(this, result);
            return result;
        }
        catch (Exception ex)
        {
            var result = new HyperVSyncResult(0, 0, 0, ex.Message);
            SyncCompleted?.Invoke(this, result);
            return result;
        }
    }

    /// <summary>Wipe every Hyper-V-managed connection but keep the
    /// folder. Used by the "Clear" button so the next sync repopulates
    /// from a clean slate.</summary>
    public async Task<int> ClearManagedAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var db    = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();

            var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == HyperVGroupName);
            if (group is null) return 0;

            var rows = await db.Connections
                .Where(c => c.GroupId == group.Id && c.ExternalId != null && c.ExternalId.StartsWith(ExternalIdPrefix))
                .ToListAsync();
            foreach (var c in rows)
            {
                if (!string.IsNullOrEmpty(c.CredentialKey))
                    try { vault.Delete(c.CredentialKey); } catch { /* not in vault */ }
            }
            db.Connections.RemoveRange(rows);
            await db.SaveChangesAsync();

            SyncCompleted?.Invoke(this, new HyperVSyncResult(0, 0, rows.Count, "cleared"));
            return rows.Count;
        }
        catch { return 0; }
    }

    private async Task RemoveHyperVGroupAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var db    = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();

            var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == HyperVGroupName);
            if (group is null) return;

            // Same scorched-earth contract as the Discovered group:
            // disabling the integration removes its surface area.
            var rows = await db.Connections.Where(c => c.GroupId == group.Id).ToListAsync();
            foreach (var c in rows)
            {
                if (!string.IsNullOrEmpty(c.CredentialKey))
                    try { vault.Delete(c.CredentialKey); } catch { /* not in vault */ }
            }
            db.Connections.RemoveRange(rows);
            db.Groups.Remove(group);
            await db.SaveChangesAsync();

            SyncCompleted?.Invoke(this, new HyperVSyncResult(0, 0, rows.Count, "cleanup"));
        }
        catch { /* non-fatal during shutdown */ }
    }

    /// <summary>Concurrent SSH/RDP probe — same heuristic as the
    /// Proxmox sync. Returns null when neither port is open so the
    /// caller can fall back to a default.</summary>
    private static async Task<ConnectionProtocol?> ProbeProtocolAsync(string ip, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return null;

        var ssh = TryConnectAsync(addr, 22,   ct);
        var rdp = TryConnectAsync(addr, 3389, ct);
        await Task.WhenAll(ssh, rdp).ConfigureAwait(false);

        if (rdp.Result && !ssh.Result) return ConnectionProtocol.Rdp;
        if (ssh.Result && !rdp.Result) return ConnectionProtocol.Ssh;
        if (rdp.Result &&  ssh.Result) return ConnectionProtocol.Rdp; // Windows-VM default
        return null;
    }

    private static async Task<bool> TryConnectAsync(IPAddress ip, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(600));
        try
        {
            using var sock = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await sock.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
            return sock.Connected;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}

public readonly record struct HyperVSyncResult(int Inserted, int Updated, int Deleted, string? Error)
{
    public bool IsSuccess => Error is null or "" or "cleared" or "cleanup";
    public override string ToString() =>
        Error is { Length: > 0 } and not "cleared" and not "cleanup"
            ? $"({Error})"
            : $"+{Inserted} ✎{Updated} −{Deleted}";
}
