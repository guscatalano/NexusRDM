using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;

namespace NexusRDM.ViewModels;

public sealed partial class SshSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISshSession    _session;
    private readonly SessionManager _mgr;
    private DispatcherQueueTimer?   _statsTimer;
    private DispatcherQueueTimer?   _hostStatsTimer;
    private CancellationTokenSource? _hostStatsCts;

    /// <summary>The underlying session, exposed so the view can
    /// type-check (<c>is PuttySshSession</c>) and wire backend-
    /// specific embed plumbing. Read-only.</summary>
    public ISshSession Session => _session;

    /// <summary>Optional in-terminal auth broker. Non-null when the
    /// connection's <c>SshAuthMode</c> uses keyboard-interactive
    /// (<c>ServerPrompt</c> / <c>KeyThenPrompt</c>): SSH.NET fires
    /// prompts into the broker, which paints them on the terminal
    /// and consumes user input until Enter. The view checks
    /// <c>AuthBroker?.IsActive</c> on every keystroke to decide
    /// whether to route to the broker or the session.</summary>
    public NexusRDM.Services.TerminalAuthBroker? AuthBroker { get; }

    public Guid              ConnectionId { get; }
    public string            DisplayName  { get; }
    public string            Host         { get; }
    /// <summary>The connection profile this session is bound to.
    /// Used by cross-launch buttons (e.g. "Files" → SFTP) so the host
    /// MainWindow can spawn a sibling tab against the same profile.</summary>
    public ConnectionProfile Profile      { get; }

    [ObservableProperty] private bool   _isConnected;
    [ObservableProperty] private bool   _isConnecting = true;
    [ObservableProperty] private string _statusMessage = "Connecting…";

    // ── Live session stats (status strip) ────────────────────────────
    // Updated by a 1-second DispatcherQueueTimer once Connect succeeds.
    [ObservableProperty] private string _uptimeDisplay = "—";
    [ObservableProperty] private string _bytesDisplay  = "↓ 0 B  ↑ 0 B";
    [ObservableProperty] private string _cipherDisplay = "—";
    [ObservableProperty] private string _serverDisplay = "—";
    [ObservableProperty] private string _ptyDisplay    = "—";

    // ── Host stats panel ─────────────────────────────────────────────
    [ObservableProperty] private bool   _hostStatsEnabled;
    [ObservableProperty] private string _hostStatsLoad   = "—";
    [ObservableProperty] private string _hostStatsMemory = "—";
    [ObservableProperty] private string _hostStatsCpu    = "—";
    [ObservableProperty] private string _hostStatsDisk   = "—";
    [ObservableProperty] private string _hostStatsUptime = "—";
    [ObservableProperty] private string _hostStatsUsers  = "—";

    /// <summary>True iff the backend supports the host-stats panel.
    /// PuTTY-backed sessions don't (no programmable channel) so the UI
    /// hides the toggle entirely.</summary>
    public bool HostStatsAvailable =>
        _session is not NexusRDM.Protocols.PuttySshSession;

    /// <summary>Raised when VT bytes arrive — the TerminalControl subscribes to this.</summary>
    public event EventHandler<byte[]>? DataReceived;

    public SshSessionViewModel(
        ConnectionProfile profile,
        ISshSession session,
        SessionManager mgr,
        NexusRDM.Services.TerminalAuthBroker? authBroker = null)
        : this(profile, session, mgr)
    {
        AuthBroker = authBroker;
    }

    public SshSessionViewModel(ConnectionProfile profile, ISshSession session, SessionManager mgr)
    {
        ConnectionId = profile.Id;
        DisplayName  = profile.DisplayName;
        Host         = $"{profile.Host}:{profile.Port}";
        Profile      = profile;
        _session     = session;
        _mgr         = mgr;

        _session.DataReceived  += (_, data) => DataReceived?.Invoke(this, data);
        _session.Disconnected  += OnSessionDisconnected;
    }

    public async Task ConnectAsync()
    {
        try
        {
            await _session.ConnectAsync();
            IsConnected    = true;
            IsConnecting   = false;
            StatusMessage  = $"Connected to {Host}";
            StartStatsTimer();
        }
        catch (Exception ex)
        {
            IsConnecting  = false;
            StatusMessage = $"Failed: {ex.Message}";
        }
    }

    /// <summary>Pulse the status-strip stats on a 1-second DispatcherQueueTimer.
    /// Cheap: reads in-memory counters on the session; no network.
    /// Wrapped defensively because <c>DispatcherQueue.GetForCurrentThread</c>
    /// throws a COM ClassFactory exception in non-WinUI test contexts
    /// rather than returning null — the test runner doesn't host a
    /// dispatcher queue at all. Failure to set up the timer is fine:
    /// the rest of the VM works without live stats, and headless test
    /// runs don't need them.</summary>
    private void StartStatsTimer()
    {
        DispatcherQueue? dq = null;
        try { dq = DispatcherQueue.GetForCurrentThread(); }
        catch { /* COM unavailable — test or background thread */ }
        if (dq is null) return;
        _statsTimer = dq.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromSeconds(1);
        _statsTimer.IsRepeating = true;
        _statsTimer.Tick += (_, _) => RefreshStats();
        RefreshStats();
        _statsTimer.Start();
    }

    private void RefreshStats()
    {
        if (!IsConnected) return;
        // Uptime: rolling, formatted as Xh Ym Zs (only show non-zero parts).
        if (_session.ConnectedAt is DateTimeOffset since)
        {
            var d = DateTimeOffset.UtcNow - since;
            UptimeDisplay = d.TotalHours >= 1
                ? $"{(int)d.TotalHours}h {d.Minutes}m"
                : d.TotalMinutes >= 1
                    ? $"{d.Minutes}m {d.Seconds}s"
                    : $"{d.Seconds}s";
        }
        BytesDisplay  = $"↓ {FormatBytes(_session.BytesReceived)}  ↑ {FormatBytes(_session.BytesSent)}";
        CipherDisplay = string.IsNullOrEmpty(_session.CipherInfo) ? "—" : _session.CipherInfo;
        ServerDisplay = string.IsNullOrEmpty(_session.ServerVersion) ? "—" : _session.ServerVersion;
        PtyDisplay    = _session.PtyCols > 0 ? $"{_session.PtyCols}×{_session.PtyRows}" : "—";
    }

    private static string FormatBytes(long n)
    {
        if (n < 1024)        return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024.0:F1} KB";
        if (n < 1024L * 1024 * 1024) return $"{n / (1024.0 * 1024):F1} MB";
        return $"{n / (1024.0 * 1024 * 1024):F2} GB";
    }

    // ── Host stats panel polling ─────────────────────────────────────

    public void StartHostStatsPolling()
    {
        if (_hostStatsTimer is not null || !HostStatsAvailable) return;
        DispatcherQueue? dq = null;
        try { dq = DispatcherQueue.GetForCurrentThread(); }
        catch { /* COM unavailable */ }
        if (dq is null) return;
        _hostStatsCts = new CancellationTokenSource();
        _hostStatsTimer = dq.CreateTimer();
        _hostStatsTimer.Interval = TimeSpan.FromSeconds(5);
        _hostStatsTimer.IsRepeating = true;
        _hostStatsTimer.Tick += async (_, _) => await PollHostStatsAsync();
        HostStatsEnabled = true;
        _ = PollHostStatsAsync(); // immediate first poll, don't wait 5s
        _hostStatsTimer.Start();
    }

    public void StopHostStatsPolling()
    {
        _hostStatsTimer?.Stop();
        _hostStatsTimer = null;
        _hostStatsCts?.Cancel();
        _hostStatsCts?.Dispose();
        _hostStatsCts = null;
        HostStatsEnabled = false;
        HostStatsLoad   = "—";
        HostStatsMemory = "—";
        HostStatsCpu    = "—";
        HostStatsDisk   = "—";
        HostStatsUptime = "—";
        HostStatsUsers  = "—";
    }

    private async Task PollHostStatsAsync()
    {
        if (!IsConnected || _hostStatsCts is null) return;
        var ct = _hostStatsCts.Token;
        try
        {
            // Run the cheap reads serially on the single exec channel.
            // /proc reads + a couple of one-shot utilities; each round
            // trip is one packet's worth of data, parses to a few values.
            var loadRaw   = await _session.ExecAsync("cat /proc/loadavg", ct);
            var memRaw    = await _session.ExecAsync("cat /proc/meminfo | head -3", ct);
            var uptimeRaw = await _session.ExecAsync("uptime -s", ct);
            var usersRaw  = await _session.ExecAsync("who | wc -l", ct);
            var diskRaw   = await _session.ExecAsync("df -P / | tail -1", ct);

            HostStatsLoad   = ParseLoadAvg(loadRaw);
            HostStatsMemory = ParseMemory(memRaw);
            HostStatsCpu    = "—"; // CPU% needs two /proc/stat samples — skip in v1
            HostStatsUptime = ParseUptime(uptimeRaw);
            HostStatsUsers  = ParseUsers(usersRaw);
            HostStatsDisk   = ParseDisk(diskRaw);
        }
        catch (OperationCanceledException) { /* poll cancelled */ }
        catch (Exception ex)
        {
            HostStatsLoad = $"err: {ex.Message}";
        }
    }

    /// <summary>"0.42 0.51 0.49 2/482 12345" → "0.42 / 0.51 / 0.49".</summary>
    private static string ParseLoadAvg(string raw)
    {
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? $"{parts[0]} / {parts[1]} / {parts[2]}" : "—";
    }

    /// <summary>"MemTotal: NkB / MemFree: NkB / MemAvailable: NkB" →
    /// "12.3 / 64.0 GB (19%)" where used = total - available.</summary>
    private static string ParseMemory(string raw)
    {
        long Pick(string key)
        {
            foreach (var line in raw.Split('\n'))
                if (line.StartsWith(key, StringComparison.Ordinal))
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var v)) return v;
                }
            return 0;
        }
        long totalKb = Pick("MemTotal:");
        long availKb = Pick("MemAvailable:");
        if (totalKb <= 0) return "—";
        double usedGB  = (totalKb - availKb) / 1024.0 / 1024.0;
        double totalGB = totalKb / 1024.0 / 1024.0;
        int pct        = totalKb > 0 ? (int)((totalKb - availKb) * 100 / totalKb) : 0;
        return $"{usedGB:F1} / {totalGB:F1} GB ({pct}%)";
    }

    /// <summary>"2026-04-12 09:14:22" → "since 2026-04-12".</summary>
    private static string ParseUptime(string raw)
    {
        var trimmed = raw.Trim();
        return string.IsNullOrEmpty(trimmed) ? "—" : $"since {trimmed}";
    }

    private static string ParseUsers(string raw)
    {
        var trimmed = raw.Trim();
        return string.IsNullOrEmpty(trimmed) ? "—" : trimmed;
    }

    /// <summary>"/dev/sda1  488123444  224311232  244112844  49%  /" →
    /// "224 GB / 466 GB (49%)".</summary>
    private static string ParseDisk(string raw)
    {
        var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return "—";
        if (!long.TryParse(parts[1], out var totalKb)) return "—";
        if (!long.TryParse(parts[2], out var usedKb))  return "—";
        double usedGB  = usedKb  / 1024.0 / 1024.0;
        double totalGB = totalKb / 1024.0 / 1024.0;
        return $"{usedGB:F0} / {totalGB:F0} GB ({parts[4]})";
    }

    public Task SendInputAsync(byte[] data) =>
        IsConnected ? _session.SendAsync(data) : Task.CompletedTask;

    public Task ResizeAsync(int cols, int rows) =>
        IsConnected ? _session.ResizeAsync(cols, rows) : Task.CompletedTask;

    private void OnSessionDisconnected(object? sender, EventArgs e)
    {
        IsConnected   = false;
        StatusMessage = "Disconnected";
        _statsTimer?.Stop();
        StopHostStatsPolling();
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _session.DisconnectAsync();
        var entry = _mgr.FindByConnectionId(ConnectionId);
        if (entry is not null) await _mgr.CloseAsync(entry);
    }

    public async ValueTask DisposeAsync() => await _session.DisposeAsync();
}
