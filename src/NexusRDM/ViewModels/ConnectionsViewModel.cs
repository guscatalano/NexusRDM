using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using System.Collections.ObjectModel;
using Windows.UI;

namespace NexusRDM.ViewModels;

public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly IConnectionService _svc;
    private readonly NexusRDM.Services.SessionManager _sessions;

    public ObservableCollection<ConnectionTreeNode> RootItems { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _searchQuery = string.Empty;
    partial void OnSearchQueryChanged(string value) => _ = LoadAsync(value);

    private readonly NexusRDM.Services.PingService _ping;

    public ConnectionsViewModel(
        IConnectionService svc,
        NexusRDM.Services.SessionManager sessions,
        NexusRDM.Services.PingService pingService,
        NexusRDM.Services.ProxmoxSyncService proxmoxSync,
        NexusRDM.Services.NetworkDiscoveryService discovery,
        NexusRDM.Services.HyperVSyncService hyperVSync)
    {
        _svc      = svc;
        _sessions = sessions;
        _ping     = pingService;
        // Tree-node dots reflect live session status — re-evaluate
        // every time the manager's session set mutates.
        _sessions.Sessions.CollectionChanged += (_, _) => RefreshConnectionStatus();

        // Listen for ping results and update the matching node.
        _ping.EntryUpdated += OnPingUpdated;

        // Re-configure when the user flips the ping settings.
        SettingsStore.PingSettingsChanged += (_, _) => ApplyPingSettings();

        // Refresh the tree after a Proxmox sync — the cluster may have
        // added/removed/renamed VMs and the user expects to see those
        // immediately, not on next manual refresh.
        proxmoxSync.SourceSynced += (_, _) => RefreshTreeFromBackground();

        // Same for auto-discovery: a finished scan may have inserted
        // new connections under "Discovered". Failed scans (Error != null)
        // also fire ScanCompleted but Inserted will be 0; LoadAsync is
        // a cheap no-op in that case.
        discovery.ScanCompleted += (_, _) => RefreshTreeFromBackground();

        // And for the Hyper-V integration — same shape, same refresh.
        hyperVSync.SyncCompleted += (_, _) => RefreshTreeFromBackground();

        // Display-only toggles for the power-state icon (one toggle
        // per source). Not a sync; just push the new flag into every
        // existing node so the icon hides/shows without waiting for a
        // cluster / WMI roundtrip.
        SettingsStore.ProxmoxDisplaySettingsChanged += (_, _) => ApplyDisplaySettings();
        SettingsStore.HyperVDisplaySettingsChanged  += (_, _) => ApplyDisplaySettings();
    }

    private void ApplyDisplaySettings()
    {
        var pveShow = SettingsStore.ReadProxmoxShowPowerState();
        var hvShow  = SettingsStore.ReadHyperVShowPowerState();
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                       ?? App.MainWin?.DispatcherQueue;
        void Apply()
        {
            foreach (var n in EnumerateProfileNodes(RootItems))
            {
                var isHv = n.Profile?.ExternalId is { } id
                           && id.StartsWith("hyperv:", System.StringComparison.Ordinal);
                n.ShowPowerState = isHv ? hvShow : pveShow;
            }
        }
        if (dispatcher is null) Apply();
        else dispatcher.TryEnqueue(Apply);
    }

    private void RefreshTreeFromBackground()
    {
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                       ?? App.MainWin?.DispatcherQueue;
        if (dispatcher is null) { _ = LoadAsync(); return; }
        dispatcher.TryEnqueue(() => _ = LoadAsync());
    }

    private void OnPingUpdated(object? sender, NexusRDM.Services.PingUpdate u)
    {
        // Hop back to the UI thread before mutating ObservableProperty values.
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                       ?? App.MainWin?.DispatcherQueue;
        void Apply()
        {
            foreach (var n in EnumerateProfileNodes(RootItems))
                if (n.Profile is { } p && p.Id == u.ConnectionId)
                {
                    n.PingState = u.State;
                    n.LatencyMs = u.LatencyMs;
                }
        }
        if (dispatcher is null) Apply();
        else                    dispatcher.TryEnqueue(Apply);
    }

    private void ApplyPingSettings()
    {
        var enabled  = SettingsStore.ReadPingEnabled();
        var interval = SettingsStore.ReadPingIntervalSeconds();
        var show     = SettingsStore.ReadPingShowLatency();

        // Push the latest globals onto every existing node so the tree
        // reflects the current toggles immediately.
        foreach (var n in EnumerateProfileNodes(RootItems))
        {
            n.PingEnabled = enabled;
            n.ShowLatency = show;
            if (!enabled)
            {
                n.PingState = NexusRDM.Services.PingState.Unknown;
                n.LatencyMs = null;
            }
        }

        var targets = EnumerateProfileNodes(RootItems)
            .Where(n => n.Profile is not null)
            .Select(n => (n.Profile!.Id, n.Profile.Host));
        _ping.Configure(enabled, interval, targets);
    }

    /// <summary>Sweeps the tree and flips each node's IsLiveConnected
    /// based on whether the session manager currently owns a session
    /// with the matching ConnectionId. Cheap enough to call on every
    /// open/close — typical lists are dozens of items.</summary>
    private void RefreshConnectionStatus()
    {
        var live = _sessions.Sessions.Select(s => s.ConnectionId).ToHashSet();
        foreach (var n in EnumerateProfileNodes(RootItems))
            n.IsLiveConnected = n.Profile is { } p && live.Contains(p.Id);
    }

    private static IEnumerable<ConnectionTreeNode> EnumerateProfileNodes(IEnumerable<ConnectionTreeNode> roots)
    {
        foreach (var n in roots)
        {
            if (n.Profile is not null) yield return n;
            foreach (var c in EnumerateProfileNodes(n.Children)) yield return c;
        }
    }

    [RelayCommand]
    public async Task LoadAsync(string? query = null)
    {
        IsLoading = true;
        try
        {
            var profiles = string.IsNullOrWhiteSpace(query)
                ? await _svc.GetAllAsync()
                : await _svc.SearchAsync(query);
            var groups = await _svc.GetGroupsAsync();

            // Map each Proxmox source's RootGroupId → SourceId so we can
            // tag the matching group node and light up the "Sync now"
            // context-menu entry. Read fails are non-fatal (e.g. fresh
            // install before any source is registered).
            var proxmoxRoots = await LoadProxmoxRootMapAsync();

            // Build the desired tree shape, then diff-merge it into
            // RootItems. Earlier this was Clear()+Add(), which made
            // every refresh recreate every TreeViewItem (visible flash
            // + lost expansion / selection / animations). The merge
            // reuses existing node instances when their key (profile
            // or group id) is unchanged, so a sync that only edits
            // DisplayName / Host updates the row in place.
            var desired = new List<ConnectionTreeNode>();
            foreach (var p in profiles.Where(p => p.GroupId is null).OrderBy(p => p.DisplayName))
                desired.Add(new ConnectionTreeNode(p));
            foreach (var g in groups.Where(g => g.ParentId is null).OrderBy(g => g.SortOrder))
                desired.Add(BuildGroupNode(g, profiles, proxmoxRoots));

            MergeChildren(RootItems, desired);

            // Initial paint: reflect any sessions already open against
            // these profiles (e.g. tabs reopened from a prior search).
            RefreshConnectionStatus();
            // Re-arm the ping service against the freshly-loaded set of
            // hosts (Configure replaces the entire target set so adds
            // and removes both flow through cleanly).
            ApplyPingSettings();
        }
        finally { IsLoading = false; }
    }

    /// <summary>Merge <paramref name="src"/> into <paramref name="dst"/>
    /// by id. Existing nodes whose key still appears in src are kept
    /// and updated in place (delegated to UpdateFromProfile /
    /// UpdateFromGroupNode); nodes no longer in src are removed; new
    /// keys are inserted in source order. Recurses into Children so
    /// the whole tree merges in one pass.</summary>
    private static void MergeChildren(
        ObservableCollection<ConnectionTreeNode> dst,
        List<ConnectionTreeNode> src)
    {
        var desiredKeys = src.Select(KeyOf).ToList();
        var desiredKeySet = new HashSet<string>(desiredKeys);

        // Drop items not in src.
        for (int i = dst.Count - 1; i >= 0; i--)
            if (!desiredKeySet.Contains(KeyOf(dst[i])))
                dst.RemoveAt(i);

        var existingByKey = dst.ToDictionary(KeyOf);

        // Walk src in order, updating / inserting / reordering.
        for (int i = 0; i < src.Count; i++)
        {
            var s = src[i];
            var key = desiredKeys[i];

            if (existingByKey.TryGetValue(key, out var d))
            {
                if (d.Profile is not null && s.Profile is not null)
                    d.UpdateFromProfile(s.Profile);
                else if (d.Profile is null && s.Profile is null)
                    d.UpdateFromGroupNode(s);

                MergeChildren(d.Children, s.Children.ToList());

                var idx = dst.IndexOf(d);
                if (idx != i) dst.Move(idx, i);
            }
            else
            {
                dst.Insert(i, s);
                existingByKey[key] = s;
            }
        }
    }

    private static string KeyOf(ConnectionTreeNode n) =>
        n.Profile is { } p ? "p:" + p.Id
        : n.GroupId is { } g ? "g:" + g
        : "?";

    private static async Task<Dictionary<Guid, Guid>> LoadProxmoxRootMapAsync()
    {
        try
        {
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NexusRDM.Data.Context.NexusDbContext>();
            return await db.ProxmoxSources
                .Where(s => s.RootGroupId != null)
                .ToDictionaryAsync(s => s.RootGroupId!.Value, s => s.Id);
        }
        catch { return new Dictionary<Guid, Guid>(); }
    }

    private static ConnectionTreeNode BuildGroupNode(
        Group g, IReadOnlyList<ConnectionProfile> all, Dictionary<Guid, Guid> proxmoxRoots)
    {
        var node = new ConnectionTreeNode(g);
        if (proxmoxRoots.TryGetValue(g.Id, out var srcId)) node.ProxmoxSourceId = srcId;
        if (string.Equals(g.Name, NexusRDM.Services.NetworkDiscoveryService.DiscoveredGroupName,
                          StringComparison.Ordinal))
            node.IsDiscoveryRoot = true;
        // Hyper-V root: same auto-managed treatment as the Discovered
        // folder (italic + AUTO badge, undeletable from the tree —
        // disable the integration in Settings instead).
        if (string.Equals(g.Name, NexusRDM.Services.HyperVSyncService.HyperVGroupName,
                          StringComparison.Ordinal))
            node.IsHyperVRoot = true;
        foreach (var p in all.Where(p => p.GroupId == g.Id).OrderBy(p => p.DisplayName))
            node.Children.Add(new ConnectionTreeNode(p));
        foreach (var child in g.Children.OrderBy(x => x.SortOrder))
            node.Children.Add(BuildGroupNode(child, all, proxmoxRoots));
        return node;
    }

    /// <summary>Loads every group (including the synthetic "(none)"
    /// option) for use in pickers — the New-Group dialog and the
    /// edit-connection group selector both consume this.</summary>
    public async Task<IReadOnlyList<GroupPickItem>> LoadGroupsForPickerAsync()
    {
        var list = new List<GroupPickItem> { new(null, "(none)") };
        foreach (var g in (await _svc.GetGroupsAsync()).OrderBy(g => g.Name))
            list.Add(new GroupPickItem(g.Id, g.Name));
        return list;
    }

    /// <summary>Creates a new group and reloads the tree.</summary>
    public async Task CreateGroupAsync(string name, Guid? parentId)
    {
        await _svc.CreateGroupAsync(new Core.Models.Group
        {
            Name     = name,
            ParentId = parentId,
        });
        await LoadAsync();
    }

    [RelayCommand]
    public async Task NewConnectionAsync()
    {
        var saved = await App.MainWin.ShowEditConnectionPanelAsync(null);
        if (saved is not null) await LoadAsync();
    }

    [RelayCommand]
    public async Task EditConnectionAsync(ConnectionTreeNode? node)
    {
        if (node?.Profile is null) return;
        var saved = await App.MainWin.ShowEditConnectionPanelAsync(node.Profile);
        if (saved is not null) await LoadAsync();
    }

    [RelayCommand]
    public async Task DeleteConnectionAsync(ConnectionTreeNode? node)
    {
        if (node?.Profile is null) return;
        var warning = await _svc.DeleteAsync(node.Profile.Id);
        await LoadAsync();

        // Surface vault-cleanup failures to the user — the DB row is gone
        // but the credential lingers, and silent log-only is too easy to
        // miss. Best effort: if MainWin isn't around (tests), skip.
        if (!string.IsNullOrEmpty(warning) && App.MainWin?.Content is FrameworkElement root)
        {
            var dlg = new ContentDialog
            {
                Title             = "Credential not removed",
                Content           = warning,
                CloseButtonText   = "OK",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = root.XamlRoot,
            };
            try { await NexusRDM.Services.DialogHost.ShowAsync(dlg); } catch { /* dialog host gone */ }
        }
    }

    // NewGroup was a placeholder for the "create connection group" UX
    // that hasn't been built; the toolbar button has been removed too.
    // Keeping the method commented-out anchor so the grep doesn't lie.

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();
    private bool CanRefresh() => !IsLoading;
}

