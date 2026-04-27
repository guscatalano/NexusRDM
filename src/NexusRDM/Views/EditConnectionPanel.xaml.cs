using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.ViewModels;

namespace NexusRDM.Views;

public sealed partial class EditConnectionPanel : UserControl
{
    public EditConnectionViewModel ViewModel { get; }

    private readonly ICredentialVault _vault;
    private readonly TaskCompletionSource<ConnectionProfile?> _tcs = new();

    public Task<ConnectionProfile?> Result => _tcs.Task;

    public EditConnectionPanel(ConnectionProfile? existing)
    {
        _vault    = App.Services.GetRequiredService<ICredentialVault>();
        var svc   = App.Services.GetRequiredService<IConnectionService>();
        ViewModel = new EditConnectionViewModel(svc, existing, _vault);

        InitializeComponent();
        _ = ViewModel.LoadGroupsAsync();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.TrySaveAsync(_vault))
            _tcs.TrySetResult(ViewModel.SavedProfile);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) =>
        _tcs.TrySetResult(null);

    // Click outside the panel (on the scrim) → cancel.
    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) =>
        _tcs.TrySetResult(null);

    // Swallow taps inside the panel so they don't bubble to the scrim.
    private void OnPanelTapped(object sender, TappedRoutedEventArgs e) =>
        e.Handled = true;

    // Tracks every element the search filter has hidden so we can restore
    // exactly those, leaving alone things that bindings (e.g. SSH/RDP
    // protocol-section visibility) had collapsed independently.
    private readonly HashSet<FrameworkElement> _searchHidden = new();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Restore previous filter pass.
        foreach (var fe in _searchHidden) fe.Visibility = Visibility.Visible;
        _searchHidden.Clear();

        var query = SearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0) return;

        FilterElement(BodyStack, query.ToLowerInvariant());
    }

    /// <summary>Walks <paramref name="el"/> and its descendants, hiding
    /// any element (and only those) whose own searchable text doesn't
    /// match <paramref name="query"/> AND that has no matching descendant.
    /// Returns true when this element (or one of its descendants)
    /// matched, so parents can decide whether to keep themselves
    /// visible.</summary>
    private bool FilterElement(FrameworkElement el, string query)
    {
        // Skip elements a binding has already collapsed (e.g. the SSH
        // section while editing an RDP profile). We don't fight those.
        if (el.Visibility == Visibility.Collapsed) return false;

        var matchesSelf = ElementMatchesSearch(el, query);

        // Recurse into containers we know how to walk.
        var hasMatchingChild = false;
        foreach (var child in EnumerateLogicalChildren(el))
        {
            if (FilterElement(child, query)) hasMatchingChild = true;
        }

        if (matchesSelf || hasMatchingChild)
        {
            // Auto-expand the Advanced expander when something inside it
            // matches; otherwise the user wouldn't see the result.
            if (el is Expander ex) ex.IsExpanded = true;
            return true;
        }

        el.Visibility = Visibility.Collapsed;
        _searchHidden.Add(el);
        return false;
    }

    private static IEnumerable<FrameworkElement> EnumerateLogicalChildren(FrameworkElement el) =>
        el switch
        {
            Panel p          => p.Children.OfType<FrameworkElement>(),
            Border b         => b.Child is FrameworkElement bc ? new[] { bc } : Array.Empty<FrameworkElement>(),
            ContentControl c => c.Content is FrameworkElement cc ? new[] { cc } : Array.Empty<FrameworkElement>(),
            _ => Array.Empty<FrameworkElement>(),
        };

    /// <summary>True if <paramref name="el"/>'s own searchable text
    /// (header / placeholder / content + the optional <c>Tag</c>)
    /// contains the query. Section-header TextBlocks are intentionally
    /// match-only on their own text so they hide unless the user types
    /// a term that's in their label.</summary>
    private static bool ElementMatchesSearch(FrameworkElement el, string query)
    {
        var text = el switch
        {
            TextBox tb        => $"{tb.Header} {tb.PlaceholderText}",
            PasswordBox pb    => $"{pb.Header} {pb.PlaceholderText}",
            CheckBox cb       => cb.Content?.ToString() ?? "",
            ComboBox combo    => combo.Header?.ToString() ?? "",
            NumberBox nb      => nb.Header?.ToString() ?? "",
            ToggleSwitch ts   => ts.Header?.ToString() ?? "",
            Expander ex       => ex.Header?.ToString() ?? "",
            TextBlock tbk     => tbk.Text ?? "",
            _ => "",
        };

        var tag = (el.Tag as string) ?? string.Empty;
        var hay = $"{text} {tag}".ToLowerInvariant();
        return hay.Contains(query);
    }
}
