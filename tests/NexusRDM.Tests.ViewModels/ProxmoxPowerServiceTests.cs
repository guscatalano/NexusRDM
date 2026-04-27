using NexusRDM.Services;
using Xunit;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Tests for the public <c>ParseExternalId</c> helper. The format
/// <c>"{node}/{type}/{vmid}"</c> is the contract between sync and
/// every consumer (power, console, detach).
/// </summary>
public sealed class ProxmoxPowerServiceTests
{
    [Fact]
    public void ParseExternalId_Qemu_RoundTrips()
    {
        var (node, type, vmid) = ProxmoxPowerService.ParseExternalId("pve1/qemu/100");
        Assert.Equal("pve1", node);
        Assert.Equal("qemu", type);
        Assert.Equal(100,    vmid);
    }

    [Fact]
    public void ParseExternalId_Lxc_RoundTrips()
    {
        var (node, type, vmid) = ProxmoxPowerService.ParseExternalId("pve2/lxc/201");
        Assert.Equal("pve2", node);
        Assert.Equal("lxc",  type);
        Assert.Equal(201,    vmid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just-a-name")]
    [InlineData("pve1/qemu")]                // missing vmid
    [InlineData("pve1//100")]                // empty type
    [InlineData("/qemu/100")]                // empty node
    [InlineData("pve1/storage/100")]         // wrong type — only qemu/lxc allowed
    [InlineData("pve1/qemu/notanumber")]     // non-int vmid
    [InlineData("pve1/qemu/100/extra")]
    public void ParseExternalId_Malformed_Throws(string ext)
    {
        Assert.Throws<System.FormatException>(() => ProxmoxPowerService.ParseExternalId(ext));
    }
}