/// <summary>One option in a group ComboBox — either the synthetic
/// "(none)" entry or a real group.</summary>
public sealed record GroupPickItem(Guid? Id, string DisplayName);

public sealed partial class ConnectionTreeNode : ObservableObject
{
    // Status-driven dot colors. The protocol distinction lives on the
    // SSH/RDP badge text now — the dot is purely connection state.
    private static readonly Color ConnectedColor    = Color.FromArgb(0xFF, 0x3D, 0xD6, 0x8C); // green
    private static readonly Color DisconnectedColor = Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B); // red
    private static readonly Color GroupColor        = Color.FromArgb(0xFF, 0x60, 0x60, 0x70); // grey

    // Mutable so the diff-merge in ConnectionsViewModel.LoadAsync can
    // update existing nodes in place rather than replacing them. The
    // observability (PropertyChanged) keeps the WinUI bindings in
    // sync — TreeView no longer recreates TreeViewItems on a refresh
    // when only display fields changed.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayNameFontStyle))]
    private string _displayName = string.Empty;
    [ObservableProperty] private string     _badgeText       = string.Empty;
    [ObservableProperty] private Visibility _badgeVisibility = Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconGlyph))]
    [NotifyPropertyChangedFor(nameof(IconVisibility))]
    [NotifyPropertyChangedFor(nameof(IconColor))]
    [NotifyPropertyChangedFor(nameof(HasCustomIconColor))]
    [NotifyPropertyChangedFor(nameof(StatusDotVisibility))]
    [NotifyPropertyChangedFor(nameof(DotColor))]
    [NotifyPropertyChangedFor(nameof(DisplayNameFontWeight))]
    [NotifyPropertyChangedFor(nameof(DisplayNameFontStyle))]
    [NotifyPropertyChangedFor(nameof(PingIconVisibility))]
    private ConnectionProfile? _profile;

    public ObservableCollection<ConnectionTreeNode> Children { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DotColor))]
    [NotifyPropertyChangedFor(nameof(IconColor))]
    private bool _isLiveConnected;

    public Color DotColor => Profile is null
        ? GroupColor
        : (IsLiveConnected ? ConnectedColor : DisconnectedColor);

    /// <summary>Per-row Segoe Fluent glyph. Groups always show a folder
    /// glyph so they read as containers; connections show whatever the
    /// user picked (or nothing, if they left the icon empty).</summary>
    public string IconGlyph =>
        Profile is null
            ? ""   // FolderHorizontal — distinct, immediately reads as "container"
            : (Profile.IconGlyph is { Length: > 0 } g ? g : string.Empty);

    /// <summary>Hides the FontIcon for connections that have no glyph
    /// chosen. Groups always show theirs.</summary>
    public Visibility IconVisibility =>
        string.IsNullOrEmpty(IconGlyph) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Color for the row icon. Groups use a distinct amber so
    /// they pop against connection rows. Connections honour any
    /// per-profile <c>IconColorHex</c> override; otherwise fall back to
    /// the status-driven palette (green=connected, red=otherwise).</summary>
    public Color IconColor
    {
        get
        {
            if (Profile is null) return Color.FromArgb(0xFF, 0xF0, 0xA7, 0x32);
            if (TryParseHex(Profile.IconColorHex) is { } overrideColor) return overrideColor;
            return DotColor;
        }
    }

    /// <summary>True when the profile has a custom icon colour pinned —
    /// the connections tree shows a small status dot beside the icon in
    /// that case so connection state is still visible at a glance.</summary>
    public bool HasCustomIconColor =>
        Profile is { IconColorHex: { Length: > 0 } } &&
        TryParseHex(Profile.IconColorHex) is not null;

    public Visibility StatusDotVisibility =>
        Profile is not null && HasCustomIconColor ? Visibility.Visible : Visibility.Collapsed;

    private static Color? TryParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var s = hex.TrimStart('#');
            if (s.Length == 6) s = "FF" + s;
            var argb = Convert.ToUInt32(s, 16);
            return Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >>  8) & 0xFF),
                (byte)( argb        & 0xFF));
        }
        catch { return null; }
    }

    /// <summary>Bold name for groups so the hierarchy reads at a glance,
    /// regular weight for connections.</summary>
    public Windows.UI.Text.FontWeight DisplayNameFontWeight => Profile is null
        ? new Windows.UI.Text.FontWeight(600)   // SemiBold
        : new Windows.UI.Text.FontWeight(400);  // Normal

    /// <summary>Italic for groups owned by an external system (Proxmox
    /// sync, auto-discovery) so they read as "managed elsewhere" at a
    /// glance — pairs with the AUTO pill on the same row.</summary>
    public Windows.UI.Text.FontStyle DisplayNameFontStyle =>
        Profile is null && IsExternallyManaged
            ? Windows.UI.Text.FontStyle.Italic
            : Windows.UI.Text.FontStyle.Normal;

    /// <summary>"AUTO" pill drawn on auto-managed group rows. Hidden
    /// elsewhere; the pill is in addition to the italic name and gives
    /// users a one-glance signal that delete/rename here won't stick.</summary>
    public Visibility AutoManagedBadgeVisibility =>
        Profile is null && IsExternallyManaged
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ── Ping status (drives a small icon next to the row) ────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingIconColor))]
    [NotifyPropertyChangedFor(nameof(PingIconVisibility))]
    [NotifyPropertyChangedFor(nameof(LatencyText))]
    private NexusRDM.Services.PingState _pingState = NexusRDM.Services.PingState.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyText))]
    private long? _latencyMs;

    /// <summary>Mirrors the global "show latency" setting; toggled by
    /// ConnectionsViewModel when the user flips it in Settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyVisibility))]
    private bool _showLatency;

    /// <summary>Mirrors the global "ping enabled" setting; hides the
    /// status icon entirely when ping is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingIconVisibility))]
    [NotifyPropertyChangedFor(nameof(LatencyVisibility))]
    private bool _pingEnabled;

    // ── Proxmox power state (running / stopped / paused glyph) ───────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerStateGlyph))]
    [NotifyPropertyChangedFor(nameof(PowerStateColor))]
    [NotifyPropertyChangedFor(nameof(PowerStateVisibility))]
    [NotifyPropertyChangedFor(nameof(PowerStateTooltip))]
    private NexusRDM.Services.ProxmoxPowerState _powerState = NexusRDM.Services.ProxmoxPowerState.Unknown;

    /// <summary>UTC timestamp of the most recent sync that wrote this
    /// row's power state. Null = never synced. Drives the
    /// <see cref="IsPowerStateStale"/> flag so the icon dims and the
    /// tooltip flips to "Stale — last seen … N min ago" when the
    /// cache hasn't been refreshed in a while.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerStateGlyph))]
    [NotifyPropertyChangedFor(nameof(PowerStateColor))]
    [NotifyPropertyChangedFor(nameof(PowerStateTooltip))]
    [NotifyPropertyChangedFor(nameof(IsPowerStateStale))]
    private DateTime? _powerStateUpdatedAt;

    /// <summary>True when the cached state is older than 10 minutes.
    /// Picks a single conservative threshold rather than chasing each
    /// source's sync interval — most setups poll every 1–15 min, so
    /// 10 covers the common case while still flagging genuinely
    /// out-of-date data on manual-sync (Hyper-V) integrations.
    /// Visual cue is a desaturated color (the play/stop/pause glyph
    /// shape stays put) so it doesn't collide with the latency "?"
    /// that means "no measurement". Tooltip carries the age.</summary>
    public bool IsPowerStateStale =>
        PowerStateUpdatedAt is { } at
        && DateTime.UtcNow - at > TimeSpan.FromMinutes(10);

    /// <summary>Mirrors <c>ProxmoxShowPowerState</c> — pushed in by
    /// ConnectionsViewModel when the user toggles the option so the
    /// icon hides/shows without waiting for the next sync.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerStateVisibility))]
    private bool _showPowerState = true;

    public string PowerStateGlyph => PowerState switch
    {
        // Segoe Fluent Icons.
        NexusRDM.Services.ProxmoxPowerState.Running => "", // Play
        NexusRDM.Services.ProxmoxPowerState.Stopped => "", // Stop
        NexusRDM.Services.ProxmoxPowerState.Paused  => "", // Pause
        _ => string.Empty,
    };

    public Color PowerStateColor => IsPowerStateStale
        ? Color.FromArgb(0xFF, 0x80, 0x80, 0x90)   // muted gray when stale
        : PowerState switch
    {
        NexusRDM.Services.ProxmoxPowerState.Running => Color.FromArgb(0xFF, 0x3D, 0xD6, 0x8C),
        NexusRDM.Services.ProxmoxPowerState.Stopped => Color.FromArgb(0xFF, 0xA0, 0xA0, 0xB0),
        NexusRDM.Services.ProxmoxPowerState.Paused  => Color.FromArgb(0xFF, 0xF0, 0xA7, 0x32),
        _ => Color.FromArgb(0, 0, 0, 0),
    };

    public Visibility PowerStateVisibility =>
        Profile is not null
        && IsManaged
        && ShowPowerState
        && PowerState != NexusRDM.Services.ProxmoxPowerState.Unknown
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Hover text for the power-state glyph. The icons alone
    /// aren't always self-evident at 11px — the tooltip spells out
    /// what each state means so a screen-reader / hover lands on real
    /// words rather than a Segoe codepoint.</summary>
    public string PowerStateTooltip
    {
        get
        {
            var label = PowerState switch
            {
                NexusRDM.Services.ProxmoxPowerState.Running => "Running",
                NexusRDM.Services.ProxmoxPowerState.Stopped => "Stopped",
                NexusRDM.Services.ProxmoxPowerState.Paused  => "Paused",
                _ => string.Empty,
            };
            if (string.IsNullOrEmpty(label)) return string.Empty;
            if (!IsPowerStateStale) return label;

            // Stale: surface the last-known value AND how long ago we
            // saw it, so the user knows whether to trust the dot.
            if (PowerStateUpdatedAt is { } at)
            {
                var age = DateTime.UtcNow - at;
                var ago = age.TotalMinutes < 60
                    ? $"{(int)age.TotalMinutes} min ago"
                    : $"{age.TotalHours:F1} hours ago";
                return $"Stale — last seen {label} {ago}. Click Sync to refresh.";
            }
            return $"Stale — last seen {label}.";
        }
    }

    public Color PingIconColor => PingState switch
    {
        NexusRDM.Services.PingState.Ok      => Color.FromArgb(0xFF, 0x3D, 0xD6, 0x8C),
        NexusRDM.Services.PingState.Failed  => Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B),
        NexusRDM.Services.PingState.Pinging => Color.FromArgb(0xFF, 0xF0, 0xA7, 0x32),
        _                                   => Color.FromArgb(0xFF, 0x60, 0x60, 0x70),
    };

    public Visibility PingIconVisibility =>
        Profile is not null && PingEnabled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What goes in the latency cell. <c>"?"</c> covers the
    /// "ping is enabled but we haven't got a measurement yet" case
    /// (Unknown / Pinging / Failed) so the column doesn't visibly
    /// flicker between numbers and blanks as states change.</summary>
    public string LatencyText =>
        LatencyMs is { } ms && PingState == NexusRDM.Services.PingState.Ok
            ? $"{ms} ms"
            : "?";

    /// <summary>Show the latency cell whenever the user opted into the
    /// column AND ping is actually running for this row. We hide it
    /// for groups (Profile == null) and when ping is globally off so
    /// the "?" doesn't appear next to rows that aren't being polled.</summary>
    public Visibility LatencyVisibility =>
        ShowLatency && Profile is not null && PingEnabled
            ? Visibility.Visible : Visibility.Collapsed;

    public ConnectionTreeNode(ConnectionProfile p)
    {
        Profile         = p;
        DisplayName     = p.DisplayName;
        BadgeText       = p.Protocol == ConnectionProtocol.Ssh ? "SSH" : "RDP";
        BadgeVisibility = Visibility.Visible;
        IsManaged       = p.IsManaged;
        SeedPingState(p.Id);
        SeedPowerState(p.Id);
    }

    /// <summary>Pull last-known power state so the tree's
    /// running/stopped/paused glyph survives reloads. Source-aware:
    /// Hyper-V rows (ExternalId starts with "hyperv:") read from
    /// HyperVSyncService; everything else managed reads from
    /// ProxmoxSyncService. The visibility flag also follows source
    /// so each toggle controls only its own VMs.</summary>
    private void SeedPowerState(Guid connectionId)
    {
        try
        {
            var isHv = Profile?.ExternalId is { } id
                       && id.StartsWith("hyperv:", StringComparison.Ordinal);
            if (isHv)
            {
                if (App.Services?.GetService(typeof(NexusRDM.Services.HyperVSyncService))
                    is NexusRDM.Services.HyperVSyncService hv)
                {
                    var info = hv.GetPowerStateInfo(connectionId);
                    _powerState = info.State;
                    _powerStateUpdatedAt = info.UpdatedAtUtc;
                }
                _showPowerState = SettingsStore.ReadHyperVShowPowerState();
            }
            else
            {
                if (App.Services?.GetService(typeof(NexusRDM.Services.ProxmoxSyncService))
                    is NexusRDM.Services.ProxmoxSyncService sync)
                {
                    var info = sync.GetPowerStateInfo(connectionId);
                    _powerState = info.State;
                    _powerStateUpdatedAt = info.UpdatedAtUtc;
                }
                _showPowerState = SettingsStore.ReadProxmoxShowPowerState();
            }
        }
        catch { /* tests / pre-DI — defaults are fine */ }
    }

    public ConnectionTreeNode(Group g)
    {
        GroupId         = g.Id;
        DisplayName     = g.Name;
        BadgeText       = string.Empty;
        BadgeVisibility = Visibility.Collapsed;
    }

    /// <summary>Pull this node's ping state from PingService's
    /// last-known cache so the latency column survives tree reloads
    /// (sync, scan completion, settings change). The cache outlives
    /// tree-node lifetimes; new nodes pick up where old nodes left
    /// off until the next ping cycle refreshes them.</summary>
    private void SeedPingState(Guid connectionId)
    {
        try
        {
            var ping = App.Services?.GetService(typeof(NexusRDM.Services.PingService))
                       as NexusRDM.Services.PingService;
            if (ping is not null)
            {
                var (state, ms) = ping.GetLast(connectionId);
                _pingState = state;
                _latencyMs = ms;
            }
        }
        catch { /* App.Services unavailable at construction (tests) — defaults are fine */ }
    }

    /// <summary>Re-bind to a fresh <see cref="ConnectionProfile"/>.
    /// Called by the diff-merge in <see cref="ConnectionsViewModel.LoadAsync"/>
    /// to update mutable fields in place when the existing node still
    /// represents the same connection (matched by Id). Avoids the
    /// flicker that <c>RootItems.Clear()+Add()</c> caused.</summary>
    public void UpdateFromProfile(ConnectionProfile p)
    {
        Profile         = p;
        DisplayName     = p.DisplayName;
        BadgeText       = p.Protocol == ConnectionProtocol.Ssh ? "SSH" : "RDP";
        BadgeVisibility = Visibility.Visible;
        IsManaged       = p.IsManaged;
        // Sync just wrote fresh values into ProxmoxSyncService /
        // HyperVSyncService's caches; pull the new state in so the
        // power-state glyph reflects reality. Without this, a node
        // created before the first sync stays Unknown forever (the
        // ctor seeded from an empty cache, the diff-merge reused the
        // node, and SeedPowerState was never re-run).
        RefreshPowerState();
    }

    /// <summary>Pull the latest power state from whichever sync
    /// service owns this row. Goes through the public
    /// <see cref="PowerState"/> property so the
    /// <c>NotifyPropertyChangedFor</c> chain fires and the bound
    /// FontIcon re-evaluates its visibility.</summary>
    public void RefreshPowerState()
    {
        if (Profile is null) return;
        try
        {
            var isHv = Profile.ExternalId is { } id
                       && id.StartsWith("hyperv:", StringComparison.Ordinal);
            if (isHv && App.Services?.GetService(typeof(NexusRDM.Services.HyperVSyncService))
                is NexusRDM.Services.HyperVSyncService hv)
            {
                var info = hv.GetPowerStateInfo(Profile.Id);
                PowerState = info.State;
                PowerStateUpdatedAt = info.UpdatedAtUtc;
            }
            else if (!isHv && App.Services?.GetService(typeof(NexusRDM.Services.ProxmoxSyncService))
                is NexusRDM.Services.ProxmoxSyncService pve)
            {
                var info = pve.GetPowerStateInfo(Profile.Id);
                PowerState = info.State;
                PowerStateUpdatedAt = info.UpdatedAtUtc;
            }
        }
        catch { /* DI unavailable in tests */ }
    }

    /// <summary>In-place update for group nodes. Reads display fields
    /// from the source node rather than a Group instance because the
    /// caller has already constructed the desired-state node.</summary>
    public void UpdateFromGroupNode(ConnectionTreeNode src)
    {
        DisplayName     = src.DisplayName;
        ProxmoxSourceId = src.ProxmoxSourceId;
        IsDiscoveryRoot = src.IsDiscoveryRoot;
    }

    /// <summary>Underlying Group.Id when this node represents a group;
    /// null for connection-profile nodes. Surfaced so the right-click
    /// "Delete group" menu can call IConnectionService.DeleteGroupAsync
    /// without re-querying the DB.</summary>
    public Guid? GroupId { get; }

    /// <summary>True when the underlying profile was imported from
    /// any auto-sync (Proxmox cluster, Hyper-V host, …). Surfaces a
    /// small per-source pill in the tree so the user can tell synced
    /// rows from manual ones at a glance — and from each other.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManagedBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(ManagedBadgeText))]
    private bool _isManaged;

    public Visibility ManagedBadgeVisibility =>
        IsManaged ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Three-letter source label drawn inside the managed
    /// pill. Differentiating Proxmox ("PVE") from Hyper-V ("HV")
    /// avoids the "what does P mean here?" confusion the original
    /// generic-P badge caused on Hyper-V rows. Falls back to an
    /// ambiguous "SYNC" if a future source forgets to add itself.</summary>
    public string ManagedBadgeText
    {
        get
        {
            if (Profile is not { ExternalId: { } extId }) return string.Empty;
            if (extId.StartsWith("hyperv:", StringComparison.Ordinal)) return "HV";
            // Proxmox is the original source — no prefix in ExternalId
            // (it's "{node}/{type}/{vmid}"). Anything that's managed
            // and isn't another known prefix is treated as Proxmox.
            return "PVE";
        }
    }

    /// <summary>Set on group nodes that are the root group of a
    /// registered Proxmox source. Drives the "Sync now" entry in the
    /// right-click menu and could later drive a cluster glyph.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProxmoxSourceRoot))]
    [NotifyPropertyChangedFor(nameof(IsExternallyManaged))]
    [NotifyPropertyChangedFor(nameof(AutoManagedBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(DisplayNameFontStyle))]
    private Guid? _proxmoxSourceId;

    public bool IsProxmoxSourceRoot => ProxmoxSourceId.HasValue;

    /// <summary>Set on the auto-created <c>Discovered</c> group so the
    /// right-click menu can hide its delete entry. The group is
    /// owned by <see cref="NexusRDM.Services.NetworkDiscoveryService"/>
    /// — disabling scheduled scans removes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExternallyManaged))]
    [NotifyPropertyChangedFor(nameof(AutoManagedBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(DisplayNameFontStyle))]
    private bool _isDiscoveryRoot;

    /// <summary>Set on the auto-created <c>Hyper-V</c> group. Same
    /// undeletable + AUTO-badge treatment as the Discovered folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExternallyManaged))]
    [NotifyPropertyChangedFor(nameof(AutoManagedBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(DisplayNameFontStyle))]
    private bool _isHyperVRoot;

    /// <summary>True when this group is owned by an external system
    /// (Proxmox sync, auto-discovery, Hyper-V sync) and should not be
    /// deletable from the tree's right-click menu. Centralised so new
    /// auto-managed group types can hook in without touching the menu.</summary>
    public bool IsExternallyManaged => IsProxmoxSourceRoot || IsDiscoveryRoot || IsHyperVRoot;
}
