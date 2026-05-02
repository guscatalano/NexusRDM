using Renci.SshNet;
using NexusRDM.Core.Interfaces;

namespace NexusRDM.Core.Protocols;

/// <summary>
/// Wraps SSH.NET SshClient + ShellStream.
/// Raw VT bytes from the shell are fired via DataReceived for
/// VtNetCore to consume and render on the UI thread.
/// </summary>
public sealed class SshSession : ISshSession
{
    private readonly SshClient        _client;
    private ShellStream?              _shell;
    private CancellationTokenSource?  _readCts;
    private uint                      _cols = 220;
    private uint                      _rows = 50;
    private bool                      _disposed;

    public Guid ConnectionId { get; }
    public bool IsConnected  => _client.IsConnected;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler?         Disconnected;

    internal SshSession(Guid connectionId, SshClient client)
    {
        ConnectionId = connectionId;
        _client      = client;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await Task.Run(_client.Connect, ct);

        var modes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint>();
        _shell    = _client.CreateShellStream("xterm-256color", _cols, _rows, 0, 0, 4096, modes);
        _readCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ReadLoopAsync(_readCts.Token);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        // The shell stream may be torn down by Disconnect/Dispose between the
        // time the user pressed a key and when this runs. Treat that as a
        // silent no-op rather than crashing the UI thread.
        if (_disposed || _shell is null || !_client.IsConnected) return;

        // SSH.NET's ShellStream overrides synchronous Write/Flush but inherits
        // the base Stream.WriteAsync/FlushAsync — those defaults deadlock here,
        // so we explicitly bounce to the sync API on the thread pool.
        var shell = _shell;
        try
        {
            await Task.Run(() =>
            {
                shell.Write(data, 0, data.Length);
                shell.Flush();
            }, ct);
        }
        catch (ObjectDisposedException) { /* stream torn down mid-write */ }
        catch (System.IO.IOException)   { /* connection dropped */ }
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        _cols = (uint)columns;
        _rows = (uint)rows;
        if (_disposed || _shell is null) return Task.CompletedTask;

        // SSH.NET 2024.2 doesn't expose a public resize on ShellStream,
        // but the underlying IChannelSession has SendWindowChangeRequest
        // (cols, rows, widthPx, heightPx). Reach in via reflection so
        // top / htop / vim / less render at the actual viewport size.
        // If a future SSH.NET release renames the field or method this
        // silently no-ops — the connection stays usable, the server just
        // believes the original PTY size.
        return Task.Run(() =>
        {
            try
            {
                var shell = _shell;
                if (shell is null) return;
                var channelField = typeof(ShellStream).GetField("_channel",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var channel = channelField?.GetValue(shell);
                if (channel is null) return;

                var method = channel.GetType().GetMethod(
                    "SendWindowChangeRequest",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(uint), typeof(uint), typeof(uint), typeof(uint) },
                    modifiers: null);
                method?.Invoke(channel, new object[] { _cols, _rows, 0u, 0u });
            }
            catch { /* SSH.NET internals shifted — PTY stays stale */ }
        }, ct);
    }

    public Task DisconnectAsync()
    {
        if (_disposed)
            return Task.CompletedTask;
        _readCts?.Cancel();
        if (_client.IsConnected)
        {
            try { _client.Disconnect(); } catch (ObjectDisposedException) { }
        }
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _shell is not null)
            {
                var shell = _shell;
                // Same async-fallback hazard as SendAsync: ShellStream doesn't
                // override ReadAsync, and the inherited default deadlocks under
                // load. Use the synchronous Read on the thread pool instead.
                int read = await Task.Run(() => shell.Read(buf, 0, buf.Length), ct);
                if (read > 0)
                {
                    var chunk = new byte[read];
                    Buffer.BlockCopy(buf, 0, chunk, 0, read);
                    DataReceived?.Invoke(this, chunk);
                }
                else
                {
                    await Task.Delay(10, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* connection dropped */ }
        finally { Disconnected?.Invoke(this, EventArgs.Empty); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync();
        _shell?.Dispose();
        _client.Dispose();
    }
}
