using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using System.Collections.ObjectModel;

namespace NexusRDM.ViewModels;

public sealed partial class AuditLogViewModel : ObservableObject
{
    private readonly IConnectionService _svc;

    public ObservableCollection<AuditEntry> Entries { get; } = [];

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => _ = LoadAsync();

    public AuditLogViewModel(IConnectionService svc) => _svc = svc;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var all = await _svc.GetRecentAuditAsync(200);
            var filtered = string.IsNullOrWhiteSpace(FilterText)
                ? all
                : all.Where(e => e.DisplayName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                              || e.Action.ToString().Contains(FilterText, StringComparison.OrdinalIgnoreCase));
            Entries.Clear();
            foreach (var e in filtered) Entries.Add(e);
        }
        finally { IsLoading = false; }
    }
}
