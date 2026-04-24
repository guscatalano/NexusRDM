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
        if (_shell is null) return;
        await _shell.WriteAsync(data, 0, data.Length, ct);
        await _shell.FlushAsync(ct);
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        // TODO M2: SSH.NET 2024.2.x doesn't expose resize via ShellStream directly.
        // When we build the terminal view we'll tear down and recreate the ShellStream
        // with the new dimensions, or use a raw channel PTY-req packet.
        _cols = (uint)columns;
        _rows = (uint)rows;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _readCts?.Cancel();
        _client.Disconnect();
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _shell is not null)
            {
                int read = await _shell.ReadAsync(buf, 0, buf.Length, ct);
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
        await DisconnectAsync();
        _shell?.Dispose();
        _client.Dispose();
    }
}
