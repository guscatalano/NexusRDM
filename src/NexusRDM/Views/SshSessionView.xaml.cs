using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NexusRDM.Protocols;
using NexusRDM.Services;
using NexusRDM.ViewModels;
using Windows.Foundation;
using WinRT.Interop;

namespace NexusRDM.Views;

public sealed partial class SshSessionView : UserControl, ISessionView
{
    public SshSessionViewModel ViewModel { get; }
    private bool _connectStarted;
    private readonly bool _isPoppedClone;

    /// <summary>Raised when the toolbar's "Files" button is clicked.
    /// MainWindow handles this by spawning an SFTP tab via
    /// <c>OpenSftpTabAsync</c> — paralleling the existing SFTP→SSH
    /// cross-launch event on <see cref="SftpView"/>.</summary>
    public event EventHandler<NexusRDM.Core.Models.ConnectionProfile>? OpenSftpRequested;

    // Strong refs to event handlers so the popped clone can unsubscribe
    // when its window closes (otherwise the closures keep the view
    // alive forever, holding refs to the now-orphaned Terminal).
    private EventHandler<byte[]>?      _dataHandler;
    private EventHandler<byte[]>?      _userInputHandler;
    private SizeChangedEventHandler?   _terminalSizeHandler;

    public SshSessionView(SshSessionViewModel vm) : this(vm, alreadyConnected: false) { }

    /// <summary>Construct a view bound to <paramref name="vm"/>. When
    /// <paramref name="alreadyConnected"/> is true, the OnLoaded path
    /// skips <c>ConnectAsync</c> — used by the pop-out path which
    /// creates a fresh view sharing the original session.</summary>
    public SshSessionView(SshSessionViewModel vm, bool alreadyConnected)
    {
        ViewModel       = vm;
        _connectStarted = alreadyConnected;
        _isPoppedClone  = alreadyConnected;
        InitializeComponent();

        HostLabel.Text       = $"{vm.Host}";
        HostStatusLabel.Text = vm.DisplayName;

        IsTabStop  = true;
        AddHandler(KeyDownEvent,
            new KeyEventHandler(OnAnyKeyDown), handledEventsToo: true);
        AddHandler(CharacterReceivedEvent,
            new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>(OnAnyCharacterReceived),
            handledEventsToo: true);

        _dataHandler = (_, data) =>
            DispatcherQueue.TryEnqueue(() => Terminal.Feed(data));
        ViewModel.DataReceived += _dataHandler;

        // User input routing:
        //   • If an auth broker is in flight (server is mid-
        //     keyboard-interactive challenge) → broker absorbs
        //     keystrokes, feeds back into terminal as needed,
        //     completes its TaskCompletionSource on Enter.
        //   • Otherwise → bytes flow into the SSH shell via the VM.
        _userInputHandler = (_, data) =>
        {
            if (ViewModel.AuthBroker is { IsActive: true } broker
                && broker.OnUserInput(data))
                return;
            _ = ViewModel.SendInputAsync(data);
        };
        Terminal.UserInput += _userInputHandler;

        // Broker → terminal display path: prompt text from the server
        // and echo of typed chars during auth both arrive here.
        // Marshalled to the UI thread because SSH.NET fires
        // AuthenticationPrompt on its connect thread.
        if (ViewModel.AuthBroker is { } authBroker)
        {
            authBroker.OutputToTerminal += (_, data) =>
                DispatcherQueue.TryEnqueue(() => Terminal.Feed(data));
        }

        _terminalSizeHandler = async (_, _) =>
        {
            // SizeLabel is bound to ViewModel.PtyDisplay (driven by the
            // 1-second stats timer), so we just propagate the size to
            // the session and the binding picks it up on the next tick.
            var (cols, rows) = Terminal.TerminalSize;
            await ViewModel.ResizeAsync(cols, rows);
        };
        Terminal.SizeChanged += _terminalSizeHandler;

        Loaded += OnLoaded;
    }

