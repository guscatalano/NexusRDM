using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;
using NexusRDM.ViewModels;
using NexusRDM.Views;
using Windows.UI;

namespace NexusRDM;

public sealed partial class MainWindow : Window
{
    public MainViewModel  ViewModel { get; }
    private readonly SessionManager     _sessions;
    private readonly ISshHandler        _ssh;
    private readonly IRdpHandler        _rdp;
    private readonly ICredentialVault   _vault;
    private readonly IConnectionService _svc;

    private enum NavSection { Connections, Audit, Settings }
    private NavSection _currentNav = NavSection.Connections;

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        _sessions = App.Services.GetRequiredService<SessionManager>();
        _ssh      = App.Services.GetRequiredService<ISshHandler>();
        _rdp      = App.Services.GetRequiredService<IRdpHandler>();
        _vault    = App.Services.GetRequiredService<ICredentialVault>();
        _svc      = App.Services.GetRequiredService<IConnectionService>();

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetTitleBarColors();

        // Title bar / taskbar icon. Path is relative to the app's base
        // dir; AppIcon.ico is copied next to NexusRDM.dll via the csproj
        // Content entry. SetIcon silently no-ops if the file's missing.
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch { /* non-fatal — falls back to default icon */ }
        ConnectionsPane.ConnectRequested += OnConnectRequested;
        ConnectionsPane.CollapseRequested += (_, _) => SidebarToggle_Click(null!, null!);
        // RDP sessions own a top-level Win32 form pinned over their host
        // tab. WinUI 3 TabView doesn't unload the inactive tab's content
        // reliably (so RdpSessionView.Unloaded isn't the right signal),
        // but SelectionChanged fires every time the selected tab changes.
        // Hide every RDP form except the one whose tab is now selected.
        SessionTabs.SelectionChanged += OnSessionTabsSelectionChanged;
        HookCloseConfirmation();
        RebuildHotkeys();

