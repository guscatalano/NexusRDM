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

    void Connect(nint hwndParent, int width, int height);
    void Disconnect();
    void Resize(int width, int height);
    void SendCtrlAltDel();
}

public interface IRdpHandler
{
    IRdpSession CreateSession(ConnectionProfile profile, string username, string password);
}
