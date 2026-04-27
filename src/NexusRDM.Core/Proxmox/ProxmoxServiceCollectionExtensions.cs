using Microsoft.Extensions.DependencyInjection;

namespace NexusRDM.Core.Proxmox;

public static class ProxmoxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProxmoxClientFactory"/>. The factory takes
    /// <see cref="Models.ProxmoxSource"/> values at call time and pulls
    /// their secrets from <see cref="Interfaces.ICredentialVault"/>, so
    /// we don't bind a client to a specific source up front.
    /// </summary>
    public static IServiceCollection AddNexusProxmox(this IServiceCollection services)
    {
        services.AddSingleton<IProxmoxClientFactory, ProxmoxClientFactory>();
        return services;
    }
}
