using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusRDM.Core.Models;
using NexusRDM.ViewModels;
using Windows.System;

namespace NexusRDM.Views;

public sealed partial class SftpView : UserControl, ISessionView
{
    public SftpSessionViewModel ViewModel { get; }
    private bool _connectStarted;

    /// <summary>Raised when the toolbar's "Terminal" button is clicked.
    /// The host (MainWindow) reacts by opening an SSH tab for the same
    /// connection profile — paralleling the existing connect-from-tree
    /// flow.</summary>
    public event EventHandler<ConnectionProfile>? OpenSshRequested;

    public SftpView(SftpSessionViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();
        HostLabel.Text = vm.Host;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_connectStarted) return;
        _connectStarted = true;
        await ViewModel.ConnectAsync();
    }

    // ── Local pane events ────────────────────────────────────────────

    private async void LocalList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (LocalList.SelectedItem is SftpEntry entry && entry.IsDirectory)
            await ViewModel.NavigateLocalAsync(entry);
    }

    private void LocalList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (LocalList.SelectedItem is not SftpEntry entry) return;
        var menu = new MenuFlyout();
        if (!entry.IsDirectory)
        {
            var upload = new MenuFlyoutItem { Text = "Upload →", Icon = new SymbolIcon(Symbol.Upload) };
            upload.Click += (_, _) => ViewModel.EnqueueUpload(entry);
            menu.Items.Add(upload);
        }
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
    }

    private async void LocalPathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            await ViewModel.RefreshLocalAsync();
            e.Handled = true;
        }
    }

    private async void LocalUp_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.LocalPath;
        var parent = System.IO.Directory.GetParent(path)?.FullName;
        if (parent is not null)
        {
            ViewModel.LocalPath = parent;
            await ViewModel.RefreshLocalAsync();
        }
    }

    private async void LocalRefresh_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshLocalAsync();

    // ── Remote pane events ───────────────────────────────────────────

    private async void RemoteList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is SftpEntry entry && entry.IsDirectory)
            await ViewModel.NavigateRemoteAsync(entry);
    }

    private async void RemoteList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is not SftpEntry entry) return;
        var menu = new MenuFlyout();
        if (!entry.IsDirectory && entry.Name != "..")
        {
            var dl = new MenuFlyoutItem { Text = "← Download", Icon = new SymbolIcon(Symbol.Download) };
            dl.Click += (_, _) => ViewModel.EnqueueDownload(entry);
            menu.Items.Add(dl);
        }
        if (entry.Name != "..")
        {
            var del = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
            del.Click += async (_, _) => await ViewModel.DeleteRemoteAsync(entry);
            menu.Items.Add(del);
        }
        if (menu.Items.Count > 0)
            menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async void RemotePathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            await ViewModel.RefreshRemoteAsync();
            e.Handled = true;
        }
    }

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        var path  = ViewModel.RemotePath;
        int slash = path.LastIndexOf('/');
        ViewModel.RemotePath = slash <= 0 ? "/" : path[..slash];
        await ViewModel.RefreshRemoteAsync();
    }

    private async void RemoteRefresh_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshRemoteAsync();

    private async void RemoteMkdir_Click(object sender, RoutedEventArgs e)
    {
        // Minimal inline dialog. Could be replaced with a styled
        // ContentDialog later, but for MVP a one-field input keeps
        // the UI shape simple.
        var tb     = new TextBox { PlaceholderText = "Folder name" };
        var dialog = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = "New remote folder",
            Content           = tb,
            PrimaryButtonText = "Create",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(tb.Text))
            await ViewModel.CreateRemoteFolderAsync(tb.Text.Trim());
    }

    // ── Cross-launch terminal ────────────────────────────────────────

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) =>
        OpenSshRequested?.Invoke(this, ViewModel.Profile);

    // ISessionView — minimal stubs, an SFTP tab has no native
    // full-screen / pop-out semantics in this iteration.
    public void ToggleFullScreen() { }
    public void PopOut() { }
}
