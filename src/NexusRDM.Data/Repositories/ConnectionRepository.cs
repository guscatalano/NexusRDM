using Microsoft.EntityFrameworkCore;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Data.Context;

namespace NexusRDM.Data.Repositories;

public sealed class ConnectionRepository : IConnectionRepository
{
    private readonly NexusDbContext _db;
    public ConnectionRepository(NexusDbContext db) => _db = db;

    public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Connections.AsNoTracking().Include(c => c.Group).OrderBy(c => c.DisplayName).ToListAsync(ct);

    public Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Connections.AsNoTracking().Include(c => c.Group).FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<ConnectionProfile> AddAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        _db.Connections.Add(profile);
        await _db.SaveChangesAsync(ct);
        // Detach so a later Update with a fresh instance with the same Id won't collide.
        _db.Entry(profile).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        return profile;
    }

    public async Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        DetachTracked<ConnectionProfile>(profile.Id);
        _db.Connections.Update(profile);
        await _db.SaveChangesAsync(ct);
        _db.Entry(profile).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        DetachTracked<ConnectionProfile>(id);
        var p = await _db.Connections.FindAsync([id], ct);
        if (p is not null) _db.Connections.Remove(p);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The DbContext is registered as Scoped but a desktop app has only one root
    /// scope, so entities tracked from earlier operations (e.g. a previous Add)
    /// stay alive and collide with new instances having the same Id. Detach any
    /// tracked entity that matches the given Id before we attach a new one.
    /// </summary>
    private void DetachTracked<T>(Guid id) where T : class
    {
        var tracked = _db.ChangeTracker.Entries<T>()
            .FirstOrDefault(e => (Guid)e.Property("Id").CurrentValue! == id);
        if (tracked is not null)
            tracked.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    }

    public async Task UpdateLastConnectedAsync(Guid id, DateTime at, CancellationToken ct = default) =>
        await _db.Connections.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastConnectedAt, at), ct);

    public async Task<IReadOnlyList<ConnectionProfile>> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query.ToLower();
        return await _db.Connections.AsNoTracking()
            .Where(c => c.DisplayName.ToLower().Contains(q) ||
                        c.Host.ToLower().Contains(q) ||
                        c.Tags.ToLower().Contains(q))
            .OrderBy(c => c.DisplayName)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken ct = default) =>
        await _db.Groups.AsNoTracking().Include(g => g.Children)
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(ct);

    public async Task<Group> AddGroupAsync(Group group, CancellationToken ct = default)
    {
        _db.Groups.Add(group);
        await _db.SaveChangesAsync(ct);
        _db.Entry(group).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        return group;
    }

    public async Task UpdateGroupAsync(Group group, CancellationToken ct = default)
    {
        DetachTracked<Group>(group.Id);
        _db.Groups.Update(group);
        await _db.SaveChangesAsync(ct);
        _db.Entry(group).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    }

    public async Task DeleteGroupAsync(Guid id, CancellationToken ct = default)
    {
        DetachTracked<Group>(id);
        var g = await _db.Groups.FindAsync([id], ct);
        if (g is not null) _db.Groups.Remove(g);
        await _db.SaveChangesAsync(ct);
    }
}
