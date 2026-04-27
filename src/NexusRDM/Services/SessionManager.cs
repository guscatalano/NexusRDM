using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using System.Collections.ObjectModel;

namespace NexusRDM.Services;

/// <summary>
/// Tracks every open session tab (SSH and RDP). Singleton.
/// </summary>
public sealed class SessionManager : IDisposable
{
    public ObservableCollection<OpenSession> Sessions { get; } = [];

    public OpenSession? FindByConnectionId(Guid id) =>
        Sessions.FirstOrDefault(s => s.ConnectionId == id);

    public OpenSession AddSsh(ConnectionProfile profile, ISshSession session)
    {
        var entry = new OpenSession(profile, sshSession: session);
        Sessions.Add(entry);
        return entry;
    }

    public OpenSession AddRdp(ConnectionProfile profile, IRdpSession session)
    {
        var entry = new OpenSession(profile, rdpSession: session);
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

/// <summary>One live session, protocol-agnostic.</summary>
public sealed class OpenSession : IAsyncDisposable
{
    public Guid               ConnectionId { get; }
    public string             DisplayName  { get; }
    public ConnectionProtocol Protocol     { get; }
    public ISshSession?       SshSession   { get; }
    public IRdpSession?       RdpSession   { get; private set; }

    /// <summary>Swap the live RDP session reference after a
    /// reconnect-from-toolbar — the new IRdpSession replaces the disposed
    /// one so SessionManager's tab-switch SetVisible calls still hit a
    /// valid form.</summary>
    public void ReplaceRdpSession(IRdpSession s) => RdpSession = s;

    internal OpenSession(ConnectionProfile profile,
        ISshSession? sshSession = null, IRdpSession? rdpSession = null)
    {
        ConnectionId = profile.Id;
        DisplayName  = profile.DisplayName;
        Protocol     = profile.Protocol;
        SshSession   = sshSession;
        RdpSession   = rdpSession;
    }

    public async ValueTask DisposeAsync()
    {
        if (SshSession is not null) await SshSession.DisposeAsync();
        RdpSession?.Dispose();
    }
}
