using NexusRDM.Core.Models;

namespace NexusRDM.Core.Interfaces;

public interface IConnectionService
{
    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default);
    Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ConnectionProfile> CreateAsync(ConnectionProfile profile, CancellationToken ct = default);
    Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default);
    /// <summary>Delete the profile and any vault credential it owns.
    /// Returns a non-null warning string when the DB row was removed but
    /// the credential could not be — callers should surface it to the
    /// user. Returns null on a fully clean delete (or when the profile
    /// didn't exist).</summary>
    Task<string?> DeleteAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken ct = default);
    Task<Group> CreateGroupAsync(Group group, CancellationToken ct = default);
    Task UpdateGroupAsync(Group group, CancellationToken ct = default);
    Task DeleteGroupAsync(Guid id, CancellationToken ct = default);

    Task RecordConnectedAsync(Guid connectionId, CancellationToken ct = default);
    Task RecordDisconnectedAsync(Guid connectionId, string? reason = null, CancellationToken ct = default);
    Task RecordFailedAsync(Guid connectionId, string reason, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectionProfile>> SearchAsync(string query, CancellationToken ct = default);

    /// <summary>Return the N most recent audit entries across all connections.</summary>
    Task<IReadOnlyList<AuditEntry>> GetRecentAuditAsync(int limit = 100, CancellationToken ct = default);
}
