using System.Net.NetworkInformation;
using Microsoft.UI.Dispatching;

namespace NexusRDM.Services;

public enum PingState { Unknown, Pinging, Ok, Failed }

/// <summary>One ping result for a saved connection.</summary>
public sealed record PingUpdate(Guid ConnectionId, PingState State, long? LatencyMs);

/// <summary>
/// Periodically pings every saved connection's host and surfaces the
/// result through <see cref="EntryUpdated"/>. Singleton in DI; the
/// connections list configures it on load and whenever the user
/// changes ping-related settings.
/// </summary>
public sealed class PingService : IDisposable
{
    /// <summary>How many hosts to ping in parallel each cycle. ICMP is
    /// almost entirely wait-for-response, so per-host CPU is negligible
    /// and the bottleneck is the per-host 2-second timeout. With 32
    /// concurrent pings a 200-host list completes a cycle in under
    /// <c>ceil(200/32) × 2s ≈ 14s</c> worst-case instead of 400s.</summary>
    private const int PingConcurrency = 32;

    private readonly object             _gate    = new();
    private CancellationTokenSource?    _cts;
    private List<(Guid Id, string Host)> _targets = new();
    private bool                        _enabled;
    private int                         _intervalSec = 30;

    /// <summary>Last-seen result per connection id. Survives tree
    /// reloads — <see cref="ConnectionTreeNode"/> seeds itself from
    /// this on construction so a sync / refresh doesn't reset every
    /// row's latency to "?". Updated on every <c>EntryUpdated</c>.</summary>
    private readonly Dictionary<Guid, (PingState State, long? LatencyMs)> _last = new();

    public event EventHandler<PingUpdate>? EntryUpdated;

    /// <summary>Returns the most recent state + latency for
    /// <paramref name="connectionId"/>, or <c>(Unknown, null)</c> if
    /// we've never pinged it. Thread-safe via the same lock that
    /// guards the target list.</summary>
    public (PingState State, long? LatencyMs) GetLast(Guid connectionId)
    {
        lock (_gate)
            return _last.TryGetValue(connectionId, out var v) ? v : (PingState.Unknown, null);
    }

    /// <summary>Replaces the entire target set + reschedules. Targets
    /// are passed in full each time so the caller doesn't have to track
    /// add/remove deltas.</summary>
    public void Configure(bool enabled, int intervalSeconds, IEnumerable<(Guid Id, string Host)> targets)
    {
        lock (_gate)
        {
            _enabled     = enabled;
            _intervalSec = Math.Max(5, intervalSeconds);
            _targets     = targets.Where(t => !string.IsNullOrWhiteSpace(t.Host)).ToList();
            RestartLocked();
        }
    }

    private void RestartLocked()
    {
        _cts?.Cancel();
        _cts = null;
        if (!_enabled || _targets.Count == 0) return;
        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                List<(Guid Id, string Host)> snapshot;
                int interval;
                lock (_gate)
                {
                    snapshot = _targets.ToList();
                    interval = _intervalSec;
                }

                // Fan out across the host list. The previous serial
                // loop made cycle time = N × per-host RTT, which made
                // the latency column visibly stale on lists with many
                // hosts (especially with a few unreachable ones each
                // burning the full 2-second timeout). Parallelism caps
                // the cycle at roughly ceil(N / Concurrency) timeouts.
                await Parallel.ForEachAsync(snapshot,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = PingConcurrency,
                        CancellationToken      = ct,
                    },
                    async (target, innerCt) =>
                    {
                        await PingOneAsync(target.Id, target.Host, innerCt).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); }
                catch (TaskCanceledException) { return; }
            }
        }
        catch { /* loop terminates on any unhandled error; user can re-toggle in Settings */ }
    }

    private async Task PingOneAsync(Guid id, string host, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        Raise(new PingUpdate(id, PingState.Pinging, null));
        PingUpdate result;
        try
        {
            using var ping = new Ping();
            // 2-second timeout — long enough for transcontinental
            // links, short enough that one bad host doesn't
            // stall the rest of the sweep.
            var reply = await ping.SendPingAsync(host, 2000).ConfigureAwait(false);
            result = reply.Status == IPStatus.Success
                ? new PingUpdate(id, PingState.Ok,     reply.RoundtripTime)
                : new PingUpdate(id, PingState.Failed, null);
        }
        catch { result = new PingUpdate(id, PingState.Failed, null); }
        Raise(result);
    }

    private void Raise(PingUpdate u)
    {
        // Cache before notify so subscribers re-querying via GetLast
        // (e.g. a tree reload that races with a ping result) see the
        // freshly written value.
        lock (_gate) _last[u.ConnectionId] = (u.State, u.LatencyMs);
        EntryUpdated?.Invoke(this, u);
    }

    public void Dispose()
    {
        lock (_gate) { _cts?.Cancel(); _cts = null; }
    }
}
