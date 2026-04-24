using Microsoft.Extensions.Logging;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Core.Services;

public sealed class ConnectionService : IConnectionService
{
    private readonly IConnectionRepository    _repo;
    private readonly IAuditRepository         _audit;
    private readonly ILogger<ConnectionService> _log;

    public ConnectionService(IConnectionRepository repo, IAuditRepository audit,
        ILogger<ConnectionService> log)
    { _repo = repo; _audit = audit; _log = log; }

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
        await _repo.UpdateAsync(p, ct);
        await _audit.LogAsync(new AuditEntry { ConnectionId = p.Id, DisplayName = p.DisplayName, Action = AuditAction.Updated }, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var p = await _repo.GetByIdAsync(id, ct);
        if (p is null) return;
        await _repo.DeleteAsync(id, ct);
        await _audit.LogAsync(new AuditEntry { ConnectionId = id, DisplayName = p.DisplayName, Action = AuditAction.Deleted }, ct);
    }

    public Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken ct = default)              => _repo.GetGroupsAsync(ct);
    public Task<Group> CreateGroupAsync(Group g, CancellationToken ct = default)                  => _repo.AddGroupAsync(g, ct);
    public Task UpdateGroupAsync(Group g, CancellationToken ct = default)                         => _repo.UpdateGroupAsync(g, ct);
    public Task DeleteGroupAsync(Guid id, CancellationToken ct = default)                         => _repo.DeleteGroupAsync(id, ct);

    public async Task RecordConnectedAsync(Guid id, CancellationToken ct = default)
    {
        await _repo.UpdateLastConnectedAsync(id, DateTime.UtcNow, ct);
        await _audit.LogAsync(new AuditEntry { ConnectionId = id, DisplayName = string.Empty, Action = AuditAction.Connected }, ct);
    }

    public async Task RecordFailedAsync(Guid id, string reason, CancellationToken ct = default)
    {
        await _audit.LogAsync(new AuditEntry { ConnectionId = id, DisplayName = string.Empty, Action = AuditAction.Failed, Detail = reason }, ct);
    }

    public Task<IReadOnlyList<AuditEntry>> GetRecentAuditAsync(int limit = 100, CancellationToken ct = default)
        => _audit.GetRecentAsync(limit, ct);
}
