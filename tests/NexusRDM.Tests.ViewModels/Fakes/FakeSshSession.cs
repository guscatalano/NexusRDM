using NexusRDM.Core.Interfaces;

namespace NexusRDM.Tests.ViewModels.Fakes;

/// <summary>
/// Test double for ISshSession. Records every byte sent so tests can assert the
/// keyboard pipeline forwarded the right input. Also lets tests push synthetic
/// data to the VM via <see cref="EmitData"/>, simulating a remote shell prompt.
/// </summary>
public sealed class FakeSshSession : ISshSession
{
    public Guid ConnectionId { get; } = Guid.NewGuid();
    public bool IsConnected  { get; private set; }

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler?         Disconnected;

    /// <summary>Concatenation of every Send call, in order.</summary>
    public List<byte> Sent { get; } = new();

    /// <summary>Set non-null to make ConnectAsync throw — used to test failure paths.</summary>
    public Exception? ConnectThrows { get; set; }

    /// <summary>Last (cols, rows) seen by ResizeAsync.</summary>
    public (int Cols, int Rows)? LastResize { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (ConnectThrows is not null) throw ConnectThrows;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        Sent.AddRange(data);
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        LastResize = (columns, rows);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    // ── Stats stubs (the VM's status strip reads these) ──────────────
    public DateTimeOffset? ConnectedAt   { get; set; }
    public long            BytesReceived { get; set; }
    public long            BytesSent     { get; set; }
    public string          ServerVersion { get; set; } = string.Empty;
    public string          CipherInfo    { get; set; } = string.Empty;
    public int             PtyCols       { get; set; }
    public int             PtyRows       { get; set; }
    public Task<string> ExecAsync(string command, CancellationToken ct = default) =>
        Task.FromResult(string.Empty);

    /// <summary>Push synthetic shell output — simulates a server sending bytes back.</summary>
    public void EmitData(byte[] data) => DataReceived?.Invoke(this, data);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Returns Sent as a string for readable assertions.</summary>
    public string SentAsString() => System.Text.Encoding.UTF8.GetString(Sent.ToArray());
}
