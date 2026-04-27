using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        NexusRDM.Services.PingService pingService)
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
            RootItems.Clear();
            foreach (var p in profiles.Where(p => p.GroupId is null).OrderBy(p => p.DisplayName))
                RootItems.Add(new ConnectionTreeNode(p));
            foreach (var g in groups.Where(g => g.ParentId is null).OrderBy(g => g.SortOrder))
                RootItems.Add(BuildGroupNode(g, profiles));
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

    private static ConnectionTreeNode BuildGroupNode(Group g, IReadOnlyList<ConnectionProfile> all)
    {
        var node = new ConnectionTreeNode(g);
        foreach (var p in all.Where(p => p.GroupId == g.Id).OrderBy(p => p.DisplayName))
            node.Children.Add(new ConnectionTreeNode(p));
        foreach (var child in g.Children.OrderBy(x => x.SortOrder))
            node.Children.Add(BuildGroupNode(child, all));
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

    public string             DisplayName    { get; }
    public string             BadgeText      { get; }
    public Visibility         BadgeVisibility { get; }
    public ConnectionProfile? Profile        { get; }
    public ObservableCollection<ConnectionTreeNode> Children { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DotColor))]
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

    // ── Ping status (drives a small icon next to the row) ────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingIconColor))]
    [NotifyPropertyChangedFor(nameof(PingIconVisibility))]
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
    private bool _pingEnabled;

    public Color PingIconColor => PingState switch
    {
        NexusRDM.Services.PingState.Ok      => Color.FromArgb(0xFF, 0x3D, 0xD6, 0x8C),
        NexusRDM.Services.PingState.Failed  => Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B),
        NexusRDM.Services.PingState.Pinging => Color.FromArgb(0xFF, 0xF0, 0xA7, 0x32),
        _                                   => Color.FromArgb(0xFF, 0x60, 0x60, 0x70),
    };

    public Visibility PingIconVisibility =>
        Profile is not null && PingEnabled ? Visibility.Visible : Visibility.Collapsed;

    public string LatencyText =>
        LatencyMs is { } ms && PingState == NexusRDM.Services.PingState.Ok
            ? $"{ms} ms" : string.Empty;

    public Visibility LatencyVisibility =>
        ShowLatency && !string.IsNullOrEmpty(LatencyText)
            ? Visibility.Visible : Visibility.Collapsed;

    public ConnectionTreeNode(ConnectionProfile p)
    {
        Profile         = p;
        DisplayName     = p.DisplayName;
        BadgeText       = p.Protocol == ConnectionProtocol.Ssh ? "SSH" : "RDP";
        BadgeVisibility = Visibility.Visible;
    }

    public ConnectionTreeNode(Group g)
    {
        DisplayName     = g.Name;
        BadgeText       = string.Empty;
        BadgeVisibility = Visibility.Collapsed;
    }
}
