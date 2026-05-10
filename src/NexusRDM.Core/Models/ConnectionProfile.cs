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

    /// <summary>Login username, stored plaintext on the profile. SSH
    /// requires it in the very first auth packet — there's no
    /// server-driven "what's your name?" mechanism — so it can't live
    /// in the credential vault alongside the password (that path
    /// always demands both). Optional: when null we fall back to the
    /// vault entry (if any) and only prompt as a last resort.</summary>
    public string? Username        { get; set; }

    /// <summary>Key into Windows Credential Manager. Null = prompt at connect time.</summary>
    public string? CredentialKey   { get; set; }

    /// <summary>SSH-only: how authentication is performed. Defaults to
    /// <see cref="SshAuthMode.Stored"/> for backward compat — every
    /// pre-existing connection behaves exactly as before. Ignored for
    /// RDP connections.</summary>
    public SshAuthMode SshAuthMode { get; set; } = SshAuthMode.Stored;

    /// <summary>SSH-only: absolute path to an OpenSSH-format private
    /// key file. Used by <see cref="SshAuthMode.PrivateKey"/> and
    /// <see cref="SshAuthMode.KeyThenPrompt"/>. Null otherwise.</summary>
    public string? SshKeyFilePath  { get; set; }

    /// <summary>SSH-only: credential-vault key for the private key's
    /// passphrase. Null = key is unencrypted, or prompt at connect.
    /// Stored separately from <see cref="CredentialKey"/> so a
    /// connection can have BOTH a passphrase-encrypted key and a
    /// fallback password (KeyThenPrompt mode).</summary>
    public string? SshKeyPassphraseCredentialKey { get; set; }

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

    /// <summary>FK to <see cref="ProxmoxSource"/> when this row was
    /// imported from a Proxmox cluster sync. Null for user-created
    /// connections.</summary>
    public Guid? ExternalSourceId { get; set; }

    /// <summary>Stable identifier in the source system. For Proxmox:
    /// <c>"{node}/{type}/{vmid}"</c> (e.g. <c>"pve1/qemu/100"</c>).
    /// Used by the sync engine to match existing rows on subsequent
    /// passes.</summary>
    public string? ExternalId { get; set; }

    /// <summary>True when the row's host/name/protocol fields are
    /// owned by the sync engine. Editor locks those fields and a sync
    /// pass can overwrite them; user-set fields (credentials, icon,
    /// tags) are always preserved.</summary>
    public bool IsManaged { get; set; }

    // Navigation
    public Group?         Group           { get; set; }
    public ProxmoxSource? ExternalSource  { get; set; }

    public static int DefaultPort(ConnectionProtocol p) => p == ConnectionProtocol.Rdp ? 3389 : 22;
}
