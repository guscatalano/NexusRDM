using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusRDM.Core.Models;
using NexusRDM.ViewModels;
using Windows.ApplicationModel.DataTransfer;
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
        if (entry.Name == "..") { /* no actions on the up-row */ }
        else if (entry.IsDirectory)
        {
            var uploadDir = new MenuFlyoutItem { Text = "Upload folder →", Icon = new SymbolIcon(Symbol.Upload) };
            uploadDir.Click += async (_, _) => await ViewModel.EnqueueUploadDirectoryAsync(entry);
            menu.Items.Add(uploadDir);
        }
        else
        {
            var upload = new MenuFlyoutItem { Text = "Upload →", Icon = new SymbolIcon(Symbol.Upload) };
            upload.Click += (_, _) => ViewModel.EnqueueUpload(entry);
            menu.Items.Add(upload);
        }
        if (menu.Items.Count > 0)
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

    // ── Drag and drop ────────────────────────────────────────────────
    //
    // Internal pane-to-pane DnD. We stash the dragged entries inside
    // DataPackage.Properties under a private key — the data never
    // leaves the app, so there's no need to serialise as
    // StorageItems or files. The opposite pane's Drop handler reads
    // the key back and enqueues the appropriate transfer direction.
    //
    // Folder drags are not handled in v1 (recursive copy is a known
    // follow-up). The DragOver/Drop handlers silently ignore them.
    private const string DragKeySftpEntries = "NexusRDM.SftpEntries";

    private void LocalList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var files = e.Items.OfType<SftpEntry>().Where(x => !x.IsDirectory && x.Name != "..").ToList();
        if (files.Count == 0) { e.Cancel = true; return; }
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.Properties[DragKeySftpEntries] = files;
    }

    private void RemoteList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var files = e.Items.OfType<SftpEntry>().Where(x => !x.IsDirectory && x.Name != "..").ToList();
        if (files.Count == 0) { e.Cancel = true; return; }
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.Properties[DragKeySftpEntries] = files;
    }

    private void LocalList_DragOver(object sender, DragEventArgs e)
    {
        // Accept only drops carrying remote entries (the opposite pane).
        // A drag that started from the same pane has Source == LocalList
        // — we don't want a same-pane drop to do anything either.
        if (e.DataView.Properties.ContainsKey(DragKeySftpEntries))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Download here";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible   = true;
        }
    }

    private void RemoteList_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Properties.ContainsKey(DragKeySftpEntries))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Upload here";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible   = true;
        }
    }

    private void LocalList_Drop(object sender, DragEventArgs e)
    {
        // Drop onto local pane = download. Filter to entries whose
        // FullPath looks remote (forward slashes, no Windows drive
        // letter) so a same-pane drop is a no-op rather than a
        // copy-to-the-same-folder.
        if (!e.DataView.Properties.TryGetValue(DragKeySftpEntries, out var raw)) return;
        if (raw is not List<SftpEntry> entries) return;
        foreach (var entry in entries)
        {
            if (IsRemotePath(entry.FullPath))
                ViewModel.EnqueueDownload(entry);
        }
    }

    private void RemoteList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.TryGetValue(DragKeySftpEntries, out var raw)) return;
        if (raw is not List<SftpEntry> entries) return;
        foreach (var entry in entries)
        {
            if (!IsRemotePath(entry.FullPath))
                ViewModel.EnqueueUpload(entry);
        }
    }

    /// <summary>True if the path looks like a remote SFTP path —
    /// posix style with leading slash and no Windows drive letter.
    /// Used to tell same-pane drops apart from cross-pane drops since
    /// both panes share the SftpEntry record type.</summary>
    private static bool IsRemotePath(string path) =>
        path.Length > 0 && path[0] == '/' && (path.Length < 2 || path[1] != ':');

    // ── Preview helpers (image + hex) ────────────────────────────────

    private const long ImagePreviewMaxBytes = 10L * 1024 * 1024;
    private const long HexPreviewMaxBytes   =  256L * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff",
    };

    private static bool IsImageExtension(string name)
    {
        var ext = System.IO.Path.GetExtension(name);
        return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
    }

    /// <summary>Stream remote bytes into a BitmapImage, show in a
    /// ContentDialog. Image bytes live in memory only; no temp file.</summary>
    private async System.Threading.Tasks.Task PreviewImageAsync(SftpEntry entry)
    {
        var bytes = await ViewModel.ReadRemoteBytesAsync(entry, ImagePreviewMaxBytes);
        if (bytes is null)
        {
            await ShowSimpleErrorAsync($"Could not read image: {entry.Name}");
            return;
        }
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using (var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream())
        {
            using (var writer = new Windows.Storage.Streams.DataWriter(ras.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            ras.Seek(0);
            await bitmap.SetSourceAsync(ras);
        }
        var image = new Image
        {
            Source = bitmap,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            MaxHeight = 600,
            MaxWidth  = 900,
        };
        var dlg = new ContentDialog
        {
            XamlRoot        = XamlRoot,
            Title           = $"{entry.Name} — {entry.Size:N0} bytes (image preview)",
            Content         = image,
            CloseButtonText = "Close",
            DefaultButton   = ContentDialogButton.Close,
        };
        await dlg.ShowAsync();
    }

    /// <summary>Classic hex+ASCII dump dialog. 16-byte rows with a
    /// gap between the two 8-byte halves, ASCII column on the right.</summary>
    private async System.Threading.Tasks.Task PreviewHexAsync(SftpEntry entry)
    {
        var bytes = await ViewModel.ReadRemoteBytesAsync(entry, HexPreviewMaxBytes);
        if (bytes is null)
        {
            await ShowSimpleErrorAsync($"Could not read file: {entry.Name}");
            return;
        }
        var dump = FormatHexDump(bytes);
        var tb = new TextBox
        {
            Text             = dump,
            IsReadOnly       = true,
            AcceptsReturn    = true,
            TextWrapping     = TextWrapping.NoWrap,
            FontFamily       = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize         = 12,
            Height           = 480,
            MinWidth         = 760,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(tb, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(tb,   ScrollBarVisibility.Auto);
        var dlg = new ContentDialog
        {
            XamlRoot        = XamlRoot,
            Title           = $"{entry.Name} — {entry.Size:N0} bytes (hex preview, not saved)",
            Content         = tb,
            CloseButtonText = "Close",
            DefaultButton   = ContentDialogButton.Close,
        };
        await dlg.ShowAsync();
    }

    private static string FormatHexDump(byte[] data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 4);
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append(i.ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++)
            {
                if (i + j < data.Length) sb.Append(data[i + j].ToString("X2")).Append(' ');
                else                     sb.Append("   ");
                if (j == 7) sb.Append(' '); // gap between the two halves
            }
            sb.Append(" |");
            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                byte b = data[i + j];
                sb.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
            }
            sb.AppendLine("|");
        }
        return sb.ToString();
    }

    private async System.Threading.Tasks.Task ShowSimpleErrorAsync(string message)
    {
        var dlg = new ContentDialog
        {
            XamlRoot        = XamlRoot,
            Title           = "Preview failed",
            Content         = message,
            CloseButtonText = "OK",
        };
        await dlg.ShowAsync();
    }

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
        if (entry.IsDirectory && entry.Name != "..")
        {
            var dlDir = new MenuFlyoutItem { Text = "← Download folder", Icon = new SymbolIcon(Symbol.Download) };
            dlDir.Click += async (_, _) => await ViewModel.EnqueueDownloadDirectoryAsync(entry);
            menu.Items.Add(dlDir);
        }
        if (!entry.IsDirectory && entry.Name != "..")
        {
            var dl = new MenuFlyoutItem { Text = "← Download", Icon = new SymbolIcon(Symbol.Download) };
            dl.Click += (_, _) => ViewModel.EnqueueDownload(entry);
            menu.Items.Add(dl);

            menu.Items.Add(new MenuFlyoutSeparator());

            // Preview (text). Cap enforced via IsEnabled; reads
            // in-memory only, never writes to local disk.
            var prev = new MenuFlyoutItem
            {
                Text      = "Preview (text)",
                Icon      = new SymbolIcon(Symbol.View),
                IsEnabled = entry.Size <= SftpSessionViewModel.PreviewMaxBytes,
            };
            ToolTipService.SetToolTip(prev, entry.Size > SftpSessionViewModel.PreviewMaxBytes
                ? $"File too large to preview ({entry.Size:N0} bytes; cap is 1 MB). Download instead."
                : $"Read the file in-memory without saving to disk ({entry.Size:N0} bytes).");
            prev.Click += async (_, _) => await PreviewRemoteAsync(entry);
            menu.Items.Add(prev);

            // Preview (image). Same in-memory pattern, capped at 10
            // MB. Extension whitelist keeps the item hidden on files
            // that obviously won't decode.
            if (IsImageExtension(entry.Name))
            {
                var img = new MenuFlyoutItem
                {
                    Text      = "Preview (image)",
                    Icon      = new SymbolIcon(Symbol.Pictures),
                    IsEnabled = entry.Size <= ImagePreviewMaxBytes,
                };
                ToolTipService.SetToolTip(img, entry.Size > ImagePreviewMaxBytes
                    ? $"Image too large ({entry.Size:N0} bytes; cap is 10 MB). Download instead."
                    : $"Decode and display the image in-memory ({entry.Size:N0} bytes).");
                img.Click += async (_, _) => await PreviewImageAsync(entry);
                menu.Items.Add(img);
            }

            // Preview (hex). For binary files where the text preview
            // would be unreadable. Capped at 256 KB since hex dumps
            // are ~4x the source size as text.
            var hex = new MenuFlyoutItem
            {
                Text      = "Preview (hex)",
                Icon      = new SymbolIcon(Symbol.Tag),
                IsEnabled = entry.Size <= HexPreviewMaxBytes,
            };
            ToolTipService.SetToolTip(hex, entry.Size > HexPreviewMaxBytes
                ? $"File too large for hex preview ({entry.Size:N0} bytes; cap is 256 KB)."
                : $"Show as hex + ASCII dump ({entry.Size:N0} bytes).");
            hex.Click += async (_, _) => await PreviewHexAsync(entry);
            menu.Items.Add(hex);

            menu.Items.Add(new MenuFlyoutSeparator());

            // Edit in place. Downloads to %TEMP%, opens in the user's
            // default editor for that extension, watches for saves
            // and re-uploads. Lifecycle ends when the SFTP tab closes
            // or via "Stop editing."
            bool alreadyEditing = ViewModel.ActiveEdits.ContainsKey(entry.FullPath);
            var edit = new MenuFlyoutItem
            {
                Text = alreadyEditing ? "Re-open editor" : "Edit in place",
                Icon = new SymbolIcon(Symbol.Edit),
            };
            ToolTipService.SetToolTip(edit,
                "Download to a temp file, open in your default editor, " +
                "auto-upload on save. The tab keeps watching until you close it.");
            edit.Click += async (_, _) => await ViewModel.BeginEditInPlaceAsync(entry);
            menu.Items.Add(edit);
            if (alreadyEditing)
            {
                var stop = new MenuFlyoutItem { Text = "Stop editing", Icon = new SymbolIcon(Symbol.Cancel) };
                stop.Click += (_, _) => ViewModel.StopEditInPlace(entry.FullPath);
                menu.Items.Add(stop);
            }
        }
        if (entry.Name != "..")
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var del = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
            del.Click += async (_, _) => await ViewModel.DeleteRemoteAsync(entry);
            menu.Items.Add(del);
        }
        if (menu.Items.Count > 0)
            menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>Read the remote file into memory and show it in a
    /// scrollable ContentDialog. Monospace font, read-only. The text
    /// never touches local disk — even the ViewModel uses a
    /// MemoryStream and discards it after decoding. Cap enforced
    /// upstream in ReadRemoteTextAsync.</summary>
    private async System.Threading.Tasks.Task PreviewRemoteAsync(SftpEntry entry)
    {
        // Show a "loading" dialog first since reading 1 MB over WAN
        // can take a few seconds.
        var loading = new ProgressRing { IsActive = true, Width = 24, Height = 24 };
        var loadingDlg = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = $"Loading {entry.Name}…",
            Content           = loading,
            CloseButtonText   = "Cancel",
        };
        var loadTask = ViewModel.ReadRemoteTextAsync(entry);
        var showTask = loadingDlg.ShowAsync().AsTask();
        var winner   = await System.Threading.Tasks.Task.WhenAny(loadTask, showTask);
        if (winner == showTask)
        {
            // User cancelled before the read finished. Let the read
            // continue in the background — at worst it consumes a few
            // MB of memory until GC.
            return;
        }
        loadingDlg.Hide();
        var text = await loadTask;
        if (text is null)
        {
            var fail = new ContentDialog
            {
                XamlRoot        = XamlRoot,
                Title           = $"Preview failed: {entry.Name}",
                Content         = "Could not read the file. It may be too large, locked, or not readable by this user.",
                CloseButtonText = "OK",
            };
            await fail.ShowAsync();
            return;
        }

        // Read-only TextBox inside a scrollable dialog. Monospace
        // font so logs / configs line up correctly.
        var tb = new TextBox
        {
            Text             = text,
            IsReadOnly       = true,
            AcceptsReturn    = true,
            TextWrapping     = TextWrapping.NoWrap,
            FontFamily       = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize         = 12,
            Height           = 480,
            MinWidth         = 760,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(tb, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(tb,   ScrollBarVisibility.Auto);

        var dlg = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = $"{entry.Name} — {entry.Size:N0} bytes (preview, not saved)",
            Content           = tb,
            CloseButtonText   = "Close",
            DefaultButton     = ContentDialogButton.Close,
        };
        await dlg.ShowAsync();
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
