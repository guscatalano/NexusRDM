using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.Data.Context;
using NexusRDM.ViewModels;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NexusRDM.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel        ViewModel  { get; }
    public ProxmoxSourcesViewModel  ProxmoxVm  { get; } = new();

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        DbPathText.Text = App.DbPath;
        // Show when the database was first created. SQLite has no
        // "CREATE TIME" column we can ask, so we use the file's
        // creation timestamp on disk; close enough.
        try
        {
            DbCreatedText.Text = System.IO.File.Exists(App.DbPath)
                ? $"Created {System.IO.File.GetCreationTime(App.DbPath):yyyy-MM-dd HH:mm}"
                : "Database file not yet created — it'll be made on next save.";
        }
        catch (Exception ex) { DbCreatedText.Text = $"(Could not read timestamp: {ex.Message})"; }

        _ = ProxmoxVm.LoadAsync();
    }

    // ── Proxmox sources ──────────────────────────────────────────────────

    private async void ProxmoxAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProxmoxSourceEditDialog { XamlRoot = this.XamlRoot };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try { await ProxmoxVm.SaveAsync(dlg.Result.ToModel(), dlg.SecretText); }
        catch (Exception ex) { await ShowErrorAsync("Could not save source", ex.Message); }
    }

    private async void ProxmoxEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var row = ProxmoxVm.Sources.FirstOrDefault(s => s.Id == id);
        if (row is null) return;

        var dlg = new ProxmoxSourceEditDialog(row) { XamlRoot = this.XamlRoot };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try { await ProxmoxVm.SaveAsync(dlg.Result.ToModel(), dlg.SecretText); }
        catch (Exception ex) { await ShowErrorAsync("Could not save source", ex.Message); }
    }

    private async void ProxmoxDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var row = ProxmoxVm.Sources.FirstOrDefault(s => s.Id == id);
        if (row is null) return;

        var confirm = new ContentDialog
        {
            XamlRoot          = this.XamlRoot,
            Title             = "Remove Proxmox source?",
            Content           = $"Removing '{row.Name}' deletes every connection imported from this cluster ({row.BaseUrl}). Manual connections are not affected.",
            PrimaryButtonText = "Remove",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try { await ProxmoxVm.DeleteAsync(id); }
        catch (Exception ex) { await ShowErrorAsync("Could not delete source", ex.Message); }
    }

    private async void ProxmoxTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var row = ProxmoxVm.Sources.FirstOrDefault(s => s.Id == id);
        if (row is null) return;

        row.LastTestResult = "Testing…";
        await ProxmoxVm.TestAsync(row);
    }

    private async void ProxmoxSync_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var row = ProxmoxVm.Sources.FirstOrDefault(s => s.Id == id);
        if (row is null) return;

        row.LastTestResult = "Syncing…";
        await ProxmoxVm.SyncAsync(row);
    }

    // ── Network discovery ────────────────────────────────────────────────

    /// <summary>Re-entrance flag: a second click while the previous
    /// scan is still in flight is a no-op. Cheaper than relying on the
    /// service's own scan-cancellation logic for UX feedback, and it
    /// stops onProgress/onComplete handlers from stacking up.</summary>
    private bool _scanInFlight;

    private void DiscoveryScanNow_Click(object sender, RoutedEventArgs e)
    {
        // Fire-and-forget by design. Earlier this method awaited the
        // scan, which (despite Task.Run inside the service) routinely
        // showed up as a UI lock — most likely because the WinUI
        // sync-context resumption on the post-await continuation lined
        // up against discovery+ping thread-pool contention. Going
        // strictly event-driven here means the click returns the next
        // tick and nothing on the UI thread waits on the scan task.
        if (_scanInFlight) return;

        var svc = App.Services.GetRequiredService<NexusRDM.Services.NetworkDiscoveryService>();
        var dispatcher = DispatcherQueue;

        EventHandler<NexusRDM.Services.DiscoveryProgress>? onProgress = null;
        EventHandler<NexusRDM.Services.DiscoveryResult>?  onComplete = null;

        onProgress = (_, p) =>
            dispatcher.TryEnqueue(() =>
                DiscoveryStatus.Text = $"Probing… {p.Probed}/{p.Total} ({p.Found} found)");

        onComplete = (_, r) =>
        {
            // Detach BOTH handlers before touching UI so a delayed
            // Progress event after completion can't keep them alive.
            svc.Progress       -= onProgress;
            svc.ScanCompleted  -= onComplete;
            dispatcher.TryEnqueue(() =>
            {
                _scanInFlight = false;
                DiscoveryStatus.Text = r.IsSuccess
                    ? $"Done — {r}"
                    : $"Failed — {r.Error}";
            });
        };

        svc.Progress      += onProgress;
        svc.ScanCompleted += onComplete;

        DiscoveryStatus.Text = "Starting…";
        _scanInFlight = true;
        // No await: the service runs the scan on a thread-pool thread
        // and reports completion via ScanCompleted above.
        _ = svc.ScanAsync(ViewModel.DiscoverySubnet);
    }

    private async void DiscoveryClear_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title             = "Clear discovered devices?",
            Content           = "Removes every connection inside the Discovered folder and its saved credentials. The folder itself stays — the next scan can repopulate it.",
            PrimaryButtonText = "Clear",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };
        if (await NexusRDM.Services.DialogHost.ShowAsync(confirm) != ContentDialogResult.Primary)
            return;

        var svc = App.Services.GetRequiredService<NexusRDM.Services.NetworkDiscoveryService>();
        try
        {
            var n = await svc.ClearDiscoveredAsync();
            DiscoveryStatus.Text = n == 0
                ? "Nothing to clear."
                : $"Cleared {n} discovered device(s).";
        }
        catch (Exception ex) { DiscoveryStatus.Text = $"Clear failed — {ex.Message}"; }
    }

    // ── Hyper-V (local) ──────────────────────────────────────────────────

    private async void HyperVTest_Click(object sender, RoutedEventArgs e)
    {
        HyperVStatus.Text = "Testing…";
        var client = App.Services.GetRequiredService<NexusRDM.Services.HyperVClient>();
        try
        {
            var d = await client.DiagnoseAccessAsync();
            HyperVStatus.Text = (d.IsSuccess ? "OK — " : "Failed — ") + d.Message;
        }
        catch (Exception ex) { HyperVStatus.Text = $"Failed — {ex.Message}"; }
    }

    private async void HyperVSyncNow_Click(object sender, RoutedEventArgs e)
    {
        HyperVStatus.Text = "Syncing…";
        var svc = App.Services.GetRequiredService<NexusRDM.Services.HyperVSyncService>();
        try
        {
            var r = await svc.SyncAsync();
            HyperVStatus.Text = r.IsSuccess ? $"Done — {r}" : $"Failed — {r.Error}";
        }
        catch (Exception ex) { HyperVStatus.Text = $"Failed — {ex.Message}"; }
    }

    private async void HyperVClear_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title             = "Clear imported VMs?",
            Content           = "Removes every Hyper-V row inside the 'Hyper-V' folder and its saved credentials. The folder stays — the next sync can repopulate it.",
            PrimaryButtonText = "Clear",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };
        if (await NexusRDM.Services.DialogHost.ShowAsync(confirm) != ContentDialogResult.Primary)
            return;

        var svc = App.Services.GetRequiredService<NexusRDM.Services.HyperVSyncService>();
        try
        {
            var n = await svc.ClearManagedAsync();
            HyperVStatus.Text = n == 0 ? "Nothing to clear." : $"Cleared {n} VM(s).";
        }
        catch (Exception ex) { HyperVStatus.Text = $"Clear failed — {ex.Message}"; }
    }

    private async Task ShowErrorAsync(string title, string body)
    {
        var dlg = new ContentDialog
        {
            XamlRoot        = this.XamlRoot,
            Title           = title,
            Content         = body,
            CloseButtonText = "OK",
        };
        await dlg.ShowAsync();
    }

    private async void ExportDb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName     = $"nexusrdm-export-{DateTime.Now:yyyyMMdd-HHmm}",
                SuggestedStartLocation = PickerLocationId.Desktop,
            };
            picker.FileTypeChoices.Add("JSON", new System.Collections.Generic.List<string> { ".json" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWin));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            // Pull every row directly from the DbContext (rather than going
            // through repos with their default top-N caps for audit). The
            // export is always a full snapshot — partial dumps would be a
            // footgun for backup/restore.
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            var connections = await db.Connections.AsNoTracking()
                .OrderBy(c => c.DisplayName)
                .ToListAsync();
            var groups = await db.Groups.AsNoTracking()
                .OrderBy(g => g.Name)
                .ToListAsync();
            var audit  = await db.AuditLog.AsNoTracking()
                .OrderByDescending(a => a.OccurredAt)
                .ToListAsync();

            var snapshot = new DatabaseExport
            {
                ExportedAt   = DateTime.UtcNow,
                AppVersion   = ViewModel.AppVersion,
                Connections  = connections,
                Groups       = groups,
                AuditEntries = audit,
            };

            var opts = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot, opts);
            await Windows.Storage.FileIO.WriteTextAsync(file, json);

            await ShowMessageAsync(
                "Export complete",
                $"Wrote {connections.Count} connection(s), {groups.Count} group(s), and " +
                $"{audit.Count} audit entries to {file.Path}.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Export failed", ex.Message);
        }
    }

    /// <summary>Container for the JSON dump. Kept here (not in
    /// NexusRDM.Core) because it's a UI-side concern and we want the
    /// shape easy to evolve without ceremony.</summary>
    private sealed class DatabaseExport
    {
        public DateTime ExportedAt   { get; set; }
        public string?  AppVersion   { get; set; }
        public System.Collections.Generic.List<NexusRDM.Core.Models.ConnectionProfile> Connections  { get; set; } = new();
        public System.Collections.Generic.List<NexusRDM.Core.Models.Group>             Groups       { get; set; } = new();
        public System.Collections.Generic.List<NexusRDM.Core.Models.AuditEntry>        AuditEntries { get; set; } = new();
    }

    private async void RevealDb_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Open the data folder in Explorer with the db file selected.
            // Process.Start with explorer.exe + /select handles the
            // missing-file case gracefully (it just opens the folder).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"/select,\"{App.DbPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Couldn't open folder", ex.Message);
        }
    }

    private async void ResetDb_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            Title             = "Reset database?",
            Content           = "This permanently deletes every saved connection, group, and audit entry, " +
                                "AND removes every NexusRDM-managed credential from Windows Credential Manager. " +
                                "The app will close — re-launch to start fresh.",
            PrimaryButtonText = "Delete and quit",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };
        if (await NexusRDM.Services.DialogHost.ShowAsync(confirm) != ContentDialogResult.Primary) return;

        // Wipe vault first while the DI scope is alive and the vault
        // can still iterate its keys. Doing this AFTER the DB delete
        // would also work, but we don't want a partial DB-gone-vault-
        // intact state to linger if the file delete throws.
        try
        {
            using var vaultScope = App.Services.CreateScope();
            var vault = vaultScope.ServiceProvider
                .GetRequiredService<NexusRDM.Core.Interfaces.ICredentialVault>();
            // Snapshot the key list — Delete mutates the underlying
            // store, so iterating ListKeys() directly would skip rows.
            foreach (var key in vault.ListKeys().ToArray())
            {
                try { vault.Delete(key); }
                catch { /* one bad entry shouldn't abort the whole wipe */ }
            }
        }
        catch { /* vault unavailable — proceed with DB reset anyway */ }

        // Best-effort drop via EF, then nuke the file. Either succeeding
        // is enough — File.Delete catches the case where EF couldn't
        // close all handles.
        try
        {
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
            await db.Database.EnsureDeletedAsync();
        }
        catch { /* fall through to direct file delete */ }

        try
        {
            if (System.IO.File.Exists(App.DbPath)) System.IO.File.Delete(App.DbPath);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                "Couldn't delete database file",
                $"{ex.Message}\n\nDelete it manually and restart:\n{App.DbPath}");
            return;
        }

        // Close every secondary window first so they don't hang on
        // dispose; then close the main window. App startup re-runs
        // Database.Migrate() which re-creates an empty schema.
        foreach (var w in App.SecondaryWindows.ToArray())
        {
            try { w.Close(); } catch { }
        }
        App.MainWin?.Close();
    }

    private async System.Threading.Tasks.Task ShowMessageAsync(string title, string body)
    {
        var dlg = new ContentDialog
        {
            Title           = title,
            Content         = body,
            CloseButtonText = "OK",
            XamlRoot        = XamlRoot,
        };
        try { await NexusRDM.Services.DialogHost.ShowAsync(dlg); } catch { /* root gone */ }
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

    /// <summary>Tracks exactly which elements *we* hid so restoring
    /// only flips those back to Visible. Setting Visible blindly on
    /// every element clobbers any in-place x:Bind Visibility binding
    /// (e.g. the custom-palette editor that's only visible when the
    /// active theme is Custom) until that binding's source changes
    /// again.</summary>
    private readonly HashSet<UIElement> _hiddenByFilter = new();

    private void ApplyFilterCore()
    {
        _sectionMap ??= BuildSectionMap();

        var pickedTag = (SettingsNav.SelectedItem as ListBoxItem)?.Tag as string ?? "ALL";
        var query     = (NavSearchBox.Text ?? string.Empty).Trim();

        // Restore everything we hid last pass — leaves elements managed
        // by their own visibility bindings untouched.
        foreach (var el in _hiddenByFilter) el.Visibility = Visibility.Visible;
        _hiddenByFilter.Clear();

        // Setting Visibility=Visible above clobbers any x:Bind that
        // was driving the element to Collapsed. x:Bind only re-fires
        // when its source property changes, so a binding-driven element
        // we previously hid stays Visible-by-restore even though the
        // source still says Collapsed. Re-assert the known cases here
        // — keep this list in sync as new conditional sections appear.
        if (CustomPaletteEditor is not null)
            CustomPaletteEditor.Visibility = ViewModel.CustomThemeVisibility;

        // Section pick: hide every section but the picked one (or all
        // for ALL). We only Collapse — never explicitly set Visible,
        // because the previous-pass restore already did that.
        if (!string.Equals(pickedTag, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (section, elements) in _sectionMap)
            {
                if (string.Equals(pickedTag, section, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var el in elements)
                {
                    el.Visibility = Visibility.Collapsed;
                    _hiddenByFilter.Add(el);
                }
            }
        }

        // Search: hide sections whose header/contents don't match the
        // query, then dim nav items that don't match either.
        if (!string.IsNullOrEmpty(query))
        {
            var q = query.ToLowerInvariant();
            foreach (var (section, elements) in _sectionMap)
            {
                // Skip sections we already hid by section-pick.
                if (elements.Count == 0 || _hiddenByFilter.Contains(elements[0])) continue;
                var hit = section.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                          elements.Any(el => ContainsSearchText(el, q));
                if (!hit)
                {
                    foreach (var el in elements)
                    {
                        el.Visibility = Visibility.Collapsed;
                        _hiddenByFilter.Add(el);
                    }
                }
            }

            foreach (var item in SettingsNav.Items.OfType<ListBoxItem>())
            {
                var tag = item.Tag as string ?? string.Empty;
                if (string.Equals(tag, "ALL", StringComparison.OrdinalIgnoreCase)) continue;
                if (!tag.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    item.Visibility = Visibility.Collapsed;
                    _hiddenByFilter.Add(item);
                }
            }
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
