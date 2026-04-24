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
        await _db.Connections.Include(c => c.Group).OrderBy(c => c.DisplayName).ToListAsync(ct);

    public Task<ConnectionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Connections.Include(c => c.Group).FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<ConnectionProfile> AddAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        _db.Connections.Add(profile);
        await _db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task UpdateAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        _db.Connections.Update(profile);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var p = await _db.Connections.FindAsync([id], ct);
        if (p is not null) _db.Connections.Remove(p);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateLastConnectedAsync(Guid id, DateTime at, CancellationToken ct = default) =>
        await _db.Connections.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastConnectedAt, at), ct);

    public async Task<IReadOnlyList<ConnectionProfile>> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = query.ToLower();
        return await _db.Connections
            .Where(c => c.DisplayName.ToLower().Contains(q) ||
                        c.Host.ToLower().Contains(q) ||
                        c.Tags.ToLower().Contains(q))
            .OrderBy(c => c.DisplayName)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken ct = default) =>
        await _db.Groups.Include(g => g.Children)
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name).ToListAsync(ct);

    public async Task<Group> AddGroupAsync(Group group, CancellationToken ct = default)
    {
        _db.Groups.Add(group);
        await _db.SaveChangesAsync(ct);
        return group;
    }

    public async Task UpdateGroupAsync(Group group, CancellationToken ct = default)
    {
        _db.Groups.Update(group);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteGroupAsync(Guid id, CancellationToken ct = default)
    {
        var g = await _db.Groups.FindAsync([id], ct);
        if (g is not null) _db.Groups.Remove(g);
        await _db.SaveChangesAsync(ct);
    }
}
