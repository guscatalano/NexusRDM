using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NexusRDM.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyFilter();

    private void NavSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter();

    private void ApplyFilter()
    {
        // SelectedIndex="0" in XAML fires SelectionChanged during
        // InitializeComponent before the body's named fields are
        // assigned. Bail until both halves of the page exist.
        if (BodyStack is null || SettingsNav is null || NavSearchBox is null) return;
        ApplyFilterCore();
    }

    /// <summary>Cached map of section header text → list of consecutive
    /// body children that belong to that section. Built once on first
    /// filter application and reused thereafter.</summary>
    private Dictionary<string, List<UIElement>>? _sectionMap;

    private Dictionary<string, List<UIElement>> BuildSectionMap()
    {
        // The body is one big StackPanel where each section starts with
        // a TextBlock styled as a header (CharacterSpacing=60, the
        // small-caps look). We walk linearly: every time we hit a
        // header TextBlock we open a new bucket and dump every
        // following sibling into it until the next header.
        var map = new Dictionary<string, List<UIElement>>(StringComparer.OrdinalIgnoreCase);
        string current = "ALL";
        map[current] = new List<UIElement>();

        foreach (var child in BodyStack.Children.OfType<UIElement>())
        {
            if (child is TextBlock { CharacterSpacing: 60 } header)
            {
                current = header.Text ?? string.Empty;
                map[current] = new List<UIElement>();
            }
            map[current].Add(child);
        }
        return map;
    }

    private void ApplyFilterCore()
    {
        _sectionMap ??= BuildSectionMap();

        var pickedTag = (SettingsNav.SelectedItem as ListBoxItem)?.Tag as string ?? "ALL";
        var query     = (NavSearchBox.Text ?? string.Empty).Trim();

        // First pass: nav-pick filtering. Empty query + ALL → show all.
        foreach (var (section, elements) in _sectionMap)
        {
            var visible = pickedTag == "ALL" ||
                          string.Equals(pickedTag, section, StringComparison.OrdinalIgnoreCase);
            foreach (var el in elements)
                el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Second pass: search filtering on top of the section pick.
        // Hide entire sections whose header + extracted text don't match.
        // Also dim nav items that aren't relevant.
        if (!string.IsNullOrEmpty(query))
        {
            var q = query.ToLowerInvariant();
            foreach (var (section, elements) in _sectionMap)
            {
                if (elements[0].Visibility == Visibility.Collapsed) continue;
                var hit = section.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                          elements.Any(el => ContainsSearchText(el, q));
                if (!hit)
                    foreach (var el in elements) el.Visibility = Visibility.Collapsed;
            }

            foreach (var item in SettingsNav.Items.OfType<ListBoxItem>())
            {
                var tag = item.Tag as string ?? string.Empty;
                if (string.Equals(tag, "ALL", StringComparison.OrdinalIgnoreCase)) continue;
                item.Visibility = tag.Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        else
        {
            foreach (var item in SettingsNav.Items.OfType<ListBoxItem>())
                item.Visibility = Visibility.Visible;
        }
    }

    private static bool ContainsSearchText(UIElement root, string query)
    {
        // Walk descendants and check every TextBlock / button content
        // for the query. Cheap — settings pages are small.
        return Walk(root, query);

        static bool Walk(DependencyObject d, string q)
        {
            switch (d)
            {
                case TextBlock tb when tb.Text?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0:
                case ContentControl cc when cc.Content is string s &&
                     s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0:
                    return true;
            }
            var n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
                if (Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i), q)) return true;
            return false;
        }
    }

    /// <summary>Opens an OS file picker filtered to a single extension
    /// and returns the chosen path (or null on cancel). Unpackaged
    /// WinUI 3 requires <c>InitializeWithWindow</c> against the main
    /// HWND or PickSingleFileAsync throws.</summary>
    private async System.Threading.Tasks.Task<string?> PickFileAsync(string ext)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWin));
        picker.FileTypeFilter.Add(ext);
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void BrowseMstscExe_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(".exe");
        if (path is not null) ViewModel.MstscExePath = path;
    }

    private async void BrowseMstscAx_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(".dll");
        if (path is not null) ViewModel.MstscAxPath = path;
    }
}
