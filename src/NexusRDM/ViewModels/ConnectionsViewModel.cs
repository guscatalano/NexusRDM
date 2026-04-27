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

    public ConnectionsViewModel(IConnectionService svc, NexusRDM.Services.SessionManager sessions)
    {
        _svc      = svc;
        _sessions = sessions;
        // Tree-node dots reflect live session status — re-evaluate
        // every time the manager's session set mutates.
        _sessions.Sessions.CollectionChanged += (_, _) => RefreshConnectionStatus();
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
            try { await dlg.ShowAsync(); } catch { /* dialog host gone */ }
        }
    }

    // NewGroup was a placeholder for the "create connection group" UX
    // that hasn't been built; the toolbar button has been removed too.
    // Keeping the method commented-out anchor so the grep doesn't lie.

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();
    private bool CanRefresh() => !IsLoading;
}

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
