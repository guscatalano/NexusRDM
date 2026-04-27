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

    /// <summary>Segoe Fluent Icons codepoint (e.g. <c>"E756"</c>). The
    /// connection-tree row renders this as the row's leading icon —
    /// replacing the previous protocol/status dot with a per-connection
    /// glyph. Null/empty falls back to a sensible protocol default.</summary>
    public string? IconGlyph       { get; set; }

    /// <summary>Optional <c>#AARRGGBB</c> override for the row icon's
    /// colour. When set, the connections tree paints the glyph in this
    /// colour and surfaces connection state via a separate small dot;
    /// when null the glyph itself remains status-coloured.</summary>
    public string? IconColorHex    { get; set; }

    public string   Tags            { get; set; } = string.Empty; // comma-separated
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedAt { get; set; }

    // Navigation
    public Group? Group { get; set; }

    public static int DefaultPort(ConnectionProtocol p) => p == ConnectionProtocol.Rdp ? 3389 : 22;
}
