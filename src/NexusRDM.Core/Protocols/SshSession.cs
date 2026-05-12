using Renci.SshNet;
using NexusRDM.Core.Diagnostics;
using NexusRDM.Core.Interfaces;

namespace NexusRDM.Core.Protocols;

/// <summary>
/// Wraps SSH.NET SshClient + ShellStream.
/// Raw VT bytes from the shell are fired via DataReceived for
/// VtNetCore to consume and render on the UI thread.
/// </summary>
public sealed class SshSession : ISshSession
{
    // SshClient is constructed lazily — we may not know the username
    // at session-creation time. It's resolved during ConnectAsync via
    // _usernamePrompt + then passed to _clientFactory to materialise
    // the actual SSH.NET client.
    private SshClient?                _client;
    private string                    _username;
    private readonly Func<string, int, CancellationToken, Task<SshClient>>? _clientFactory;
    private readonly SshKeyboardPromptHandler? _usernamePrompt;
    private ShellStream?              _shell;
    private CancellationTokenSource?  _readCts;
    private uint                      _cols = 220;
    private uint                      _rows = 50;
    private bool                      _disposed;
    // Stats counters — Interlocked-updated from the read loop (threadpool)
    // and SendAsync's lambda. Reads from UI thread don't need locking
    // because long reads on x64 are atomic, but Interlocked.Add keeps
    // the writes torn-free.
    private long                      _bytesReceived;
    private long                      _bytesSent;
    private DateTimeOffset?           _connectedAt;

    public Guid ConnectionId { get; }
    public bool IsConnected  => _client?.IsConnected ?? false;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler?         Disconnected;

    public DateTimeOffset? ConnectedAt       => _connectedAt;
    public long            BytesReceived     => Interlocked.Read(ref _bytesReceived);
    public long            BytesSent         => Interlocked.Read(ref _bytesSent);
    public int             PtyCols           => (int)_cols;
    public int             PtyRows           => (int)_rows;
    public string          ConnectedUsername => _username ?? string.Empty;

    public string ServerVersion
    {
        // SSH.NET's BaseClient.ConnectionInfo getter throws
        // ObjectDisposedException after Disconnect/Dispose. The stats
        // timer can fire one more time before its host VM observes the
        // Disconnected event — and that tick crashes the dispatcher
        // queue. Return empty instead.
        get
        {
            try { return _client?.ConnectionInfo?.ServerVersion ?? string.Empty; }
            catch (ObjectDisposedException) { return string.Empty; }
        }
    }

    /// <summary>"&lt;cipher&gt; + &lt;mac&gt;" combined string — what most
    /// SSH clients show in their status bar. Returns empty when the
    /// channel hasn't negotiated yet, or when the client has been
    /// disposed mid-tick.</summary>
    public string CipherInfo
    {
        get
        {
            try
            {
                var ci = _client?.ConnectionInfo;
                if (ci is null) return string.Empty;
                var enc = ci.CurrentClientEncryption ?? string.Empty;
                var mac = ci.CurrentClientHmacAlgorithm ?? string.Empty;
                return string.IsNullOrEmpty(mac) ? enc : $"{enc} + {mac}";
            }
            catch (ObjectDisposedException) { return string.Empty; }
        }
    }

    public async Task<string> ExecAsync(string command, CancellationToken ct = default)
    {
        if (_disposed || _client is null || !_client.IsConnected)
            return string.Empty;
        // Separate exec channel — does NOT touch the user's interactive
        // shell. SSH.NET's SshCommand opens its own channel, runs, closes.
        // We bounce to the thread pool because Execute() blocks on
        // network I/O.
        var client = _client;
        return await Task.Run(() =>
        {
            try
            {
                using var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(5);
                return cmd.Execute() ?? string.Empty;
            }
            catch (Exception ex)
            {
                SshLog.Warn($"ExecAsync failed: {ex.GetType().Name}: {ex.Message}");
                return string.Empty;
            }
        }, ct);
    }

    /// <summary>Legacy eager-construction path — used by callers that
    /// already know the username and want to build the SshClient
    /// themselves. The new
    /// <see cref="SshHandler.CreateSessionForProfile"/> flow uses the
    /// lazy ctor below instead.</summary>
    internal SshSession(Guid connectionId, SshClient client)
    {
        ConnectionId = connectionId;
        _client      = client;
        _username    = string.Empty; // already baked into client.ConnectionInfo
    }

