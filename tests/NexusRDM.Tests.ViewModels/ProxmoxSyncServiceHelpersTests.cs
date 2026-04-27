using System.Collections.Generic;
using NexusRDM.Core.Models;
using NexusRDM.Core.Proxmox;
using NexusRDM.Services;
using Xunit;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Pure-function tests for <see cref="ProxmoxSyncService"/>'s internal
/// helpers — tag parsing, protocol/host resolution, LXC IP parsing,
/// and the agent-network IP picker. These are the bits that decide
/// what each managed connection looks like, so even small regressions
/// here ripple through the tree.
/// </summary>
public sealed class ProxmoxSyncServiceHelpersTests
{
    // ── ParseTags ────────────────────────────────────────────────────────

    [Fact]
    public void ParseTags_RecognisesProtocolDirectives()
    {
        var d = ProxmoxSyncService.ParseTags("prod;nexus:rdp;db");
        Assert.True(d.ForceRdp);
        Assert.False(d.ForceSsh);
        Assert.False(d.ForceConsole);
        Assert.False(d.Skip);
    }

    [Fact]
    public void ParseTags_SkipShortCircuits()
    {
        var d = ProxmoxSyncService.ParseTags("prod;nexus:skip;web");
        Assert.True(d.Skip);
    }

    [Fact]
    public void ParseTags_KeyValueOverrides()
    {
        var d = ProxmoxSyncService.ParseTags("nexus:user=admin;nexus:host=10.0.0.5;nexus:port=2222");
        Assert.Equal("admin",    d.User);
        Assert.Equal("10.0.0.5", d.Host);
        Assert.Equal(2222,       d.Port);
    }

    [Fact]
    public void ParseTags_TolerateBothSemicolonAndCommaSeparators()
    {
        // Some clusters / migrations use commas instead of the PVE-
        // canonical semicolons. Both should parse equivalently.
        var d1 = ProxmoxSyncService.ParseTags("prod;nexus:rdp");
        var d2 = ProxmoxSyncService.ParseTags("prod,nexus:rdp");
        Assert.Equal(d1.ForceRdp, d2.ForceRdp);
    }

    [Fact]
    public void ParseTags_NullOrEmpty_ReturnsEmptyDirectives()
    {
        var d = ProxmoxSyncService.ParseTags(null);
        Assert.False(d.ForceRdp); Assert.False(d.ForceSsh); Assert.False(d.Skip);
        Assert.Null(d.User); Assert.Null(d.Host); Assert.Null(d.Port);
    }

    [Fact]
    public void ParseTags_NonNexusTagsAreIgnored()
    {
        var d = ProxmoxSyncService.ParseTags("prod;db;backup");
        Assert.False(d.ForceRdp);
        Assert.False(d.ForceSsh);
        Assert.False(d.Skip);
        Assert.Null(d.User);
    }

    // ── ResolveProtocol ──────────────────────────────────────────────────

    [Fact]
    public void ResolveProtocol_TagPinWinsOverEverything()
    {
        var d = ProxmoxSyncService.ParseTags("nexus:ssh");
        var result = ProxmoxSyncService.ResolveProtocol(d, ProxmoxDefaultProtocol.Rdp, "prod", "win11");
        Assert.Equal(ConnectionProtocol.Ssh, result);
    }

    [Fact]
    public void ResolveProtocol_AutoOnWindowsOsType_PicksRdp()
    {
        var d = ProxmoxSyncService.ParseTags("");
        var result = ProxmoxSyncService.ResolveProtocol(d, ProxmoxDefaultProtocol.Auto, "", "win11");
        Assert.Equal(ConnectionProtocol.Rdp, result);
    }

    [Fact]
    public void ResolveProtocol_AutoOnLinuxOsType_PicksSsh()
    {
        var d = ProxmoxSyncService.ParseTags("");
        var result = ProxmoxSyncService.ResolveProtocol(d, ProxmoxDefaultProtocol.Auto, "", "l26");
        Assert.Equal(ConnectionProtocol.Ssh, result);
    }

    [Fact]
    public void ResolveProtocol_ConsoleDefault_FallsBackToSshUntilWebView2Lands()
    {
        var d = ProxmoxSyncService.ParseTags("");
        var result = ProxmoxSyncService.ResolveProtocol(d, ProxmoxDefaultProtocol.Console, "", "l26");
        Assert.Equal(ConnectionProtocol.Ssh, result);
    }

    [Fact]
    public void ResolveProtocol_AutoNoOsType_FallsBackToTagHeuristic()
    {
        var d = ProxmoxSyncService.ParseTags("windows-server");
        var result = ProxmoxSyncService.ResolveProtocol(d, ProxmoxDefaultProtocol.Auto, "windows-server", null);
        Assert.Equal(ConnectionProtocol.Rdp, result);
    }

    // ── ResolveHost ──────────────────────────────────────────────────────

    [Fact]
    public void ResolveHost_TagOverride_BeatsDiscoveredIp()
    {
        var d = ProxmoxSyncService.ParseTags("nexus:host=override.local");
        var r = new ProxmoxClusterResource { Vmid = 1, Name = "vm-name" };
        Assert.Equal("override.local", ProxmoxSyncService.ResolveHost(d, r, "10.0.0.5"));
    }

