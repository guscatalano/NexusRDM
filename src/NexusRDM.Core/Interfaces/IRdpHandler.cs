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

    /// <summary>Reparent the RDP child window into <paramref name="hwndParent"/>
    /// at offset (<paramref name="x"/>,<paramref name="y"/>) within the parent's
    /// client area. The position matters because the child is reparented into
    /// the *window* HWND, not into the XAML host element directly, so we have
    /// to translate the panel's bounds into window coordinates ourselves.</summary>
    void Connect(nint hwndParent, int x, int y, int width, int height);
    void Disconnect();
    void Resize(int x, int y, int width, int height);
    void SendCtrlAltDel();
}

public interface IRdpHandler
{
    IRdpSession CreateSession(ConnectionProfile profile, string username, string password);
}
