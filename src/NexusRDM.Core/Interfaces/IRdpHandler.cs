using NexusRDM.Core.Models;

namespace NexusRDM.Core.Interfaces;

/// <summary>
/// Abstraction over the Windows MSTSC/AxMSTSCLib RDP ActiveX control.
/// The concrete implementation lives in the UI project where it can host the Win32 HWND.
/// </summary>
public interface IRdpSession : IDisposable
{
    Guid   ConnectionId  { get; }
    bool   IsConnected   { get; }

    event EventHandler?        Connected;
    event EventHandler<string>? Disconnected;   // arg = reason string
    event EventHandler<string>? FatalError;

    /// <summary>Begin the RDP session. <paramref name="hwndParent"/> is the
    /// HWND that should own the resulting remote-desktop window (typically
    /// the WinUI app window). The (x, y, width, height) hint suggests an
    /// initial placement, but the implementation may ignore it — Phase 2's
    /// MstscAx backend uses a standalone owned window the user moves
    /// freely, so coords aren't authoritative.</summary>
    void Connect(nint hwndParent, int x, int y, int width, int height);
    void Disconnect();
    void Resize(int x, int y, int width, int height);
    void SendCtrlAltDel();

    /// <summary>Bring the remote-desktop window to the foreground. No-op for
    /// backends without a separate window (the Mstsc child-process backend
    /// doesn't have a handle to its window).</summary>
    void BringToFront();

    /// <summary>Show or hide the embedded remote-desktop window. Used to
    /// hide the owned top-level form when its host XAML tab is no longer
    /// visible (tab switch). No-op for backends without an embedded
    /// window.</summary>
    void SetVisible(bool visible);

    /// <summary>Toggle the RDP control's full-screen mode. The OCX handles
    /// the transition itself (separate full-screen window with built-in
    /// connection bar; Ctrl+Alt+Break exits). No-op when no OCX is
    /// hosted.</summary>
    void ToggleFullScreen();

    /// <summary>Detach the embedded window from its host tab so the user
    /// can freely move/resize it as a normal sizable window. After
    /// pop-out, tab-switch hides and panel-follow are suppressed. One-way
    /// for now — no re-attach.</summary>
    void PopOut();

    /// <summary>Toggle the RDP control's SmartSizing property. <c>true</c>
    /// scales the remote desktop to fit the host window; <c>false</c>
    /// shows it at its native resolution with scrollbars. No-op without
    /// an OCX.</summary>
    void SetSmartSizing(bool enabled);

    /// <summary>Live-resize the active RDP session to the given resolution.
    /// Backed by <c>IMsRdpClient9.UpdateSessionDisplaySettings</c>; the
    /// remote server renegotiates display dimensions without dropping the
    /// connection. No-op without an OCX.</summary>
    void SetResolution(int width, int height);
}

public interface IRdpHandler
{
    IRdpSession CreateSession(ConnectionProfile profile, string username, string password);
}
