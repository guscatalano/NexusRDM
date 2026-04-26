namespace NexusRDM.Core.Models;

public enum ConnectionProtocol
{
    Rdp,
    Ssh
}

public enum SshAuthMethod
{
    Password,
    PrivateKey,
    KeyboardInteractive
}

public enum RdpColorDepth
{
    Colors8Bit  = 8,
    Colors15Bit = 15,
    Colors16Bit = 16,
    Colors24Bit = 24,
    Colors32Bit = 32
}

public enum RdpAudioMode
{
    PlayOnClient  = 0,
    PlayOnServer  = 1,
    NoPlayback    = 2
}

/// <summary>
/// Backend used to start an RDP session. Default is <see cref="Mstsc"/>: the
/// classic Windows Remote Desktop client launched as a separate process.
/// <see cref="MstscAx"/> hosts the in-proc <c>MsRdpClient</c> ActiveX control
/// (mstscax.dll) and gives us programmatic control over the session at the
/// cost of a Win32/Forms host. <see cref="FreeRdp"/> is reserved for a
/// future cross-platform backend; selecting it currently throws.
/// </summary>
public enum RdpLaunchMode
{
    Mstsc   = 0,
    MstscAx = 1,
    FreeRdp = 2,
}
