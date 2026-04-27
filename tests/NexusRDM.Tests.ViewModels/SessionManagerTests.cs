using NexusRDM.Core.Models;
using NexusRDM.Services;
using NexusRDM.Tests.ViewModels.Fakes;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Covers the SessionManager surface added/changed during the reconnect
/// work: <see cref="OpenSession.ReplaceRdpSession"/> swap and the
/// FindByConnectionId lookup that drives the manager's tab-switch
/// SetVisible plumbing.
/// </summary>
public sealed class SessionManagerTests
{
    [Fact]
    public void AddRdp_AppendsEntry_AndReturnsIt()
    {
        var (mgr, profile, fake) = Build();

        var entry = mgr.AddRdp(profile, fake);

        Assert.Single(mgr.Sessions);
        Assert.Same(entry,        mgr.Sessions[0]);
        Assert.Same(fake,         entry.RdpSession);
        Assert.Equal(profile.Id,  entry.ConnectionId);
    }

    [Fact]
    public void FindByConnectionId_FindsExistingEntry()
    {
        var (mgr, profile, fake) = Build();
        mgr.AddRdp(profile, fake);

        var found = mgr.FindByConnectionId(profile.Id);

        Assert.NotNull(found);
        Assert.Same(fake, found!.RdpSession);
    }

    [Fact]
    public void FindByConnectionId_ReturnsNull_ForUnknownId()=>
        Assert.Null(new SessionManager().FindByConnectionId(Guid.NewGuid()));

    [Fact]
    public void ReplaceRdpSession_SwapsTheLiveReference_WithoutTouchingTheCollection()
    {
        var (mgr, profile, oldSession) = Build();
        var entry = mgr.AddRdp(profile, oldSession);
        var newSession = new FakeRdpSession();

        entry.ReplaceRdpSession(newSession);

        // Same OpenSession instance (so any held tab-Tag stays valid),
        // but the RdpSession reference points to the fresh session.
        Assert.Single(mgr.Sessions);
        Assert.Same(entry,      mgr.Sessions[0]);
        Assert.Same(newSession, mgr.Sessions[0].RdpSession);
        Assert.NotSame(oldSession, mgr.Sessions[0].RdpSession);
    }

    [Fact]
    public async Task CloseAsync_RemovesEntry_AndDisposesRdpSession()
    {
        var (mgr, profile, fake) = Build();
        var entry = mgr.AddRdp(profile, fake);

        await mgr.CloseAsync(entry);

        Assert.Empty(mgr.Sessions);
        Assert.Equal(1, fake.DisposeCalls);
    }

    private static (SessionManager mgr, ConnectionProfile profile, FakeRdpSession fake) Build()
    {
        var profile = new ConnectionProfile
        {
            DisplayName = "host",
            Host        = "host.local",
            Port        = 3389,
            Protocol    = ConnectionProtocol.Rdp,
        };
        return (new SessionManager(), profile, new FakeRdpSession());
    }
}
