using NexusRDM.Core.Diagnostics;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Diagnostics;

namespace NexusRDM.Core.Protocols;

/// <summary>
/// SSH.NET <see cref="SftpClient"/> wrapper. Lives in its own TCP
/// connection independent of any <see cref="SshSession"/> — so a
/// directory-tree refresh on a slow link doesn't block keystrokes in
/// the terminal tab, and so a transfer can continue after the terminal
/// tab is closed (well, until this session's tab also closes).
/// </summary>
public sealed class SftpSession : ISftpSession
{
    private SftpClient? _client;
    private readonly Func<CancellationToken, Task<SftpClient>> _clientFactory;
    private readonly string _username;
    private string?         _homeDir;
    private bool            _disposed;

    public Guid   ConnectionId { get; }
    public bool   IsConnected  => _client?.IsConnected ?? false;
    public string Username     => _username;

    public event EventHandler? Disconnected;
    public event EventHandler<SftpTransferEventArgs>? TransferCompleted;

    internal SftpSession(
        Guid connectionId, string username,
        Func<CancellationToken, Task<SftpClient>> clientFactory)
    {
        ConnectionId    = connectionId;
        _username       = username;
        _clientFactory  = clientFactory;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_client is not null) return;
        _client = await _clientFactory(ct);
        // SftpClient.Connect blocks on the network; bounce to the
        // thread pool so the UI thread keeps moving.
        await Task.Run(() => _client.Connect(), ct);
        SshLog.Info($"SFTP connected: user={_username} conn={ConnectionId}");
    }

    public Task DisconnectAsync()
    {
        if (_disposed || _client is null) return Task.CompletedTask;
        try { if (_client.IsConnected) _client.Disconnect(); }
        catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    public async Task<string> GetHomeDirectoryAsync(CancellationToken ct = default)
    {
        if (_homeDir is not null) return _homeDir;
        if (_client is null || !_client.IsConnected) return string.Empty;
        var client = _client;
        _homeDir = await Task.Run(() =>
        {
            try { return client.WorkingDirectory ?? "/"; }
            catch { return "/"; }
        }, ct);
        return _homeDir;
    }

    public async Task<IReadOnlyList<SftpEntry>> ListDirectoryAsync(
        string path, CancellationToken ct = default)
    {
        if (_client is null || !_client.IsConnected) return Array.Empty<SftpEntry>();
        var client = _client;
        return await Task.Run(() =>
        {
            var list = new List<SftpEntry>();
            foreach (var f in client.ListDirectory(path))
            {
                // Strip the literal "." (current dir) — keep ".."
                // because the UI uses it for up-navigation.
                if (f.Name == ".") continue;
                list.Add(new SftpEntry(
                    Name:         f.Name,
                    FullPath:     f.FullName,
                    IsDirectory:  f.IsDirectory,
                    IsSymlink:    f.IsSymbolicLink,
                    Size:         f.Length,
                    LastModified: new DateTimeOffset(f.LastWriteTime),
                    Permissions:  PackPermissions(f.Attributes)));
            }
            return (IReadOnlyList<SftpEntry>)list;
        }, ct);
    }

    public async Task<long> DownloadFileAsync(
        string remotePath, Stream output,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (_client is null || !_client.IsConnected) return 0;
        var client = _client;
        var sw     = Stopwatch.StartNew();
        long bytes = 0;
        bool ok    = false;
        string?    err = null;
        try
        {
            await Task.Run(() =>
            {
                // SftpClient.DownloadFile uses a fixed-size buffer and
                // invokes the progress callback per chunk; we just
                // forward the cumulative-bytes count.
                client.DownloadFile(remotePath, output, n =>
                {
                    bytes = (long)n;
                    progress?.Report(bytes);
                });
            }, ct);
            ok = true;
            return bytes;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            SshLog.Warn($"SFTP download failed: {remotePath} — {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            sw.Stop();
            TransferCompleted?.Invoke(this, new SftpTransferEventArgs
            {
                Direction    = SftpTransferDirection.Download,
                LocalPath    = (output as FileStream)?.Name ?? "(stream)",
                RemotePath   = remotePath,
                Bytes        = bytes,
                Elapsed      = sw.Elapsed,
                Success      = ok,
                ErrorMessage = err,
            });
        }
    }

    public async Task<long> UploadFileAsync(
        Stream input, string remotePath,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (_client is null || !_client.IsConnected) return 0;
        var client = _client;
        var sw     = Stopwatch.StartNew();
        long bytes = 0;
        bool ok    = false;
        string?    err = null;
        try
        {
            await Task.Run(() =>
            {
                client.UploadFile(input, remotePath, true, n =>
                {
                    bytes = (long)n;
                    progress?.Report(bytes);
                });
            }, ct);
            ok = true;
            return bytes;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            SshLog.Warn($"SFTP upload failed: {remotePath} — {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            sw.Stop();
            TransferCompleted?.Invoke(this, new SftpTransferEventArgs
            {
                Direction    = SftpTransferDirection.Upload,
                LocalPath    = (input as FileStream)?.Name ?? "(stream)",
                RemotePath   = remotePath,
                Bytes        = bytes,
                Elapsed      = sw.Elapsed,
                Success      = ok,
                ErrorMessage = err,
            });
        }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (_client is null || !_client.IsConnected) return Task.CompletedTask;
        var client = _client;
        return Task.Run(() => client.CreateDirectory(path), ct);
    }

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        if (_client is null || !_client.IsConnected) return Task.CompletedTask;
        var client = _client;
        return Task.Run(() =>
        {
            if (isDirectory) client.DeleteDirectory(path);
            else             client.DeleteFile(path);
        }, ct);
    }

    public Task RenameAsync(string fromPath, string toPath, CancellationToken ct = default)
    {
        if (_client is null || !_client.IsConnected) return Task.CompletedTask;
        var client = _client;
        return Task.Run(() => client.RenameFile(fromPath, toPath), ct);
    }

    /// <summary>Pack SSH.NET's per-bit permission booleans into a
    /// classic octal mode short (e.g. 0755). Owner / group / others
    /// triplets in the low 9 bits — what <c>ls -l</c> displays.</summary>
    private static short PackPermissions(Renci.SshNet.Sftp.SftpFileAttributes a)
    {
        // Bit layout matches POSIX: owner-read/write/exec, group-rwx,
        // others-rwx in the low 9 bits.
        int m = 0;
        if (a.OwnerCanRead)     m |= 0b100_000_000;
        if (a.OwnerCanWrite)    m |= 0b010_000_000;
        if (a.OwnerCanExecute)  m |= 0b001_000_000;
        if (a.GroupCanRead)     m |= 0b000_100_000;
        if (a.GroupCanWrite)    m |= 0b000_010_000;
        if (a.GroupCanExecute)  m |= 0b000_001_000;
        if (a.OthersCanRead)    m |= 0b000_000_100;
        if (a.OthersCanWrite)   m |= 0b000_000_010;
        if (a.OthersCanExecute) m |= 0b000_000_001;
        return (short)m;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync();
        _client?.Dispose();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }
}
