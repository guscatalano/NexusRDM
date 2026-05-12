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
    private ConflictChoice? _appliedToRemaining;

    public enum ConflictChoice
    {
        Overwrite,
        Skip,
        Cancel,
        OverwriteAll,
        SkipAll,
    }

    /// <summary>Hook the View installs to ask the user how to resolve
    /// a destination-already-exists conflict at transfer time. Runs on
    /// the UI thread (caller hops via DispatcherQueue if needed).
    /// Null = silent overwrite (matches the pre-MVP behaviour).</summary>
    public Func<string /* destPath */, bool /* isRemoteDest */, Task<ConflictChoice>>? OnConflictAsk { get; set; }

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

    // Aggregate progress across the current batch (queue lifetime).
    // Reset to zero when the queue drains. Lets the status bar show
    // "23/147 files · 12.5 / 89 MB" so a recursive copy of 200 files
    // isn't just "Upload foo.txt · queue: 199".
    [ObservableProperty] private int    _queueDoneFiles;
    [ObservableProperty] private int    _queueTotalFiles;
    [ObservableProperty] private long   _queueDoneBytes;
    [ObservableProperty] private long   _queueTotalBytes;
    [ObservableProperty] private string _batchProgress = string.Empty;

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
        // Same order rule as the remote pane: ".." first, then
        // directories alphabetically, then files alphabetically.
        snapshot = snapshot
            .OrderByDescending(e => e.Name == "..")
            .ThenByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        if (remote.IsDirectory) return; // dir download path is EnqueueDownloadDirectoryAsync
        var localDest = Path.Combine(LocalPath, remote.Name);
        EnqueueOne(new TransferRequest(SftpTransferDirection.Download, localDest, remote.FullPath, remote.Size));
        _ = PumpQueueAsync();
    }

    public void EnqueueUpload(SftpEntry local)
    {
        if (local.IsDirectory) return;
        var remoteDest = RemotePath.TrimEnd('/') + "/" + local.Name;
        EnqueueOne(new TransferRequest(SftpTransferDirection.Upload, local.FullPath, remoteDest, local.Size));
        _ = PumpQueueAsync();
    }

    /// <summary>Single funnel for adding to the transfer queue.
    /// Maintains <see cref="QueueTotalFiles"/> + <see cref="QueueTotalBytes"/>
    /// so the status bar can show batch-wide aggregates regardless of
    /// whether the enqueue came from a recursive walk or a single
    /// drag-and-drop.</summary>
    private void EnqueueOne(TransferRequest req)
    {
        _queue.Enqueue(req);
        QueueTotalFiles++;
        QueueTotalBytes += Math.Max(0, req.TotalSize);
        QueueDepth = _queue.Count;
        UpdateBatchProgress();
    }

    private void UpdateBatchProgress()
    {
        if (QueueTotalFiles == 0)
        {
            BatchProgress = string.Empty;
            return;
        }
        BatchProgress =
            $"{QueueDoneFiles}/{QueueTotalFiles} files · " +
            $"{FormatBytes(QueueDoneBytes)} / {FormatBytes(QueueTotalBytes)}";
    }

    private static string FormatBytes(long n)
    {
        if (n < 1024)        return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024.0:F1} KB";
        if (n < 1024L * 1024 * 1024) return $"{n / (1024.0 * 1024):F1} MB";
        return $"{n / (1024.0 * 1024 * 1024):F2} GB";
    }

    /// <summary>Recursive download. Walk the remote tree rooted at
    /// <paramref name="remoteDir"/>, mirror its structure under the
    /// local pane's current path, and enqueue every file as an
    /// individual transfer. Skips symlinks (to avoid loops) and
    /// silently logs (via SshLog.Warn) any directory whose listing
    /// fails — typically permissions.</summary>
    public async Task EnqueueDownloadDirectoryAsync(SftpEntry remoteDir)
    {
        if (!IsConnected || !remoteDir.IsDirectory) return;
        var localRoot = System.IO.Path.Combine(LocalPath, remoteDir.Name);
        await WalkRemoteAsync(remoteDir.FullPath, localRoot);
        _ = PumpQueueAsync();
    }

    private async Task WalkRemoteAsync(string remotePath, string localPath)
    {
        try { System.IO.Directory.CreateDirectory(localPath); }
        catch (Exception ex)
        {
            NexusRDM.Core.Diagnostics.SshLog.Warn($"WalkRemote mkdir local failed: {localPath} — {ex.Message}");
            return;
        }
        IReadOnlyList<SftpEntry> entries;
        try { entries = await _sftp.ListDirectoryAsync(remotePath); }
        catch (Exception ex)
        {
            NexusRDM.Core.Diagnostics.SshLog.Warn($"WalkRemote list failed: {remotePath} — {ex.Message}");
            return;
        }
        foreach (var e in entries)
        {
            if (e.Name == "." || e.Name == "..") continue;
            if (e.IsSymlink) continue; // don't follow — could be a cycle
            var localChild = System.IO.Path.Combine(localPath, e.Name);
            if (e.IsDirectory)
            {
                await WalkRemoteAsync(e.FullPath, localChild);
            }
            else
            {
                EnqueueOne(new TransferRequest(
                    SftpTransferDirection.Download, localChild, e.FullPath, e.Size));
            }
        }
    }

    /// <summary>Recursive upload. Walk the local tree rooted at
    /// <paramref name="localDir"/>, mirror its structure under the
    /// remote pane's current path, and enqueue every file. Creates
    /// missing remote directories on the way down.</summary>
    public async Task EnqueueUploadDirectoryAsync(SftpEntry localDir)
    {
        if (!IsConnected || !localDir.IsDirectory) return;
        var remoteRoot = RemotePath.TrimEnd('/') + "/" + localDir.Name;
        try { await _sftp.CreateDirectoryAsync(remoteRoot); }
        catch { /* may already exist */ }
        await WalkLocalAsync(localDir.FullPath, remoteRoot);
        _ = PumpQueueAsync();
    }

    private async Task WalkLocalAsync(string localPath, string remotePath)
    {
        try
        {
            foreach (var sub in System.IO.Directory.EnumerateDirectories(localPath))
            {
                var name      = System.IO.Path.GetFileName(sub);
                var remoteSub = remotePath + "/" + name;
                try { await _sftp.CreateDirectoryAsync(remoteSub); } catch { /* exists */ }
                await WalkLocalAsync(sub, remoteSub);
            }
            foreach (var file in System.IO.Directory.EnumerateFiles(localPath))
            {
                var name = System.IO.Path.GetFileName(file);
                long size = 0;
                try { size = new System.IO.FileInfo(file).Length; } catch { }
                EnqueueOne(new TransferRequest(
                    SftpTransferDirection.Upload, file, remotePath + "/" + name, size));
            }
        }
        catch (Exception ex)
        {
            NexusRDM.Core.Diagnostics.SshLog.Warn($"WalkLocal failed: {localPath} — {ex.Message}");
        }
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
                QueueDoneFiles++;
                QueueDoneBytes += Math.Max(0, req.TotalSize);
                UpdateBatchProgress();
            }
            TransferStatus  = "Idle";
            TransferPercent = 0;
            // Reset batch aggregates so the next single-file drop
            // starts from 0/1, not N/N+1.
            //
            // NOTE: we deliberately do NOT reset _appliedToRemaining
            // here. The user's complaint was "the dialog keeps popping
            // up for every file" — that's because each single drag
            // was a 1-file batch and the apply-to-all only stuck for
            // the current batch. Keeping the choice sticky across
            // batches means "Apply to remaining" really means "for
            // this tab, until I close it or restart."
            QueueDoneFiles      = 0;
            QueueTotalFiles     = 0;
            QueueDoneBytes      = 0;
            QueueTotalBytes     = 0;
            UpdateBatchProgress();
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

        // Pre-flight: does the destination already exist? If yes, ask
        // the user (or apply the previously-chosen "all" answer). Only
        // ask when a handler is installed — without one, we preserve
        // the silent-overwrite behaviour callers might depend on.
        if (OnConflictAsk is not null)
        {
            bool destExists = req.Direction == SftpTransferDirection.Upload
                ? await _sftp.ExistsAsync(req.RemotePath)
                : System.IO.File.Exists(req.LocalPath);
            if (destExists)
            {
                ConflictChoice choice;
                if (_appliedToRemaining is ConflictChoice.OverwriteAll) choice = ConflictChoice.Overwrite;
                else if (_appliedToRemaining is ConflictChoice.SkipAll)  choice = ConflictChoice.Skip;
                else
                {
                    var destForPrompt = req.Direction == SftpTransferDirection.Upload
                        ? req.RemotePath : req.LocalPath;
                    choice = await OnConflictAsk(destForPrompt, req.Direction == SftpTransferDirection.Upload);
                    if (choice == ConflictChoice.OverwriteAll || choice == ConflictChoice.SkipAll)
                        _appliedToRemaining = choice;
                }
                switch (choice)
                {
                    case ConflictChoice.Skip:
                    case ConflictChoice.SkipAll:
                        TransferStatus = $"Skipped: {Path.GetFileName(req.RemotePath)}";
                        return;
                    case ConflictChoice.Cancel:
                        // Drop the rest of the queue so the user isn't
                        // asked again for every remaining conflict.
                        _queue.Clear();
                        QueueDepth = 0;
                        TransferStatus = "Cancelled.";
                        return;
                }
            }
        }
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

    /// <summary>Stream a remote file into a byte array. Used by the
    /// image / hex preview paths — never writes to disk. Returns null
    /// when the file exceeds <paramref name="maxBytes"/>, on read
    /// failure, or when called against a directory.</summary>
    public async Task<byte[]?> ReadRemoteBytesAsync(SftpEntry entry, long maxBytes, CancellationToken ct = default)
    {
        if (!IsConnected || entry.IsDirectory) return null;
        if (entry.Size > maxBytes)             return null;
        var ms = new System.IO.MemoryStream(capacity: (int)Math.Min(entry.Size, 64 * 1024));
        try { await _sftp.DownloadFileAsync(entry.FullPath, ms, progress: null, ct); }
        catch { return null; }
        return ms.ToArray();
    }

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

    /// <summary>Delete a local file or directory. Symmetric with the
    /// existing remote-delete; only used by the local-pane right-click
    /// menu. No undo / recycle bin in v1 — straight to permanent
    /// deletion, with confirmation handled by the View.</summary>
    [RelayCommand]
    public async Task DeleteLocalAsync(SftpEntry e)
    {
        if (e.Name == "..") return;
        await Task.Run(() =>
        {
            try
            {
                if (e.IsDirectory) System.IO.Directory.Delete(e.FullPath, recursive: true);
                else               System.IO.File.Delete(e.FullPath);
            }
            catch (Exception ex)
            {
                NexusRDM.Core.Diagnostics.SshLog.Warn($"DeleteLocal failed: {e.FullPath} — {ex.Message}");
            }
        });
        await RefreshLocalAsync();
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

    // ── Edit in place ────────────────────────────────────────────────
    //
    // Workflow: download remote file to a per-session %TEMP%
    // directory; Process.Start it (shell verb open, so it uses the
    // user's default editor); a FileSystemWatcher watches the file
    // for writes. On change (debounced 500ms because most editors
    // emit several Change events per save) we re-upload. The
    // EditSession stays live until either the user closes the SFTP
    // tab (DisposeAsync sweeps them) or they invoke "Stop editing"
    // from the right-click menu.

    private readonly Dictionary<string, EditSession> _activeEdits = new();

    /// <summary>Begin an edit-in-place session for <paramref name="remote"/>.
    /// Downloads to %TEMP%, opens in the user's default editor,
    /// starts watching for saves. Re-entrant: a second call against
    /// the same remote path just re-launches the editor pointed at
    /// the existing temp file.</summary>
    public async Task<EditSession?> BeginEditInPlaceAsync(SftpEntry remote)
    {
        if (!IsConnected || remote.IsDirectory) return null;

        // Re-use an existing session if already editing this file.
        if (_activeEdits.TryGetValue(remote.FullPath, out var existing))
        {
            existing.LaunchEditor();
            return existing;
        }

        var tempDir  = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "NexusRDM-edit",
            ConnectionId.ToString("N"),
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        var tempPath = System.IO.Path.Combine(tempDir, remote.Name);

        // Initial download. The watcher attaches AFTER this completes
        // so we don't fire a spurious upload on our own write.
        try
        {
            using var fs = System.IO.File.Create(tempPath);
            await _sftp.DownloadFileAsync(remote.FullPath, fs);
        }
        catch (Exception ex)
        {
            NexusRDM.Core.Diagnostics.SshLog.Warn($"Edit-in-place download failed: {remote.FullPath} — {ex.Message}");
            try { System.IO.Directory.Delete(tempDir, recursive: true); } catch { }
            return null;
        }

        var session = new EditSession(this, remote.FullPath, tempPath);
        // Snapshot the remote mtime right after download so the
        // conflict check has a meaningful baseline. If the stat fails
        // we leave baseline null → no conflict detection (best effort,
        // matches "ignore if server doesn't support stat" behaviour).
        session.BaselineMTime = await _sftp.GetRemoteMTimeAsync(remote.FullPath);
        _activeEdits[remote.FullPath] = session;
        session.Start();
        return session;
    }

    /// <summary>Stop an edit-in-place session. Cleans up the watcher
    /// + best-effort deletes the temp file. Doesn't try to close the
    /// user's editor — we don't own that process beyond launching it.</summary>
    public void StopEditInPlace(string remotePath)
    {
        if (_activeEdits.Remove(remotePath, out var session))
            session.Dispose();
    }

    public IReadOnlyDictionary<string, EditSession> ActiveEdits => _activeEdits;

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _activeEdits.Values.ToList())
            s.Dispose();
        _activeEdits.Clear();
        await _sftp.DisposeAsync();
    }

    public sealed class EditSession : IDisposable
    {
        private readonly SftpSessionViewModel _owner;
        private System.IO.FileSystemWatcher?  _watcher;
        private System.Threading.Timer?       _debounce;
        private bool                          _uploading;
        private bool                          _disposed;

        public string RemotePath { get; }
        public string TempPath   { get; }

        /// <summary>The remote file's mtime when we downloaded it.
        /// Compared against the live remote mtime before each
        /// re-upload to detect "someone else edited the file on the
        /// server while you were editing locally" conflicts. Null
        /// when the initial stat failed (no conflict detection).</summary>
        public DateTimeOffset? BaselineMTime { get; internal set; }

        /// <summary>Conflict-resolution callback. Invoked on the
        /// threadpool when a conflict is detected; the implementation
        /// is responsible for hopping to the UI thread to prompt the
        /// user. Return true to overwrite the server version, false
        /// to skip this upload (the watcher stays attached and the
        /// next save can prompt again). Null = silent overwrite
        /// (matches v1 behaviour).</summary>
        public Func<DateTimeOffset, Task<bool>>? OnConflictDetected { get; set; }

        internal EditSession(SftpSessionViewModel owner, string remotePath, string tempPath)
        {
            _owner     = owner;
            RemotePath = remotePath;
            TempPath   = tempPath;
        }

        internal void Start()
        {
            var dir = System.IO.Path.GetDirectoryName(TempPath)!;
            var name = System.IO.Path.GetFileName(TempPath);
            _watcher = new System.IO.FileSystemWatcher(dir, name)
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnLocalChanged;
            LaunchEditor();
        }

        internal void LaunchEditor()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = TempPath,
                    UseShellExecute = true, // opens with user's default app for that extension
                });
            }
            catch (Exception ex)
            {
                NexusRDM.Core.Diagnostics.SshLog.Warn($"Edit-in-place launch failed: {TempPath} — {ex.Message}");
            }
        }

        private void OnLocalChanged(object sender, System.IO.FileSystemEventArgs e)
        {
            // Editors typically save in a flurry — Word writes a .tmp,
            // renames, touches mtime, etc. Debounce so we re-upload
            // once after the burst settles instead of every event.
            _debounce?.Dispose();
            _debounce = new System.Threading.Timer(_ => _ = UploadAsync(), null, 500, System.Threading.Timeout.Infinite);
        }

        private async Task UploadAsync()
        {
            if (_disposed || _uploading) return;
            _uploading = true;
            try
            {
                // Conflict check: has the remote file changed since we
                // downloaded it? If so, hand off to the view's callback
                // to ask the user how to resolve. No callback installed
                // = silent overwrite (v1 behaviour preserved).
                if (BaselineMTime is DateTimeOffset baseline)
                {
                    var current = await _owner._sftp.GetRemoteMTimeAsync(RemotePath);
                    if (current is DateTimeOffset c && c > baseline.AddSeconds(1))
                    {
                        // 1s slop tolerates filesystem mtime quantisation
                        // (some FS round to whole seconds, the upload we
                        // just made might appear "newer" by half a second).
                        bool overwrite = true;
                        if (OnConflictDetected is not null)
                            overwrite = await OnConflictDetected(c);
                        if (!overwrite)
                        {
                            NexusRDM.Core.Diagnostics.SshLog.Info(
                                $"Edit-in-place conflict skipped: {RemotePath} (remote mtime {c:O} > baseline {baseline:O})");
                            return;
                        }
                    }
                }

                // Retry the open a few times — editors sometimes hold
                // an exclusive handle for a beat after save.
                System.IO.FileStream? fs = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { fs = System.IO.File.OpenRead(TempPath); break; }
                    catch (System.IO.IOException)
                    {
                        await Task.Delay(100);
                    }
                }
                if (fs is null) return;
                using (fs)
                {
                    await _owner._sftp.UploadFileAsync(fs, RemotePath);
                }
                // Refresh baseline so the next save doesn't re-trigger
                // the conflict path against our own upload.
                BaselineMTime = await _owner._sftp.GetRemoteMTimeAsync(RemotePath);
                NexusRDM.Core.Diagnostics.SshLog.Info($"Edit-in-place re-uploaded: {RemotePath}");
            }
            catch (Exception ex)
            {
                NexusRDM.Core.Diagnostics.SshLog.Warn($"Edit-in-place upload failed: {RemotePath} — {ex.Message}");
            }
            finally
            {
                _uploading = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watcher?.Dispose();
            _debounce?.Dispose();
            try
            {
                var dir = System.IO.Path.GetDirectoryName(TempPath)!;
                System.IO.Directory.Delete(dir, recursive: true);
            }
            catch { /* file may still be open in editor */ }
        }
    }

    private readonly record struct TransferRequest(
        SftpTransferDirection Direction,
        string LocalPath,
        string RemotePath,
        long TotalSize);
}
