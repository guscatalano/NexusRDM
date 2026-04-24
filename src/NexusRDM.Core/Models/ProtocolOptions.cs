namespace NexusRDM.Core.Models;

/// <summary>RDP-specific connection settings (serialised to JSON in the DB).</summary>
public class RdpOptions
{
    public int    Width            { get; set; } = 1920;
    public int    Height           { get; set; } = 1080;
    public bool   FullScreen       { get; set; } = false;
    public RdpColorDepth ColorDepth { get; set; } = RdpColorDepth.Colors32Bit;
    public RdpAudioMode  AudioMode  { get; set; } = RdpAudioMode.PlayOnClient;
    public bool   RedirectClipboard { get; set; } = true;
    public bool   RedirectDrives    { get; set; } = false;
    public bool   RedirectPrinters  { get; set; } = false;
    public string? GatewayServer    { get; set; }
    public string? Domain           { get; set; }
    public string? AdminConsole     { get; set; }  // /admin flag
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
