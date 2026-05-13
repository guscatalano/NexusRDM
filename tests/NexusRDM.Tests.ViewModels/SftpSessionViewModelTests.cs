using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;
using NexusRDM.Tests.ViewModels.Fakes;
using NexusRDM.ViewModels;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Tests the SFTP file-manager VM. Covers the transfer queue,
/// recursive walks, conflict-resolution flow (OnConflictAsk + apply-
/// to-all), remote/local listing, delete, byte/text reads. The fake
/// session captures every call so we can assert exact wire-level
/// behaviour without spinning up a network.
/// </summary>
public sealed class SftpSessionViewModelTests
{
    // ── Connect lifecycle ────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_OnSuccess_SetsState_AndFetchesHome()
    {
        var (vm, fake) = Build();
        fake.HomeDir = "/home/alice";

        await vm.ConnectAsync();

        Assert.True(vm.IsConnected);
        Assert.False(vm.IsConnecting);
        Assert.Contains("Connected", vm.StatusMessage);
        Assert.Equal("/home/alice", vm.RemotePath);
    }

    [Fact]
    public async Task ConnectAsync_OnFailure_SetsFailedStatus()
    {
        var fake = new FakeSftpSession();
        // Force the connect to throw — easiest by re-wrapping connect.
        var vm = new SftpSessionViewModel(MakeProfile(), new ThrowingSftp("auth bombed"), new SessionManager());

        await vm.ConnectAsync();

        Assert.False(vm.IsConnected);
        Assert.StartsWith("Failed", vm.StatusMessage);
        Assert.Contains("auth bombed", vm.StatusMessage);
    }

    // ── Remote listing & navigation ──────────────────────────────────

