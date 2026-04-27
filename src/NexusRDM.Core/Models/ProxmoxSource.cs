namespace NexusRDM.Core.Models;

/// <summary>
/// A registered Proxmox cluster (or standalone PVE node treated as a
/// single-node cluster). NexusRDM polls <see cref="BaseUrl"/> on the
/// configured interval, enumerates VMs/CTs via
/// <c>/api2/json/cluster/resources?type=vm</c>, and projects each one
/// into a managed <see cref="ConnectionProfile"/> under
/// <see cref="RootGroupId"/>. Imported connections carry
/// <c>ExternalSourceId == this.Id</c>.
///
/// Auth secret never lives here — it goes through
/// <see cref="Interfaces.ICredentialVault"/> keyed by
/// <c>"proxmox:{Id}"</c> (token-secret for API tokens, password for
/// ticket auth).
/// </summary>
public class ProxmoxSource
{
    public Guid   Id          { get; set; } = Guid.NewGuid();
    public string Name        { get; set; } = string.Empty;

    /// <summary>Cluster entry URL — e.g. <c>https://pve.lan:8006</c>.
    /// Any reachable node URL works; <c>cluster/resources</c> returns
    /// the whole cluster regardless of which node we hit.</summary>
    public string BaseUrl     { get; set; } = string.Empty;

    public ProxmoxAuthMode AuthMode { get; set; } = ProxmoxAuthMode.ApiToken;

    /// <summary>For API-token auth: <c>USER@REALM!TOKENID</c>. For
    /// password auth: <c>USER</c>. Realm is part of the string.</summary>
    public string AuthUser    { get; set; } = string.Empty;

    /// <summary><c>pam</c> / <c>pve</c>. Only consulted for password
    /// (ticket) auth; API-token strings carry their own realm.</summary>
    public string Realm       { get; set; } = "pam";

    /// <summary>Honor self-signed certs. Common in homelabs; defaults
    /// off so the user opts in explicitly.</summary>
    public bool   IgnoreTlsErrors { get; set; }

    /// <summary>SHA-256 thumbprint of the cert seen on first successful
    /// connect. We warn if it changes thereafter.</summary>
    public string? PinnedCertThumbprint { get; set; }

    public int       SyncIntervalMinutes { get; set; } = 15;
    public DateTime? LastSyncUtc         { get; set; }
    public string?   LastSyncError       { get; set; }

    /// <summary>Group all imported VMs land under. Created on first
    /// successful sync if null.</summary>
    public Guid?  RootGroupId { get; set; }

    /// <summary>Default protocol when an imported VM doesn't specify
    /// one via <c>#nexus:rdp/ssh/console</c> tag and OS heuristics are
    /// inconclusive.</summary>
    public ProxmoxDefaultProtocol DefaultProtocol { get; set; } = ProxmoxDefaultProtocol.Auto;

    /// <summary>Default username applied to imported VMs that don't
    /// override via <c>#nexus:user=...</c>.</summary>
    public string? DefaultUsername { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum ProxmoxAuthMode
{
    ApiToken = 0,
    Password = 1,
}

public enum ProxmoxDefaultProtocol
{
    /// <summary>Pick RDP for Windows (<c>ostype</c> starts with <c>win</c>),
    /// SSH for Linux (<c>l26</c>, etc.), Console otherwise.</summary>
    Auto    = 0,
    Rdp     = 1,
    Ssh     = 2,
    Console = 3,
}
