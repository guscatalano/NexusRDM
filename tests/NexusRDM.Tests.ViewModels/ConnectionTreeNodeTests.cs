using NexusRDM.Core.Models;
using NexusRDM.ViewModels;
using Windows.UI;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Covers the recently-added per-row visual logic on
/// <see cref="ConnectionTreeNode"/>: optional icon glyphs, group vs.
/// connection styling, and the live-status driven dot/icon color.
/// </summary>
public sealed class ConnectionTreeNodeTests
{
    [Fact]
    public void Group_ShowsFolderGlyph_AmberColor_BoldName()
    {
        var node = new ConnectionTreeNode(new Group { Name = "Lab" });

        Assert.Equal("Lab", node.DisplayName);
        Assert.False(string.IsNullOrEmpty(node.IconGlyph));
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, node.IconVisibility);

        // Amber folder color (#F0A732) — distinct from connection
        // status colors so groups read as "containers".
        Assert.Equal(0xF0, node.IconColor.R);
        Assert.Equal(0xA7, node.IconColor.G);
        Assert.Equal(0x32, node.IconColor.B);

        Assert.Equal(600, (int)node.DisplayNameFontWeight.Weight); // SemiBold
    }

    [Fact]
    public void Connection_WithNoIcon_HidesGlyph()
    {
        var node = new ConnectionTreeNode(new ConnectionProfile
        {
            DisplayName = "spark",
            Protocol    = ConnectionProtocol.Ssh,
            Host        = "spark.local",
            Port        = 22,
            IconGlyph   = null,
        });

        Assert.Empty(node.IconGlyph);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, node.IconVisibility);
        Assert.Equal(400, (int)node.DisplayNameFontWeight.Weight); // Normal
    }

    [Fact]
    public void Connection_WithIcon_ShowsGlyph()
    {
        var node = new ConnectionTreeNode(new ConnectionProfile
        {
            DisplayName = "db1",
            Protocol    = ConnectionProtocol.Ssh,
            Host        = "db1",
            Port        = 22,
            IconGlyph   = "", // Storage glyph
        });

        Assert.Equal("", node.IconGlyph);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, node.IconVisibility);
    }

    [Fact]
    public void Connection_DotColor_FollowsLiveConnectedFlag()
    {
        var node = new ConnectionTreeNode(new ConnectionProfile
        {
            DisplayName = "thanatos",
            Protocol    = ConnectionProtocol.Rdp,
            Host        = "thanatos",
            Port        = 3389,
        });

        // Default: not connected → red.
        Assert.Equal(0xFF6B6BU, ToRgb(node.DotColor));

        node.IsLiveConnected = true;
        Assert.Equal(0x3DD68CU, ToRgb(node.DotColor)); // green

        node.IsLiveConnected = false;
        Assert.Equal(0xFF6B6BU, ToRgb(node.DotColor)); // red again
    }

    [Fact]
    public void IconColor_ForConnection_TracksDotColor()
    {
        var node = new ConnectionTreeNode(new ConnectionProfile
        {
            DisplayName = "x",
            Protocol    = ConnectionProtocol.Ssh,
            Host        = "x",
            Port        = 22,
            IconGlyph   = "",
        });
        node.IsLiveConnected = true;

        Assert.Equal(node.DotColor, node.IconColor);
    }

    [Fact]
    public void Ping_DefaultIsUnknown_ShowsQuestionMark()
    {
        // Contract changed: "no measurement yet" used to render an empty
        // string; now it renders "?" so the latency cell never flickers
        // between numbers and blanks as states cycle. Visibility (which
        // is what actually controls whether the cell appears in the
        // tree) is covered by LatencyVisibility_RequiresShowLatency...
        var node = new ConnectionTreeNode(new ConnectionProfile
        {
            DisplayName = "x",
            Protocol    = ConnectionProtocol.Ssh,
            Host        = "x",
            Port        = 22,
        });

        Assert.Equal(NexusRDM.Services.PingState.Unknown, node.PingState);
        Assert.Null(node.LatencyMs);
        Assert.Equal("?", node.LatencyText);
    }

    [Fact]
    public void Ping_OkWithLatency_FormatsAsMs()
    {
        var node = new ConnectionTreeNode(new ConnectionProfile
        {
            DisplayName = "x",
            Protocol    = ConnectionProtocol.Ssh,
            Host        = "x",
            Port        = 22,
        });
        node.PingState = NexusRDM.Services.PingState.Ok;
        node.LatencyMs = 23;

        Assert.Equal("23 ms", node.LatencyText);
    }

    [Fact]
    public void Ping_FailedShowsQuestionMark()
    {
        // A failed ping shouldn't pretend to know latency even if a
        // stale value is still on the node — render "?" instead of the
        // stored number.
        var node = new ConnectionTreeNode(new ConnectionProfile
        {
            DisplayName = "x",
            Protocol    = ConnectionProtocol.Ssh,
            Host        = "x",
            Port        = 22,
        });
        node.LatencyMs = 99;
        node.PingState = NexusRDM.Services.PingState.Failed;

        Assert.Equal("?", node.LatencyText);
    }

    [Fact]
    public void LatencyVisibility_RequiresShowLatencyAndPingEnabled()
    {
        // Contract: visibility is independent of whether we have a
        // measurement (the cell shows "?" when not). Required gates:
        // ShowLatency, PingEnabled, and Profile != null.
        var node = new ConnectionTreeNode(new ConnectionProfile
        {
            DisplayName = "x",
            Protocol    = ConnectionProtocol.Ssh,
            Host        = "x",
            Port        = 22,
        });

        // Defaults: ShowLatency=false → collapsed.
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, node.LatencyVisibility);

        node.ShowLatency = true;
        // PingEnabled still false → collapsed.
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, node.LatencyVisibility);

        node.PingEnabled = true;
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, node.LatencyVisibility);

        // Even without a measurement, the cell stays Visible (renders "?").
        Assert.Equal("?", node.LatencyText);
    }

    private static uint ToRgb(Color c) => (uint)((c.R << 16) | (c.G << 8) | c.B);
}
