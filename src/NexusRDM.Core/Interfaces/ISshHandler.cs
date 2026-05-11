using NexusRDM.Core.Models;

namespace NexusRDM.Core.Interfaces;

public interface ISshSession : IAsyncDisposable
{
    Guid ConnectionId { get; }
    bool IsConnected  { get; }

    /// <summary>Raw VT data stream received from the server. Subscribe to render terminal output.</summary>
    event EventHandler<byte[]>? DataReceived;
    event EventHandler?         Disconnected;

    Task ConnectAsync(CancellationToken ct = default);
    Task SendAsync(byte[] data, CancellationToken ct = default);
    Task ResizeAsync(int columns, int rows, CancellationToken ct = default);
    Task DisconnectAsync();

    // ── Session statistics ──────────────────────────────────────────
    // All free / local-only — no extra channel traffic. Backends that
    // can't surface a given stat (PuTTY, demo) return sensible empties
    // ("" / 0) rather than throw.

    /// <summary>When <see cref="ConnectAsync"/> succeeded. Null while
    /// disconnected. Drives the "uptime" counter in the status strip.</summary>
    DateTimeOffset? ConnectedAt { get; }

    /// <summary>Total bytes received from the server since connect.</summary>
    long BytesReceived { get; }

    /// <summary>Total bytes sent to the server since connect.</summary>
    long BytesSent { get; }

    /// <summary>SSH banner, e.g. <c>"SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3"</c>.
    /// Empty when not connected or when the backend doesn't expose it
    /// (PuTTY-backed sessions).</summary>
    string ServerVersion { get; }

    /// <summary>Negotiated cipher + MAC, e.g. <c>"aes256-gcm + hmac-sha2-256"</c>.
    /// Empty for backends that don't expose the channel.</summary>
    string CipherInfo { get; }

    /// <summary>Current PTY width in columns.</summary>
    int PtyCols { get; }

    /// <summary>Current PTY height in rows.</summary>
    int PtyRows { get; }

    /// <summary>The username actually used for the live auth session.
    /// Important when <see cref="ConnectionProfile.Username"/> is empty
    /// and the user typed one into the terminal broker's <c>"login as: "</c>
    /// prompt — we want SFTP / future tunnels to reuse what SSH resolved
    /// instead of re-prompting. Empty until <see cref="ConnectAsync"/>
    /// returns successfully.</summary>
    string ConnectedUsername { get; }

    /// <summary>Run a one-shot command on a separate exec channel
    /// (does NOT touch the user's interactive shell). Used by the
    /// optional host-stats panel to poll <c>/proc/loadavg</c>,
    /// <c>/proc/meminfo</c>, etc. Throws <see cref="NotSupportedException"/>
    /// on backends that don't expose a programmable channel (PuTTY).</summary>
    Task<string> ExecAsync(string command, CancellationToken ct = default);
}

/// <summary>Callback the keyboard-interactive auth flow uses to ask
/// the UI for responses to server-driven prompts. Each invocation
/// receives the prompt text + an "is this a password / hidden input"
/// hint and returns the user's typed response. Returning null cancels
/// auth.</summary>
public delegate Task<string?> SshKeyboardPromptHandler(
    string promptText, bool echoSuppressed, CancellationToken ct);

public interface ISshHandler
{
    /// <summary>Create an SSH session but do NOT connect yet.</summary>
    ISshSession CreateSession(ConnectionProfile profile, string username, string password);

    /// <summary>Create a session using private-key authentication.</summary>
    ISshSession CreateSessionWithKey(ConnectionProfile profile, string username,
        string privateKeyPath, string? passphrase = null);

    /// <summary>Create a session built around the connection's
    /// configured <see cref="SshAuthMode"/>. The handler picks the
    /// auth-method chain (password / key / keyboard-interactive,
    /// possibly multiple) based on the profile + the supplied hints:
    ///
    ///   • <paramref name="username"/> — may be empty. When empty,
    ///     <paramref name="onPrompt"/> is used to render
    ///     <c>"login as: "</c> into the terminal at connect time.
    ///   • <paramref name="storedPassword"/> — vault-resolved password
    ///     for Stored mode, or the fallback for KeyThenPrompt when no
    ///     server-side prompt is desired.
    ///   • <paramref name="keyPassphrase"/> — vault-resolved passphrase
    ///     for the private key file, when applicable.
    ///   • <paramref name="onPrompt"/> — invoked for both the
    ///     pre-handshake username prompt (when username is empty) and
    ///     for keyboard-interactive challenges from the server during
    ///     auth. The UI's TerminalAuthBroker is the natural backing.
    /// </summary>
    ISshSession CreateSessionForProfile(
        ConnectionProfile profile,
        string username,
        string? storedPassword,
        string? keyPassphrase,
        SshKeyboardPromptHandler? onPrompt);
}
