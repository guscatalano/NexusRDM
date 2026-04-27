using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.Core.Interfaces;

namespace NexusRDM.Services;

/// <summary>
/// Single entry point for every <see cref="ContentDialog"/> the app
/// shows. Solves two recurring issues:
///   1. WinUI 3 raises an exception if a second ContentDialog is
///      requested while another is already open. Callers from
///      different surfaces (sidebar delete, app-close confirm, etc.)
///      could race; we serialise via a semaphore.
///   2. Embedded mstscax forms are top-level Win32 windows owned by the
///      WinUI HWND — they paint above any in-app overlay including
///      ContentDialog. We park them offscreen for the duration of the
///      dialog and restore the active tab's form afterwards.
/// </summary>
public static class DialogHost
{
    private static readonly System.Threading.SemaphoreSlim _gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        var hidden = HideEmbeddedForms();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            RestoreEmbeddedForms(hidden);
            _gate.Release();
        }
    }

    private static List<IRdpSession> HideEmbeddedForms()
    {
        var hidden = new List<IRdpSession>();
        try
        {
            var sessions = App.Services?.GetService(typeof(SessionManager)) as SessionManager;
            if (sessions is null) return hidden;
            foreach (var s in sessions.Sessions)
                if (s.RdpSession is { } rdp)
                {
                    try { rdp.SetVisible(false); hidden.Add(rdp); }
                    catch { /* best effort */ }
                }
        }
        catch { /* DI not ready yet */ }
        return hidden;
    }

    private static void RestoreEmbeddedForms(List<IRdpSession> hidden)
    {
        // Only re-show the form attached to the currently-selected tab;
        // others stay hidden until tab-switch picks them up. Mirrors the
        // behaviour of MainWindow.OnSessionTabsSelectionChanged.
        try
        {
            if (App.MainWin?.Content is not FrameworkElement root) return;
            if (root.FindName("SessionTabs") is not TabView tabs) return;
            var selected = tabs.SelectedItem as TabViewItem;
            foreach (var rdp in hidden)
            {
                var visible = tabs.TabItems.OfType<TabViewItem>()
                    .Any(item => ReferenceEquals(item, selected)
                              && item.Tag is OpenSession os
                              && ReferenceEquals(os.RdpSession, rdp));
                try { rdp.SetVisible(visible); }
                catch { /* best effort */ }
            }
        }
        catch { /* MainWin already torn down */ }
    }
}
