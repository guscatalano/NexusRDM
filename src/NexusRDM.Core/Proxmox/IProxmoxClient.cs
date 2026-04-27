using NexusRDM.Core.Models;

namespace NexusRDM.Core.Proxmox;

/// <summary>
/// Read-only Proxmox VE API surface NexusRDM consumes today: identify
/// the cluster, enumerate its VMs, and (later) discover guest IPs and
/// mint console tickets. Power actions live on a sibling interface so
/// read-only callers (sync engine) can't accidentally invoke them.
/// </summary>
public interface IProxmoxClient
{
    /// <summary><c>GET /api2/json/version</c>. Used by the "Test
    /// connection" button to confirm the URL + creds reach a real PVE
    /// without enumerating anything.</summary>
    Task<ProxmoxVersion> GetVersionAsync(CancellationToken ct = default);

    /// <summary><c>GET /api2/json/cluster/resources?type=vm</c>.
    /// Returns every QEMU and LXC across the cluster in a single call;
    /// works on standalone nodes too (cluster-of-one).</summary>
    Task<IReadOnlyList<ProxmoxClusterResource>> GetClusterResourcesAsync(CancellationToken ct = default);

    /// <summary><c>GET /api2/json/access/permissions</c>. Returns the
    /// effective ACL map for the authenticated principal as
    /// <c>{ path → { permission → 1 } }</c>. An empty map (or one
    /// containing zero <c>VM.Audit</c> entries) is the canonical
    /// symptom of a Privsep=1 token that hasn't been granted ACLs of
    /// its own.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>>
        GetAccessPermissionsAsync(CancellationToken ct = default);

    /// <summary><c>GET /api2/json/nodes/{node}/qemu/{vmid}/agent/network-get-interfaces</c>.
    /// Requires qemu-guest-agent installed and running in the guest;
    /// PVE returns 500 with "QEMU guest agent is not running" otherwise.
    /// Returns null when the agent isn't available so callers can fall
    /// back without inspecting exception messages.</summary>
    Task<ProxmoxAgentNetwork?> TryGetQemuAgentNetworkAsync(
        string node, int vmid, CancellationToken ct = default);

    /// <summary><c>GET /api2/json/nodes/{node}/{type}/{vmid}/config</c>.
    /// We consume <c>ostype</c>, <c>agent</c>, and the <c>net0..N</c>
    /// fields — LXC's net0 is the primary IP-discovery source since
    /// containers don't run qemu-guest-agent.</summary>
    Task<ProxmoxVmConfig?> TryGetVmConfigAsync(
        string node, string type, int vmid, CancellationToken ct = default);

    /// <summary><c>POST /api2/json/nodes/{node}/{type}/{vmid}/status/{action}</c>.
    /// Returns the task UPID that the caller can poll for completion.
    /// Requires <c>VM.PowerMgmt</c> on the VM's ACL path; a 403 surfaces
    /// as a thrown <see cref="HttpRequestException"/> with the body.
    /// <paramref name="type"/> is <c>"qemu"</c> or <c>"lxc"</c>.</summary>
    Task<string> PowerActionAsync(string node, string type, int vmid,
        ProxmoxPowerAction action, CancellationToken ct = default);
}

public enum ProxmoxPowerAction
{
    /// <summary>Hard start — same as the green ▶ button in the PVE UI.</summary>
    Start    = 0,
    /// <summary>Graceful shutdown — sends ACPI signal via guest agent.
    /// Honours guest-OS shutdown timers; falls back to hard stop after
    /// <c>shutdown_timeout</c>.</summary>
    Shutdown = 1,
    /// <summary>Graceful reboot via guest agent / ACPI.</summary>
    Reboot   = 2,
    /// <summary>Hard power-off — equivalent to pulling the plug. Use
    /// only when shutdown hangs.</summary>
    Stop     = 3,
    /// <summary>Hard reset (QEMU only). Equivalent to a reset button
    /// press; the guest sees a cold reboot.</summary>
    Reset    = 4,
}

/// <summary>
/// Builds <see cref="IProxmoxClient"/> instances bound to a specific
/// <see cref="ProxmoxSource"/>. The factory pulls the auth secret from
/// the credential vault by source id, so callers never see the secret.
/// </summary>
public interface IProxmoxClientFactory
{
    IProxmoxClient Create(ProxmoxSource source);
}

/// <summary>
/// Vault-key helper exposed publicly so the Settings UI can save the
/// secret without referencing the internal <c>ProxmoxClient</c> type.
/// Format: <c>"proxmox:{Id:N}"</c> — 32-hex Guid, no dashes.
/// </summary>
public static class ProxmoxVault
{
    public static string KeyFor(ProxmoxSource source) => $"proxmox:{source.Id:N}";
    public static string KeyFor(Guid sourceId)        => $"proxmox:{sourceId:N}";
}
