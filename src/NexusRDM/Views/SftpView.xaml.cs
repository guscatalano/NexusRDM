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
        // Install the conflict-resolution callback. Runs on the UI
        // thread since RunOneTransferAsync is awaited from PumpQueueAsync
        // which is on the UI thread (the file I/O inside happens on
        // Task.Run, but RunOneTransferAsync itself is invoked from
        // the UI dispatcher).
        ViewModel.OnConflictAsk = AskOverwriteAsync;
        Loaded += OnLoaded;
    }

    /// <summary>"Destination already exists" dialog. Three options:
    /// overwrite, skip, cancel the rest of the batch. The "Apply to
    /// remaining" checkbox only appears when there are actually more
    /// files queued behind this one — for a single-file drop it'd be
    /// a meaningless option.</summary>
    private async Task<SftpSessionViewModel.ConflictChoice> AskOverwriteAsync(string destPath, bool isRemote)
    {
        var location = isRemote ? "server" : "local disk";
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text         = $"\"{destPath}\" already exists on the {location}.",
            TextWrapping = TextWrapping.Wrap,
        });

        // Always show the checkbox. Apply-to-all is now sticky across
        // batches for this tab, so even a 1-file drag benefits — the
        // user can pre-commit "always overwrite" and never see this
        // dialog again until the tab closes.
        var apply = new CheckBox
        {
            Content = "Apply to all conflicts in this tab (until I close it)",
        };
        stack.Children.Add(apply);
        var dlg = new ContentDialog
        {
            XamlRoot            = XamlRoot,
            Title               = "Overwrite?",
            Content             = stack,
            PrimaryButtonText   = "Overwrite",
            SecondaryButtonText = "Skip",
            CloseButtonText     = ViewModel.QueueDepth > 0 ? "Cancel batch" : "Cancel",
            DefaultButton       = ContentDialogButton.Primary,
        };
        var result = await dlg.ShowAsync();
        bool toAll = apply.IsChecked == true;
        return result switch
        {
            ContentDialogResult.Primary   => toAll ? SftpSessionViewModel.ConflictChoice.OverwriteAll : SftpSessionViewModel.ConflictChoice.Overwrite,
            ContentDialogResult.Secondary => toAll ? SftpSessionViewModel.ConflictChoice.SkipAll      : SftpSessionViewModel.ConflictChoice.Skip,
            _                              => SftpSessionViewModel.ConflictChoice.Cancel,
        };
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

    private async void LocalList_RightTapped(object sender, RightTappedRoutedEventArgs e)
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

        // Always-available local actions: open the current folder in
        // Explorer, plus delete (with confirmation) on a real entry.
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());

        var openExplorer = new MenuFlyoutItem
        {
            Text = "Open in Explorer",
            Icon = new SymbolIcon(Symbol.OpenFile),
        };
        openExplorer.Click += (_, _) => OpenLocalExplorer(entry);
        menu.Items.Add(openExplorer);

        if (entry.Name != "..")
        {
            var del = new MenuFlyoutItem { Text = "Delete (local)", Icon = new SymbolIcon(Symbol.Delete) };
            del.Click += async (_, _) => await ConfirmAndDeleteLocalAsync(entry);
            menu.Items.Add(del);
        }

        if (menu.Items.Count > 0)
            menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>Open Windows Explorer at the current local pane path
    /// (or at the selected entry, when the user right-clicked one).
    /// If the entry is a file, we open the parent folder with the
    /// file pre-selected via the <c>/select,</c> verb — same as
    /// "Show in Explorer" elsewhere.</summary>
    private static void OpenLocalExplorer(SftpEntry entry)
    {
        try
        {
            var args = entry.IsDirectory
                ? $"\"{entry.FullPath}\""
                : $"/select,\"{entry.FullPath}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = args,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            NexusRDM.Core.Diagnostics.SshLog.Warn($"Open in Explorer failed: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task ConfirmAndDeleteLocalAsync(SftpEntry entry)
    {
        var dlg = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = $"Delete {(entry.IsDirectory ? "folder" : "file")}?",
            Content           = $"\"{entry.FullPath}\" will be permanently deleted from your local disk. " +
                                "This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteLocalAsync(entry);
    }

    /// <summary>Confirm-then-delete on the remote side. Same shape as
    /// the local confirmation but the wording calls out "from the
    /// server" since remote deletes are typically more dangerous —
    /// they hit the user's actual infrastructure, no recycle bin.
    /// Directory deletes are recursive on the server (rm -rf
    /// equivalent via SshNet's DeleteDirectory). The dialog default
    /// button is Cancel to make the destructive path require an
    /// explicit click.</summary>
    private async System.Threading.Tasks.Task ConfirmAndDeleteRemoteAsync(SftpEntry entry)
    {
        var what = entry.IsDirectory ? "folder (and everything inside it)" : "file";
        var dlg = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = $"Delete remote {(entry.IsDirectory ? "folder" : "file")}?",
            Content           = $"\"{entry.FullPath}\" will be permanently deleted from the server. " +
                                $"This removes the {what}. There's no undo and no recycle bin on the remote.",
            PrimaryButtonText = "Delete",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteRemoteAsync(entry);
    }

    private async void LocalPathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            // TextBox's TwoWay binding only pushes back on LostFocus —
            // hitting Enter alone doesn't update ViewModel.LocalPath
            // before we refresh. Read the box directly and force the
            // VM into sync, then refresh against the new path.
            var typed = LocalPathBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(typed) && typed != ViewModel.LocalPath)
                ViewModel.LocalPath = typed;
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
    /// gap between the two 8-byte halves, ASCII column on the right.
    /// Word wrap is on because the user asked for it — accept that
    /// narrow dialogs will break the column alignment.</summary>
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
            Text         = dump,
            IsReadOnly   = true,
            AcceptsReturn= true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily   = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize     = 12,
            Height       = 480,
            MinWidth     = 760,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(tb, ScrollBarVisibility.Auto);

        await ShowPreviewDialogAsync(
            title:    $"{entry.Name} — {entry.Size:N0} bytes (hex preview, not saved)",
            content:  tb,
            copyText: dump);
    }

    /// <summary>Shared dialog frame for text + hex previews. Wires a
    /// secondary "Copy" button that pushes the supplied text to the
    /// clipboard without dismissing the dialog — so the user can
    /// keep reading after a copy.</summary>
    private async System.Threading.Tasks.Task ShowPreviewDialogAsync(string title, FrameworkElement content, string copyText)
    {
        var dlg = new ContentDialog
        {
            XamlRoot            = XamlRoot,
            Title               = title,
            Content             = content,
            PrimaryButtonText   = "Copy",
            CloseButtonText     = "Close",
            DefaultButton       = ContentDialogButton.Close,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            // Don't dismiss — let the user copy and keep reading.
            args.Cancel = true;
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(copyText);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
            catch (Exception ex)
            {
                NexusRDM.Core.Diagnostics.SshLog.Warn($"Preview copy failed: {ex.Message}");
            }
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
            edit.Click += async (_, _) =>
            {
                var session = await ViewModel.BeginEditInPlaceAsync(entry);
                if (session is null) return;
                // Conflict callback runs on the watcher's threadpool —
                // hop to the UI thread to actually show the dialog.
                session.OnConflictDetected = remoteMtime =>
                {
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
                        System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        var dlg = new ContentDialog
                        {
                            XamlRoot = XamlRoot,
                            Title    = $"Conflict on {entry.Name}",
                            Content  = $"The server's copy of \"{entry.Name}\" was modified at " +
                                       $"{remoteMtime.LocalDateTime:G}, after you started editing.\n\n" +
                                       "Overwriting will lose the server-side changes. " +
                                       "Cancelling keeps the file open in your editor; the next save will " +
                                       "prompt again.",
                            PrimaryButtonText = "Overwrite server",
                            CloseButtonText   = "Cancel save",
                            DefaultButton     = ContentDialogButton.Close,
                        };
                        tcs.SetResult(await dlg.ShowAsync() == ContentDialogResult.Primary);
                    });
                    return tcs.Task;
                };
            };
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
            var del = new MenuFlyoutItem { Text = "Delete (remote)", Icon = new SymbolIcon(Symbol.Delete) };
            del.Click += async (_, _) => await ConfirmAndDeleteRemoteAsync(entry);
            menu.Items.Add(del);

            var props = new MenuFlyoutItem { Text = "Properties…", Icon = new SymbolIcon(Symbol.Setting) };
            props.Click += async (_, _) => await ShowRemotePropertiesAsync(entry);
            menu.Items.Add(props);
        }
        if (menu.Items.Count > 0)
            menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>Read-only details dialog for a remote entry. Shows
    /// everything <see cref="SftpEntry"/> carries plus a properly
    /// formatted <c>rwxr-xr-x</c> permission string + the octal
    /// equivalent (000-777). The path field is selectable so the user
    /// can copy it.</summary>
    private async System.Threading.Tasks.Task ShowRemotePropertiesAsync(SftpEntry entry)
    {
        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(110) },
                                  new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
            ColumnSpacing = 8,
            RowSpacing    = 4,
            MinWidth      = 480,
        };

        int row = 0;
        void AddField(string label, string value, bool selectable = false)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock
            {
                Text       = label,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity    = 0.7,
                FontSize   = 12,
            };
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            grid.Children.Add(l);

            FrameworkElement v;
            if (selectable)
            {
                v = new TextBox
                {
                    Text       = value,
                    IsReadOnly = true,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    FontSize   = 12,
                    BorderThickness = new Thickness(0),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Padding    = new Thickness(0),
                };
            }
            else
            {
                v = new TextBlock
                {
                    Text         = value,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily   = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    FontSize     = 12,
                };
            }
            Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            grid.Children.Add(v);
            row++;
        }

        var type = entry.IsSymlink ? "Symlink" : entry.IsDirectory ? "Directory" : "File";
        AddField("Name",       entry.Name);
        AddField("Path",       entry.FullPath, selectable: true);
        AddField("Type",       type);
        AddField("Size",       entry.IsDirectory ? "—" : $"{entry.Size:N0} bytes");
        AddField("Modified",   entry.LastModified.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
        AddField("Permissions", $"{FormatPermissions(entry.Permissions, entry.IsDirectory, entry.IsSymlink)}  ({Convert.ToString(entry.Permissions, 8).PadLeft(3, '0')})");

        var dlg = new ContentDialog
        {
            XamlRoot        = XamlRoot,
            Title           = entry.Name,
            Content         = grid,
            CloseButtonText = "Close",
            DefaultButton   = ContentDialogButton.Close,
        };
        await dlg.ShowAsync();
    }

    /// <summary>Render a POSIX permission triplet as the classic
    /// <c>rwxr-xr-x</c> form with a leading type char (<c>d</c> for
    /// directory, <c>l</c> for symlink, <c>-</c> for regular file).
    /// Matches what <c>ls -l</c> produces on the server.</summary>
    private static string FormatPermissions(short mode, bool isDir, bool isSymlink)
    {
        var sb = new System.Text.StringBuilder(10);
        sb.Append(isSymlink ? 'l' : isDir ? 'd' : '-');
        sb.Append((mode & 0b100_000_000) != 0 ? 'r' : '-');
        sb.Append((mode & 0b010_000_000) != 0 ? 'w' : '-');
        sb.Append((mode & 0b001_000_000) != 0 ? 'x' : '-');
        sb.Append((mode & 0b000_100_000) != 0 ? 'r' : '-');
        sb.Append((mode & 0b000_010_000) != 0 ? 'w' : '-');
        sb.Append((mode & 0b000_001_000) != 0 ? 'x' : '-');
        sb.Append((mode & 0b000_000_100) != 0 ? 'r' : '-');
        sb.Append((mode & 0b000_000_010) != 0 ? 'w' : '-');
        sb.Append((mode & 0b000_000_001) != 0 ? 'x' : '-');
        return sb.ToString();
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
        // font so logs / configs line up correctly. TextWrapping.Wrap
        // for the preview UX — long single-line content (minified
        // JSON, base64 blobs) was rendering as one horizontal line
        // running off the right edge.
        var tb = new TextBox
        {
            Text                = text,
            IsReadOnly          = true,
            AcceptsReturn       = true,
            TextWrapping        = TextWrapping.Wrap,
            FontFamily          = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize            = 12,
            Height              = 480,
            MinWidth            = 760,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(tb, ScrollBarVisibility.Auto);

        await ShowPreviewDialogAsync(
            title:   $"{entry.Name} — {entry.Size:N0} bytes (preview, not saved)",
            content: tb,
            copyText: text);
    }

    private async void RemotePathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            // Same Enter-vs-LostFocus issue as LocalPathBox: TextBox.Text
            // doesn't push to the bound source until the box loses
            // focus. Sync the typed value into the VM before refresh
            // so we list the directory the user typed, not the old one.
            var typed = RemotePathBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(typed) && typed != ViewModel.RemotePath)
                ViewModel.RemotePath = typed;
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

    private async void RemoteNewFile_Click(object sender, RoutedEventArgs e)
    {
        // Same shape as the New folder dialog. Uploads a zero-byte
        // stream — equivalent to `touch <name>` on the server.
        var tb     = new TextBox { PlaceholderText = "filename.txt" };
        var dialog = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = "New remote file",
            Content           = tb,
            PrimaryButtonText = "Create",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(tb.Text))
            await ViewModel.CreateRemoteFileAsync(tb.Text.Trim());
    }

    // ── Cross-launch terminal ────────────────────────────────────────

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) =>
        OpenSshRequested?.Invoke(this, ViewModel.Profile);

    // ISessionView — minimal stubs, an SFTP tab has no native
    // full-screen / pop-out semantics in this iteration.
    public void ToggleFullScreen() { }
    public void PopOut() { }
}
