using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using System.Collections.ObjectModel;

namespace NexusRDM.Services;

/// <summary>
/// Tracks every open session tab. Singleton — injected into MainWindow and ViewModels.
/// </summary>
public sealed class SessionManager : IDisposable
{
    public ObservableCollection<OpenSession> Sessions { get; } = [];

    public OpenSession? FindByConnectionId(Guid id) =>
        Sessions.FirstOrDefault(s => s.ConnectionId == id);

    public OpenSession AddSsh(ConnectionProfile profile, ISshSession session)
    {
        var entry = new OpenSession(profile, session);
        Sessions.Add(entry);
        return entry;
    }

    public async Task CloseAsync(OpenSession session)
    {
        Sessions.Remove(session);
        await session.DisposeAsync();
    }

    public void Dispose()
    {
        foreach (var s in Sessions.ToList())
            s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Sessions.Clear();
    }
}

/// <summary>Represents one live session regardless of protocol.</summary>
public sealed class OpenSession : IAsyncDisposable
{
    public Guid              ConnectionId { get; }
    public string            DisplayName  { get; }
    public ConnectionProtocol Protocol    { get; }
    public ISshSession?      SshSession  { get; }

    public OpenSession(ConnectionProfile profile, ISshSession ssh)
    {
        ConnectionId = profile.Id;
        DisplayName  = profile.DisplayName;
        Protocol     = profile.Protocol;
        SshSession   = ssh;
    }

    public async ValueTask DisposeAsync()
    {
        if (SshSession is not null)
            await SshSession.DisposeAsync();
    }
}
