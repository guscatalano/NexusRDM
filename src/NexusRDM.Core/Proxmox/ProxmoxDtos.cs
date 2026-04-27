using System.Text.Json.Serialization;

namespace NexusRDM.Core.Proxmox;

/// <summary>
/// Proxmox responses are wrapped in <c>{ "data": ... }</c>. This generic
/// envelope pulls the inner payload out so callers don't have to thread
/// the wrapper through every parse site.
/// </summary>
public sealed class ProxmoxEnvelope<T>
{
    [JsonPropertyName("data")] public T? Data { get; set; }
}

public sealed class ProxmoxVersion
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("repoid")]  public string? RepoId  { get; set; }
}

/// <summary>
/// One row from <c>cluster/resources?type=vm</c>. Covers both QEMU and
/// LXC; <see cref="Type"/> distinguishes them. We model only the fields
/// the sync engine actually consumes — Proxmox returns ~30 columns and
/// we ignore the rest to keep parsing forward-compatible.
/// </summary>
public sealed class ProxmoxClusterResource
{
    /// <summary><c>"qemu"</c> or <c>"lxc"</c>. Other types
    /// (<c>"node"</c>, <c>"storage"</c>) are filtered server-side via
    /// <c>?type=vm</c>.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("vmid")] public int    Vmid { get; set; }
    [JsonPropertyName("node")] public string? Node { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary><c>"running"</c>, <c>"stopped"</c>, <c>"paused"</c>,
    /// <c>"unknown"</c>.</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }

    /// <summary>Semicolon-separated list of user tags. Proxmox hands
    /// us the raw string (e.g. <c>"prod;nexus:rdp;db"</c>); we split
    /// downstream.</summary>
    [JsonPropertyName("tags")] public string? Tags { get; set; }

    /// <summary>Cluster-unique resource id like
    /// <c>"qemu/100"</c> — useful for logging.</summary>
    [JsonPropertyName("id")] public string? Id { get; set; }
}

/// <summary>
/// Ticket payload from <c>POST /access/ticket</c>. Issued for password
/// auth only; API tokens skip this dance.
/// </summary>
public sealed class ProxmoxTicket
{
    [JsonPropertyName("ticket")]              public string? Ticket             { get; set; }
    [JsonPropertyName("CSRFPreventionToken")] public string? CsrfPreventionToken { get; set; }
    [JsonPropertyName("username")]            public string? Username           { get; set; }
}

/// <summary>Top-level wrapper for
/// <c>/nodes/{n}/qemu/{vmid}/agent/network-get-interfaces</c> —
/// the inner <c>data</c> envelope contains <c>{ "result": [ ... ] }</c>
/// (note the extra layer compared to most PVE endpoints).</summary>
public sealed class ProxmoxAgentNetwork
{
    [JsonPropertyName("result")] public List<ProxmoxAgentInterface>? Result { get; set; }
}

public sealed class ProxmoxAgentInterface
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>Format mirrors the QMP wire shape: kebab-case keys.</summary>
    [JsonPropertyName("ip-addresses")] public List<ProxmoxAgentIp>? IpAddresses { get; set; }

    /// <summary>MAC address. Useful for picking primary interface when
    /// multiple have routable IPs.</summary>
    [JsonPropertyName("hardware-address")] public string? HardwareAddress { get; set; }
}

public sealed class ProxmoxAgentIp
{
    /// <summary><c>ipv4</c> or <c>ipv6</c>.</summary>
    [JsonPropertyName("ip-address-type")] public string? Type   { get; set; }
    [JsonPropertyName("ip-address")]      public string? Address { get; set; }
    [JsonPropertyName("prefix")]          public int     Prefix { get; set; }
}

/// <summary>Subset of <c>/nodes/{n}/{type}/{vmid}/config</c> we consume.
/// LXC stores per-NIC strings as <c>net0</c>/<c>net1</c>/... with the
/// shape <c>name=eth0,bridge=vmbr0,gw=10.0.0.1,hwaddr=...,ip=10.0.0.5/24</c>.
/// We only model the first NIC by default; the parser walks net0..net9.</summary>
public sealed class ProxmoxVmConfig
{
    /// <summary>QEMU agent enabled: <c>"enabled=1"</c> in the same
    /// comma-separated format. Empty string when not set.</summary>
    [JsonPropertyName("agent")] public string? Agent { get; set; }

    /// <summary>QEMU OS hint — <c>win10</c>, <c>win11</c>, <c>l26</c>
    /// (Linux 2.6+), <c>other</c>. Drives the auto-protocol heuristic
    /// downstream.</summary>
    [JsonPropertyName("ostype")] public string? OsType { get; set; }

    [JsonPropertyName("net0")] public string? Net0 { get; set; }
    [JsonPropertyName("net1")] public string? Net1 { get; set; }
    [JsonPropertyName("net2")] public string? Net2 { get; set; }
    [JsonPropertyName("net3")] public string? Net3 { get; set; }
}

