using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NexusRDM.ViewModels;
using Windows.Foundation;

namespace NexusRDM.Views;

public sealed partial class SshSessionView : UserControl, ISessionView
{
    public SshSessionViewModel ViewModel { get; }
    private bool _connectStarted;
    private readonly bool _isPoppedClone;

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

        _userInputHandler = (_, data) => _ = ViewModel.SendInputAsync(data);
        Terminal.UserInput += _userInputHandler;

        _terminalSizeHandler = async (_, _) =>
        {
            var (cols, rows) = Terminal.TerminalSize;
            SizeLabel.Text   = $"{cols}×{rows}";
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
        Focus(FocusState.Programmatic);

        // Popped clone hosts a fresh window whose AppWindow has its own
        // size; HookSizeTracking would target the main window instead.
        if (!_isPoppedClone) HookSizeTracking();

        if (_connectStarted) return;
        _connectStarted = true;

        WriteTrace($"[ Connecting to {ViewModel.Host} ... ]");
        try
        {
            await ViewModel.ConnectAsync();
            WriteTrace(ViewModel.IsConnected
                ? "[ Connected. Awaiting shell prompt... ]"
                : $"[ Connect returned but not connected: {ViewModel.StatusMessage} ]");
        }
        catch (Exception ex)
        {
            WriteTrace($"[ ConnectAsync threw: {ex.GetType().Name}: {ex.Message} ]");
        }
    }

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

    // ── Window-mode controls (toolbar buttons + global hotkeys) ──────────

    private Microsoft.UI.Xaml.Window? _poppedWindow;
    private SshSessionView?           _poppedView;
    private TabViewItem?              _hostTab;

    /// <summary>The window we live in when <c>_isPoppedClone</c>. Set by
    /// the original after constructing the clone, before activating —
    /// lets the clone close its own window on hotkey or toolbar click.</summary>
    private Microsoft.UI.Xaml.Window? _hostingWindowForClone;

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
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
