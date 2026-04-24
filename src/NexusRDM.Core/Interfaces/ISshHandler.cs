using NexusRDM.Core.Models;

namespace NexusRDM.Core.Interfaces;

public interface ISshSession : IAsyncDisposable
{
    Guid ConnectionId { get; }
    bool IsConnected  { get; }

    /// <summary>Raw VT data stream received from the server. Subscribe to render terminal output.</summary>
    event EventHandler<byte[]>? DataReceived;
    event EventHandler?         Disconnected;

    Task ConnectAsync(CancellationToken ct = default);
    Task SendAsync(byte[] data, CancellationToken ct = default);
    Task ResizeAsync(int columns, int rows, CancellationToken ct = default);
    Task DisconnectAsync();
}

public interface ISshHandler
{
    /// <summary>Create an SSH session but do NOT connect yet.</summary>
    ISshSession CreateSession(ConnectionProfile profile, string username, string password);

    /// <summary>Create a session using private-key authentication.</summary>
    ISshSession CreateSessionWithKey(ConnectionProfile profile, string username,
        string privateKeyPath, string? passphrase = null);
}
