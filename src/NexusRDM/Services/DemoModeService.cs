using NexusRDM.Core.Models;

namespace NexusRDM.Services;

/// <summary>
/// In-memory demo: pretend the user has a richly populated tree
/// (Proxmox cluster, Hyper-V host, manual SSH/RDP rows) so they can
/// poke around without filling their real database. While
/// <see cref="IsActive"/> is true:
///   - The connections tree builds from <see cref="GetProfiles"/> /
///     <see cref="GetGroups"/> instead of the DB.
///   - Sync engines (Proxmox / Hyper-V / Discovery) check
///     <see cref="IsActive"/> and bail, so background ticks don't
///     mutate the real DB or surface real cluster data while the
///     user is exploring fake content.
///   - <see cref="GetPowerState"/> seeds the per-row glyph just like
///     the real sync caches.
///
/// Exiting demo mode discards the in-memory state and reloads the
/// real tree. Nothing here ever hits the DB.
/// </summary>
public sealed class DemoModeService
{
    private readonly Group _serversGroup    = new() { Id = NewSeed("g-servers"),    Name = "Production servers" };
    private readonly Group _devGroup        = new() { Id = NewSeed("g-dev"),        Name = "Dev workstations" };
    private readonly Group _proxmoxGroup    = new() { Id = NewSeed("g-pve"),        Name = "homelab" };       // Proxmox cluster root
    private readonly Group _hypervGroup     = new() { Id = NewSeed("g-hv"),         Name = "Hyper-V" };
    private readonly Group _discoveredGroup = new() { Id = NewSeed("g-discovered"), Name = "Discovered" };

    private readonly List<ConnectionProfile> _profiles;
    private readonly Dictionary<Guid, ProxmoxPowerState> _powerStates;

    public DemoModeService()
    {
        _profiles    = new List<ConnectionProfile>();
        _powerStates = new Dictionary<Guid, ProxmoxPowerState>();

        // Top-level manual rows — SSH/RDP at the root.
        AddConn("router-edge",    "192.168.1.1",   22,   ConnectionProtocol.Ssh, group: null);
        AddConn("nas-storage",    "10.0.0.5",      22,   ConnectionProtocol.Ssh, group: null);

        // Production servers folder.
        AddConn("web-prod-01",    "10.10.0.21",    22,   ConnectionProtocol.Ssh, group: _serversGroup.Id);
        AddConn("web-prod-02",    "10.10.0.22",    22,   ConnectionProtocol.Ssh, group: _serversGroup.Id);
        AddConn("db-prod",        "10.10.0.30",    22,   ConnectionProtocol.Ssh, group: _serversGroup.Id);
        AddConn("rdp-jumpbox",    "10.10.0.50",    3389, ConnectionProtocol.Rdp, group: _serversGroup.Id);

        // Dev workstations.
        AddConn("dev-windows-11", "192.168.6.50",  3389, ConnectionProtocol.Rdp, group: _devGroup.Id);
        AddConn("dev-ubuntu",     "192.168.6.51",  22,   ConnectionProtocol.Ssh, group: _devGroup.Id);

        // Proxmox-managed VMs (italic AUTO folder + PVE pill).
        AddManaged("pi-hole",     "10.0.0.41",  22,   ConnectionProtocol.Ssh, _proxmoxGroup.Id, "pve1/lxc/100",  ProxmoxPowerState.Running);
        AddManaged("home-assist", "10.0.0.42",  22,   ConnectionProtocol.Ssh, _proxmoxGroup.Id, "pve1/lxc/101",  ProxmoxPowerState.Running);
        AddManaged("immich",      "10.0.0.43",  22,   ConnectionProtocol.Ssh, _proxmoxGroup.Id, "pve1/lxc/102",  ProxmoxPowerState.Stopped);
        AddManaged("win-game-vm", "10.0.0.44",  3389, ConnectionProtocol.Rdp, _proxmoxGroup.Id, "pve1/qemu/200", ProxmoxPowerState.Paused);
        AddManaged("backup-srv",  "10.0.0.45",  22,   ConnectionProtocol.Ssh, _proxmoxGroup.Id, "pve2/lxc/103",  ProxmoxPowerState.Running);

        // Hyper-V VMs (HV pill).
        AddManaged("Win11-Sandbox",   "192.168.10.5", 3389, ConnectionProtocol.Rdp, _hypervGroup.Id, "hyperv:1A2B3C", ProxmoxPowerState.Running);
        AddManaged("Server2025-Test", "192.168.10.6", 3389, ConnectionProtocol.Rdp, _hypervGroup.Id, "hyperv:4D5E6F", ProxmoxPowerState.Stopped);
        AddManaged("Linux-CI-runner", "192.168.10.7", 22,   ConnectionProtocol.Ssh, _hypervGroup.Id, "hyperv:7G8H9I", ProxmoxPowerState.Running);

        // Auto-discovered hosts.
        AddConn("workstation-3.lan", "192.168.6.103", 22,   ConnectionProtocol.Ssh, _discoveredGroup.Id, "auto-discovered");
        AddConn("printer-color",     "192.168.6.42",  3389, ConnectionProtocol.Rdp, _discoveredGroup.Id, "auto-discovered");
    }

