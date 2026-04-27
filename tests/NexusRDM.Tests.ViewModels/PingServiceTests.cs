using System;
using System.Reflection;
using NexusRDM.Services;
using Xunit;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Tests for the <c>_last</c> cache that backs
/// <see cref="PingService.GetLast"/>. The cache is what lets tree
/// nodes survive reloads without resetting their latency / state —
/// covering it pins down the contract.
/// </summary>
public sealed class PingServiceTests
{
    [Fact]
    public void GetLast_UnknownId_ReturnsUnknownAndNullLatency()
    {
        var svc = new PingService();
        var (state, ms) = svc.GetLast(Guid.NewGuid());
        Assert.Equal(PingState.Unknown, state);
        Assert.Null(ms);
    }

    [Fact]
    public void GetLast_AfterRaise_ReturnsCachedValue()
    {
        // Raise is a private helper that writes the cache and fires
        // EntryUpdated. We invoke it via reflection because production
        // code only calls it from the runloop, but the cache contract
        // is part of the public surface (GetLast).
        var svc = new PingService();
        var id = Guid.NewGuid();
        InvokeRaise(svc, new PingUpdate(id, PingState.Ok, 42));

        var (state, ms) = svc.GetLast(id);
        Assert.Equal(PingState.Ok, state);
        Assert.Equal(42, ms);
    }

    [Fact]
    public void GetLast_LatestRaiseWins()
    {
        var svc = new PingService();
        var id = Guid.NewGuid();
        InvokeRaise(svc, new PingUpdate(id, PingState.Ok,     12));
        InvokeRaise(svc, new PingUpdate(id, PingState.Failed, null));

        var (state, ms) = svc.GetLast(id);
        Assert.Equal(PingState.Failed, state);
        Assert.Null(ms);
    }

    private static void InvokeRaise(PingService svc, PingUpdate u)
    {
        var raise = typeof(PingService).GetMethod("Raise", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PingService.Raise not found via reflection.");
        raise.Invoke(svc, new object?[] { u });
    }
}
