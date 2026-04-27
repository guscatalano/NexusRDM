using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Core.Interfaces;

public interface IConnectionRepository
{
    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default);
    Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ConnectionProfile> AddAsync(ConnectionProfile profile, CancellationToken ct = default);
    Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task UpdateLastConnectedAsync(Guid id, DateTime at, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectionProfile>> SearchAsync(string query, CancellationToken ct = default);

    Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken ct = default);
    Task<Group> AddGroupAsync(Group group, CancellationToken ct = default);
    Task UpdateGroupAsync(Group group, CancellationToken ct = default);
    Task DeleteGroupAsync(Guid id, CancellationToken ct = default);
}

public interface IAuditRepository
{
    Task LogAsync(AuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetForConnectionAsync(Guid connectionId, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>Bulk-delete every audit entry whose <c>OccurredAt</c> is
    /// older than <paramref name="cutoff"/>. Returns the number of rows
    /// removed so callers can log retention activity.</summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}
