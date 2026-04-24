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

    [ObservableProperty]
    private string _searchQuery = string.Empty;

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

    private static ConnectionTreeNode BuildGroupNode(Group group,
        IReadOnlyList<ConnectionProfile> all)
    {
        var node = new ConnectionTreeNode(group);
        foreach (var p in all.Where(p => p.GroupId == group.Id).OrderBy(p => p.DisplayName))
            node.Children.Add(new ConnectionTreeNode(p));
        foreach (var child in group.Children.OrderBy(g => g.SortOrder))
            node.Children.Add(BuildGroupNode(child, all));
        return node;
    }

    [RelayCommand]
    private void NewConnection() { /* TODO M1: open EditConnectionDialog */ }

    [RelayCommand]
    private void NewGroup() { /* TODO M1: open NewGroupDialog */ }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();
    private bool CanRefresh()   => !IsLoading;

    // XAML-friendly alias
    public IAsyncRelayCommand RefreshCommand => RefreshAsyncCommand;
}

/// <summary>Unified tree node for both Group folders and ConnectionProfile leaves.</summary>
public sealed class ConnectionTreeNode
{
    public string                                   DisplayName { get; }
    public string                                   Glyph       { get; }
    public ConnectionProfile?                       Profile     { get; }
    public ObservableCollection<ConnectionTreeNode> Children    { get; } = [];

    public ConnectionTreeNode(ConnectionProfile p)
    {
        Profile     = p;
        DisplayName = p.DisplayName;
        Glyph       = p.Protocol == ConnectionProtocol.Ssh ? "\uE704" : "\uE8AF";
    }

    public ConnectionTreeNode(Group g)
    {
        DisplayName = g.Name;
        Glyph       = "\uE8B7"; // folder icon
    }
}
