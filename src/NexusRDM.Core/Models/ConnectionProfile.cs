namespace NexusRDM.Core.Models;

/// <summary>
/// A saved remote connection. Persisted in SQLite.
/// Credentials are stored in Windows Credential Manager — never here.
/// </summary>
public class ConnectionProfile
{
    public Guid   Id               { get; set; } = Guid.NewGuid();
    public string DisplayName      { get; set; } = string.Empty;
    public ConnectionProtocol Protocol { get; set; }
    public string Host             { get; set; } = string.Empty;
    public int    Port             { get; set; }
    public Guid?  GroupId          { get; set; }

    /// <summary>Key into Windows Credential Manager. Null = prompt at connect time.</summary>
    public string? CredentialKey   { get; set; }

    /// <summary>JSON-serialized RdpOptions. Null for SSH connections.</summary>
    public string? RdpSettingsJson { get; set; }

    /// <summary>JSON-serialized SshOptions. Null for RDP connections.</summary>
    public string? SshSettingsJson { get; set; }

    public string   Tags            { get; set; } = string.Empty; // comma-separated
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedAt { get; set; }

    // Navigation
    public Group? Group { get; set; }

    public static int DefaultPort(ConnectionProtocol p) => p == ConnectionProtocol.Rdp ? 3389 : 22;
}
