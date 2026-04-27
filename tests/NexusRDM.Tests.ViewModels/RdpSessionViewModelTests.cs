using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;
using NexusRDM.Tests.ViewModels.Fakes;
using NexusRDM.ViewModels;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Covers the recently-added RDP toolbar plumbing on the ViewModel:
/// state-flag derivation, resize bound caching, smart-sizing /
/// resolution forwarding, the RDP-events feed, and the
/// reconnect-after-disconnect flow.
/// </summary>
public sealed class RdpSessionViewModelTests
{
    [Fact]
    public void IsDisconnected_True_WhenNeitherConnectedNorConnecting()
    {
        var (vm, fake, _, _) = Build();

        // A fresh VM defaults to IsConnecting = true; let it settle.
        fake.RaiseDisconnected("init");

        Assert.False(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.True (vm.IsDisconnected);
        Assert.False(vm.IsActive);
    }

    [Fact]
    public void IsActive_True_WhenConnected()
    {
        var (vm, fake, _, _) = Build();
        fake.RaiseConnected();

        Assert.True (vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.False(vm.IsDisconnected);
        Assert.True (vm.IsActive);
    }

    [Fact]
    public void Disconnected_Event_FlipsIsDisconnected_WithoutClosingTab()
    {
        var (vm, fake, _, mgr) = Build();
        fake.RaiseConnected();

        fake.RaiseDisconnected("server kicked us");

        Assert.True(vm.IsDisconnected);
        // A user-facing disconnect must NOT remove the OpenSession entry —
        // tab close (X) is the only path that mutates the manager.
        Assert.Single(mgr.Sessions);
    }

    [Fact]
    public void StatusMessage_ReflectsLifecycle()
    {
        var (vm, fake, _, _) = Build();
        fake.RaiseConnected();
        Assert.Contains("Connected", vm.StatusMessage);

        fake.RaiseDisconnected("test");
        Assert.Contains("Disconnected", vm.StatusMessage);

        fake.RaiseFatalError("boom");
        Assert.Contains("Error", vm.StatusMessage);
    }

    [Fact]
    public void StartConnection_CachesHwndAndBounds_ForReconnect()
    {
        var (vm, fake, _, _) = Build();

        vm.StartConnection(hwndParent: 0xDEAD, x: 10, y: 20, width: 800, height: 600);

        Assert.Single(fake.Connects);
        Assert.Equal((nint)0xDEAD, fake.Connects[0].hwnd);
        Assert.Equal(800,          fake.Connects[0].w);
    }

    [Fact]
    public void Resize_ForwardsToSession_AndCachesBounds()
    {
        var (vm, fake, _, _) = Build();
        vm.StartConnection(0xDEAD, 0, 0, 800, 600);
        fake.Resizes.Clear();

        vm.Resize(5, 6, 1024, 768);

        Assert.Single(fake.Resizes);
        Assert.Equal((5, 6, 1024, 768), fake.Resizes[0]);
    }

    [Fact]
    public void OnSmartSizingChanged_ForwardsToSession()
    {
        var (vm, fake, _, _) = Build();
        // Default is true — toggle off to observe the forwarded call.
        vm.SmartSizing = false;

        Assert.Contains(false, fake.SmartSizingCalls);
    }

    [Theory]
    [InlineData(RdpDefaultResolution.Res1920x1080, 1920, 1080)]
    [InlineData(RdpDefaultResolution.Res2560x1440, 2560, 1440)]
    [InlineData(RdpDefaultResolution.Res1024x768,  1024,  768)]
    public void SelectedResolution_FixedSize_ForwardsToSession(RdpDefaultResolution res, int w, int h)
    {
        var (vm, fake, _, _) = Build();

        vm.SelectedResolution = res;

        Assert.Single(fake.ResolutionCalls);
        Assert.Equal((w, h), fake.ResolutionCalls[0]);
    }

    [Fact]
    public void SelectedResolution_PresetsRaiseEvent_NotDirectForward()
    {
        // MatchMonitor / MatchPanel can't be resolved without an HWND
        // and panel rect — those come from the View. The VM raises
        // ResolutionPresetRequested and the View is expected to call
        // SetResolution back with concrete pixels.
        var (vm, fake, _, _) = Build();
        var captured = new List<RdpDefaultResolution>();
        vm.ResolutionPresetRequested += (_, p) => captured.Add(p);

        vm.SelectedResolution = RdpDefaultResolution.MatchMonitor;
        vm.SelectedResolution = RdpDefaultResolution.MatchPanel;

        Assert.Equal(new[] { RdpDefaultResolution.MatchMonitor, RdpDefaultResolution.MatchPanel },
                     captured);
        Assert.Empty(fake.ResolutionCalls); // VM didn't forward directly.
    }

    [Fact]
    public void RdpEvent_AppendsToEventsCollection_OnUiThread()
    {
        var (vm, fake, _, _) = Build();

        fake.RaiseRdpEvent("OnConnecting");
        fake.RaiseRdpEvent("OnAuthenticationWarningDisplayed", "tls");
        fake.RaiseRdpEvent("OnDisconnected", "1");

        Assert.Equal(3, vm.Events.Count);
        Assert.Equal("OnConnecting",                    vm.Events[0].Kind);
        Assert.Equal("tls",                             vm.Events[1].Detail);
        Assert.Equal("OnDisconnected",                  vm.Events[2].Kind);
    }

    [Fact]
    public void Events_CapAt500_DropsOldest()
    {
        var (vm, fake, _, _) = Build();

        for (int i = 0; i < 600; i++)
            fake.RaiseRdpEvent($"E{i}");

        Assert.Equal(500, vm.Events.Count);
        // Oldest 100 dropped → first remaining is E100.
        Assert.Equal("E100", vm.Events[0].Kind);
        Assert.Equal("E599", vm.Events[^1].Kind);
    }

    [Fact]
    public void Disconnect_TerminatesSession_WithoutDisposing_ForReconnect()
    {
        var (vm, fake, _, mgr) = Build();
        fake.RaiseConnected();

        vm.DisconnectCommand.Execute(null);

        Assert.Equal(1, fake.DisconnectCalls);
        // The tab entry stays in the manager; only Dispose / tab-close
        // removes it. Disconnect alone leaves the OpenSession alive.
        Assert.Single(mgr.Sessions);
    }

    [Fact]
    public void Connect_BuildsFreshSession_AndReplacesInManager_AfterDisconnect()
    {
        var (vm, fake, handler, mgr) = Build();
        // Initial connect to capture hwnd+bounds.
        vm.StartConnection(0xDEAD, 1, 2, 800, 600);
        fake.RaiseConnected();
        fake.RaiseDisconnected("kicked");

        Assert.True(vm.IsDisconnected);

        // Reconnect from the toolbar.
        vm.ConnectCommand.Execute(null);

        // A SECOND IRdpSession was built via the handler and got the
        // cached hwnd + last bounds replayed onto Connect.
        Assert.Equal(2, handler.Sessions.Count);
        Assert.Single(handler.Sessions[1].Connects);
        Assert.Equal((nint)0xDEAD, handler.Sessions[1].Connects[0].hwnd);
        Assert.Equal(800,          handler.Sessions[1].Connects[0].w);

        // Manager's RdpSession reference now points to the new session.
        var entry = mgr.Sessions[0];
        Assert.Same(handler.Sessions[1], entry.RdpSession);
    }

    [Fact]
    public void Connect_DisposesOldSession_BeforeBuildingNewOne()
    {
        var (vm, fake, _, _) = Build();
        vm.StartConnection(0xDEAD, 0, 0, 800, 600);
        fake.RaiseDisconnected("kicked");

        vm.ConnectCommand.Execute(null);

        Assert.Equal(1, fake.DisposeCalls);
    }

    [Fact]
    public void Connect_IsNoop_WhenAlreadyConnecting()
    {
        var (vm, fake, handler, _) = Build();
        // VM is born with IsConnecting=true — Connect must bail.
        vm.ConnectCommand.Execute(null);

        Assert.Single(handler.Sessions); // initial only
        Assert.Empty(fake.Connects);     // no replay
    }

    [Fact]
    public void Connect_IsNoop_WhenNeverInitiallyStarted()
    {
        var (vm, _, handler, _) = Build();
        // No StartConnection → _lastHwnd stays 0; Reconnect must bail.
        ((Microsoft.UI.Xaml.Window?)null)?.ToString();
        vm.ConnectCommand.Execute(null);

        Assert.Single(handler.Sessions); // initial only
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static (RdpSessionViewModel vm,
                    FakeRdpSession      fake,
                    FakeRdpHandler      handler,
                    SessionManager      mgr) Build()
    {
        var profile = new ConnectionProfile
        {
            DisplayName = "thanatos",
            Host        = "thanatos.local",
            Port        = 3389,
            Protocol    = ConnectionProtocol.Rdp,
        };
        var handler = new FakeRdpHandler();
        var initial = (FakeRdpSession)handler.CreateSession(profile, "gus", "secret");

        var mgr = new SessionManager();
        mgr.AddRdp(profile, initial);

        var vm = new RdpSessionViewModel(profile, initial, handler, "gus", "secret", mgr);
        return (vm, initial, handler, mgr);
    }
}