        AddWelcomeTab();
    }

    private void OnSessionTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SessionTabs.SelectedItem as TabViewItem;
        foreach (var item in SessionTabs.TabItems.OfType<TabViewItem>())
        {
            if (item.Tag is OpenSession os && os.RdpSession is { } rdp)
                rdp.SetVisible(ReferenceEquals(item, selected));
        }
    }

    private void SetTitleBarColors()
    {
        if (AppWindow.TitleBar is { } tb)
        {
            tb.ExtendsContentIntoTitleBar = true;
            var bg = Color.FromArgb(0xFF, 0x22, 0x22, 0x2A);
            tb.BackgroundColor         = bg;
            tb.InactiveBackgroundColor = bg;
            tb.ButtonBackgroundColor         = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor         = Color.FromArgb(0xFF, 0x80, 0x80, 0x90);
            tb.ButtonHoverBackgroundColor    = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
        }
    }

    private void AddWelcomeTab()
    {
        var bg0    = (SolidColorBrush)Application.Current.Resources["NxBg0"];
        var bg1    = (SolidColorBrush)Application.Current.Resources["NxBg1"];
        var brd    = (SolidColorBrush)Application.Current.Resources["NxBrd"];
        var tx1    = (SolidColorBrush)Application.Current.Resources["NxTx1"];
        var tx2    = (SolidColorBrush)Application.Current.Resources["NxTx2"];
        var tx3    = (SolidColorBrush)Application.Current.Resources["NxTx3"];
        var ssh    = (SolidColorBrush)Application.Current.Resources["NxSsh"];
        var rdp    = (SolidColorBrush)Application.Current.Resources["NxRdp"];
        var accent = (SolidColorBrush)Application.Current.Resources["NxAccent"];

        var root = new Grid { Background = bg0, Padding = new Thickness(40, 32, 40, 32) };

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment           = HorizontalAlignment.Center,
        };

        var stack = new StackPanel { Spacing = 18, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        titleRow.Children.Add(new FontIcon { Glyph = "", FontSize = 28, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(new TextBlock { Text = "Nexus RDM", FontSize = 22, FontWeight = new Windows.UI.Text.FontWeight(600), Foreground = tx1, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(titleRow);

        stack.Children.Add(new TextBlock
        {
            Text = "A unified remote-desktop manager for SSH and RDP sessions. " +
                   "Define your connections once, store credentials securely in the Windows Credential Manager, " +
                   "and jump into a tabbed session with a single click.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = tx2,
        });

        stack.Children.Add(SectionHeader("GET STARTED", tx3));
        stack.Children.Add(StepCard(bg1, brd, accent, tx1, tx2, "1", "Add a connection",
            "Click “New” in the left pane. Pick SSH or RDP, fill in host/port, and choose how to authenticate."));
        stack.Children.Add(StepCard(bg1, brd, accent, tx1, tx2, "2", "Save credentials",
            "Enter username/password and tick “Save to Windows Credential Manager.” Your secrets never live in plain text."));
        stack.Children.Add(StepCard(bg1, brd, accent, tx1, tx2, "3", "Open a connection",
            "Click any connection in the tree. SSH opens in a terminal tab; RDP launches the embedded Remote Desktop control."));

        stack.Children.Add(SectionHeader("PROTOCOLS", tx3));
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        // Use Segoe Fluent Icons rather than colored dots so the legend
        // doesn't look like a connection-status indicator (those live in
        // the connection tree and tabs and *do* color-code by status).
        legend.Children.Add(LegendIcon("", "SSH — terminal sessions", ssh, tx2));   // CommandPrompt
        legend.Children.Add(LegendIcon("", "RDP — remote desktop",    rdp, tx2));   // Remote
        stack.Children.Add(legend);

        stack.Children.Add(SectionHeader("TIPS", tx3));
        stack.Children.Add(BulletLine("•  Use the search box (Ctrl+F) to filter the connection tree.", tx2));
        stack.Children.Add(BulletLine("•  Group connections to keep environments organised.",          tx2));
        stack.Children.Add(BulletLine("•  The Audit pane records every session open, close, and error.", tx2));
        stack.Children.Add(BulletLine("•  Settings let you change theme and default ports.",            tx2));

        scroller.Content = stack;
        root.Children.Add(scroller);

        SessionTabs.TabItems.Add(new TabViewItem
        {
            Header     = "Home",
            Tag        = "welcome",
            Content    = root,
            IconSource = new SymbolIconSource { Symbol = Symbol.Home }
        });
    }

    private static TextBlock SectionHeader(string text, SolidColorBrush fg) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = new Windows.UI.Text.FontWeight(600),
        Foreground = fg,
        Margin = new Thickness(0, 8, 0, 0),
    };

    private static Border StepCard(SolidColorBrush bg, SolidColorBrush brd, SolidColorBrush accent,
                                   SolidColorBrush tx1, SolidColorBrush tx2,
                                   string number, string title, string body)
    {
        var card = new Border
        {
            Background      = bg,
            BorderBrush     = brd,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(14, 12, 14, 12),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var num = new TextBlock
        {
            Text = number,
            FontSize = 16,
            FontWeight = new Windows.UI.Text.FontWeight(600),
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(num, 0);

        var inner = new StackPanel { Spacing = 2 };
        inner.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = new Windows.UI.Text.FontWeight(600), Foreground = tx1 });
        inner.Children.Add(new TextBlock { Text = body, FontSize = 12, Foreground = tx2, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(inner, 1);

        grid.Children.Add(num);
        grid.Children.Add(inner);
        card.Child = grid;
        return card;
    }

    private static StackPanel LegendDot(SolidColorBrush dot, string label, SolidColorBrush fg)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static StackPanel LegendIcon(string glyph, string label, SolidColorBrush iconFg, SolidColorBrush textFg)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = iconFg, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = textFg, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static TextBlock BulletLine(string text, SolidColorBrush fg) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = fg,
        TextWrapping = TextWrapping.Wrap,
    };

    private bool _sidebarCollapsed;

    /// <summary>Rebuilds the four global keyboard accelerators (next/prev
    /// tab, toggle full screen, toggle pop out) from the current settings.
    /// Called once at startup and re-called whenever the user edits a
    /// hotkey on the Settings page.</summary>
    public void RebuildHotkeys()
    {
        if (Content is not FrameworkElement root) return;
        root.KeyboardAccelerators.Clear();

        Add("HotkeyNextTab",    "Ctrl+Tab",       OnHotkeyNextTab);
        Add("HotkeyPrevTab",    "Ctrl+Shift+Tab", OnHotkeyPrevTab);
        Add("HotkeyFullScreen", "F11",            OnHotkeyToggleFullScreen);
        Add("HotkeyPopOut",     "Ctrl+Shift+P",   OnHotkeyTogglePopOut);

        void Add(string key, string fallback, TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
        {
            // Skip silently when the user has unticked the enable
            // checkbox — letting the accelerator register would make the
            // chord still "fire" with the default binding even after a
            // user explicitly disabled it.
            if (!SettingsStore.ReadHotkeyEnabled($"{key}Enabled")) return;
            var parsed = SettingsStore.ParseHotkey(SettingsStore.ReadHotkey(key, fallback));
            if (parsed is null) return;
            var acc = new KeyboardAccelerator { Key = parsed.Value.Key, Modifiers = parsed.Value.Mods };
            acc.Invoked += handler;
            root.KeyboardAccelerators.Add(acc);
        }
    }

    private void OnHotkeyNextTab(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        var n = SessionTabs.TabItems.Count;
        if (n <= 1) return;
        SessionTabs.SelectedIndex = (SessionTabs.SelectedIndex + 1) % n;
    }
    private void OnHotkeyPrevTab(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        var n = SessionTabs.TabItems.Count;
        if (n <= 1) return;
        SessionTabs.SelectedIndex = (SessionTabs.SelectedIndex - 1 + n) % n;
    }
    private void OnHotkeyToggleFullScreen(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (SessionTabs.SelectedItem is TabViewItem t && t.Content is ISessionView v) v.ToggleFullScreen();
    }
    private void OnHotkeyTogglePopOut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (SessionTabs.SelectedItem is TabViewItem t && t.Content is ISessionView v) v.PopOut();
    }

    // ── Sidebar splitter (drag to resize the connections pane) ──────────

    /// <summary>True between PointerPressed and PointerReleased on the
    /// splitter; PointerMoved only resizes when this is set so other
    /// pointer events (e.g. hover-only) don't accidentally drag.</summary>
    private bool   _splitterDragging;
    /// <summary>Anchor point captured at PointerPressed in main-window
    /// coordinates. Move deltas are computed against this; we don't use
    /// the per-event delta because PointerMoved fires at variable rates
    /// and accumulating floats drifts.</summary>
    private double _splitterStartX;
    private double _splitterStartWidth;

    // Cursor handling intentionally omitted: ProtectedCursor is
    // a `protected` member, only settable from a UIElement subclass.
    // Subclassing Border just to override the cursor for a 4-px
    // splitter is more weight than it's worth — the visible bar
    // already reads as draggable, and PointerPressed gives us hit
    // feedback. If we ever want a true resize cursor we can swap in
    // the CommunityToolkit GridSplitter or add a custom-control.
    private void SidebarSplitter_PointerEntered(object sender, PointerRoutedEventArgs e) { }
    private void SidebarSplitter_PointerExited(object sender, PointerRoutedEventArgs e)  { }

    private void SidebarSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement el) return;
        _splitterDragging   = true;
        _splitterStartX     = e.GetCurrentPoint(this.Content).Position.X;
        _splitterStartWidth = SidebarColumn.ActualWidth;
        el.CapturePointer(e.Pointer);
    }

    private void SidebarSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        var x       = e.GetCurrentPoint(this.Content).Position.X;
        var newWidth = _splitterStartWidth + (x - _splitterStartX);
        // Honor ColumnDefinition's clamps directly so the pane can't
        // collapse out from under the user or run past the safety cap.
        var min = SidebarColumn.MinWidth;
        var max = SidebarColumn.MaxWidth;
        if (double.IsFinite(min) && newWidth < min) newWidth = min;
        if (double.IsFinite(max) && newWidth > max) newWidth = max;
        SidebarColumn.Width = new GridLength(newWidth);
    }

    private void SidebarSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        _splitterDragging = false;
        if (sender is UIElement el) el.ReleasePointerCapture(e.Pointer);
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        if (_sidebarCollapsed)
        {
            // Drop the column to a hairline (0 + the 1px separator) so
            // the TabView gets the reclaimed space; the strip overlays
            // a 22-px reveal handle on the left.
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width    = new GridLength(22);
            ConnectionsPane.Visibility       = Visibility.Collapsed;
            SidebarCollapsedStrip.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarColumn.MinWidth = 160;
            SidebarColumn.Width    = new GridLength(240);
            ConnectionsPane.Visibility       = Visibility.Visible;
            SidebarCollapsedStrip.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnNavConn_Click(object sender, RoutedEventArgs e) => ShowNav(NavSection.Connections);
    private void BtnNavAudit_Click(object sender, RoutedEventArgs e) => ShowNav(NavSection.Audit);
    private void BtnNavSettings_Click(object sender, RoutedEventArgs e) => ShowNav(NavSection.Settings);

    private void BtnCopyVisualTree_Click(object sender, RoutedEventArgs e)
    {
        // Dump the current visual tree to the clipboard for diagnostic use.
        // The Window itself isn't a UIElement; its Content is the root we
        // want to walk. Lives in the sidebar (not on the Settings page) so
        // clicking it doesn't navigate the user away and mutate the very
        // tree they're trying to capture.
        var tree = NexusRDM.Services.VisualTreeDump.Build(Content as DependencyObject);
        var rdp  = NexusRDM.Services.VisualTreeDump.DumpRdpWindows();
        var dump = tree + Environment.NewLine + rdp;

        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(dump);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

        CopyVisualTreeStatus.Text = $"Copied {dump.Length:N0} chars to clipboard.";
        CopyVisualTreeFlyout.ShowAt(BtnCopyVisualTree);
    }

    private async void BtnTakeScreenshot_Click(object sender, RoutedEventArgs e)
    {
        // RenderTargetBitmap captures the window's content as BGRA8.
        // We encode to PNG via an in-memory random-access stream,
        // then File.WriteAllBytes to disk — simpler than going
        // through StorageFile and works around the apartment quirks
        // of the WinRT file APIs in WinUI 3.
        try
        {
            if (Content is not Microsoft.UI.Xaml.UIElement root)
            {
                ScreenshotStatus.Text = "Window has no content to capture.";
                ScreenshotFlyout.ShowAt(BtnTakeScreenshot);
                return;
            }

            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(root);
            var pixels = await rtb.GetPixelsAsync();

            var dir = System.IO.Path.Combine(App.AppDataDir, "screenshots");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"nexusrdm-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ms);
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight,
                96, 96,
                System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
            await encoder.FlushAsync();

            // Pull the encoded PNG bytes out of the in-memory stream
            // and dump them to disk in one shot.
            var bytes = new byte[ms.Size];
            ms.Seek(0);
            using (var reader = new Windows.Storage.Streams.DataReader(ms.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)ms.Size);
                reader.ReadBytes(bytes);
            }
            await System.IO.File.WriteAllBytesAsync(path, bytes);

            ScreenshotStatus.Text = $"Saved to:\n{path}";
        }
        catch (Exception ex)
        {
            ScreenshotStatus.Text = $"Failed: {ex.Message}";
        }
        ScreenshotFlyout.ShowAt(BtnTakeScreenshot);
    }

    private void ShowNav(NavSection nav)
    {
        _currentNav = nav;
        ConnView.Visibility        = nav == NavSection.Connections ? Visibility.Visible : Visibility.Collapsed;
        AuditPage.Visibility       = nav == NavSection.Audit       ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageView.Visibility = nav == NavSection.Settings    ? Visibility.Visible : Visibility.Collapsed;

        // Hide every embedded RDP form when leaving the Connections
        // section — the forms are top-level Win32 windows owned by the
        // WinUI window and otherwise stay pinned over their (now-hidden)
        // host panel, painting on top of Audit / Settings. When we come
        // back, OnSessionTabsSelectionChanged restores only the form
        // that belongs to the currently selected tab.
        if (nav != NavSection.Connections)
        {
            foreach (var s in _sessions.Sessions)
            {
                try { s.RdpSession?.SetVisible(false); } catch { /* best effort */ }
            }
        }
        else
        {
            var selected = SessionTabs.SelectedItem as TabViewItem;
            foreach (var item in SessionTabs.TabItems.OfType<TabViewItem>())
            {
                if (item.Tag is OpenSession os && os.RdpSession is { } rdp)
                {
                    try { rdp.SetVisible(ReferenceEquals(item, selected)); }
                    catch { /* best effort */ }
                }
            }
        }

        var accentBg = (SolidColorBrush)Application.Current.Resources["NxBg3"];
        var clear    = new SolidColorBrush(Colors.Transparent);
        BtnNavConn.Background     = nav == NavSection.Connections ? accentBg : clear;
        BtnNavAudit.Background    = nav == NavSection.Audit       ? accentBg : clear;
        BtnNavSettings.Background = nav == NavSection.Settings    ? accentBg : clear;

        var onFg  = (SolidColorBrush)Application.Current.Resources["NxTx1"];
        var offFg = (SolidColorBrush)Application.Current.Resources["NxTx3"];
        SetNavIconFg(BtnNavConn,     nav == NavSection.Connections ? onFg : offFg);
        SetNavIconFg(BtnNavAudit,    nav == NavSection.Audit       ? onFg : offFg);
        SetNavIconFg(BtnNavSettings, nav == NavSection.Settings    ? onFg : offFg);
    }

    private static void SetNavIconFg(Button btn, SolidColorBrush brush)
    {
        if (btn.Content is IconElement icon) icon.Foreground = brush;
    }

    private async void OnConnectRequested(object? sender, ConnectionProfile profile)
    {
        ShowNav(NavSection.Connections);
        // Tree-click reuses an open tab if one already exists; the right-click
        // Duplicate path bypasses this by calling Open*TabAsync directly.
        foreach (var item in SessionTabs.TabItems.OfType<TabViewItem>())
            if (item.Tag is OpenSession os && os.ConnectionId == profile.Id)
            { SessionTabs.SelectedItem = item; return; }

        if (profile.Protocol == ConnectionProtocol.Ssh)
            await OpenSshTabAsync(profile);
        else
            await OpenRdpTabAsync(profile);
    }

    private async Task OpenSshTabAsync(ConnectionProfile profile)
    {
        var (username, password) = await ResolveCredentialsAsync(profile);
        if (username is null) return;
        var session = _ssh.CreateSession(profile, username, password!);
        var entry   = _sessions.AddSsh(profile, session);
        var vm      = new SshSessionViewModel(profile, session, _sessions);
        // Use the same Segoe Fluent glyph the home-page legend shows
        // for SSH (CommandPrompt, U+E756) so tab + legend stay in sync.
        AddSessionTab(profile, entry, "", new SshSessionView(vm),
            (SolidColorBrush)Application.Current.Resources["NxSsh"]);
        WireSessionAuditEvents(profile, session);
    }

    private async Task OpenRdpTabAsync(ConnectionProfile profile)
    {
        var (username, password) = await ResolveCredentialsAsync(profile);
        if (username is null) return;
        // Pass the vault password through to the OCX (mstscax sets it on
        // AdvancedSettings9.ClearTextPassword for silent auth). Without
        // this the remote server always prompts even when creds are saved.
        var session = _rdp.CreateSession(profile, username, password ?? string.Empty);
        var entry   = _sessions.AddRdp(profile, session);
        var vm      = new RdpSessionViewModel(profile, session, _rdp, username, password ?? string.Empty, _sessions);
        // Same glyph the home-page legend uses for RDP (Remote, U+E8AF).
        AddSessionTab(profile, entry, "", new RdpSessionView(vm),
            (SolidColorBrush)Application.Current.Resources["NxRdp"]);
        WireSessionAuditEvents(profile, session);
    }

    /// <summary>Forwards session lifecycle events into the audit log so
    /// the user can see when a connection actually came up or dropped —
    /// not just when the tab opened/closed. Fire-and-forget on a
    /// background task because the events arrive on arbitrary threads.</summary>
    /// <summary>Marshal an audit-record call back to the UI thread and
    /// catch any failure. Without this, the EF DbContext's app-wide
    /// scope can be hit concurrently by session-thread events and UI
    /// commands; the resulting concurrency exception goes silent under
    /// fire-and-forget <c>_ = task</c>.</summary>
    private void RecordAudit(Func<Task> call) => DispatcherQueue.TryEnqueue(async () =>
    {
        try { await call(); }
        catch { /* never fault the UI thread for an audit miss */ }
    });

    private void WireSessionAuditEvents(ConnectionProfile profile, ISshSession ssh)
    {
        // Run audit writes on the UI dispatcher (EF DbContext is shared
        // app-wide and not thread-safe; the SSH read-loop thread firing
        // simultaneously with the user's UI command was silently dropping
        // the audit row via fire-and-forget exception swallowing).
        ssh.Disconnected += (_, _) => RecordAudit(() =>
            _svc.RecordDisconnectedAsync(profile.Id, "session ended"));
        var connectedLogged = false;
        ssh.DataReceived += (_, _) =>
        {
            if (connectedLogged) return;
            connectedLogged = true;
            RecordAudit(() => _svc.RecordConnectedAsync(profile.Id));
        };
    }

    private void WireSessionAuditEvents(ConnectionProfile profile, IRdpSession rdp)
    {
        rdp.Connected    += (_, _)      => RecordAudit(() => _svc.RecordConnectedAsync(profile.Id));
        rdp.Disconnected += (_, reason) => RecordAudit(() => _svc.RecordDisconnectedAsync(profile.Id, reason));
        rdp.FatalError   += (_, msg)    => RecordAudit(() => _svc.RecordFailedAsync(profile.Id, msg));

        // Pop-out close → form snapped back to the panel rect. If the
        // user is now on a different tab than they were when they popped
        // out, the form would otherwise sit visible on top of the wrong
        // tab. Push it back through the per-tab visibility logic.
        rdp.ReAttached   += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            var selected = SessionTabs.SelectedItem as TabViewItem;
            var owner = SessionTabs.TabItems.OfType<TabViewItem>()
                .FirstOrDefault(i => i.Tag is OpenSession os && ReferenceEquals(os.RdpSession, rdp));
            try { rdp.SetVisible(ReferenceEquals(owner, selected)); }
            catch { /* best effort */ }
        });
    }

    private void AddSessionTab(ConnectionProfile profile, OpenSession entry, string iconGlyph, UIElement content, SolidColorBrush dotColor)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        // Status dot: starts red (not yet connected), flips to green on
        // the session's Connected event and back to red on Disconnected.
        // The dotColor argument (protocol-coded) is now ignored — protocol
        // is communicated by the tab icon (Globe / Remote).
        var connected   = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x3D, 0xD6, 0x8C));
        var disconnected= new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));
        var statusDot   = new Ellipse { Width = 7, Height = 7, Fill = disconnected };
        header.Children.Add(statusDot);
        header.Children.Add(new TextBlock { Text = profile.DisplayName, FontSize = 12, Foreground = (SolidColorBrush)Application.Current.Resources["NxTx1"] });

        // Wire the dot to the underlying session events. Both backends
        // expose Connected/Disconnected; we marshal back to the UI thread
        // because RDP fires from the WinForms STA.
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Mark(bool live) => ui.TryEnqueue(() => statusDot.Fill = live ? connected : disconnected);
        if (entry.SshSession is { } ssh)
        {
            ssh.Disconnected += (_, _) => Mark(false);
            // ISshSession lacks an explicit Connected event — flip green
            // on first byte received.
            var seenData = false;
            ssh.DataReceived += (_, _) => { if (!seenData) { seenData = true; Mark(true); } };
        }
        if (entry.RdpSession is { } rdp)
        {
            rdp.Connected    += (_, _)  => Mark(true);
            rdp.Disconnected += (_, _)  => Mark(false);
            rdp.FatalError   += (_, _)  => Mark(false);
        }

        var tab = new TabViewItem
        {
            Header     = header,
            // Tag carries the OpenSession instance — close handler disposes
            // exactly this session (matters when duplicate tabs share a
            // ConnectionId).
            Tag        = entry,
            Content    = content,
            IconSource = new FontIconSource { Glyph = iconGlyph }
        };

        // Right-click flyout — currently exposes Duplicate. Only meaningful for
        // SSH because RDP's mstsc-based session can't share state across tabs.
        var menu = new MenuFlyout();
        if (profile.Protocol == ConnectionProtocol.Ssh)
        {
            var dup = new MenuFlyoutItem { Text = "Duplicate", Icon = new SymbolIcon(Symbol.Copy) };
            dup.Click += async (_, _) => await OpenSshTabAsync(profile);
            menu.Items.Add(dup);
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        var close = new MenuFlyoutItem { Text = "Close", Icon = new SymbolIcon(Symbol.Cancel) };
        close.Click += async (_, _) =>
        {
            await _sessions.CloseAsync(entry);
            SessionTabs.TabItems.Remove(tab);
        };
        menu.Items.Add(close);
        tab.ContextFlyout = menu;

        SessionTabs.TabItems.Add(tab);
        SessionTabs.SelectedItem = tab;
    }

    private async Task<(string? Username, string? Password)> ResolveCredentialsAsync(ConnectionProfile profile)
    {
        if (profile.CredentialKey is not null)
        {
            var c = _vault.Load(profile.CredentialKey);
            if (c is not null) return (c.Value.Username, c.Value.Password);
        }
        var dlg = new CredentialPromptDialog { XamlRoot = Content.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return (null, null);
        return (dlg.Username, dlg.Password);
    }

    public async Task<ConnectionProfile?> ShowEditConnectionPanelAsync(ConnectionProfile? existing)
    {
        var panel = new EditConnectionPanel(existing);
        OverlayHost.Children.Clear();
        OverlayHost.Children.Add(panel);
        OverlayHost.Visibility = Visibility.Visible;

        // Edit-connection is a 440-DIP right-anchored slide-over. Each
        // embedded RDP form is a top-level Win32 window owned by the
        // WinUI app, so it paints above the slide-over. Instead of
        // hiding the live session, narrow every form by 440 DIPs (in
        // raw pixels) so the slide-over occupies that strip on the
        // right while the user keeps watching the session.
        var dpi      = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale    = dpi <= 0 ? 1.0 : dpi / 96.0;
        var insetPx  = (int)(440 * scale);

        var inset    = new List<IRdpSession>();
        foreach (var s in _sessions.Sessions)
            if (s.RdpSession is { } rdp)
            {
                try { rdp.SetRightInset(insetPx); inset.Add(rdp); }
                catch { /* best effort */ }
            }

        try
        {
            return await panel.Result;
        }
        finally
        {
            OverlayHost.Visibility = Visibility.Collapsed;
            OverlayHost.Children.Clear();

            foreach (var rdp in inset)
            {
                try { rdp.SetRightInset(0); } catch { /* best effort */ }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    private async void SessionTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        // Close the exact OpenSession this tab owns; identifying by
        // ConnectionId would dispose the wrong session when duplicate tabs are
        // open against the same connection.
        if (args.Tab.Tag is OpenSession os)
        {
            // Skip per-tab confirmation when the user already confirmed
            // an app-wide close — we don't want to prompt N times during
            // teardown.
            if (!_suppressTabConfirm
                && SettingsStore.ReadConfirmCloseActive()
                && IsSessionActive(os))
            {
                var ok = await ConfirmCloseAsync(
                    "Close active session?",
                    $"'{os.DisplayName}' is still connected. Close it anyway?");
                if (!ok) return;
            }
            await _sessions.CloseAsync(os);
        }
        sender.TabItems.Remove(args.Tab);
    }

    /// <summary>True while an app-wide close is unwinding so per-tab
    /// close handlers don't prompt again.</summary>
    private bool _suppressTabConfirm;
    private bool _confirmedClose;

    private static bool IsSessionActive(OpenSession s) =>
        (s.SshSession?.IsConnected ?? false) ||
        (s.RdpSession?.IsConnected ?? false);

    private bool AnyActiveSession() =>
        _sessions.Sessions.Any(IsSessionActive);

    private async Task<bool> ConfirmCloseAsync(string title, string body)
    {
        // DialogHost serialises against any other dialog already up
        // (sidebar delete, settings reset, etc.) and parks every
        // embedded RDP form so the buttons are reachable.
        var dlg = new ContentDialog
        {
            Title             = title,
            Content           = body,
            PrimaryButtonText = "Close",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = Content.XamlRoot,
        };
        return await DialogHost.ShowAsync(dlg) == ContentDialogResult.Primary;
    }

    /// <summary>Hook AppWindow.Closing so we can prompt before the window
    /// actually goes away. Wired from the constructor.</summary>
    private void HookCloseConfirmation()
    {
        AppWindow.Closing += (sender, args) =>
        {
            if (_confirmedClose) return;
            if (!SettingsStore.ReadConfirmCloseActive()) return;
            if (!AnyActiveSession())                     return;

            // Cancel synchronously so WinUI keeps the window alive, then
            // run the dialog + final Close on the dispatcher. The handler
            // itself stays sync so AppWindow doesn't see a half-evaluated
            // args.Cancel — async lambdas race with WinUI's close pipeline.
            args.Cancel = true;
            DispatcherQueue.TryEnqueue(async () =>
            {
                var ok = await ConfirmCloseAsync(
                    "Close Nexus RDM?",
                    "There are active sessions. Closing will disconnect all of them.");
                if (!ok) return;

                _confirmedClose      = true;
                _suppressTabConfirm  = true;
                Close();
            });
        };
    }
}
