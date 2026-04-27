using NexusRDM.Core.Models;

namespace NexusRDM.Core.Interfaces;

/// <summary>One entry in an RDP session's event log. Captured by the
/// session and surfaced to the UI for the "RDP events" pop-up. Detail is
/// free-form (e.g. a disconnect reason, an error code).</summary>
public sealed record RdpEventEntry(DateTime Timestamp, string Kind, string Detail)
{
    /// <summary>Pre-formatted timestamp for binding directly from XAML —
    /// x:Bind doesn't accept string-literal arguments inside method
    /// calls, so we expose the formatted form as a property.</summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
}

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

    /// <summary>Catch-all stream of session lifecycle events for the
    /// "RDP events" diagnostic window. Fires for Connecting / Connected /
    /// Disconnected / FatalError plus internal milestones (form created,
    /// OCX initialized, pop-out toggled, etc.). Subscribers should expect
    /// events on arbitrary threads.</summary>
    event EventHandler<RdpEventEntry>? RdpEvent;

    /// <summary>Fires after the user closes a popped-out window and the
    /// form snaps back to the host tab's panel rect. Hosts should
    /// re-apply tab visibility — the form may need to be hidden again
    /// if the user is now on a different tab than when they popped out.</summary>
    event EventHandler? ReAttached;

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

    /// <summary>Reserve <paramref name="rightInsetPx"/> raw pixels on the
    /// right side of the form's tracked panel rect. Used by the in-app
    /// edit-connection slide-over so the embedded RDP form narrows
    /// instead of hiding entirely — the user sees the live session while
    /// editing. Pass 0 to clear. No-op for non-embedded backends.</summary>
    void SetRightInset(int rightInsetPx);
}

public interface IRdpHandler
{
    IRdpSession CreateSession(ConnectionProfile profile, string username, string password);
}
