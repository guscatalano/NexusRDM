using NexusRDM.Core.Interfaces;

namespace NexusRDM.Tests.ViewModels.Fakes;

/// <summary>
/// Test double for IRdpSession. Records every method call so tests can
/// assert the ViewModel forwarded the right action, and exposes hooks
/// (<see cref="RaiseConnected"/>, etc.) so tests can simulate the real
/// session firing lifecycle events from arbitrary threads.
/// </summary>
public sealed class FakeRdpSession : IRdpSession
{
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public bool IsConnected   { get; set; }

    public event EventHandler?              Connected;
    public event EventHandler<string>?      Disconnected;
    public event EventHandler<string>?      FatalError;
    public event EventHandler<RdpEventEntry>? RdpEvent;

    // Recorded calls — tuples kept tiny so test assertions stay readable.
    public List<(nint hwnd, int x, int y, int w, int h)> Connects { get; } = new();
    public int    DisconnectCalls { get; private set; }
    public List<(int x, int y, int w, int h)>            Resizes  { get; } = new();
    public int    BringToFrontCalls    { get; private set; }
    public List<bool> SetVisibleCalls  { get; } = new();
    public int    ToggleFullScreenCalls{ get; private set; }
    public int    PopOutCalls          { get; private set; }
    public List<bool> SmartSizingCalls { get; } = new();
    public List<(int w, int h)> ResolutionCalls { get; } = new();
    public List<int> RightInsetCalls          { get; } = new();
    public int    SendCtrlAltDelCalls  { get; private set; }
    public int    DisposeCalls         { get; private set; }

    public void Connect(nint hwndParent, int x, int y, int width, int height)
    {
        Connects.Add((hwndParent, x, y, width, height));
        IsConnected = true; // simulate happy path
    }

    public void Disconnect()        { DisconnectCalls++; IsConnected = false; }
    public void Resize(int x, int y, int width, int height) => Resizes.Add((x, y, width, height));
    public void SendCtrlAltDel()    => SendCtrlAltDelCalls++;
    public void BringToFront()      => BringToFrontCalls++;
    public void SetVisible(bool v)  => SetVisibleCalls.Add(v);
    public void ToggleFullScreen()  => ToggleFullScreenCalls++;
    public void PopOut()            => PopOutCalls++;
    public void SetSmartSizing(bool enabled)        => SmartSizingCalls.Add(enabled);
    public void SetResolution(int width, int height) => ResolutionCalls.Add((width, height));
    public void SetRightInset(int rightInsetPx)      => RightInsetCalls.Add(rightInsetPx);

    public void Dispose() => DisposeCalls++;

    // ── Test hooks ────────────────────────────────────────────────────────
    public void RaiseConnected()                  => Connected?.Invoke(this, EventArgs.Empty);
    public void RaiseDisconnected(string reason)  => Disconnected?.Invoke(this, reason);
    public void RaiseFatalError(string msg)       => FatalError?.Invoke(this, msg);
    public void RaiseRdpEvent(string kind, string detail = "") =>
        RdpEvent?.Invoke(this, new RdpEventEntry(DateTime.Now, kind, detail));
}
