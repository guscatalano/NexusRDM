using NexusRDM.Core.Models;

namespace NexusRDM.Core.Interfaces;

public interface IConnectionService
{
    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default);
    Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<ConnectionProfile> CreateAsync(ConnectionProfile profile, CancellationToken ct = default);
    Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken ct = default);
    Task<Group> CreateGroupAsync(Group group, CancellationToken ct = default);
    Task UpdateGroupAsync(Group group, CancellationToken ct = default);
    Task DeleteGroupAsync(Guid id, CancellationToken ct = default);

    Task RecordConnectedAsync(Guid connectionId, CancellationToken ct = default);
    Task RecordFailedAsync(Guid connectionId, string reason, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectionProfile>> SearchAsync(string query, CancellationToken ct = default);
}
