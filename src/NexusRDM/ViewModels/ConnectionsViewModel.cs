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

    public ObservableCollection<ConnectionTreeNode> RootItems { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _searchQuery = string.Empty;
    partial void OnSearchQueryChanged(string value) => _ = LoadAsync(value);

    public ConnectionsViewModel(IConnectionService svc) => _svc = svc;

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

    [RelayCommand]
    private void NewGroup() { }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();
    private bool CanRefresh() => !IsLoading;
}

public sealed class ConnectionTreeNode
{
    // Prototype dot colors
    private static readonly Color SshOnColor  = Color.FromArgb(0xFF, 0x3D, 0xD6, 0x8C); // #3DD68C
    private static readonly Color RdpOnColor  = Color.FromArgb(0xFF, 0x4D, 0xA6, 0xFF); // #4DA6FF
    private static readonly Color OffColor    = Color.FromArgb(0xFF, 0x40, 0x40, 0x50); // #404050
    private static readonly Color GroupColor  = Color.FromArgb(0xFF, 0x60, 0x60, 0x70); // folder grey

    public string             DisplayName    { get; }
    public Color              DotColor       { get; }
    public string             BadgeText      { get; }
    public Visibility         BadgeVisibility { get; }
    public ConnectionProfile? Profile        { get; }
    public ObservableCollection<ConnectionTreeNode> Children { get; } = [];

    public ConnectionTreeNode(ConnectionProfile p)
    {
        Profile     = p;
        DisplayName = p.DisplayName;
        // Show connected dot if was recently connected, grey otherwise
        DotColor    = p.Protocol == ConnectionProtocol.Ssh
            ? (p.LastConnectedAt.HasValue ? SshOnColor : OffColor)
            : (p.LastConnectedAt.HasValue ? RdpOnColor : OffColor);
        BadgeText       = p.Protocol == ConnectionProtocol.Ssh ? "SSH" : "RDP";
        BadgeVisibility = Visibility.Visible;
    }

    public ConnectionTreeNode(Group g)
    {
        DisplayName     = g.Name;
        DotColor        = GroupColor;
        BadgeText       = string.Empty;
        BadgeVisibility = Visibility.Collapsed;
    }
}
