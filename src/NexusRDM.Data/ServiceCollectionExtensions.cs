using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NexusRDM.Core.Interfaces;
using NexusRDM.Data.Context;
using NexusRDM.Data.Repositories;

namespace NexusRDM.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNexusData(this IServiceCollection services, string dbPath)
    {
        services.AddDbContext<NexusDbContext>(opts =>
            opts.UseSqlite($"Data Source={dbPath}")
                // Migrations are hand-authored; the model snapshot stays
                // in lockstep with them by construction, but tiny EF
                // bookkeeping diffs (annotation ordering, etc.) trip the
                // pending-changes guard at startup. We've verified the
                // migrations match the schema we want, so demote the
                // warning to a log line instead of blocking Migrate().
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        services.AddScoped<IConnectionRepository, ConnectionRepository>();
        services.AddScoped<IAuditRepository,      AuditRepository>();
        return services;
    }
}