    /// <summary>Unsubscribe every event handler so this view can be
    /// garbage-collected. Called by the original view when the popped
    /// clone's window closes.</summary>
    public void Detach()
    {
        if (_dataHandler is not null)         ViewModel.DataReceived -= _dataHandler;
        if (_userInputHandler is not null)    Terminal.UserInput     -= _userInputHandler;
        if (_terminalSizeHandler is not null) Terminal.SizeChanged   -= _terminalSizeHandler;
        _dataHandler         = null;
        _userInputHandler    = null;
        _terminalSizeHandler = null;
    }

    private void HookSizeTracking()
    {
        if (App.MainWin?.Content is not FrameworkElement root) return;

        void Update()
        {
            var available = root.ActualHeight - 80;
            if (available > 200) RootGrid.Height = available;
        }

        Update();
        root.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(Update);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Focus the terminal directly (not the surrounding UserControl)
        // so the user can immediately type at the `login as: ` /
        // password prompts without first clicking into the terminal.
        Terminal.Focus(FocusState.Programmatic);

        // Popped clone hosts a fresh window whose AppWindow has its own
        // size; HookSizeTracking would target the main window instead.
        if (!_isPoppedClone) HookSizeTracking();

        if (_connectStarted) return;
        _connectStarted = true;

        // PuTTYNG backend: hide the in-app terminal, show the host
        // panel, hand the WinUI window's HWND to the session BEFORE
        // ConnectAsync (PuTTYNG accepts -hwndparent only at launch).
        // Host-stats toggle only makes sense for the embedded backend,
        // which exposes a programmable SSH channel via SshSession.ExecAsync.
        // The PuTTY-backed session has no such channel, so hide the
        // button rather than show it disabled and confusing.
        HostStatsToggle.Visibility = ViewModel.HostStatsAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (ViewModel.Session is PuttySshSession puttySession)
        {
            Terminal.Visibility       = Visibility.Collapsed;
            PuttyHostPanel.Visibility = Visibility.Visible;
            try
            {
                var mainHwnd = WindowNative.GetWindowHandle(App.MainWin);
                puttySession.SetOwnerHwnd(mainHwnd);
            }
            catch (Exception ex)
            {
                WriteTrace($"[ Couldn't bind host HWND to PuTTYNG: {ex.Message} ]");
            }
        }

        WriteTrace($"[ Connecting to {ViewModel.Host} ... ]");
        try
        {
            await ViewModel.ConnectAsync();

            // Push the initial host-panel rect into the session now
            // that PuTTYNG is up. PuttyHostPanel_SizeChanged will
            // keep it in sync from here.
            if (ViewModel.Session is PuttySshSession putty2)
                ApplyPuttyHostBounds(putty2);

            // Sync the real terminal size to the SSH PTY. SshSession
            // creates the shell with its hardcoded default 220×50
            // because the size handshake hasn't happened yet; the
            // first Terminal.SizeChanged event only fires when the
            // UserControl resizes, which often won't happen again
            // between Connect and the user running their first
            // command. Without this call, programs that probe TIOCGWINSZ
            // (top, htop, vim, less) think the screen is 220-wide and
            // wrap every long line. Safe to call even when the size
            // happens to already match — it's a no-op via VtNetCore's
            // SendWindowChangeRequest path.
            if (ViewModel.IsConnected)
            {
                var (cols, rows) = Terminal.TerminalSize;
                await ViewModel.ResizeAsync(cols, rows);
            }

            if (!ViewModel.IsConnected)
                WriteTrace($"[ Connect returned but not connected: {ViewModel.StatusMessage} ]");
            // No "connected, awaiting prompt" message on success — by
            // the time we await past ConnectAsync the banner + prompt
            // have already been Fed into the terminal, so injecting
            // our own trace bytes here paints them out of order at the
            // bottom of the real output.
        }
        catch (Exception ex)
        {
            WriteTrace($"[ ConnectAsync threw: {ex.GetType().Name}: {ex.Message} ]");
        }
    }

