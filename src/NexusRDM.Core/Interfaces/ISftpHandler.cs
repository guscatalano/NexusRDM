using NexusRDM.Core.Models;

namespace NexusRDM.Core.Interfaces;

/// <summary>
/// One live SFTP session against a remote host. Independent from
/// <see cref="ISshSession"/> — we deliberately use a separate
/// <c>SftpClient</c> + TCP connection so big transfers can't stall
/// the interactive terminal session, and so the two can be opened
/// / closed / disconnected independently. The two share only the
/// <see cref="ConnectionProfile"/> they were built from.
/// </summary>
public interface ISftpSession : IAsyncDisposable
{
    Guid   ConnectionId { get; }
    bool   IsConnected  { get; }
    string Username     { get; }

    event EventHandler? Disconnected;
    /// <summary>Fires once after the underlying SftpClient finishes
    /// connecting and authenticating. The tab's status dot wires off
    /// this to flip from red→green. Not fired for failed connects;
    /// callers should also handle <see cref="Disconnected"/>.</summary>
    event EventHandler? Connected;
    event EventHandler<SftpTransferEventArgs>? TransferCompleted;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    /// <summary>The user's home directory on the remote — resolved
    /// from the server on first call, cached after. Empty string if
    /// the server doesn't surface it.</summary>
    Task<string> GetHomeDirectoryAsync(CancellationToken ct = default);

    /// <summary>List remote directory contents. Excludes the literal
    /// <c>.</c> entry; keeps <c>..</c> so the UI can render an
    /// up-navigation row. Throws on permission / not-found.</summary>
    Task<IReadOnlyList<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Download <paramref name="remotePath"/> into
    /// <paramref name="output"/>. Progress fires with cumulative bytes
    /// written so far. Returns total bytes transferred.</summary>
    Task<long> DownloadFileAsync(
        string remotePath, Stream output,
        IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>Upload <paramref name="input"/> to <paramref name="remotePath"/>.
    /// Overwrites if it exists. Returns total bytes transferred.</summary>
    Task<long> UploadFileAsync(
        Stream input, string remotePath,
        IProgress<long>? progress = null, CancellationToken ct = default);

    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
    Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default);
    Task RenameAsync(string fromPath, string toPath, CancellationToken ct = default);

    /// <summary>Return the remote file's last-write timestamp. Used by
    /// edit-in-place conflict detection: we snapshot mtime at download
    /// time and compare before each re-upload. Returns null on stat
    /// failure (file deleted, permission denied, etc.).</summary>
    Task<DateTimeOffset?> GetRemoteMTimeAsync(string path, CancellationToken ct = default);

    /// <summary>True if a remote file or directory exists at <paramref name="path"/>.
    /// Used by transfer-time overwrite confirmation. Failures (permission
    /// denied, broken symlink, transient I/O) return false rather than
    /// throw — a "can't tell" answer should never abort a transfer.</summary>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
}

/// <summary>Audit-loggable transfer event raised when a file finishes
/// (or fails). Subscribers persist these into the audit table; the UI
/// uses them to update the transfer-history list.</summary>
public sealed class SftpTransferEventArgs : EventArgs
{
    public required SftpTransferDirection Direction   { get; init; }
    public required string                LocalPath   { get; init; }
    public required string                RemotePath  { get; init; }
    public required long                  Bytes       { get; init; }
    public required TimeSpan              Elapsed     { get; init; }
    public required bool                  Success     { get; init; }
    public          string?               ErrorMessage { get; init; }
}

public enum SftpTransferDirection { Upload, Download }

/// <summary>Factory for SFTP sessions; mirrors <see cref="ISshHandler"/>
/// so the host can pick "open SFTP for this profile" using the same
/// credential resolution path as the SSH session.</summary>
public interface ISftpHandler
{
    /// <summary>Build an SFTP session but do NOT connect. The host
    /// then awaits <see cref="ISftpSession.ConnectAsync"/>. Credential
    /// arguments mirror <see cref="ISshHandler.CreateSessionForProfile"/>
    /// so the existing UI resolver
    /// (<c>MainWindow.ResolveSshCredentialsAsync</c>) works for both.</summary>
    ISftpSession CreateSessionForProfile(
        ConnectionProfile profile,
        string username,
        string? storedPassword,
        string? keyPassphrase,
        SshKeyboardPromptHandler? onPrompt);
}