    [Fact]
    public void ResolveHost_DiscoveredIp_BeatsName()
    {
        var d = ProxmoxSyncService.ParseTags("");
        var r = new ProxmoxClusterResource { Vmid = 1, Name = "vm-name" };
        Assert.Equal("10.0.0.5", ProxmoxSyncService.ResolveHost(d, r, "10.0.0.5"));
    }

    [Fact]
    public void ResolveHost_NameWhenNothingElse()
    {
        var d = ProxmoxSyncService.ParseTags("");
        var r = new ProxmoxClusterResource { Vmid = 100, Name = "web-prod" };
        Assert.Equal("web-prod", ProxmoxSyncService.ResolveHost(d, r, null));
    }

    [Fact]
    public void ResolveHost_VmidPlaceholderWhenNoNameAtAll()
    {
        var d = ProxmoxSyncService.ParseTags("");
        var r = new ProxmoxClusterResource { Vmid = 100, Name = null };
        Assert.Equal("vm-100", ProxmoxSyncService.ResolveHost(d, r, null));
    }

    // ── TryParseLxcStaticIp ──────────────────────────────────────────────

    [Fact]
    public void LxcStaticIp_ParsesNet0()
    {
        var cfg = new ProxmoxVmConfig
        {
            Net0 = "name=eth0,bridge=vmbr0,gw=10.0.0.1,hwaddr=AA:BB:CC:DD:EE:FF,ip=10.0.0.5/24,type=veth",
        };
        Assert.Equal("10.0.0.5", ProxmoxSyncService.TryParseLxcStaticIp(cfg));
    }

    [Fact]
    public void LxcStaticIp_DhcpReturnsNull()
    {
        var cfg = new ProxmoxVmConfig { Net0 = "name=eth0,bridge=vmbr0,ip=dhcp" };
        Assert.Null(ProxmoxSyncService.TryParseLxcStaticIp(cfg));
    }

    [Fact]
    public void LxcStaticIp_FallsThroughToNet1WhenNet0Absent()
    {
        var cfg = new ProxmoxVmConfig { Net1 = "name=eth1,ip=192.168.50.10/24" };
        Assert.Equal("192.168.50.10", ProxmoxSyncService.TryParseLxcStaticIp(cfg));
    }

    [Fact]
    public void LxcStaticIp_NoIpField_ReturnsNull()
    {
        var cfg = new ProxmoxVmConfig { Net0 = "name=eth0,bridge=vmbr0" };
        Assert.Null(ProxmoxSyncService.TryParseLxcStaticIp(cfg));
    }

    [Fact]
    public void LxcStaticIp_BareIpWithoutCidr_ReturnsAsIs()
    {
        var cfg = new ProxmoxVmConfig { Net0 = "name=eth0,ip=10.0.0.5" };
        Assert.Equal("10.0.0.5", ProxmoxSyncService.TryParseLxcStaticIp(cfg));
    }

    // ── PickBestIp ───────────────────────────────────────────────────────

    [Fact]
    public void PickBestIp_PrefersIpv4OverIpv6()
    {
        var ifs = new List<ProxmoxAgentInterface>
        {
            new() { Name = "eth0", IpAddresses = new()
            {
                new() { Type = "ipv6", Address = "2001:db8::1" },
                new() { Type = "ipv4", Address = "192.168.1.10" },
            }},
        };
        Assert.Equal("192.168.1.10", ProxmoxSyncService.PickBestIp(ifs));
    }

    [Fact]
    public void PickBestIp_SkipsLoopbackAndLinkLocal()
    {
        var ifs = new List<ProxmoxAgentInterface>
        {
            new() { Name = "lo", IpAddresses = new()
            {
                new() { Type = "ipv4", Address = "127.0.0.1" },
            }},
            new() { Name = "eth0", IpAddresses = new()
            {
                new() { Type = "ipv4", Address = "169.254.10.10" },  // link-local
                new() { Type = "ipv4", Address = "10.0.0.20" },
            }},
        };
        Assert.Equal("10.0.0.20", ProxmoxSyncService.PickBestIp(ifs));
    }

    [Fact]
    public void PickBestIp_SkipsDockerAndVirtualBridges()
    {
        var ifs = new List<ProxmoxAgentInterface>
        {
            new() { Name = "docker0", IpAddresses = new()
            {
                new() { Type = "ipv4", Address = "172.17.0.1" },
            }},
            new() { Name = "br-1234", IpAddresses = new()
            {
                new() { Type = "ipv4", Address = "172.18.0.1" },
            }},
            new() { Name = "eth0", IpAddresses = new()
            {
                new() { Type = "ipv4", Address = "192.168.1.50" },
            }},
        };
        Assert.Equal("192.168.1.50", ProxmoxSyncService.PickBestIp(ifs));
    }

    [Fact]
    public void PickBestIp_FallsBackWhenAllInterfacesAreBridge()
    {
        // No "primary" interface left after the filter — pick any
        // routable IP from what we have rather than returning null.
        var ifs = new List<ProxmoxAgentInterface>
        {
            new() { Name = "docker0", IpAddresses = new()
            {
                new() { Type = "ipv4", Address = "172.17.0.1" },
            }},
        };
        Assert.Equal("172.17.0.1", ProxmoxSyncService.PickBestIp(ifs));
    }

    [Fact]
    public void PickBestIp_NoIpsReturnsNull()
    {
        Assert.Null(ProxmoxSyncService.PickBestIp(new List<ProxmoxAgentInterface>()));
    }
}
