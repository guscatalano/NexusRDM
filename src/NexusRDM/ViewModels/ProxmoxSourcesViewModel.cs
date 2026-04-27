using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Core.Proxmox;
using NexusRDM.Data.Context;
using NexusRDM.Services;

namespace NexusRDM.ViewModels;

/// <summary>
/// Backs the "Proxmox sources" section on the Settings page. Loads the
/// list from the database, exposes per-row commands, and routes
/// add/edit through the database + credential vault.
///
/// Test-connection results don't mutate the source row — they're just
/// surfaced via <see cref="ProxmoxSourceRowVm.LastTestResult"/> so the
/// user gets feedback without polluting <c>LastSyncError</c>.
/// </summary>
public sealed partial class ProxmoxSourcesViewModel : ObservableObject
{
    public ObservableCollection<ProxmoxSourceRowVm> Sources { get; } = new();

    public async Task LoadAsync()
    {
        Sources.Clear();
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var rows = await db.ProxmoxSources.AsNoTracking()
            .OrderBy(s => s.Name).ToListAsync();
        foreach (var r in rows) Sources.Add(new ProxmoxSourceRowVm(r));
    }

    /// <summary>Persist a brand-new or edited source. The secret is
    /// written to <see cref="ICredentialVault"/> under
    /// <see cref="ProxmoxClient.VaultKey(ProxmoxSource)"/>; the source
    /// row itself only stores the public auth-user string.</summary>
    public async Task SaveAsync(ProxmoxSource source, string secret)
    {
        using var scope = App.Services.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();

        var existing = await db.ProxmoxSources.FirstOrDefaultAsync(s => s.Id == source.Id);
        if (existing is null)
        {
            db.ProxmoxSources.Add(source);
        }
        else
        {
            existing.Name                = source.Name;
            existing.BaseUrl             = source.BaseUrl;
            existing.AuthMode            = source.AuthMode;
            existing.AuthUser            = source.AuthUser;
            existing.Realm               = source.Realm;
            existing.IgnoreTlsErrors     = source.IgnoreTlsErrors;
            existing.SyncIntervalMinutes = source.SyncIntervalMinutes;
            existing.DefaultProtocol     = source.DefaultProtocol;
            existing.DefaultUsername     = source.DefaultUsername;
            existing.IsEnabled           = source.IsEnabled;
        }
        await db.SaveChangesAsync();

        if (!string.IsNullOrEmpty(secret))
            vault.Save(ProxmoxVault.KeyFor(source), source.AuthUser, secret);

        await LoadAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        using var scope = App.Services.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var vault = scope.ServiceProvider.GetRequiredService<ICredentialVault>();

        var row = await db.ProxmoxSources.FirstOrDefaultAsync(s => s.Id == id);
        if (row is null) return;

        // Hard-delete every connection that originated from this source.
        // The sync engine's contract is that the cluster IS the source of
        // truth, so removing the cluster removes its imported rows.
        var managed = await db.Connections.Where(c => c.ExternalSourceId == id).ToListAsync();
        foreach (var c in managed)
        {
            if (!string.IsNullOrEmpty(c.CredentialKey))
                try { vault.Delete(c.CredentialKey); } catch { /* not in vault */ }
        }
        db.Connections.RemoveRange(managed);

        db.ProxmoxSources.Remove(row);
        await db.SaveChangesAsync();

        try { vault.Delete(ProxmoxVault.KeyFor(row)); } catch { /* no secret saved */ }

        await LoadAsync();
    }

    /// <summary>Run a full sync against the source — pulls
    /// <c>cluster/resources</c>, diffs against existing managed
    /// connections, applies inserts/updates/hard-deletes. Result string
    /// is stored on the row VM.</summary>
    public async Task SyncAsync(ProxmoxSourceRowVm row)
    {
        var sync = App.Services.GetRequiredService<ProxmoxSyncService>();
        try
        {
            var result = await sync.SyncAsync(row.Id);
            row.LastTestResult     = $"Synced — {result}";
            row.LastTestSucceeded  = true;
            row.LastSyncUtc        = DateTime.UtcNow;
            row.LastSyncError      = null;
        }
        catch (Exception ex)
        {
            row.LastTestResult     = $"Sync failed: {ex.Message}";
            row.LastTestSucceeded  = false;
            row.LastSyncError      = ex.Message;
        }
    }

