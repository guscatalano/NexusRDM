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

/// <summary>
/// Default resolution policy applied when an RDP session opens. Drives
/// the value passed to <c>IMsRdpClient.DesktopWidth/DesktopHeight</c>
/// before <c>Connect</c>.
/// </summary>
/// <remarks>
/// Index order matches the ComboBox in SettingsPage.xaml. Adding entries
/// is fine; reordering will silently change persisted preferences.
/// </remarks>
public enum RdpDefaultResolution
{
    /// <summary>Use the dimensions of the monitor currently hosting the
    /// app window. Best fit for full-screen / pop-out use.</summary>
    MatchMonitor = 0,
    /// <summary>Use the host tab's panel size at connect time. Cleanest
    /// when SmartSizing is on and you want a 1:1 in-tab render.</summary>
    MatchPanel   = 1,
    Res1024x768  = 2,
    Res1280x720  = 3,
    Res1366x768  = 4,
    Res1600x900  = 5,
    Res1920x1080 = 6,
    Res2560x1440 = 7,
    Res3840x2160 = 8,
}
