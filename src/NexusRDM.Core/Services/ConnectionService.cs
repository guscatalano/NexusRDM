using Microsoft.Extensions.Logging;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Core.Services;

public sealed class ConnectionService : IConnectionService
{
    private readonly IConnectionRepository    _repo;
    private readonly IAuditRepository         _audit;
    private readonly ICredentialVault?        _vault;
    private readonly ILogger<ConnectionService> _log;

    public ConnectionService(IConnectionRepository repo, IAuditRepository audit,
        ILogger<ConnectionService> log, ICredentialVault? vault = null)
    { _repo = repo; _audit = audit; _log = log; _vault = vault; }

    public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default) => _repo.GetAllAsync(ct);
    public Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)     => _repo.GetByIdAsync(id, ct);
    public Task<IReadOnlyList<ConnectionProfile>> SearchAsync(string q, CancellationToken ct = default) => _repo.SearchAsync(q, ct);

    public async Task<ConnectionProfile> CreateAsync(ConnectionProfile p, CancellationToken ct = default)
    {
        var created = await _repo.AddAsync(p, ct);
        await _audit.LogAsync(new AuditEntry { ConnectionId = created.Id, DisplayName = created.DisplayName, Action = AuditAction.Created }, ct);
        _log.LogInformation("Created connection {Name} ({Id})", created.DisplayName, created.Id);
        return created;
    }

    public async Task UpdateAsync(ConnectionProfile p, CancellationToken ct = default)
    {
        // Snapshot the previous state so the audit Detail can list the
        // exact fields the user changed, not just "Updated". The diff
        // runs before UpdateAsync because the repo will overwrite the
        // tracked entity in place.
        var before = await _repo.GetByIdAsync(p.Id, ct);
        await _repo.UpdateAsync(p, ct);
        var detail = before is null ? null : DiffProfile(before, p);
        await _audit.LogAsync(new AuditEntry
        {
            ConnectionId = p.Id,
            DisplayName  = p.DisplayName,
            Action       = AuditAction.Updated,
            Detail       = detail,
        }, ct);
    }

    public async Task<string?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var p = await _repo.GetByIdAsync(id, ct);
        if (p is null) return null;

        // Drop the saved credential if this profile owned one. Best-effort:
        // if wincred refuses, log + return a warning that callers can show
        // to the user; we still proceed with the DB delete because leaving
        // a profile around just because its credential is sticky is worse
        // than the orphan record in the vault.
        string? warning = null;
        if (_vault is not null && !string.IsNullOrEmpty(p.CredentialKey))
        {
            try
            {
                _vault.Delete(p.CredentialKey);
                _log.LogInformation("Deleted credential {Key} for {Name}", p.CredentialKey, p.DisplayName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete credential {Key} for {Name}; orphaned in vault",
                    p.CredentialKey, p.DisplayName);
                warning =
                    $"Couldn't remove the saved credential for '{p.DisplayName}' " +
                    $"(key: {p.CredentialKey}). It's still in Windows Credential Manager. " +
                    $"Reason: {ex.Message}";
            }
        }

        await _repo.DeleteAsync(id, ct);
        await _audit.LogAsync(new AuditEntry { ConnectionId = id, DisplayName = p.DisplayName, Action = AuditAction.Deleted }, ct);
        return warning;
    }

    public Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken ct = default)              => _repo.GetGroupsAsync(ct);
    public Task<Group> CreateGroupAsync(Group g, CancellationToken ct = default)                  => _repo.AddGroupAsync(g, ct);
    public Task UpdateGroupAsync(Group g, CancellationToken ct = default)                         => _repo.UpdateGroupAsync(g, ct);
    public Task DeleteGroupAsync(Guid id, CancellationToken ct = default)                         => _repo.DeleteGroupAsync(id, ct);

    public async Task RecordConnectedAsync(Guid id, CancellationToken ct = default)
    {
        await _repo.UpdateLastConnectedAsync(id, DateTime.UtcNow, ct);
        var p = await _repo.GetByIdAsync(id, ct);
        await _audit.LogAsync(new AuditEntry
        {
            ConnectionId = id,
            DisplayName  = p?.DisplayName ?? string.Empty,
            Action       = AuditAction.Connected,
            Detail       = p is null ? null : $"{p.Protocol} → {p.Host}:{p.Port}",
        }, ct);
    }

    public async Task RecordDisconnectedAsync(Guid id, string? reason = null, CancellationToken ct = default)
    {
        var p = await _repo.GetByIdAsync(id, ct);
        await _audit.LogAsync(new AuditEntry
        {
            ConnectionId = id,
            DisplayName  = p?.DisplayName ?? string.Empty,
            Action       = AuditAction.Disconnected,
            Detail       = reason,
        }, ct);
    }

    public async Task RecordFailedAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var p = await _repo.GetByIdAsync(id, ct);
        await _audit.LogAsync(new AuditEntry
        {
            ConnectionId = id,
            DisplayName  = p?.DisplayName ?? string.Empty,
            Action       = AuditAction.Failed,
            Detail       = reason,
        }, ct);
    }

    /// <summary>Builds a human-readable summary of the fields that
    /// changed between two <see cref="ConnectionProfile"/> snapshots.
    /// Returns null when nothing changed (the audit Detail stays empty
    /// in that case so we don't mislead the user).</summary>
    private static string? DiffProfile(ConnectionProfile a, ConnectionProfile b)
    {
        var parts = new List<string>();

        void Compare<T>(string field, T x, T y)
        {
            if (!Equals(x, y))
                parts.Add($"{field}: {Render(x)} → {Render(y)}");
        }
        static string Render(object? v) =>
            v switch { null => "(none)", "" => "(empty)", _ => v.ToString()! };

        Compare("Name",        a.DisplayName,    b.DisplayName);
        Compare("Protocol",    a.Protocol,       b.Protocol);
        Compare("Host",        a.Host,           b.Host);
        Compare("Port",        a.Port,           b.Port);
        Compare("Tags",        a.Tags,           b.Tags);
        Compare("Group",       a.GroupId,        b.GroupId);
        Compare("Credential",  a.CredentialKey,  b.CredentialKey);
        // Embedded JSON blobs: surfacing the full diff would dwarf
        // everything else, so just flag that protocol-specific options
        // moved.
        if (!string.Equals(a.RdpSettingsJson, b.RdpSettingsJson, StringComparison.Ordinal))
            parts.Add("RDP options changed");
        if (!string.Equals(a.SshSettingsJson, b.SshSettingsJson, StringComparison.Ordinal))
            parts.Add("SSH options changed");

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    public Task<IReadOnlyList<AuditEntry>> GetRecentAuditAsync(int limit = 100, CancellationToken ct = default)
        => _audit.GetRecentAsync(limit, ct);
}
