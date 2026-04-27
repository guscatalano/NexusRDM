using System.Collections.Specialized;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusRDM.Core.Interfaces;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT.Interop;

namespace NexusRDM.Views;

/// <summary>
/// Floating diagnostic window listing every event the underlying RDP
/// session raises. Pinned always-on-top via OverlappedPresenter so it
/// stays above the embedded mstscax form (which is itself a top-level
/// owned Win32 window).
/// </summary>
public sealed partial class RdpEventsWindow : Window
{
    private readonly System.Collections.ObjectModel.ObservableCollection<RdpEventEntry> _events;

    public RdpEventsWindow(string title, System.Collections.ObjectModel.ObservableCollection<RdpEventEntry> events)
    {
        InitializeComponent();
        Title  = $"RDP events — {title}";
        _events = events;

        EventsList.ItemsSource = events;
        HeaderLabel.Text       = $"RDP events — {title}";
        UpdateStatus();

        events.CollectionChanged += OnEventsChanged;

        App.SecondaryWindows.Add(this);
        Closed += (_, _) =>
        {
            events.CollectionChanged -= OnEventsChanged;
            App.SecondaryWindows.Remove(this);
        };

        // Float above the RDP client (top-level owned form). The OCX
        // form is owned by the main WinUI window, so a regular secondary
        // window would land at the same z-level — IsAlwaysOnTop pins us
        // above all of it.
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(true, true);
        }

        // Smaller default size — this is a side panel.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 480));
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateStatus();
        // Auto-scroll to newest entry.
        if (_events.Count > 0)
            EventsList.ScrollIntoView(_events[^1]);
    }

    private void UpdateStatus() => StatusLabel.Text = $"{_events.Count} event(s)";

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _events.Clear();
        UpdateStatus();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

    private void EventsList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Right-click anywhere in the list copies — a faster path than
        // hitting the toolbar button. Honors current selection.
        CopyToClipboard();
        e.Handled = true;
    }

    /// <summary>Builds a TSV-style text block (time, kind, detail) from
    /// the currently-selected rows — or every row if nothing is selected
    /// — and pushes it onto the system clipboard.</summary>
    private void CopyToClipboard()
    {
        var rows = EventsList.SelectedItems.Count > 0
            ? EventsList.SelectedItems.OfType<RdpEventEntry>()
            : _events.AsEnumerable();

        var sb = new StringBuilder();
        foreach (var entry in rows)
            sb.Append(entry.TimeText).Append('\t')
              .Append(entry.Kind).Append('\t')
              .Append(entry.Detail).AppendLine();

        var text = sb.ToString();
        if (text.Length == 0) return;

        var pkg = new DataPackage();
        pkg.SetText(text);
        Clipboard.SetContent(pkg);

        StatusLabel.Text = $"Copied {(EventsList.SelectedItems.Count > 0 ? EventsList.SelectedItems.Count : _events.Count)} event(s).";
    }
}
