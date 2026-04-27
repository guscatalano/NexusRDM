using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Core.Services;
using System.Collections.ObjectModel;

namespace NexusRDM.ViewModels;

public sealed partial class AuditLogViewModel : ObservableObject
{
    private readonly IConnectionService _svc;
    private readonly DispatcherQueue?   _ui = TryGetDispatcher();
    private static DispatcherQueue? TryGetDispatcher()
    {
        try   { return DispatcherQueue.GetForCurrentThread(); }
        catch { return null; }
    }

    public ObservableCollection<AuditEntry> Entries { get; } = [];

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => _ = LoadAsync();

    public AuditLogViewModel(IConnectionService svc, IAuditNotifier? notifier = null)
    {
        _svc = svc;
        // Primary auto-refresh: notifier fires the moment ConnectionService
        // writes an entry. Hop back to the UI thread before LoadAsync.
        if (notifier is not null)
            notifier.EntryWritten += (_, _) =>
            {
                if (_ui is null) _ = LoadAsync();
                else             _ui.TryEnqueue(() => _ = LoadAsync());
            };

        // Polling backup: reload every 3s regardless. Catches anything
        // the notifier path misses (e.g. an entry written from a scope
        // we didn't wire, or future tools that write directly through
        // IAuditRepository without going through ConnectionService).
        // Cheap — the typical query is a 200-row top-N over an indexed
        // timestamp column.
        if (_ui is not null)
        {
            var timer = _ui.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.IsRepeating = true;
            timer.Tick += (_, _) => { if (!IsLoading) _ = LoadAsync(); };
            timer.Start();
        }
    }

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
