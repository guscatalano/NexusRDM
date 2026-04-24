using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;

namespace NexusRDM.ViewModels;

public sealed partial class RdpSessionViewModel : ObservableObject, IDisposable
{
    private readonly IRdpSession    _session;
    private readonly SessionManager _mgr;

    public Guid   ConnectionId { get; }
    public string DisplayName  { get; }
    public string Host         { get; }

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isConnecting = true;
    [ObservableProperty] private string _statusMessage = "Connecting…";

    public RdpSessionViewModel(ConnectionProfile profile, IRdpSession session, SessionManager mgr)
    {
        ConnectionId = profile.Id;
        DisplayName  = profile.DisplayName;
        Host         = $"{profile.Host}:{profile.Port}";
        _session     = session;
        _mgr         = mgr;

        _session.Connected    += (_, _) => { IsConnected = true; IsConnecting = false; StatusMessage = $"Connected to {Host}"; };
        _session.Disconnected += (_, reason) => { IsConnected = false; StatusMessage = $"Disconnected: {reason}"; };
        _session.FatalError   += (_, msg)    => { IsConnected = false; IsConnecting = false; StatusMessage = $"Error: {msg}"; };
    }

    /// <summary>Called by the view once it has a valid HWND to host the RDP client.</summary>
    public void StartConnection(nint hwndParent, int width, int height) =>
        _session.Connect(hwndParent, width, height);

    public void Resize(int width, int height) => _session.Resize(width, height);

    public void SendCtrlAltDel() => _session.SendCtrlAltDel();

    [RelayCommand]
    private void Disconnect()
    {
        _session.Disconnect();
        var entry = _mgr.FindByConnectionId(ConnectionId);
        if (entry is not null) _mgr.CloseAsync(entry);
    }

    public void Dispose() => _session.Dispose();
}
