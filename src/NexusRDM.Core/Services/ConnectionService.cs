using Microsoft.Extensions.Logging;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Core.Services;

/// <summary>
/// Orchestrates the connection repository and audit log.
/// This is what ViewModels depend on — never the repository directly.
/// </summary>
public sealed class ConnectionService : IConnectionService
{
    private readonly IConnectionRepository    _repo;
    private readonly IAuditRepository         _audit;
    private readonly ILogger<ConnectionService> _log;

    public ConnectionService(
        IConnectionRepository repo,
        IAuditRepository audit,
        ILogger<ConnectionService> log)
    {
        _repo  = repo;
        _audit = audit;
        _log   = log;
    }

    public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default)
        => _repo.GetAllAsync(ct);

    public Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _repo.GetByIdAsync(id, ct);

    public async Task<ConnectionProfile> CreateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var created = await _repo.AddAsync(profile, ct);
        await _audit.LogAsync(new AuditEntry
        {
            ConnectionId = created.Id,
            DisplayName  = created.DisplayName,
            Action       = AuditAction.Created
        }, ct);
        _log.LogInformation("Created connection {Name} ({Id})", created.DisplayName, created.Id);
        return created;
    }

    public async Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        await _repo.UpdateAsync(profile, ct);
        await _audit.LogAsync(new AuditEntry
        {
            ConnectionId = profile.Id,
            DisplayName  = profile.DisplayName,
            Action       = AuditAction.Updated
        }, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var profile = await _repo.GetByIdAsync(id, ct);
        if (profile is null) return;
        await _repo.DeleteAsync(id, ct);
        await _audit.LogAsync(new AuditEntry
        {
            ConnectionId = id,
            DisplayName  = profile.DisplayName,
            Action       = AuditAction.Deleted
        }, ct);
    }

    public Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken ct = default)
        => _repo.GetGroupsAsync(ct);

    public Task<Group> CreateGroupAsync(Group group, CancellationToken ct = default)
        => _repo.AddGroupAsync(group, ct);

    public Task UpdateGroupAsync(Group group, CancellationToken ct = default)
        => _repo.UpdateGroupAsync(group, ct);

    public Task DeleteGroupAsync(Guid id, CancellationToken ct = default)
        => _repo.DeleteGroupAsync(id, ct);

    public async Task RecordConnectedAsync(Guid connectionId, CancellationToken ct = default)
    {
        await _repo.UpdateLastConnectedAsync(connectionId, DateTime.UtcNow, ct);
        await _audit.LogAsync(new AuditEntry
        {
            ConnectionId = connectionId,
            DisplayName  = string.Empty,
            Action       = AuditAction.Connected
        }, ct);
    }

    public async Task RecordFailedAsync(Guid connectionId, string reason, CancellationToken ct = default)
    {
        await _audit.LogAsync(new AuditEntry
        {
            ConnectionId = connectionId,
            DisplayName  = string.Empty,
            Action       = AuditAction.Failed,
            Detail       = reason
        }, ct);
    }

    public Task<IReadOnlyList<ConnectionProfile>> SearchAsync(string query, CancellationToken ct = default)
        => _repo.SearchAsync(query, ct);
}