    private void AddConn(string name, string host, int port, ConnectionProtocol protocol,
        Guid? group, string tags = "")
        => _profiles.Add(Conn(name, host, port, protocol, group, tags));

    private void AddManaged(string name, string host, int port, ConnectionProtocol protocol,
        Guid group, string src, ProxmoxPowerState state)
    {
        var p = Managed(name, host, port, protocol, group, src);
        _profiles.Add(p);
        _powerStates[p.Id] = state;
    }

    public bool IsActive { get; private set; }
    public event EventHandler? IsActiveChanged;

    public void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        IsActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        IsActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ConnectionProfile> GetProfiles() => _profiles;

    public IReadOnlyList<Group> GetGroups() => new[]
    {
        _serversGroup, _devGroup, _proxmoxGroup, _hypervGroup, _discoveredGroup,
    };

    public Guid ProxmoxRootGroupId => _proxmoxGroup.Id;
    public Guid HyperVRootGroupId  => _hypervGroup.Id;
    public Guid DiscoveryRootGroupId => _discoveredGroup.Id;

    public ProxmoxPowerState GetPowerState(Guid id)
        => _powerStates.TryGetValue(id, out var v) ? v : ProxmoxPowerState.Unknown;

    public DateTime GetPowerStateUpdatedAtUtc() =>
        // Pretend it was synced just now so the icon reads as fresh
        // (not stale-grey).
        DateTime.UtcNow;

    private static Guid NewSeed(string s)
    {
        // Deterministic ids so consecutive demo activations reuse the
        // same Guids — tree node identity stays stable across the
        // diff-merge, no flicker. Hash the entire name (instead of
        // truncating to 16 raw UTF-8 bytes) so siblings sharing a
        // long common prefix — "demo:c-web-prod-01" vs
        // "demo:c-web-prod-02" — don't collide and crash MergeChildren
        // with a duplicate-key exception.
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("demo:" + s));
        return new Guid(hash.AsSpan(0, 16).ToArray());
    }

    private ConnectionProfile Conn(string name, string host, int port, ConnectionProtocol protocol,
        Guid? group, string tags = "")
        => new()
        {
            Id          = NewSeed("c-" + name),
            DisplayName = name,
            Host        = host,
            Port        = port,
            Protocol    = protocol,
            GroupId     = group,
            Tags        = tags,
        };

    private ConnectionProfile Managed(string name, string host, int port, ConnectionProtocol protocol,
        Guid group, string src)
    {
        var p = Conn(name, host, port, protocol, group);
        p.IsManaged  = true;
        p.ExternalId = src;
        p.Tags       = src.StartsWith("hyperv:") ? "hyperv" : "demo-managed";
        return p;
    }

    public override string ToString() => IsActive ? "Demo mode: ON" : "Demo mode: OFF";
}
