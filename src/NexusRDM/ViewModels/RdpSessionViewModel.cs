using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;

namespace NexusRDM.ViewModels;

public sealed partial class RdpSessionViewModel : ObservableObject, IDisposable
{
    private readonly IRdpSession     _session;
    private readonly SessionManager  _mgr;
    // Captured at construction (UI thread) so events from non-UI threads —
    // e.g. MstscAxRdpSession's Forms STA thread — can hop back here before
    // touching observable properties. Without this, OnPropertyChanged fires
    // on the wrong thread and the XAML binding throws
    // RPC_E_WRONG_THREAD ("marshalled for a different thread").
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    public Guid   ConnectionId { get; }
    public string DisplayName  { get; }
    public string Host         { get; }

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isConnecting = true;
    [ObservableProperty] private string _statusMessage = "Connecting…";

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

    public RdpSessionViewModel(ConnectionProfile profile, IRdpSession session, SessionManager mgr)
    {
        ConnectionId = profile.Id;
        DisplayName  = profile.DisplayName;
        Host         = $"{profile.Host}:{profile.Port}";
        _session     = session;
        _mgr         = mgr;

        _session.Connected    += (_, _)      => OnUi(() => { IsConnected = true;  IsConnecting = false; StatusMessage = $"Connected to {Host}"; });
        _session.Disconnected += (_, reason) => OnUi(() => { IsConnected = false; StatusMessage = $"Disconnected: {reason}"; });
        _session.FatalError   += (_, msg)    => OnUi(() => { IsConnected = false; IsConnecting = false; StatusMessage = $"Error: {msg}"; });
    }

    private void OnUi(Action a)
    {
        if (_ui is null) { a(); return; }
        _ui.TryEnqueue(() => a());
    }

    public void StartConnection(nint hwndParent, int x, int y, int width, int height) =>
        _session.Connect(hwndParent, x, y, width, height);

    public void Resize(int x, int y, int width, int height) => _session.Resize(x, y, width, height);

    public void BringToFront()           => _session.BringToFront();
    public void SetVisible(bool visible) => _session.SetVisible(visible);
    public void ToggleFullScreen()       => _session.ToggleFullScreen();
    public void PopOut()                 => _session.PopOut();

    public void SendCtrlAltDel() => _session.SendCtrlAltDel();

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        _session.Disconnect();
        var entry = _mgr.FindByConnectionId(ConnectionId);
        if (entry is not null) await _mgr.CloseAsync(entry);
    }

    public void Dispose() => _session.Dispose();
}
