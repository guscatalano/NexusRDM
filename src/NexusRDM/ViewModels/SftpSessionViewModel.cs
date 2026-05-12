using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;
using System.Collections.ObjectModel;
using System.IO;

namespace NexusRDM.ViewModels;

/// <summary>
/// Two-pane SFTP file manager. Left pane = local filesystem (Windows
/// paths), right pane = remote SFTP. Both panes share the same
/// <see cref="SftpEntry"/> record so the view template can be the same;
/// only the directory-listing path differs.
///
/// Transfers run serially against the single <see cref="ISftpSession"/>
/// — one upload or download at a time, with the rest queued. Each
/// completion fires <see cref="ISftpSession.TransferCompleted"/> which
/// we forward to the audit log.
/// </summary>
public sealed partial class SftpSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISftpSession   _sftp;
    private readonly SessionManager _mgr;
    private readonly Queue<TransferRequest> _queue = new();
    private bool _pumpRunning;

    public ISftpSession Sftp => _sftp;

    /// <summary>The connection profile this session is bound to. Used
    /// by the cross-launch "Open Terminal" button so it can reuse the
    /// same profile to open an SSH session.</summary>
    public ConnectionProfile Profile { get; }

    public Guid   ConnectionId { get; }
    public string DisplayName  { get; }
    public string Host         { get; }

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isConnecting = true;
    [ObservableProperty] private string _statusMessage = "Connecting…";

    // ── Local pane ───────────────────────────────────────────────────
    [ObservableProperty] private string _localPath = string.Empty;
    public ObservableCollection<SftpEntry> LocalEntries { get; } = new();

    // ── Remote pane ──────────────────────────────────────────────────
    [ObservableProperty] private string _remotePath = "/";
    public ObservableCollection<SftpEntry> RemoteEntries { get; } = new();

    // ── Transfer state ───────────────────────────────────────────────
    [ObservableProperty] private string _transferStatus = "Idle";
    [ObservableProperty] private int    _transferPercent;
    [ObservableProperty] private int    _queueDepth;

    public SftpSessionViewModel(
        ConnectionProfile profile, ISftpSession sftp, SessionManager mgr)
    {
        Profile      = profile;
        ConnectionId = profile.Id;
        DisplayName  = profile.DisplayName;
        Host         = $"{profile.Host}:{profile.Port}";
        _sftp        = sftp;
        _mgr         = mgr;

        // Local pane defaults to the user's profile folder — most users
        // want to start at "my downloads / my documents" not C:\.
        LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _sftp.Disconnected += OnSessionDisconnected;
    }

    public async Task ConnectAsync()
    {
        try
        {
            await _sftp.ConnectAsync();
            IsConnected   = true;
            IsConnecting  = false;
            StatusMessage = $"Connected to {Host}";
            // Remote pane defaults to the user's home directory; ignore
            // failures, fall back to "/".
            RemotePath = await _sftp.GetHomeDirectoryAsync() is { Length: > 0 } home ? home : "/";
            await RefreshLocalAsync();
            await RefreshRemoteAsync();
        }
        catch (Exception ex)
        {
            IsConnecting  = false;
            StatusMessage = $"Failed: {ex.Message}";
        }
    }

    // ── Local pane operations ────────────────────────────────────────

    public Task RefreshLocalAsync() => Task.Run(() =>
    {
        var snapshot = ListLocal(LocalPath);
        App.MainWin?.DispatcherQueue.TryEnqueue(() =>
        {
            LocalEntries.Clear();
            foreach (var e in snapshot) LocalEntries.Add(e);
        });
    });

    private static List<SftpEntry> ListLocal(string path)
    {
        var result = new List<SftpEntry>();
        if (!Directory.Exists(path)) return result;
        // Insert ".." unless we're at a drive root.
        var parent = Directory.GetParent(path);
        if (parent is not null)
        {
            result.Add(new SftpEntry("..", parent.FullName, true, false, 0,
                DateTimeOffset.FromFileTime(parent.LastWriteTime.ToFileTime()), 0));
        }
        try
        {
            foreach (var d in Directory.EnumerateDirectories(path))
            {
                var info = new DirectoryInfo(d);
                result.Add(new SftpEntry(info.Name, info.FullName, true, false, 0,
                    new DateTimeOffset(info.LastWriteTime), 0));
            }
            foreach (var f in Directory.EnumerateFiles(path))
            {
                var info = new FileInfo(f);
                result.Add(new SftpEntry(info.Name, info.FullName, false, false, info.Length,
                    new DateTimeOffset(info.LastWriteTime), 0));
            }
        }
        catch (UnauthorizedAccessException) { /* skip locked dirs */ }
        return result;
    }

    [RelayCommand]
    public async Task NavigateLocalAsync(SftpEntry entry)
    {
        if (!entry.IsDirectory) return;
        LocalPath = entry.FullPath;
        await RefreshLocalAsync();
    }

    // ── Remote pane operations ───────────────────────────────────────

    public async Task RefreshRemoteAsync()
    {
        if (!IsConnected) return;
        try
        {
            var entries = await _sftp.ListDirectoryAsync(RemotePath);
            RemoteEntries.Clear();
            // Sort: directories first, then files; alpha within each.
            foreach (var e in entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                RemoteEntries.Add(e);
        }
        catch (Exception ex)
        {
            StatusMessage = $"List failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task NavigateRemoteAsync(SftpEntry entry)
    {
        if (!entry.IsDirectory) return;
        // ".." → parent of current path; normal dir → entry.FullPath.
        if (entry.Name == "..")
        {
            int slash = RemotePath.LastIndexOf('/');
            RemotePath = slash <= 0 ? "/" : RemotePath[..slash];
        }
        else
        {
            RemotePath = entry.FullPath;
        }
        await RefreshRemoteAsync();
    }

    // ── Transfers ────────────────────────────────────────────────────

    public void EnqueueDownload(SftpEntry remote)
    {
        if (remote.IsDirectory) return; // dir download is a follow-up
        var localDest = Path.Combine(LocalPath, remote.Name);
        _queue.Enqueue(new TransferRequest(SftpTransferDirection.Download, localDest, remote.FullPath, remote.Size));
        QueueDepth = _queue.Count;
        _ = PumpQueueAsync();
    }

    public void EnqueueUpload(SftpEntry local)
    {
        if (local.IsDirectory) return;
        var remoteDest = RemotePath.TrimEnd('/') + "/" + local.Name;
        _queue.Enqueue(new TransferRequest(SftpTransferDirection.Upload, local.FullPath, remoteDest, local.Size));
        QueueDepth = _queue.Count;
        _ = PumpQueueAsync();
    }

    private async Task PumpQueueAsync()
    {
        if (_pumpRunning) return;
        _pumpRunning = true;
        try
        {
            while (_queue.Count > 0)
            {
                var req = _queue.Dequeue();
                QueueDepth = _queue.Count;
                await RunOneTransferAsync(req);
            }
            TransferStatus  = "Idle";
            TransferPercent = 0;
            // Refresh both panes — the just-completed transfer changed
            // sizes / added an entry on one side.
            await RefreshLocalAsync();
            await RefreshRemoteAsync();
        }
        finally { _pumpRunning = false; }
    }

    private async Task RunOneTransferAsync(TransferRequest req)
    {
        var label = req.Direction == SftpTransferDirection.Upload
            ? $"Upload {Path.GetFileName(req.LocalPath)}"
            : $"Download {Path.GetFileName(req.RemotePath)}";
        TransferStatus  = label;
        TransferPercent = 0;
        var progress = new Progress<long>(bytes =>
        {
            if (req.TotalSize > 0)
                TransferPercent = (int)Math.Min(100, bytes * 100 / req.TotalSize);
        });
        try
        {
            if (req.Direction == SftpTransferDirection.Upload)
            {
                using var fs = File.OpenRead(req.LocalPath);
                await _sftp.UploadFileAsync(fs, req.RemotePath, progress);
            }
            else
            {
                using var fs = File.Create(req.LocalPath);
                await _sftp.DownloadFileAsync(req.RemotePath, fs, progress);
            }
        }
        catch (Exception ex)
        {
            TransferStatus = $"{label} failed: {ex.Message}";
        }
    }

    // ── Misc commands ────────────────────────────────────────────────

    /// <summary>Stream a remote file into memory + decode as UTF-8.
    /// Returns null on failure or if the file exceeds
    /// <see cref="PreviewMaxBytes"/>. NEVER writes anything to local
    /// disk — the file lives only inside the returned string. Used by
    /// the right-click "Preview" path in the SFTP view.</summary>
    public async Task<string?> ReadRemoteTextAsync(SftpEntry entry, CancellationToken ct = default)
    {
        if (!IsConnected || entry.IsDirectory)            return null;
        if (entry.Size > PreviewMaxBytes)                 return null;
        var ms = new System.IO.MemoryStream(capacity: (int)Math.Min(entry.Size, 64 * 1024));
        try
        {
            await _sftp.DownloadFileAsync(entry.FullPath, ms, progress: null, ct);
        }
        catch
        {
            return null;
        }
        // Decode as UTF-8 with fallback. Invalid bytes become U+FFFD
        // replacement chars rather than throw — for a "show me what's
        // in this file" UX, showing partial garbled content is better
        // than failing outright.
        var enc = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        return enc.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>Hard cap on inline-preview file size. 1 MB is enough
    /// for typical config files, scripts, logs you'd realistically
    /// preview; bigger files should be downloaded properly.</summary>
    public const long PreviewMaxBytes = 1L * 1024 * 1024;

    [RelayCommand]
    public async Task CreateRemoteFolderAsync(string name)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(name)) return;
        var path = RemotePath.TrimEnd('/') + "/" + name;
        await _sftp.CreateDirectoryAsync(path);
        await RefreshRemoteAsync();
    }

    [RelayCommand]
    public async Task DeleteRemoteAsync(SftpEntry e)
    {
        if (!IsConnected) return;
        await _sftp.DeleteAsync(e.FullPath, e.IsDirectory);
        await RefreshRemoteAsync();
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        await _sftp.DisconnectAsync();
        var entry = _mgr.FindByConnectionId(ConnectionId);
        if (entry is not null) await _mgr.CloseAsync(entry);
    }

    private void OnSessionDisconnected(object? sender, EventArgs e)
    {
        IsConnected   = false;
        StatusMessage = "Disconnected";
    }

    public async ValueTask DisposeAsync() => await _sftp.DisposeAsync();

    private readonly record struct TransferRequest(
        SftpTransferDirection Direction,
        string LocalPath,
        string RemotePath,
        long TotalSize);
}
