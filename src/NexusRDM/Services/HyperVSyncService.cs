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

    /// <summary>Last-known power state per managed connection, same
    /// shape as <see cref="ProxmoxSyncService.GetPowerState"/>.
    /// Reuses the <see cref="ProxmoxPowerState"/> enum since the
    /// running/stopped/paused tri-state is identical across sources;
    /// the name's a historical artefact, not a Proxmox-only thing.</summary>
    private readonly Dictionary<Guid, (ProxmoxPowerState State, DateTime UpdatedAtUtc)> _powerStates = new();
    private readonly object _powerLock = new();

    public ProxmoxPowerState GetPowerState(Guid connectionId)
        => GetPowerStateInfo(connectionId).State;

    public (ProxmoxPowerState State, DateTime? UpdatedAtUtc) GetPowerStateInfo(Guid connectionId)
    {
        lock (_powerLock)
        {
            if (_powerStates.TryGetValue(connectionId, out var v))
                return (v.State, v.UpdatedAtUtc);
            return (ProxmoxPowerState.Unknown, null);
        }
    }

    private void SetPowerState(Guid id, HyperVVmState state)
    {
        var mapped = state switch
        {
            HyperVVmState.Running => ProxmoxPowerState.Running,
            HyperVVmState.Off     => ProxmoxPowerState.Stopped,
            HyperVVmState.Paused  => ProxmoxPowerState.Paused,
            HyperVVmState.Saved   => ProxmoxPowerState.Stopped, // saved-state reads as stopped
            _                     => ProxmoxPowerState.Unknown, // transitional states
        };
        lock (_powerLock) _powerStates[id] = (mapped, DateTime.UtcNow);
    }

    public event EventHandler<HyperVSyncResult>? SyncCompleted;

    /// <summary>True when scheduled syncs can run silently in the
    /// background. The agent's <c>requireAdministrator</c> manifest
    /// triggers UAC on each launch UNLESS the parent NexusRDM is
    /// already elevated, in which case ShellExecute skips the
    /// prompt. Timer is therefore on iff this returns true AND the
    /// integration toggle is on.</summary>
    public bool IsScheduledSyncAvailable => HyperVClient.IsCurrentProcessElevated();

    public HyperVSyncService(IServiceProvider services)
    {
        _services = services;
        SettingsStore.HyperVSettingsChanged += (_, _) => OnSettingsChanged();
        OnSettingsChanged();
    }

    private void OnSettingsChanged()
    {
        _timer?.Dispose();
        _timer = null;

        if (!SettingsStore.ReadHyperVEnabled())
        {
            _ = RemoveHyperVGroupAsync();
            return;
        }

        // Toggle is on. Only arm the timer if we'd run silently.
        // Unelevated parent → skip the timer; manual Sync still works
        // and the Settings panel surfaces the state.
        if (!IsScheduledSyncAvailable) return;

        var interval = TimeSpan.FromMinutes(SettingsStore.ReadHyperVSyncIntervalMinutes());
        _timer = new Timer(async _ =>
        {
            try { await SyncAsync(); }
            catch { /* surfaced via SyncCompleted */ }
        }, null, TimeSpan.FromSeconds(15), interval);
    }

    /// <summary>Run a sync. Always launches the elevated agent — one
    /// UAC prompt per call. Cancelling the prompt surfaces as
    /// <see cref="OperationCanceledException"/> wrapped in the
    /// <see cref="HyperVSyncResult"/>'s Error field.</summary>
    public async Task<HyperVSyncResult> SyncAsync(CancellationToken ct = default)
    {
        // No-op while demo mode is on — see ProxmoxSyncService for the
        // matching rationale.
        var demoCheck = _services.GetService(typeof(DemoModeService)) as DemoModeService;
        if (demoCheck?.IsActive == true)
            return new HyperVSyncResult(0, 0, 0, "demo mode");

        IReadOnlyList<HyperVVm> vms;
        try { vms = await new HyperVClient().ListVmsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            var fail = new HyperVSyncResult(0, 0, 0, ex.Message);
            SyncCompleted?.Invoke(this, fail);
            return fail;
        }
        return await ApplyVmsAsync(vms, ct, isManual: true).ConfigureAwait(false);
    }

    /// <summary>Diff-merge a pre-fetched list of VMs into the DB.
    /// Used by both the manual <see cref="SyncAsync"/> path and the
    /// background loop (which already has the data from the agent's
    /// JSON file). Lifted out of SyncAsync so neither path
    /// re-implements it.</summary>
    private async Task<HyperVSyncResult> ApplyVmsAsync(
        IReadOnlyList<HyperVVm> vms, CancellationToken ct = default, bool isManual = false)
    {
        try
        {
            // (legacy local "vms" var stays in scope for the body below)

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
                    SetPowerState(row.Id, vm.State);
                    updated++;
                }
                else
                {
                    var protocol = !string.IsNullOrEmpty(vm.Ip) && SettingsStore.ReadHyperVProbeProtocol()
                        ? await ProbeProtocolAsync(vm.Ip!, ct).ConfigureAwait(false) ?? ConnectionProtocol.Rdp
                        : ConnectionProtocol.Rdp; // sensible default for Hyper-V (mostly Windows VMs)

                    var newProfile = new ConnectionProfile
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
                    };
                    db.Connections.Add(newProfile);
                    SetPowerState(newProfile.Id, vm.State);
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

            // Audit only manual syncs — the background loop fires
            // every interval and would balloon the log.
            if (isManual)
            {
                try
                {
                    var audit = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
                    await audit.LogAsync(new AuditEntry
                    {
                        ConnectionId = Guid.Empty,
                        DisplayName  = HyperVGroupName,
                        Action       = AuditAction.Synced,
                        Detail       = $"Hyper-V sync: +{inserted} ✎{updated} −{deleted}",
                    }, ct);
                }
                catch { /* audit failure shouldn't undo a successful sync */ }
            }

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

    // ── Background loop (long-lived elevated agent) ─────────────────────

    private System.Diagnostics.Process? _loopProc;
    private string?                     _loopOutputPath;
    private Timer?                      _loopPoll;
    private DateTime                    _loopLastFileWriteUtc = DateTime.MinValue;

    /// <summary>True while the long-lived elevated agent is running.
    /// Settings UI binds to this to flip the "start / stop" button
    /// label.</summary>
    public bool IsBackgroundLoopRunning => _loopProc is { HasExited: false };

    /// <summary>Spawn the elevated agent in <c>loop</c> mode. UAC
    /// prompts once at this call. After that the agent runs silently
    /// and rewrites its output JSON every interval; we poll that
    /// file and feed the data into <see cref="ApplyVmsAsync"/>, the
    /// same diff-merge the manual Sync uses.</summary>
    public async Task StartBackgroundLoopAsync()
    {
        if (IsBackgroundLoopRunning) return;

        var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "NexusRDM.HyperVAgent.exe");
        if (!System.IO.File.Exists(exe))
            throw new System.IO.FileNotFoundException(
                "Hyper-V agent missing (NexusRDM.HyperVAgent.exe). Reinstall or rebuild.", exe);

        var interval = Math.Max(1, SettingsStore.ReadHyperVSyncIntervalMinutes()) * 60;
        _loopOutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"nexusrdm-hv-loop-{Guid.NewGuid():N}.json");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName        = exe,
            UseShellExecute = true,
            Verb            = "runas",
            CreateNoWindow  = true,
            WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("loop");
        psi.ArgumentList.Add(interval.ToString());
        psi.ArgumentList.Add(_loopOutputPath);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());

        try
        {
            _loopProc = System.Diagnostics.Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            _loopOutputPath = null;
            throw new OperationCanceledException("UAC prompt was cancelled.", wex);
        }

        if (_loopProc is null)
        {
            _loopOutputPath = null;
            throw new InvalidOperationException("Failed to start Hyper-V agent loop.");
        }

        // Poll the output file at half the agent's interval (min 10s),
        // ignore unchanged files via mtime, parse + apply on each new
        // version. Background work — no UAC, no UI thread blocking.
        var pollSeconds = Math.Max(10, interval / 2);
        _loopPoll = new Timer(async _ => await PollLoopFileAsync().ConfigureAwait(false),
            null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(pollSeconds));

        await Task.CompletedTask;
    }

    public void StopBackgroundLoop()
    {
        _loopPoll?.Dispose();
        _loopPoll = null;
        try { _loopProc?.Kill(); } catch { /* may already be gone */ }
        _loopProc = null;
        if (_loopOutputPath is not null)
        {
            try { System.IO.File.Delete(_loopOutputPath); } catch { }
            _loopOutputPath = null;
        }
    }

    private async Task PollLoopFileAsync()
    {
        if (_loopOutputPath is null) return;
        try
        {
            var fi = new System.IO.FileInfo(_loopOutputPath);
            if (!fi.Exists || fi.Length == 0) return;
            if (fi.LastWriteTimeUtc <= _loopLastFileWriteUtc) return; // nothing new
            _loopLastFileWriteUtc = fi.LastWriteTimeUtc;

            using var stream = System.IO.File.OpenRead(_loopOutputPath);
            var doc = await System.Text.Json.JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out _))
            {
                // Agent surfaced a transient WMI error in its file —
                // ignore for now; next iteration may succeed.
                return;
            }

            // Agent's `loop` writes the same DTO shape as `list`.
            var dtos = System.Text.Json.JsonSerializer.Deserialize<List<AgentLoopVmDto>>(
                doc.RootElement.GetRawText(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dtos is null) return;

            var vms = dtos.Select(d => new HyperVVm(
                d.Id ?? "",
                d.Name ?? d.Id ?? "",
                Enum.TryParse<HyperVVmState>(d.State, ignoreCase: true, out var s) ? s : HyperVVmState.Unknown,
                d.Ip)).ToList();

            await ApplyVmsAsync(vms).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "HyperV background poll");
        }
    }

    private sealed class AgentLoopVmDto
    {
        public string?  Id    { get; set; }
        public string?  Name  { get; set; }
        public string?  State { get; set; }
        public string?  Ip    { get; set; }
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
        StopBackgroundLoop();
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
