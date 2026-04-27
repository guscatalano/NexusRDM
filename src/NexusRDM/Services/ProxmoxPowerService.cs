using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusRDM.Core.Models;
using NexusRDM.Core.Proxmox;
using NexusRDM.Data.Context;

namespace NexusRDM.Services;

/// <summary>
/// Thin façade between the UI and <see cref="IProxmoxClient.PowerActionAsync"/>.
/// Resolves a managed <see cref="ConnectionProfile"/> back to its source
/// + node/type/vmid coordinates, builds a client for the right cluster,
/// and translates errors into user-readable messages.
///
/// Lives next to <see cref="ProxmoxSyncService"/> for symmetry; both are
/// the WinUI-side seam between the connection tree and the Core API
/// surface.
/// </summary>
public sealed class ProxmoxPowerService
{
    private readonly IServiceProvider _services;
    public ProxmoxPowerService(IServiceProvider services) => _services = services;

    /// <summary>Trigger <paramref name="action"/> against the cluster
    /// node hosting <paramref name="connection"/>. Returns the task
    /// UPID on success. Throws if the connection isn't a managed
    /// Proxmox row, or if the API rejects the call.</summary>
    public async Task<string> InvokeAsync(
        ConnectionProfile connection, ProxmoxPowerAction action, CancellationToken ct = default)
    {
        if (!connection.IsManaged || connection.ExternalSourceId is null
         || string.IsNullOrEmpty(connection.ExternalId))
            throw new InvalidOperationException(
                "Power actions are only available on Proxmox-managed connections.");

        var (node, type, vmid) = ParseExternalId(connection.ExternalId);

        using var scope = _services.CreateScope();
        var db      = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IProxmoxClientFactory>();

        var source = await db.ProxmoxSources
            .FirstOrDefaultAsync(s => s.Id == connection.ExternalSourceId, ct)
            ?? throw new InvalidOperationException(
                $"Proxmox source {connection.ExternalSourceId} no longer exists.");

        var client = factory.Create(source);
        try { return await client.PowerActionAsync(node, type, vmid, action, ct); }
        finally { (client as IDisposable)?.Dispose(); }
    }

    /// <summary>External id format is <c>"{node}/{type}/{vmid}"</c> —
    /// the same string the sync engine writes. Throws on malformed
    /// input so callers get a clear error instead of a 404 from PVE.</summary>
    public static (string Node, string Type, int Vmid) ParseExternalId(string externalId)
    {
        var parts = externalId.Split('/');
        if (parts.Length != 3
         || string.IsNullOrEmpty(parts[0])
         || (parts[1] != "qemu" && parts[1] != "lxc")
         || !int.TryParse(parts[2], out var vmid))
            throw new FormatException(
                $"Malformed Proxmox ExternalId '{externalId}'. Expected 'node/qemu|lxc/vmid'.");
        return (parts[0], parts[1], vmid);
    }
}
