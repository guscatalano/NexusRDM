using Microsoft.EntityFrameworkCore;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Data.Context;

namespace NexusRDM.Data.Repositories;

public sealed class AuditRepository : IAuditRepository
{
    private readonly NexusDbContext _db;
    public AuditRepository(NexusDbContext db) => _db = db;

    public async Task LogAsync(AuditEntry entry, CancellationToken ct = default)
    {
        _db.AuditLog.Add(entry);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> GetForConnectionAsync(
        Guid connectionId, int limit = 50, CancellationToken ct = default) =>
        await _db.AuditLog
            .Where(a => a.ConnectionId == connectionId)
            .OrderByDescending(a => a.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(
        int limit = 100, CancellationToken ct = default) =>
        await _db.AuditLog
            .OrderByDescending(a => a.OccurredAt)
            .Take(limit)
            .ToListAsync(ct);
}
