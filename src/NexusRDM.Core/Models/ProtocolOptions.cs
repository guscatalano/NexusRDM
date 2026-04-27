namespace NexusRDM.Core.Models;

/// <summary>
/// RDP-specific connection settings (serialised to JSON in the DB).
/// New fields keep their default values when older saved JSON omits
/// them, so adding properties is forward-compatible.
/// </summary>
public class RdpOptions
{
    // ── Display ──────────────────────────────────────────────────────────
    public int    Width            { get; set; } = 1920;
    public int    Height           { get; set; } = 1080;
    public bool   FullScreen       { get; set; } = false;
    public RdpColorDepth ColorDepth { get; set; } = RdpColorDepth.Colors32Bit;

    // ── Audio ────────────────────────────────────────────────────────────
    public RdpAudioMode  AudioMode  { get; set; } = RdpAudioMode.PlayOnClient;
    /// <summary>Redirect the local microphone to the remote session.</summary>
    public bool   AudioCapture     { get; set; } = false;

    // ── Redirections ─────────────────────────────────────────────────────
    public bool   RedirectClipboard { get; set; } = true;
    public bool   RedirectDrives    { get; set; } = false;
    public bool   RedirectPrinters  { get; set; } = false;
    public bool   RedirectSmartCards{ get; set; } = false;
    public bool   RedirectPorts     { get; set; } = false;
    /// <summary>Plug-and-play devices (cameras, phones).</summary>
    public bool   RedirectDevices   { get; set; } = false;
    /// <summary>Point-of-sale devices.</summary>
    public bool   RedirectPOSDevices{ get; set; } = false;

    // ── Gateway ──────────────────────────────────────────────────────────
    public string? GatewayServer    { get; set; }
    public string? GatewayUsername  { get; set; }
    public string? GatewayDomain    { get; set; }
    public RdpGatewayUsage GatewayUsageMethod { get; set; } = RdpGatewayUsage.NoUse;

    // ── Connection ───────────────────────────────────────────────────────
    public string? Domain           { get; set; }
    /// <summary>Connect to the server console session (mstsc /admin).</summary>
    public bool   AdminConsole      { get; set; } = false;
    /// <summary>Connection-broker routing token (LB info).</summary>
    public string? LoadBalanceInfo  { get; set; }
    public bool   AutoReconnect     { get; set; } = true;

    // ── Authentication ───────────────────────────────────────────────────
    public bool   EnableCredSspSupport { get; set; } = true;
    public RdpAuthenticationLevel AuthenticationLevel { get; set; } = RdpAuthenticationLevel.WarnIfNoAuth;
    public bool   PromptForCredentials { get; set; } = false;

    // ── Performance ──────────────────────────────────────────────────────
    public RdpNetworkType NetworkType { get; set; } = RdpNetworkType.Auto;
    public bool   DesktopBackground { get; set; } = false;
    public bool   VisualStyles      { get; set; } = false;
    public bool   FontSmoothing     { get; set; } = true;
    public bool   MenuAnimations    { get; set; } = false;
    public bool   WindowDrag        { get; set; } = false;
    public bool   DesktopComposition{ get; set; } = false;
    public bool   BitmapCaching     { get; set; } = true;

    // ── Keyboard ─────────────────────────────────────────────────────────
    public RdpKeyboardHook KeyboardHookMode { get; set; } = RdpKeyboardHook.RemoteOnFullScreen;

    // ── Connection bar ───────────────────────────────────────────────────
    public bool   ConnectionBar     { get; set; } = true;
    public bool   PinConnectionBar  { get; set; } = false;
}

/// <summary>SSH-specific connection settings (serialised to JSON in the DB).</summary>
public class SshOptions
{
    public SshAuthMethod AuthMethod      { get; set; } = SshAuthMethod.Password;
    public string?       PrivateKeyPath  { get; set; }
    public string?       PrivateKeyPassphrase { get; set; }  // stored in Cred Manager
    public string        TerminalType    { get; set; } = "xterm-256color";
    public int           Columns         { get; set; } = 220;
    public int           Rows            { get; set; } = 50;
    public int           KeepAliveSeconds { get; set; } = 30;
    public string?       JumpHost        { get; set; }
}
