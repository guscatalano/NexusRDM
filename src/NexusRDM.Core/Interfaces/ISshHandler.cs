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
