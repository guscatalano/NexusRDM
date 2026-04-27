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
    private readonly object             _gate    = new();
    private CancellationTokenSource?    _cts;
    private List<(Guid Id, string Host)> _targets = new();
    private bool                        _enabled;
    private int                         _intervalSec = 30;

    public event EventHandler<PingUpdate>? EntryUpdated;

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

                foreach (var (id, host) in snapshot)
                {
                    if (ct.IsCancellationRequested) return;
                    EntryUpdated?.Invoke(this, new PingUpdate(id, PingState.Pinging, null));
                    PingUpdate result;
                    try
                    {
                        using var ping = new Ping();
                        // 2-second timeout — long enough for transcontinental
                        // links, short enough that one bad host doesn't
                        // stall the rest of the sweep.
                        var reply = await ping.SendPingAsync(host, 2000);
                        result = reply.Status == IPStatus.Success
                            ? new PingUpdate(id, PingState.Ok,     reply.RoundtripTime)
                            : new PingUpdate(id, PingState.Failed, null);
                    }
                    catch { result = new PingUpdate(id, PingState.Failed, null); }
                    EntryUpdated?.Invoke(this, result);
                }

                try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); }
                catch (TaskCanceledException) { return; }
            }
        }
        catch { /* loop terminates on any unhandled error; user can re-toggle in Settings */ }
    }

    public void Dispose()
    {
        lock (_gate) { _cts?.Cancel(); _cts = null; }
    }
}