    private void PuttyHostPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel?.Session is PuttySshSession putty)
            ApplyPuttyHostBounds(putty);
    }

    /// <summary>Translate the host panel's XAML coords into client-
    /// relative pixels (DPI-aware) and feed them to the session. PuTTY
    /// is a true Win32 child of the WinUI window after AttachToOwner,
    /// so SetWindowPos uses parent-client coords — no screen translation.</summary>
    private void ApplyPuttyHostBounds(PuttySshSession session)
    {
        try
        {
            var rootContent = App.MainWin.Content as UIElement ?? PuttyHostPanel;
            var transform   = PuttyHostPanel.TransformToVisual(rootContent);
            var topLeft     = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            var mainHwnd = WindowNative.GetWindowHandle(App.MainWin);
            var dpi      = GetDpiForWindow(mainHwnd);
            var scale    = dpi <= 0 ? 1.0 : dpi / 96.0;

            session.SetEmbeddedRect(
                clientX: (int)(topLeft.X * scale),
                clientY: (int)(topLeft.Y * scale),
                width:   (int)(PuttyHostPanel.ActualWidth  * scale),
                height:  (int)(PuttyHostPanel.ActualHeight * scale));
        }
        catch { /* layout in flux; the next SizeChanged will retry */ }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hWnd);

    private async void OnAnyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        var bytes = Terminal.TranslateSpecialKeyForView(e.Key);
        if (bytes is { Length: > 0 })
        {
            await ViewModel.SendInputAsync(bytes);
            e.Handled = true;
        }
    }

    private async void OnAnyCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (e.Character < 0x20 || e.Character == 0x7F) return;
        await ViewModel.SendInputAsync(Encoding.UTF8.GetBytes(new[] { e.Character }));
        e.Handled = true;
    }

    private void WriteTrace(string line) =>
        Terminal.Feed(Encoding.UTF8.GetBytes(line + "\r\n"));

    // ── Host stats panel ─────────────────────────────────────────────────

    /// <summary>Toggle the host-stats panel. The first time the user
    /// turns it on we show a ContentDialog explaining what will happen
    /// (commands sent to the server every 5s, visible in audit logs).
    /// Honoured only for backends that support a programmable channel —
    /// PuTTY-backed sessions hide the toggle entirely.</summary>
    private async void HostStatsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;

        if (btn.IsChecked == true)
        {
            // Turning ON — ask for confirmation with full disclosure.
            // Belt-and-suspenders: the toggle's IsEnabled is bound to
            // ViewModel.IsConnected, but binding latency could allow a
            // click through. Check explicitly.
            if (!ViewModel.HostStatsAvailable || !ViewModel.IsConnected)
            {
                btn.IsChecked = false;
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title    = "Enable host stats?",
                Content  = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text =
                        "While the panel is open, NexusRDM will run a small set of read-only " +
                        "commands on this server every 5 seconds:\n\n" +
                        "    cat /proc/loadavg\n" +
                        "    cat /proc/meminfo | head -3\n" +
                        "    uptime -s\n" +
                        "    who | wc -l\n" +
                        "    df -P / | tail -1\n\n" +
                        "These run on a separate SSH exec channel (not your interactive shell), " +
                        "but they will show up in the server's audit logs and consume a small " +
                        "amount of bandwidth. You can disable any time by clicking the toggle again."
                },
                PrimaryButtonText   = "Enable",
                CloseButtonText     = "Cancel",
                DefaultButton       = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                btn.IsChecked = false;
                return;
            }

            ViewModel.StartHostStatsPolling();
            HostStatsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ViewModel.StopHostStatsPolling();
            HostStatsPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ── Window-mode controls (toolbar buttons + global hotkeys) ──────────

    private Microsoft.UI.Xaml.Window? _poppedWindow;
    private SshSessionView?           _poppedView;
    private TabViewItem?              _hostTab;

    /// <summary>The window we live in when <c>_isPoppedClone</c>. Set by
    /// the original after constructing the clone, before activating —
    /// lets the clone close its own window on hotkey or toolbar click.</summary>
    private Microsoft.UI.Xaml.Window? _hostingWindowForClone;

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OpenSftp_Click(object sender, RoutedEventArgs e) =>
        OpenSftpRequested?.Invoke(this, ViewModel.Profile);
    private void PopOut_Click(object sender, RoutedEventArgs e)     => PopOut();

    public void ToggleFullScreen()
    {
        // Popped clone: target its own hosting window, not the main one.
        var win = _isPoppedClone ? _hostingWindowForClone
                                 : (_poppedWindow ?? App.MainWin);
        if (win?.AppWindow is not { } aw) return;

        if (aw.Presenter is Microsoft.UI.Windowing.FullScreenPresenter)
            aw.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
        else
            aw.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }

    public void PopOut()
    {
        // If we ARE the popped clone, "Pop out" closes the floating
        // window (which re-attaches the original on its Closed handler).
        if (_isPoppedClone)
        {
            try { _hostingWindowForClone?.Close(); }
            catch { /* tearing down */ }
            return;
        }

        // If we already have a popped window, toggle off.
        if (_poppedWindow is not null)
        {
            try { _poppedWindow.Close(); } catch { /* tearing down */ }
            return;
        }

        if (App.MainWin is null) return;
        var sessionTabs = (App.MainWin.Content as FrameworkElement)?.FindName("SessionTabs") as TabView;
        _hostTab = sessionTabs?.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(t => ReferenceEquals(t.Content, this));
        if (_hostTab is null) return;

        // Park the tab with the placeholder FIRST. Activating the new
        // window below steals foreground focus from MainWindow; an
        // unfocused TabView won't re-render its active tab's content
        // presenter until the tab is reselected, leaving the user
        // looking at a stale "still in tab" view until they click off
        // and back. Swapping content while MainWindow still has focus
        // forces an immediate repaint.
        _hostTab.Content = BuildPoppedPlaceholder();

        // Build a brand-new view bound to the SAME ViewModel so the
        // popped window has its own UIElement tree. This avoids the
        // cross-XamlRoot Window.Content assignment that throws
        // "Value does not fall within the expected range" — both views
        // render the live data feed via the shared VM.
        _poppedView = new SshSessionView(ViewModel, alreadyConnected: true);

        var win = new Microsoft.UI.Xaml.Window
        {
            Title   = $"Nexus RDM — {ViewModel.DisplayName}",
            Content = _poppedView,
        };
        _poppedView._hostingWindowForClone = win;
        _poppedWindow = win;
        App.SecondaryWindows.Add(win);
        win.Activate();

        win.Closed += (_, _) =>
        {
            App.SecondaryWindows.Remove(win);
            try { _poppedView?.Detach(); } catch (Exception ex) { CrashLogger.Log(ex, "popped Detach"); }
            _poppedView   = null;
            _poppedWindow = null;
            var tab = _hostTab;
            _hostTab = null;
            // Re-attach the original view to the tab. Skip when the app is
            // tearing down — at shutdown the secondary-window cleanup loop
            // closes us while the main window's XamlRoot is already
            // disposing, and assigning Content throws E_INVALIDARG.
            if (tab is null || App.IsShuttingDown) return;
            try { tab.Content = this; }
            catch (Exception ex) { CrashLogger.Log(ex, "popped re-attach"); }
        };
    }

    private static Border BuildPoppedPlaceholder()
    {
        var sp = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            Spacing             = 6,
        };
        sp.Children.Add(new TextBlock
        {
            Text       = "Session is popped out.",
            FontSize   = 13,
            FontWeight = new Windows.UI.Text.FontWeight(600),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text       = "Close the floating window to dock it back here.",
            FontSize   = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        return new Border { Child = sp };
    }
}
