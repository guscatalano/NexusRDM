using NexusRDM.Core.Models;
using NexusRDM.Services;
using Xunit;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// noVNC URL builder tests. PVE's web UI requires the
/// <c>?console=kvm|lxc</c> query plus a <c>#v1:</c> hash to land on
/// the right VM — without the hash you get the dashboard.
/// </summary>
public sealed class ProxmoxConsoleServiceTests
{
    [Fact]
    public void BuildConsoleUrl_Qemu_UsesKvmConsoleKind()
    {
        var src = new ProxmoxSource { BaseUrl = "https://pve.lan:8006" };
        var url = ProxmoxConsoleService.BuildConsoleUrl(src, "pve1", "qemu", 100);

        Assert.Contains("console=kvm",      url);
        Assert.Contains("novnc=1",          url);
        Assert.Contains("node=pve1",        url);
        Assert.Contains("vmid=100",         url);
        Assert.Contains("#v1:0:=qemu/100",  url);
        Assert.StartsWith("https://pve.lan:8006/?", url);
    }

    [Fact]
    public void BuildConsoleUrl_Lxc_UsesLxcConsoleKind()
    {
        var src = new ProxmoxSource { BaseUrl = "https://pve.lan:8006" };
        var url = ProxmoxConsoleService.BuildConsoleUrl(src, "pve2", "lxc", 201);
        Assert.Contains("console=lxc",     url);
        Assert.Contains("#v1:0:=lxc/201",  url);
    }

    [Fact]
    public void BuildConsoleUrl_StripsTrailingSlashFromBaseUrl()
    {
        var src = new ProxmoxSource { BaseUrl = "https://pve.lan:8006/" };
        var url = ProxmoxConsoleService.BuildConsoleUrl(src, "pve1", "qemu", 100);
        // No double slash before the query.
        Assert.DoesNotContain("8006//", url);
        Assert.Contains("8006/?", url);
    }

    [Fact]
    public void BuildConsoleUrl_EscapesNodeName()
    {
        var src = new ProxmoxSource { BaseUrl = "https://pve.lan:8006" };
        var url = ProxmoxConsoleService.BuildConsoleUrl(src, "node with space", "qemu", 1);
        Assert.Contains("node=node%20with%20space", url);
    }
}