    /// <summary>Lazy-construction ctor. Username may be empty — if so,
    /// <see cref="ConnectAsync"/> will use <paramref name="usernamePrompt"/>
    /// to ask for one (rendered into the terminal via the broker)
    /// before invoking <paramref name="clientFactory"/>. This is what
    /// lets the UX skip the username dialog entirely.</summary>
    internal SshSession(
        Guid connectionId,
        string username,
        Func<string, int, CancellationToken, Task<SshClient>> clientFactory,
        SshKeyboardPromptHandler? usernamePrompt)
    {
        ConnectionId    = connectionId;
        _username       = username ?? string.Empty;
        _clientFactory  = clientFactory;
        _usernamePrompt = usernamePrompt;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Lazy path: ask for the username via the broker if we don't
        // have one yet (SSH bakes the username into the very first
        // auth packet, so the SshClient can only be built after).
        if (_client is null && string.IsNullOrEmpty(_username) && _usernamePrompt is not null)
        {
            var prompted = await _usernamePrompt("login as: ", false, ct);
            if (string.IsNullOrEmpty(prompted))
                throw new OperationCanceledException("Username not provided — aborting connection.");
            _username = prompted!;
        }

        // Auth retry loop — runs until the user successfully logs in
        // OR cancels by pressing Ctrl+C in the password prompt (the
        // broker returns null, our factory throws OperationCanceledException,
        // and that propagates out of this loop). Matches the user's
        // request: don't give up after N tries, just keep prompting.
        // Behavior diverges from stock ssh.exe (which gives up at 3)
        // but the cancel path keeps you from being stuck forever.
        for (int attempt = 0; ; attempt++)
        {
            if (_client is null)
            {
                if (_clientFactory is null)
                    throw new InvalidOperationException(
                        "SshSession has no SshClient (no factory + no eager ctor).");
                _client = await _clientFactory(_username, attempt, ct);
            }

            try
            {
                await Task.Run(_client.Connect, ct);
                break;
            }
            catch (Renci.SshNet.Common.SshAuthenticationException ex)
            {
                // Push the failure into the terminal as if it were
                // server output, so the user sees `Permission denied`
                // exactly where the next prompt will appear.
                var msg = "\r\n" + ex.Message + "\r\n";
                DataReceived?.Invoke(this, System.Text.Encoding.UTF8.GetBytes(msg));
                try { _client.Dispose(); } catch { }
                _client = null;
                // Loop continues — factory re-prompts for credentials
                // because attempt > 0.
            }
        }

        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("SSH authentication failed after retries.");

        // pty-req terminal modes (RFC 4254 §8). Matches what xterm /
        // PuTTY negotiate by default. The big-impact one is IUTF8=1 —
        // tells the server's line discipline that bytes coming in are
        // UTF-8 so multibyte erase / kill / werase work on whole code
        // points instead of single bytes. The rest mirror "canonical
        // interactive shell" defaults; without an explicit modes dict
        // most servers fall back to sane values anyway, but being
        // explicit avoids surprises on stripped-down embedded systems.
        var modes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint>
        {
            // Input handling
            { Renci.SshNet.Common.TerminalModes.IUTF8,  1 }, // UTF-8 input
            { Renci.SshNet.Common.TerminalModes.ICRNL,  1 }, // CR → NL on input
            { Renci.SshNet.Common.TerminalModes.IXON,   1 }, // Ctrl+S / Ctrl+Q flow control
            { Renci.SshNet.Common.TerminalModes.IMAXBEL,1 }, // BEL on full input queue

            // Local modes
            { Renci.SshNet.Common.TerminalModes.ISIG,   1 }, // Ctrl+C/Z/\ generate signals
            { Renci.SshNet.Common.TerminalModes.ICANON, 1 }, // Line buffering / editing
            { Renci.SshNet.Common.TerminalModes.IEXTEN, 1 }, // Extended functions (Ctrl+V etc.)
            { Renci.SshNet.Common.TerminalModes.ECHO,   1 }, // Echo input
            { Renci.SshNet.Common.TerminalModes.ECHOE,  1 }, // Backspace erases on echo
            { Renci.SshNet.Common.TerminalModes.ECHOK,  1 }, // Kill erases line
            { Renci.SshNet.Common.TerminalModes.ECHOCTL,1 }, // Render ^X for control chars

            // Output
            { Renci.SshNet.Common.TerminalModes.OPOST,  1 }, // Output post-processing
            { Renci.SshNet.Common.TerminalModes.ONLCR,  1 }, // NL → CRNL on output

            // Character size
            { Renci.SshNet.Common.TerminalModes.CS8,    1 }, // 8-bit characters

            // Special character bindings (ASCII control codes)
            { Renci.SshNet.Common.TerminalModes.VINTR,   3   }, // Ctrl+C
            { Renci.SshNet.Common.TerminalModes.VQUIT,   28  }, // Ctrl+\
            { Renci.SshNet.Common.TerminalModes.VERASE,  127 }, // DEL (backspace)
            { Renci.SshNet.Common.TerminalModes.VKILL,   21  }, // Ctrl+U
            { Renci.SshNet.Common.TerminalModes.VEOF,    4   }, // Ctrl+D
            { Renci.SshNet.Common.TerminalModes.VSUSP,   26  }, // Ctrl+Z
            { Renci.SshNet.Common.TerminalModes.VWERASE, 23  }, // Ctrl+W
            { Renci.SshNet.Common.TerminalModes.VLNEXT,  22  }, // Ctrl+V
            { Renci.SshNet.Common.TerminalModes.VREPRINT,18  }, // Ctrl+R
        };
        _shell    = _client.CreateShellStream("xterm-256color", _cols, _rows, 0, 0, 4096, modes);
        SshLog.Info($"Shell opened: term=xterm-256color cols={_cols} rows={_rows} conn={ConnectionId}");
        _connectedAt = DateTimeOffset.UtcNow;
        _readCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ReadLoopAsync(_readCts.Token);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        // The shell stream may be torn down by Disconnect/Dispose between the
        // time the user pressed a key and when this runs. Treat that as a
        // silent no-op rather than crashing the UI thread.
        if (_disposed || _shell is null || _client is null || !_client.IsConnected) return;

        // SSH.NET's ShellStream overrides synchronous Write/Flush but inherits
        // the base Stream.WriteAsync/FlushAsync — those defaults deadlock here,
        // so we explicitly bounce to the sync API on the thread pool.
        // The try/catch lives INSIDE the lambda: callers occasionally
        // fire-and-forget this Task (e.g. fast keystroke loops), and a faulted
        // Task with no observer can still surface the inner exception through
        // the threadpool dispatcher on some runtimes. Catching at the source
        // guarantees the worker exits cleanly regardless of whether the
        // caller awaits.
        var shell = _shell;
        // Diagnostic: log every send. Keystrokes are 1–10 bytes so the
        // log stays compact; the hex preview makes it obvious whether
        // 'q' / ESC / Ctrl+C actually reach the wire.
        SshLog.Debug($"Send bytes={data.Length} preview={HexPreview(data, 32)} conn={ConnectionId}");
        Interlocked.Add(ref _bytesSent, data.Length);
        await Task.Run(() =>
        {
            try
            {
                shell.Write(data, 0, data.Length);
                shell.Flush();
            }
            catch (ObjectDisposedException ex)
            {
                SshLog.Warn($"Send failed (disposed): {ex.Message} conn={ConnectionId}");
            }
            catch (System.IO.IOException ex)
            {
                SshLog.Warn($"Send failed (IO): {ex.Message} conn={ConnectionId}");
            }
        }, ct);
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        _cols = (uint)columns;
        _rows = (uint)rows;
        SshLog.Debug($"PTY resize requested: cols={columns} rows={rows} conn={ConnectionId}");
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
                SshLog.Debug($"PTY resize sent to server: cols={_cols} rows={_rows} conn={ConnectionId}");
            }
            catch (Exception ex)
            {
                SshLog.Warn($"PTY resize reflection failed: {ex.Message} conn={ConnectionId}");
            }
        }, ct);
    }

    public Task DisconnectAsync()
    {
        if (_disposed)
            return Task.CompletedTask;
        _readCts?.Cancel();
        if (_client is not null && _client.IsConnected)
        {
            try { _client.Disconnect(); } catch (ObjectDisposedException) { }
        }
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        long totalBytes = 0;
        int  chunkCount = 0;
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
                    Interlocked.Add(ref _bytesReceived, read);
                    DataReceived?.Invoke(this, chunk);

                    // Diagnostic: log every SMALL chunk (≤ 256 bytes)
                    // verbosely — those are the control sequences and
                    // command echoes we need to debug. Large chunks
                    // (full top frames, file dumps) get a one-line
                    // summary without the hex preview to avoid log
                    // spam at MB/s throughput.
                    totalBytes += read;
                    if (read <= 256)
                    {
                        SshLog.Debug($"Read chunk #{chunkCount} bytes={read} preview={HexPreview(chunk, 64)} conn={ConnectionId}");
                    }
                    else if (chunkCount < 4 || (chunkCount & 0x3F) == 0)
                    {
                        SshLog.Debug($"Read chunk #{chunkCount} bytes={read} (large, preview suppressed) conn={ConnectionId}");
                    }
                    chunkCount++;
                }
                else
                {
                    await Task.Delay(10, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SshLog.Warn($"Read loop exited: {ex.GetType().Name}: {ex.Message} totalBytes={totalBytes} conn={ConnectionId}");
        }
        finally
        {
            SshLog.Info($"Read loop ended: totalBytes={totalBytes} chunks={chunkCount} conn={ConnectionId}");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Compact hex+ASCII preview of the first N bytes of a
    /// chunk for log readability. Escape sequences show up as
    /// "1B 5B 3F 31 30 34 39 68 (ESC[?1049h)" — easy to spot the
    /// alt-buffer enter/exit in the log.</summary>
    private static string HexPreview(byte[] data, int max)
    {
        int n = Math.Min(data.Length, max);
        var sb = new System.Text.StringBuilder(n * 4);
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("X2")).Append(' ');
        sb.Append('|');
        for (int i = 0; i < n; i++)
        {
            byte b = data[i];
            sb.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
        }
        if (data.Length > max) sb.Append("…");
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync();
        _shell?.Dispose();
        _client?.Dispose();
    }
}
