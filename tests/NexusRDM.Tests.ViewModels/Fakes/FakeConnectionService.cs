using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Tests.ViewModels.Fakes;

/// <summary>
/// In-memory IConnectionService stub. Records the last Create/Update payload and
/// surfaces simple state so VM tests can assert what the VM tried to persist.
/// </summary>
public sealed class FakeConnectionService : IConnectionService
{
    public List<ConnectionProfile> Profiles { get; } = new();
    public List<Group>             Groups   { get; } = new();
    public ConnectionProfile?      LastCreated  { get; private set; }
    public ConnectionProfile?      LastUpdated  { get; private set; }
    public Guid?                   LastDeletedId { get; private set; }
    public Func<ConnectionProfile, Task>? OnCreate { get; set; }

    public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConnectionProfile>>(Profiles);

    public Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Profiles.FirstOrDefault(p => p.Id == id));

    public async Task<ConnectionProfile> CreateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        if (OnCreate is not null) await OnCreate(profile);
        LastCreated = profile;
        Profiles.Add(profile);
        return profile;
    }

    public Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        LastUpdated = profile;
        var existing = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        if (existing is not null) Profiles.Remove(existing);
        Profiles.Add(profile);
        return Task.CompletedTask;
    }

    public Task<string?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        LastDeletedId = id;
        Profiles.RemoveAll(p => p.Id == id);
        return Task.FromResult<string?>(null);
    }

    public Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Group>>(Groups);

    public Task<Group> CreateGroupAsync(Group group, CancellationToken ct = default)
    {
        Groups.Add(group);
        return Task.FromResult(group);
    }

    public Task UpdateGroupAsync(Group group, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteGroupAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordConnectedAsync(Guid connectionId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordDisconnectedAsync(Guid connectionId, string? reason = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordFailedAsync(Guid connectionId, string reason, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ConnectionProfile>> SearchAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConnectionProfile>>(
            Profiles.Where(p => p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());

    public Task<IReadOnlyList<AuditEntry>> GetRecentAuditAsync(int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(Array.Empty<AuditEntry>());
}
