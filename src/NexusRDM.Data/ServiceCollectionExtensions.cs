using Microsoft.EntityFrameworkCore;
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
            opts.UseSqlite($"Data Source={dbPath}"));

        services.AddScoped<IConnectionRepository, ConnectionRepository>();
        services.AddScoped<IAuditRepository,      AuditRepository>();
        return services;
    }
}