    /// <summary>Ping the cluster: <c>GET /version</c> + a
    /// <c>cluster/resources?type=vm</c> count. When 0 VMs come back we
    /// also pull <c>access/permissions</c> to distinguish "really an
    /// empty cluster" from "token has no ACLs" — the latter is the
    /// most common Privsep=1 misconfiguration and silently returns
    /// an empty list.</summary>
    public async Task TestAsync(ProxmoxSourceRowVm row)
    {
        var factory = App.Services.GetRequiredService<IProxmoxClientFactory>();
        try
        {
            var client = factory.Create(row.ToModel());
            var version = await client.GetVersionAsync();
            var resources = await client.GetClusterResourcesAsync();

            if (resources.Count > 0)
            {
                row.LastTestResult    = $"OK — PVE {version.Version} • {resources.Count} VM(s)";
                row.LastTestSucceeded = true;
                return;
            }

            // Zero results — disambiguate empty cluster vs. missing ACLs.
            try
            {
                var perms = await client.GetAccessPermissionsAsync();
                var hasAnyPerms  = perms.Count > 0;
                var hasVmAudit   = perms.Values.Any(m => m.ContainsKey("VM.Audit"));

                if (!hasAnyPerms)
                {
                    row.LastTestResult = "Auth OK but token has zero ACLs. " +
                        "API tokens default to Privsep=1, so the token user " +
                        "(e.g. root@pam!nexus) needs its own permission row in " +
                        "Datacenter → Permissions — granting the role to the " +
                        "underlying user is not enough. Add at least PVEAuditor on '/'.";
                }
                else if (!hasVmAudit)
                {
                    row.LastTestResult = $"Auth OK ({perms.Count} ACL path(s)) but the role " +
                        "lacks VM.Audit, so cluster/resources is filtered to empty. " +
                        "Use PVEAuditor or another role that includes VM.Audit.";
                }
                else
                {
                    row.LastTestResult = $"OK — PVE {version.Version}, but no VMs match. " +
                        "Token has VM.Audit; the cluster genuinely has 0 VMs/CTs at the granted paths.";
                }
            }
            catch (Exception permEx)
            {
                row.LastTestResult = $"PVE {version.Version} reachable, 0 VMs returned. " +
                    $"Could not query permissions: {permEx.Message}";
            }
            row.LastTestSucceeded = false;
        }
        catch (Exception ex)
        {
            row.LastTestResult = $"Failed: {ex.Message}";
            row.LastTestSucceeded = false;
        }
    }
}

public sealed partial class ProxmoxSourceRowVm : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty] private string  _name              = string.Empty;
    [ObservableProperty] private string  _baseUrl           = string.Empty;
    [ObservableProperty] private ProxmoxAuthMode _authMode  = ProxmoxAuthMode.ApiToken;
    [ObservableProperty] private string  _authUser          = string.Empty;
    [ObservableProperty] private string  _realm             = "pam";
    [ObservableProperty] private bool    _ignoreTlsErrors;
    [ObservableProperty] private int     _syncIntervalMinutes = 15;
    [ObservableProperty] private ProxmoxDefaultProtocol _defaultProtocol = ProxmoxDefaultProtocol.Auto;
    [ObservableProperty] private string? _defaultUsername;
    [ObservableProperty] private bool    _isEnabled         = true;
    [ObservableProperty] private DateTime? _lastSyncUtc;
    [ObservableProperty] private string?  _lastSyncError;
    [ObservableProperty] private string?  _lastTestResult;
    [ObservableProperty] private bool     _lastTestSucceeded;

    public string LastSyncDisplay =>
        LastSyncUtc is null ? "never"
        : $"{(DateTime.UtcNow - LastSyncUtc.Value).TotalMinutes:F0} min ago";

    public ProxmoxSourceRowVm() { Id = Guid.NewGuid(); }

    public ProxmoxSourceRowVm(ProxmoxSource source)
    {
        Id                  = source.Id;
        Name                = source.Name;
        BaseUrl             = source.BaseUrl;
        AuthMode            = source.AuthMode;
        AuthUser            = source.AuthUser;
        Realm               = source.Realm;
        IgnoreTlsErrors     = source.IgnoreTlsErrors;
        SyncIntervalMinutes = source.SyncIntervalMinutes;
        DefaultProtocol     = source.DefaultProtocol;
        DefaultUsername     = source.DefaultUsername;
        IsEnabled           = source.IsEnabled;
        LastSyncUtc         = source.LastSyncUtc;
        LastSyncError       = source.LastSyncError;
    }

    public ProxmoxSource ToModel() => new()
    {
        Id                  = Id,
        Name                = Name,
        BaseUrl             = BaseUrl,
        AuthMode            = AuthMode,
        AuthUser            = AuthUser,
        Realm               = Realm,
        IgnoreTlsErrors     = IgnoreTlsErrors,
        SyncIntervalMinutes = SyncIntervalMinutes,
        DefaultProtocol     = DefaultProtocol,
        DefaultUsername     = DefaultUsername,
        IsEnabled           = IsEnabled,
        LastSyncUtc         = LastSyncUtc,
        LastSyncError       = LastSyncError,
    };
}
