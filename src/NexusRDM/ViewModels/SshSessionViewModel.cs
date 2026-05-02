using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;

namespace NexusRDM.ViewModels;

public sealed partial class SshSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISshSession    _session;
    private readonly SessionManager _mgr;

    /// <summary>The underlying session, exposed so the view can
    /// type-check (<c>is PuttySshSession</c>) and wire backend-
    /// specific embed plumbing. Read-only.</summary>
    public ISshSession Session => _session;

    public Guid   ConnectionId { get; }
    public string DisplayName  { get; }
    public string Host         { get; }

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isConnecting = true;
    [ObservableProperty] private string _statusMessage = "Connecting…";

    /// <summary>Raised when VT bytes arrive — the TerminalControl subscribes to this.</summary>
    public event EventHandler<byte[]>? DataReceived;

    public SshSessionViewModel(ConnectionProfile profile, ISshSession session, SessionManager mgr)
    {
        ConnectionId = profile.Id;
        DisplayName  = profile.DisplayName;
        Host         = $"{profile.Host}:{profile.Port}";
        _session     = session;
        _mgr         = mgr;

        _session.DataReceived  += (_, data) => DataReceived?.Invoke(this, data);
        _session.Disconnected  += OnSessionDisconnected;
    }

    public async Task ConnectAsync()
    {
        try
        {
            await _session.ConnectAsync();
            IsConnected    = true;
            IsConnecting   = false;
            StatusMessage  = $"Connected to {Host}";
        }
        catch (Exception ex)
        {
            IsConnecting  = false;
            StatusMessage = $"Failed: {ex.Message}";
        }
    }

    public Task SendInputAsync(byte[] data) =>
        IsConnected ? _session.SendAsync(data) : Task.CompletedTask;

    public Task ResizeAsync(int cols, int rows) =>
        IsConnected ? _session.ResizeAsync(cols, rows) : Task.CompletedTask;

    private void OnSessionDisconnected(object? sender, EventArgs e)
    {
        IsConnected   = false;
        StatusMessage = "Disconnected";
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _session.DisconnectAsync();
        var entry = _mgr.FindByConnectionId(ConnectionId);
        if (entry is not null) await _mgr.CloseAsync(entry);
    }

    public async ValueTask DisposeAsync() => await _session.DisposeAsync();
}
