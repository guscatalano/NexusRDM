using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.Tests.ViewModels.Fakes;

/// <summary>
/// Test double for IRdpHandler — records every CreateSession call and
/// hands back a fresh <see cref="FakeRdpSession"/>. Used to verify the
/// reconnect path in RdpSessionViewModel builds a brand-new session
/// after the old one disconnects.
/// </summary>
public sealed class FakeRdpHandler : IRdpHandler
{
    public List<(ConnectionProfile profile, string user, string pass)> Calls { get; } = new();
    public List<FakeRdpSession> Sessions { get; } = new();

    public IRdpSession CreateSession(ConnectionProfile profile, string username, string password)
    {
        Calls.Add((profile, username, password));
        var s = new FakeRdpSession();
        Sessions.Add(s);
        return s;
    }
}
