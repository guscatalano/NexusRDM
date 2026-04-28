using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Services;

/// <summary>
/// Demo-mode <see cref="IRdpSession"/> backing. The real RDP path
/// hosts a Win32 mstscax window over a XAML placeholder; that's
/// expensive and requires a reachable host. For the demo flow we
/// instead return this no-op session that fires
/// <see cref="Connected"/> immediately and lets
/// <see cref="Views.RdpSessionView"/> render a static "DEMO RDP"
/// placeholder bitmap inside the host panel. Detected by view via a
/// <c>vm.Session is DemoRdpSession</c> check exposed through
/// <c>RdpSessionViewModel.IsDemo</c>.
/// </summary>
internal sealed class DemoRdpSession : IRdpSession
{
    public DemoRdpSession(ConnectionProfile profile)
    {
        ConnectionId = profile.Id;
    }

    public Guid ConnectionId { get; }
    public bool IsConnected  { get; private set; }

    public event EventHandler?               Connected;
    public event EventHandler<string>?       Disconnected;
    public event EventHandler<string>?       FatalError;
    public event EventHandler<RdpEventEntry>? RdpEvent;
    public event EventHandler?               ReAttached;

    public void Connect(nint hwndParent, int x, int y, int width, int height)
    {
        // Real RDP backends fire Connected after the OCX negotiates
        // the session; here we synthesise the same signal on a
        // short delay so the UI gets to show its "Connecting…" spinner
        // for a beat — matches what a real handshake looks like.
        Task.Run(async () =>
        {
            await Task.Delay(450);
            IsConnected = true;
            Connected?.Invoke(this, EventArgs.Empty);
            RdpEvent?.Invoke(this, new RdpEventEntry(
                DateTime.Now, "Connected", "Demo session"));
        });
    }

    public void Disconnect()
    {
        if (!IsConnected) return;
        IsConnected = false;
        Disconnected?.Invoke(this, "Demo session ended");
    }

    public void Resize(int x, int y, int width, int height) { }
    public void SendCtrlAltDel() { }
    public void BringToFront()   { }
    public void SetVisible(bool visible) { }
    public void ToggleFullScreen() { }
    public void PopOut() { }
    public void SetSmartSizing(bool enabled) { }
    public void SetResolution(int width, int height) { }
    public void SetRightInset(int rightInsetPx) { }

    public void Dispose() => Disconnect();
}
