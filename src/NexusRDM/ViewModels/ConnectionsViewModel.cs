using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using System.Collections.ObjectModel;

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

    // RelayCommand strips "Async" → generates NewConnectionCommand, EditConnectionCommand,
    // DeleteConnectionCommand, RefreshCommand — no manual aliases needed.

    [RelayCommand]
    public async Task NewConnectionAsync()
    {
        var saved = await Views.EditConnectionDialog.ShowForNewAsync();
        if (saved is not null) await LoadAsync();
    }

    [RelayCommand]
    public async Task EditConnectionAsync(ConnectionTreeNode? node)
    {
        if (node?.Profile is null) return;
        var saved = await Views.EditConnectionDialog.ShowForEditAsync(node.Profile);
        if (saved is not null) await LoadAsync();
    }

    [RelayCommand]
    public async Task DeleteConnectionAsync(ConnectionTreeNode? node)
    {
        if (node?.Profile is null) return;
        await _svc.DeleteAsync(node.Profile.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private void NewGroup() { /* TODO M4 */ }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();
    private bool CanRefresh() => !IsLoading;
}

public sealed class ConnectionTreeNode
{
    public string DisplayName { get; }
    public string Glyph      { get; }
    public ConnectionProfile? Profile { get; }
    public ObservableCollection<ConnectionTreeNode> Children { get; } = [];

    public ConnectionTreeNode(ConnectionProfile p)
    {
        Profile = p; DisplayName = p.DisplayName;
        Glyph = p.Protocol == ConnectionProtocol.Ssh ? "\uE704" : "\uE8AF";
    }
    public ConnectionTreeNode(Group g) { DisplayName = g.Name; Glyph = "\uE8B7"; }
}
