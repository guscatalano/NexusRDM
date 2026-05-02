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

/// <summary>Maps to mstscax <c>AdvancedSettings9.GatewayUsageMethod</c>.</summary>
public enum RdpGatewayUsage
{
    NoUse  = 0,
    Direct = 1,
    Detect = 2,
    Default = 3,
}

/// <summary>Maps to mstscax <c>AdvancedSettings9.NetworkConnectionType</c>
/// — used as a hint to the server about which graphics features to enable.
/// The integer values mirror the OCX's own constants
/// (<c>CONNECTION_TYPE_*</c>); the OCX rejects 0, so <see cref="Auto"/>
/// uses 7 = <c>CONNECTION_TYPE_AUTODETECT</c>.</summary>
public enum RdpNetworkType
{
    Modem        = 1,
    LowBroadband = 2,
    Satellite    = 3,
    HighBroadband= 4,
    Wan          = 5,
    Lan          = 6,
    Auto         = 7,
}

/// <summary>Where Windows-key combos (Alt+Tab, Win, etc.) are routed.
/// Maps to mstscax <c>AdvancedSettings9.KeyboardHookMode</c>.</summary>
public enum RdpKeyboardHook
{
    LocalOnly          = 0,
    RemoteAlways       = 1,
    RemoteOnFullScreen = 2,
}

/// <summary>Server-auth strictness. Maps to mstscax
/// <c>AdvancedSettings9.AuthenticationLevel</c>.</summary>
public enum RdpAuthenticationLevel
{
    NoAuthRequired   = 0,
    AuthRequired     = 1,
    WarnIfNoAuth     = 2,
}

/// <summary>Whether activating a connection in the tree opens it on a
/// single click or a double click.</summary>
public enum ConnectionClickBehavior
{
    SingleClick = 0,
    DoubleClick = 1,
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
/// SSH terminal backend selection.
/// <see cref="Embedded"/> uses our in-app VtNetCore-based terminal — fast,
/// integrated with the audit log, supports per-cell selection/copy, but
/// stumbles on heavyweight curses apps (top, htop, less) because the
/// emulator is incomplete. <see cref="PuttyNg"/> embeds PuTTYNG (a fork
/// of PuTTY designed for in-window hosting) into the session tab via
/// owner-window pinning — battle-tested terminal at the cost of a
/// separate process and no audit-log piping. PuTTYNG.exe is downloaded
/// to %LocalAppData% on first use.
/// </summary>
public enum SshLaunchMode
{
    Embedded = 0,
    PuttyNg  = 1,
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
