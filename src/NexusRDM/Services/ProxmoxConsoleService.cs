using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexusRDM.Core.Models;
using NexusRDM.Data.Context;

namespace NexusRDM.Services;

/// <summary>
/// Opens the Proxmox built-in noVNC console for a managed connection.
///
/// V1 implementation launches the user's default browser pointed at
/// PVE's <c>?console=kvm/lxc/shell</c> URL. Why not an embedded
/// WebView2?
///
///   The noVNC websocket authenticates against <c>PVEAuthCookie</c> —
///   the same cookie PVE's web UI uses. API tokens explicitly cannot
///   mint that cookie (PVE has no "token → session ticket" exchange),
///   so a fully-embedded tab couldn't authenticate when the source
///   uses token auth. The browser already has a session if the user
///   has logged into PVE; PVE prompts for credentials otherwise.
///
/// A future revision can add an embedded WebView2 path for password-
/// auth sources where we already hold a real ticket — that's a clean
/// follow-up rather than something to gate v1 on.
/// </summary>
public sealed class ProxmoxConsoleService
{
    private readonly IServiceProvider _services;
    public ProxmoxConsoleService(IServiceProvider services) => _services = services;

    /// <summary>Constructs the noVNC URL and hands it to the OS shell.
    /// Throws when the connection isn't a managed Proxmox row or its
    /// source has been deleted.</summary>
    public async Task OpenAsync(ConnectionProfile connection, CancellationToken ct = default)
    {
        if (!connection.IsManaged || connection.ExternalSourceId is null
         || string.IsNullOrEmpty(connection.ExternalId))
            throw new InvalidOperationException(
                "The web console is only available on Proxmox-managed connections.");

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var source = await db.ProxmoxSources
            .FirstOrDefaultAsync(s => s.Id == connection.ExternalSourceId, ct)
            ?? throw new InvalidOperationException(
                $"Proxmox source {connection.ExternalSourceId} no longer exists.");

        var (node, type, vmid) = ProxmoxPowerService.ParseExternalId(connection.ExternalId);
        var url = BuildConsoleUrl(source, node, type, vmid);

        // ProcessStartInfo with UseShellExecute=true picks the user's
        // default browser regardless of how it's registered. We don't
        // open the URL in-process — that'd hang the UI thread on shell
        // resolution.
        Process.Start(new ProcessStartInfo
        {
            FileName        = url,
            UseShellExecute = true,
        });
    }

    /// <summary>Visible for testing — pure URL construction with no IO.
    /// PVE accepts <c>console=kvm</c> for QEMU and <c>console=lxc</c>
    /// for containers; the path itself routes to the right websocket.</summary>
    public static string BuildConsoleUrl(ProxmoxSource source, string node, string type, int vmid)
    {
        var consoleKind = type == "lxc" ? "lxc" : "kvm";
        var basePart    = source.BaseUrl.TrimEnd('/');
        // The web UI's #v1 fragment routes the SPA to the right view.
        // Without it PVE lands on the dashboard and the user has to
        // navigate manually.
        return $"{basePart}/?console={consoleKind}" +
               $"&novnc=1" +
               $"&node={Uri.EscapeDataString(node)}" +
               $"&resize=scale" +
               $"&vmid={vmid}" +
               $"#v1:0:={type}/{vmid}:::::";
    }
}
