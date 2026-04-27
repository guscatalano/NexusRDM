using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;

namespace NexusRDM.ViewModels;

public sealed partial class RdpSessionViewModel : ObservableObject, IDisposable
{
    private          IRdpSession     _session;
    private readonly IRdpHandler     _handler;
    private readonly ConnectionProfile _profile;
    private readonly string          _username;
    private readonly string          _password;
    private readonly SessionManager  _mgr;
    // Captured at construction (UI thread) so events from non-UI threads —
    // e.g. MstscAxRdpSession's Forms STA thread — can hop back here before
    // touching observable properties. Without this, OnPropertyChanged fires
    // on the wrong thread and the XAML binding throws
    // RPC_E_WRONG_THREAD ("marshalled for a different thread").
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    // Last hwnd+bounds from the View's StartConnection — replayed on
    // reconnect from the toolbar so we re-attach to the same panel rect.
    private nint                       _lastHwnd;
    private (int X, int Y, int W, int H) _lastBounds;

    public Guid   ConnectionId { get; }
    public string DisplayName  { get; }
    public string Host         { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private bool _isConnecting = true;

    [ObservableProperty] private string _statusMessage = "Connecting…";

    /// <summary>Toolbar uses this to flip Disconnect → Connect: shown only
    /// when the session is fully torn down (not connected and not in the
    /// middle of connecting). Recomputed from IsConnected/IsConnecting via
    /// NotifyPropertyChangedFor.</summary>
    public bool IsDisconnected => !IsConnected && !IsConnecting;

    /// <summary>Inverse of <see cref="IsDisconnected"/> — bound to the
    /// Disconnect button's Visibility so we don't need a value converter.</summary>
    public bool IsActive => !IsDisconnected;

    /// <summary>True = scale the remote desktop to fit the host window;
    /// false = native resolution with scrollbars. Bound TwoWay to a
    /// checkbox in the toolbar; the partial-method handler forwards the
    /// change to the OCX's SmartSizing property.</summary>
    [ObservableProperty] private bool   _smartSizing = true;
    partial void OnSmartSizingChanged(bool value) => _session.SetSmartSizing(value);

    /// <summary>Common RDP session resolutions. Selecting one calls
    /// IMsRdpClient9.UpdateSessionDisplaySettings on the live session — the
    /// remote server renegotiates without dropping the connection.</summary>
    public IReadOnlyList<string> Resolutions { get; } = new[]
    {
        "1024 × 768",
        "1280 × 720",
        "1366 × 768",
        "1600 × 900",
        "1920 × 1080",
        "2560 × 1440",
    };

    [ObservableProperty] private string? _selectedResolution;
    partial void OnSelectedResolutionChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // Format: "<w> × <h>" (Unicode multiplication sign U+00D7).
        var parts = value.Split('×', 'x', 'X');
        if (parts.Length != 2) return;
        if (!int.TryParse(parts[0].Trim(), out var w)) return;
        if (!int.TryParse(parts[1].Trim(), out var h)) return;
        _session.SetResolution(w, h);
    }

    public RdpSessionViewModel(
        ConnectionProfile profile,
        IRdpSession       session,
        IRdpHandler       handler,
        string            username,
        string            password,
        SessionManager    mgr)
    {
        ConnectionId = profile.Id;
        DisplayName  = profile.DisplayName;
        Host         = $"{profile.Host}:{profile.Port}";
        _profile     = profile;
        _session     = session;
        _handler     = handler;
        _username    = username;
        _password    = password;
        _mgr         = mgr;

        WireSession(_session);
    }

    /// <summary>Live event feed for the "RDP events" pop-up — every event
    /// the session raises (Connecting/Connected/Disconnected/FatalError
    /// plus everything the OCX dispinterface fires) is appended here on
    /// the UI thread. Capped at 500 entries so a long-running session
    /// doesn't grow unbounded.</summary>
    public ObservableCollection<RdpEventEntry> Events { get; } = new();

    private const int MaxEvents = 500;

    private void WireSession(IRdpSession s)
    {
        s.Connected    += OnSessionConnected;
        s.Disconnected += OnSessionDisconnected;
        s.FatalError   += OnSessionFatalError;
        s.RdpEvent     += OnSessionRdpEvent;
    }

    private void UnwireSession(IRdpSession s)
    {
        s.Connected    -= OnSessionConnected;
        s.Disconnected -= OnSessionDisconnected;
        s.FatalError   -= OnSessionFatalError;
        s.RdpEvent     -= OnSessionRdpEvent;
    }

    private void OnSessionRdpEvent(object? sender, RdpEventEntry entry) =>
        OnUi(() =>
        {
            Events.Add(entry);
            while (Events.Count > MaxEvents) Events.RemoveAt(0);
        });

    private void OnSessionConnected(object? sender, EventArgs e) =>
        OnUi(() => { IsConnected = true;  IsConnecting = false; StatusMessage = $"Connected to {Host}"; });

    private void OnSessionDisconnected(object? sender, string reason) =>
        OnUi(() => { IsConnected = false; IsConnecting = false; StatusMessage = $"Disconnected: {reason}"; });

    private void OnSessionFatalError(object? sender, string msg) =>
        OnUi(() => { IsConnected = false; IsConnecting = false; StatusMessage = $"Error: {msg}"; });

    private void OnUi(Action a)
    {
        if (_ui is null) { a(); return; }
        _ui.TryEnqueue(() => a());
    }

    public void StartConnection(nint hwndParent, int x, int y, int width, int height)
    {
        _lastHwnd   = hwndParent;
        _lastBounds = (x, y, width, height);
        _session.Connect(hwndParent, x, y, width, height);
    }

    public void Resize(int x, int y, int width, int height)
    {
        _lastBounds = (x, y, width, height);
        _session.Resize(x, y, width, height);
    }

    public void BringToFront()           => _session.BringToFront();
    public void SetVisible(bool visible) => _session.SetVisible(visible);
    public void ToggleFullScreen()       => _session.ToggleFullScreen();
    public void PopOut()                 => _session.PopOut();

    public void SendCtrlAltDel() => _session.SendCtrlAltDel();

    [RelayCommand]
    private void Disconnect()
    {
        // Just terminate the live session; leave the tab open so the user
        // can hit Connect to re-establish without losing their place. Tab
        // close (the X) is the only path that fully removes the entry.
        _session.Disconnect();
    }

    [RelayCommand]
    private void Connect()
    {
        if (IsConnected || IsConnecting) return;
        if (_lastHwnd == 0)              return; // never had an initial connect

        // Replace the dead session with a fresh one — IRdpSession isn't
        // restartable after Disconnect (the WinForms STA thread has run
        // Application.Run to completion), so we build a new instance.
        UnwireSession(_session);
        try { _session.Dispose(); } catch { /* best effort */ }

        _session = _handler.CreateSession(_profile, _username, _password);
        WireSession(_session);

        var entry = _mgr.FindByConnectionId(ConnectionId);
        entry?.ReplaceRdpSession(_session);
        // Push current SmartSizing onto the new session at the next
        // Connected event (the OCX hasn't been built yet, so calling now
        // is a no-op). The event handler updates StatusMessage.

        IsConnecting   = true;
        StatusMessage  = "Connecting…";
        _session.Connect(_lastHwnd, _lastBounds.X, _lastBounds.Y, _lastBounds.W, _lastBounds.H);
    }

    public void Dispose()
    {
        UnwireSession(_session);
        _session.Dispose();
    }
}
