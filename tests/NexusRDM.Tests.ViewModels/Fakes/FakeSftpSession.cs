using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Tests.ViewModels.Fakes;

/// <summary>
/// Test double for <see cref="ISftpSession"/>. Records every Upload /
/// Download / mkdir / delete / rename / exists call so tests can
/// assert the VM dispatched the right operations in the right order.
/// Listings come from a pre-loaded in-memory tree; tests build the
/// tree, hand the fake to the VM, and inspect call records after.
/// </summary>
public sealed class FakeSftpSession : ISftpSession
{
    public Guid   ConnectionId { get; } = Guid.NewGuid();
    public bool   IsConnected  { get; private set; }
    public string Username     { get; set; } = "demo";

    public event EventHandler? Disconnected;
    public event EventHandler<SftpTransferEventArgs>? TransferCompleted;

    /// <summary>Pre-loaded "remote" tree. Key = full path, value =
    /// child entries inside that directory. Tests populate before
    /// passing the fake to the VM.</summary>
    public Dictionary<string, List<SftpEntry>> RemoteTree { get; } = new();

    /// <summary>Pre-loaded mtimes for GetRemoteMTimeAsync. Default
    /// (path not in dict) returns null.</summary>
    public Dictionary<string, DateTimeOffset?> RemoteMTimes { get; } = new();

    /// <summary>Pre-loaded existence map for ExistsAsync. Default
    /// (path not in dict) returns false.</summary>
    public Dictionary<string, bool> RemoteExists { get; } = new();

    public string HomeDir { get; set; } = "/home/demo";

    // Call records — tests assert against these.
    public List<(string Path, byte[] Bytes)> Uploads     { get; } = new();
    public List<string>                      Downloads   { get; } = new();
    public List<string>                      CreatedDirs { get; } = new();
    public List<string>                      Deleted     { get; } = new();
    public List<(string From, string To)>    Renamed     { get; } = new();
    public List<string>                      Existed     { get; } = new();
    public List<string>                      Listed      { get; } = new();
    public List<string>                      MTimedAt    { get; } = new();

    /// <summary>Optional hook for "this upload should pretend to
    /// fail" — keyed by remote path. Each entry pops on use.</summary>
    public Dictionary<string, Exception> UploadFailures   { get; } = new();
    public Dictionary<string, Exception> DownloadFailures { get; } = new();

    public Task ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<string> GetHomeDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(HomeDir);

    public Task<IReadOnlyList<SftpEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        Listed.Add(path);
        return Task.FromResult<IReadOnlyList<SftpEntry>>(
            RemoteTree.TryGetValue(path, out var entries) ? entries : new List<SftpEntry>());
    }

    public Task<long> DownloadFileAsync(
        string remotePath, Stream output,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        Downloads.Add(remotePath);
        if (DownloadFailures.TryGetValue(remotePath, out var ex)) throw ex;
        // Write a recognisable marker so callers (e.g. ReadRemoteTextAsync
        // tests) can verify the bytes flowed through.
        var data = System.Text.Encoding.UTF8.GetBytes($"contents:{remotePath}");
        output.Write(data, 0, data.Length);
        TransferCompleted?.Invoke(this, new SftpTransferEventArgs
        {
            Direction = SftpTransferDirection.Download,
            LocalPath = (output as FileStream)?.Name ?? "(stream)",
            RemotePath = remotePath,
            Bytes = data.Length,
            Elapsed = TimeSpan.Zero,
            Success = true,
        });
        return Task.FromResult<long>(data.Length);
    }

    public Task<long> UploadFileAsync(
        Stream input, string remotePath,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (UploadFailures.TryGetValue(remotePath, out var ex)) throw ex;
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        Uploads.Add((remotePath, ms.ToArray()));
        TransferCompleted?.Invoke(this, new SftpTransferEventArgs
        {
            Direction = SftpTransferDirection.Upload,
            LocalPath = (input as FileStream)?.Name ?? "(stream)",
            RemotePath = remotePath,
            Bytes = ms.Length,
            Elapsed = TimeSpan.Zero,
            Success = true,
        });
        return Task.FromResult<long>(ms.Length);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        CreatedDirs.Add(path);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct = default)
    {
        Deleted.Add(path);
        return Task.CompletedTask;
    }

    public Task RenameAsync(string fromPath, string toPath, CancellationToken ct = default)
    {
        Renamed.Add((fromPath, toPath));
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetRemoteMTimeAsync(string path, CancellationToken ct = default)
    {
        MTimedAt.Add(path);
        return Task.FromResult(RemoteMTimes.TryGetValue(path, out var t) ? t : null);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        Existed.Add(path);
        return Task.FromResult(RemoteExists.TryGetValue(path, out var v) && v);
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