    [Fact]
    public async Task RefreshRemote_SortsFoldersBeforeFiles_AndAlpha()
    {
        var (vm, fake) = await ConnectedAsync();
        fake.RemoteTree["/home/demo"] = new()
        {
            Entry("zeta.txt", false),
            Entry("alpha", true),
            Entry("beta.txt", false),
            Entry("Aardvark", true),
        };

        await vm.RefreshRemoteAsync();

        Assert.Equal(new[] { "Aardvark", "alpha", "beta.txt", "zeta.txt" },
            vm.RemoteEntries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public async Task NavigateRemote_DotDot_GoesToParent()
    {
        var (vm, fake) = await ConnectedAsync();
        vm.RemotePath = "/home/demo/sub";
        fake.RemoteTree["/home/demo"] = new() { Entry("file.txt", false) };

        await vm.NavigateRemoteAsync(new SftpEntry("..", "/home/demo", true, false, 0, DateTimeOffset.UtcNow, 0));

        Assert.Equal("/home/demo", vm.RemotePath);
    }

    [Fact]
    public async Task NavigateRemote_DotDot_AtRoot_StaysAtRoot()
    {
        var (vm, fake) = await ConnectedAsync();
        vm.RemotePath = "/";

        await vm.NavigateRemoteAsync(new SftpEntry("..", "/", true, false, 0, DateTimeOffset.UtcNow, 0));

        Assert.Equal("/", vm.RemotePath);
    }

    // ── Single-file transfer queue ───────────────────────────────────

    [Fact]
    public async Task EnqueueUpload_PushesFile_ThroughFakeSession()
    {
        var (vm, fake) = await ConnectedAsync();
        vm.LocalPath  = "C:\\dev";
        vm.RemotePath = "/srv/upload";
        var local = new SftpEntry("report.pdf", "C:\\dev\\report.pdf", false, false,
            Size: 1234, DateTimeOffset.UtcNow, 0);

        // The pump opens File.OpenRead on the path — make a real temp
        // file so the upload's stream open succeeds.
        var tempFile = WriteTempFile("hello-upload");
        local = local with { FullPath = tempFile };

        vm.EnqueueUpload(local);
        await WaitForQueueDrainAsync(vm);

        Assert.Single(fake.Uploads);
        // EnqueueUpload uses entry.Name for the remote destination,
        // not the full local path basename. We kept the entry's Name
        // as "report.pdf" but pointed FullPath at the temp file.
        Assert.Equal("/srv/upload/report.pdf", fake.Uploads[0].Path);
    }

    [Fact]
    public async Task EnqueueDownload_PullsFile_ToLocalPath()
    {
        var (vm, fake) = await ConnectedAsync();
        var tempDir = Directory.CreateTempSubdirectory("nexus-sftp-test-").FullName;
        vm.LocalPath = tempDir;

        var remote = new SftpEntry("data.json", "/etc/data.json", false, false, 99,
            DateTimeOffset.UtcNow, 0);
        vm.EnqueueDownload(remote);
        await WaitForQueueDrainAsync(vm);

        Assert.Single(fake.Downloads);
        Assert.Equal("/etc/data.json", fake.Downloads[0]);
        Assert.True(File.Exists(Path.Combine(tempDir, "data.json")));
    }

    [Fact]
    public async Task Queue_TracksTotalsAndDone_Across_MultipleFiles()
    {
        var (vm, fake) = await ConnectedAsync();
        var tempDir = Directory.CreateTempSubdirectory("nexus-sftp-test-").FullName;
        vm.LocalPath  = tempDir;
        vm.RemotePath = "/dst";

        var a = MakeLocalEntry(WriteTempFile("a"));
        var b = MakeLocalEntry(WriteTempFile("bb"));
        var c = MakeLocalEntry(WriteTempFile("ccc"));
        vm.EnqueueUpload(a);
        vm.EnqueueUpload(b);
        vm.EnqueueUpload(c);
        await WaitForQueueDrainAsync(vm);

        Assert.Equal(3, fake.Uploads.Count);
        // QueueTotalFiles / QueueDoneFiles reset on drain so the next
        // single-file drop starts at 0/1.
        Assert.Equal(0, vm.QueueTotalFiles);
        Assert.Equal(0, vm.QueueDoneFiles);
    }

    // ── Conflict resolution ──────────────────────────────────────────

    [Fact]
    public async Task Conflict_Overwrite_StillUploads()
    {
        var (vm, fake) = await ConnectedAsync();
        var temp = WriteTempFile("data");
        var dest = $"/srv/{Path.GetFileName(temp)}";
        fake.RemoteExists[dest] = true;
        vm.OnConflictAsk = (_, _) => Task.FromResult(SftpSessionViewModel.ConflictChoice.Overwrite);
        vm.RemotePath = "/srv";

        vm.EnqueueUpload(MakeLocalEntry(temp));
        await WaitForQueueDrainAsync(vm);

        Assert.Single(fake.Uploads);
    }

    [Fact]
    public async Task Conflict_Skip_DoesNotUpload()
    {
        var (vm, fake) = await ConnectedAsync();
        var temp = WriteTempFile("data");
        var dest = $"/srv/{Path.GetFileName(temp)}";
        fake.RemoteExists[dest] = true;
        vm.OnConflictAsk = (_, _) => Task.FromResult(SftpSessionViewModel.ConflictChoice.Skip);
        vm.RemotePath = "/srv";

        vm.EnqueueUpload(MakeLocalEntry(temp));
        await WaitForQueueDrainAsync(vm);

        Assert.Empty(fake.Uploads);
    }

    [Fact]
    public async Task Conflict_OverwriteAll_AppliesSilentlyToFurtherFiles()
    {
        var (vm, fake) = await ConnectedAsync();
        var t1 = WriteTempFile("one"); var t2 = WriteTempFile("two"); var t3 = WriteTempFile("three");
        foreach (var p in new[] { t1, t2, t3 })
            fake.RemoteExists[$"/srv/{Path.GetFileName(p)}"] = true;
        vm.RemotePath = "/srv";

        int prompted = 0;
        vm.OnConflictAsk = (_, _) =>
        {
            prompted++;
            return Task.FromResult(SftpSessionViewModel.ConflictChoice.OverwriteAll);
        };

        vm.EnqueueUpload(MakeLocalEntry(t1));
        vm.EnqueueUpload(MakeLocalEntry(t2));
        vm.EnqueueUpload(MakeLocalEntry(t3));
        await WaitForQueueDrainAsync(vm);

        Assert.Equal(1, prompted);      // asked once
        Assert.Equal(3, fake.Uploads.Count); // all three still uploaded
    }

    [Fact]
    public async Task Conflict_Cancel_DropsRestOfQueue()
    {
        var (vm, fake) = await ConnectedAsync();
        var t1 = WriteTempFile("one"); var t2 = WriteTempFile("two"); var t3 = WriteTempFile("three");
        foreach (var p in new[] { t1, t2, t3 })
            fake.RemoteExists[$"/srv/{Path.GetFileName(p)}"] = true;
        vm.RemotePath = "/srv";

        vm.OnConflictAsk = (_, _) =>
            Task.FromResult(SftpSessionViewModel.ConflictChoice.Cancel);

        vm.EnqueueUpload(MakeLocalEntry(t1));
        vm.EnqueueUpload(MakeLocalEntry(t2));
        vm.EnqueueUpload(MakeLocalEntry(t3));
        await WaitForQueueDrainAsync(vm);

        // Each Cancel drops the current batch's queue and unwinds the
        // pump. With a synchronous fake, each EnqueueUpload runs its
        // own end-to-end pump before the next call lands, so we don't
        // assert on prompt-count (that depends on whether the I/O
        // yields between enqueues). What matters: no file was
        // overwritten.
        Assert.Empty(fake.Uploads);
    }

    [Fact]
    public async Task Conflict_OverwriteAll_PersistsAcrossSeparateBatches()
    {
        // User explicit ask: dialog should not re-prompt after a
        // single-file drag if they checked "apply to all" earlier.
        // Each Enqueue is a single-file batch but the override is
        // sticky for the tab.
        var (vm, fake) = await ConnectedAsync();
        var t1 = WriteTempFile("one"); var t2 = WriteTempFile("two");
        foreach (var p in new[] { t1, t2 })
            fake.RemoteExists[$"/srv/{Path.GetFileName(p)}"] = true;
        vm.RemotePath = "/srv";

        int prompted = 0;
        vm.OnConflictAsk = (_, _) =>
        {
            prompted++;
            return Task.FromResult(SftpSessionViewModel.ConflictChoice.OverwriteAll);
        };

        vm.EnqueueUpload(MakeLocalEntry(t1));
        await WaitForQueueDrainAsync(vm);
        vm.EnqueueUpload(MakeLocalEntry(t2));
        await WaitForQueueDrainAsync(vm);

        Assert.Equal(1, prompted); // only the first batch asked
        Assert.Equal(2, fake.Uploads.Count);
    }

    // ── Recursive walks ──────────────────────────────────────────────

    [Fact]
    public async Task EnqueueDownloadDirectoryAsync_EnqueuesEveryFileInTree()
    {
        var (vm, fake) = await ConnectedAsync();
        var tempDir = Directory.CreateTempSubdirectory("nexus-sftp-test-").FullName;
        vm.LocalPath = tempDir;
        fake.RemoteTree["/srv/dir"] = new()
        {
            Entry("a.txt", false, "/srv/dir"),
            Entry("sub",   true,  "/srv/dir"),
        };
        fake.RemoteTree["/srv/dir/sub"] = new()
        {
            Entry("b.txt", false, "/srv/dir/sub"),
            Entry("c.txt", false, "/srv/dir/sub"),
        };

        var root = new SftpEntry("dir", "/srv/dir", true, false, 0, DateTimeOffset.UtcNow, 0);
        await vm.EnqueueDownloadDirectoryAsync(root);
        await WaitForQueueDrainAsync(vm);

        Assert.Equal(3, fake.Downloads.Count);
        Assert.Contains("/srv/dir/a.txt",     fake.Downloads);
        Assert.Contains("/srv/dir/sub/b.txt", fake.Downloads);
        Assert.Contains("/srv/dir/sub/c.txt", fake.Downloads);
    }

    [Fact]
    public async Task EnqueueUploadDirectoryAsync_CreatesRemoteDirs_AndUploadsEveryFile()
    {
        var (vm, fake) = await ConnectedAsync();
        // Build a local tree:
        //   tmp/proj/file1.txt
        //   tmp/proj/sub/file2.txt
        var local = Directory.CreateTempSubdirectory("nexus-sftp-test-").FullName;
        var proj  = Path.Combine(local, "proj");
        var sub   = Path.Combine(proj, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(proj, "file1.txt"), "1");
        File.WriteAllText(Path.Combine(sub,  "file2.txt"), "22");
        vm.RemotePath = "/dst";

        var rootEntry = new SftpEntry("proj", proj, true, false, 0, DateTimeOffset.UtcNow, 0);
        await vm.EnqueueUploadDirectoryAsync(rootEntry);
        await WaitForQueueDrainAsync(vm);

        Assert.Contains("/dst/proj",     fake.CreatedDirs);
        Assert.Contains("/dst/proj/sub", fake.CreatedDirs);
        Assert.Equal(2, fake.Uploads.Count);
        Assert.Contains(fake.Uploads, u => u.Path == "/dst/proj/file1.txt");
        Assert.Contains(fake.Uploads, u => u.Path == "/dst/proj/sub/file2.txt");
    }

    // ── Local delete ─────────────────────────────────────────────────

    [Fact]
    public async Task DeleteLocalAsync_File_RemovesFromDisk()
    {
        var (vm, _) = await ConnectedAsync();
        var path = WriteTempFile("delete-me");
        Assert.True(File.Exists(path));
        var entry = MakeLocalEntry(path);

        await vm.DeleteLocalAsync(entry);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteLocalAsync_RefusesDotDot()
    {
        var (vm, _) = await ConnectedAsync();
        var dotdot = new SftpEntry("..", "C:\\windows", true, false, 0, DateTimeOffset.UtcNow, 0);

        await vm.DeleteLocalAsync(dotdot);

        // Should NOT touch C:\windows — refuses silently because ".."
        // is the up-row sentinel.
        Assert.True(Directory.Exists("C:\\Windows"));
    }

    // ── Remote read paths ────────────────────────────────────────────

    [Fact]
    public async Task ReadRemoteTextAsync_ReturnsDecodedString()
    {
        var (vm, _) = await ConnectedAsync();
        var entry = new SftpEntry("config.ini", "/etc/config.ini", false, false,
            Size: 100, DateTimeOffset.UtcNow, 0);

        var text = await vm.ReadRemoteTextAsync(entry);

        Assert.NotNull(text);
        Assert.Contains("contents:/etc/config.ini", text);
    }

    [Fact]
    public async Task ReadRemoteTextAsync_RefusesOversize()
    {
        var (vm, _) = await ConnectedAsync();
        var huge = new SftpEntry("huge.bin", "/etc/huge.bin", false, false,
            Size: SftpSessionViewModel.PreviewMaxBytes + 1, DateTimeOffset.UtcNow, 0);

        var text = await vm.ReadRemoteTextAsync(huge);

        Assert.Null(text);
    }

    [Fact]
    public async Task ReadRemoteBytesAsync_RespectsMaxBytes()
    {
        var (vm, _) = await ConnectedAsync();
        var entry = new SftpEntry("big.img", "/big.img", false, false,
            Size: 5 * 1024 * 1024, DateTimeOffset.UtcNow, 0);

        var bytes4mb = await vm.ReadRemoteBytesAsync(entry, 4 * 1024 * 1024);
        var bytes10mb = await vm.ReadRemoteBytesAsync(entry, 10 * 1024 * 1024);

        Assert.Null(bytes4mb);      // 5MB file > 4MB cap → refused
        Assert.NotNull(bytes10mb);  // 5MB file < 10MB cap → returned
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (SftpSessionViewModel vm, FakeSftpSession fake) Build()
    {
        var fake = new FakeSftpSession();
        var vm   = new SftpSessionViewModel(MakeProfile(), fake, new SessionManager());
        return (vm, fake);
    }

    private static async Task<(SftpSessionViewModel vm, FakeSftpSession fake)> ConnectedAsync()
    {
        var (vm, fake) = Build();
        await vm.ConnectAsync();
        return (vm, fake);
    }

    private static ConnectionProfile MakeProfile() => new()
    {
        Id          = Guid.NewGuid(),
        DisplayName = "test",
        Host        = "example.com",
        Port        = 22,
        Protocol    = ConnectionProtocol.Ssh,
    };

    /// <summary>The transfer queue runs asynchronously. We need to
    /// wait for it to drain — poll QueueDepth + the pump-running
    /// flag (via observed state) for a bounded interval.</summary>
    private static async Task WaitForQueueDrainAsync(SftpSessionViewModel vm)
    {
        // Wait up to 5 seconds for the queue to fully drain.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (vm.QueueDepth == 0 && vm.TransferStatus == "Idle") return;
            await Task.Delay(20);
        }
        // Best-effort — don't fail the test outright; the assertions
        // following will catch the actual missing behaviour.
    }

    private static SftpEntry MakeLocalEntry(string path) =>
        new SftpEntry(Path.GetFileName(path), path, false, false,
            new FileInfo(path).Length, new DateTimeOffset(File.GetLastWriteTime(path)), 0);

    private static string WriteTempFile(string content)
    {
        var p = Path.Combine(Path.GetTempPath(), "nexus-sftp-test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
        File.WriteAllText(p, content);
        return p;
    }

    private static SftpEntry Entry(string name, bool isDir, string parentPath = "/home/demo") =>
        new SftpEntry(
            name,
            parentPath.TrimEnd('/') + "/" + name,
            isDir, false,
            isDir ? 0 : 100, DateTimeOffset.UtcNow, 0);

    /// <summary>Wrapper used by ConnectAsync_OnFailure_SetsFailedStatus —
    /// ConnectAsync on a fresh fake throws.</summary>
    private sealed class ThrowingSftp : ISftpSession
    {
        private readonly string _message;
        public ThrowingSftp(string message) => _message = message;

        public Guid   ConnectionId { get; } = Guid.NewGuid();
        public bool   IsConnected  => false;
        public string Username     => "";
        public event EventHandler? Disconnected { add { } remove { } }
        public event EventHandler? Connected    { add { } remove { } }
        public event EventHandler<SftpTransferEventArgs>? TransferCompleted { add { } remove { } }
        public Task ConnectAsync(CancellationToken ct = default) => throw new InvalidOperationException(_message);
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<string> GetHomeDirectoryAsync(CancellationToken ct = default) => Task.FromResult("/");
        public Task<IReadOnlyList<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SftpEntry>>(Array.Empty<SftpEntry>());
        public Task<long> DownloadFileAsync(string remotePath, Stream output, IProgress<long>? progress = null, CancellationToken ct = default) => Task.FromResult<long>(0);
        public Task<long> UploadFileAsync(Stream input, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default) => Task.FromResult<long>(0);
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameAsync(string from, string to, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DateTimeOffset?> GetRemoteMTimeAsync(string path, CancellationToken ct = default) => Task.FromResult<DateTimeOffset?>(null);
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
