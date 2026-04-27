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

    public SshSessionView(SshSessionViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();

        HostLabel.Text       = $"{vm.Host}";
        HostStatusLabel.Text = vm.DisplayName;

        // Make this UserControl the keyboard input target. Listening with
        // handledEventsToo guarantees we still see input even if a child marks
        // the event handled — which is how we keep typing working regardless of
        // which descendant currently holds focus.
        IsTabStop  = true;
        AddHandler(KeyDownEvent,
            new KeyEventHandler(OnAnyKeyDown), handledEventsToo: true);
        AddHandler(CharacterReceivedEvent,
            new TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs>(OnAnyCharacterReceived),
            handledEventsToo: true);

        ViewModel.DataReceived += (_, data) =>
            DispatcherQueue.TryEnqueue(() => Terminal.Feed(data));

        Terminal.UserInput += async (_, data) =>
            await ViewModel.SendInputAsync(data);

        Terminal.SizeChanged += async (_, _) =>
        {
            var (cols, rows) = Terminal.TerminalSize;
            SizeLabel.Text   = $"{cols}×{rows}";
            await ViewModel.ResizeAsync(cols, rows);
        };

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Track the main window's content height and stretch RootGrid to match
    /// it (minus chrome). The TabView's content host hugs the UserControl's
    /// DesiredSize, so layout-only "Stretch" can't fill the tab area —
    /// we have to push an explicit Height down.
    /// </summary>
    private void HookSizeTracking()
    {
        if (App.MainWin?.Content is not FrameworkElement root) return;

        void Update()
        {
            // Subtract title bar (32) + tab strip (~40) + small bottom margin.
            var available = root.ActualHeight - 80;
            if (available > 200) RootGrid.Height = available;
        }

        Update();
        root.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(Update);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Always reclaim focus when the tab is shown — TabView re-attaches
        // content on activation and focus drifts to the tab strip otherwise.
        Focus(FocusState.Programmatic);

        HookSizeTracking();

        // Connect exactly once per tab lifetime.
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
        // handledEventsToo:true on the AddHandler means we *also* see events
        // that the TerminalControl already handled — but it sent the bytes,
        // so we must not send them a second time. Without this guard, every
        // keystroke duplicates ("a" → "aa") whenever the terminal has focus.
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
        if (e.Handled) return;  // see OnAnyKeyDown — same dedup rule
        if (e.Character < 0x20 || e.Character == 0x7F) return;
        await ViewModel.SendInputAsync(Encoding.UTF8.GetBytes(new[] { e.Character }));
        e.Handled = true;
    }

    /// <summary>Push a synthetic line of text into the terminal renderer for diagnostics.</summary>
    private void WriteTrace(string line) =>
        Terminal.Feed(Encoding.UTF8.GetBytes(line + "\r\n"));

    // ── Window-mode controls (toolbar buttons + global hotkeys) ──────────

    private Microsoft.UI.Xaml.Window? _poppedWindow;
    private TabViewItem?              _hostTab;

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void PopOut_Click(object sender, RoutedEventArgs e)     => PopOut();

    /// <summary>Toggles full-screen on whichever window currently hosts
    /// this view. If we're not popped out, the main window flips between
    /// the FullScreen presenter and the default Overlapped one.</summary>
    public void ToggleFullScreen()
    {
        var win = _poppedWindow ?? App.MainWin;
        if (win?.AppWindow is not { } aw) return;

        if (aw.Presenter is Microsoft.UI.Windowing.FullScreenPresenter)
            aw.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
        else
            aw.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }

    /// <summary>Detaches the view from its host tab and into a new
    /// always-on-top window. Calling again on an already-popped session
    /// closes the popped window, which re-attaches the view via the
    /// Closed handler.</summary>
    public void PopOut()
    {
        if (_poppedWindow is not null)
        {
            try { _poppedWindow.Close(); } catch { /* tearing down */ }
            return;
        }

        if (App.MainWin is null) return;
        var sessionTabs = (App.MainWin.Content as FrameworkElement)?.FindName("SessionTabs") as TabView;
        _hostTab = sessionTabs?.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(t => ReferenceEquals(t.Content, this));

        if (_hostTab is null)
        {
            CreatePoppedWindow();
            return;
        }

        var tab = _hostTab;

        // WinUI 3 keeps the UserControl's XamlRoot bound to its current
        // window even after we swap the tab's content; assigning it as
        // another Window's Content before that's released throws
        // "Value does not fall within the expected range". The Unloaded
        // event is the moment XAML actually clears the binding, so we
        // wait for it (and one extra dispatcher hop) before reparenting.
        void OnUnloadedForPopOut(object sender, RoutedEventArgs e)
        {
            Unloaded -= OnUnloadedForPopOut;
            DispatcherQueue.TryEnqueue(CreatePoppedWindow);
        }
        Unloaded += OnUnloadedForPopOut;
        tab.Content = BuildPoppedPlaceholder();
    }

    private void CreatePoppedWindow()
    {
        var win = new Microsoft.UI.Xaml.Window
        {
            Title   = $"Nexus RDM — {ViewModel.DisplayName}",
            Content = this,
        };
        _poppedWindow = win;
        App.SecondaryWindows.Add(win);
        win.Activate();
        win.Closed += (_, _) =>
        {
            App.SecondaryWindows.Remove(win);
            _poppedWindow = null;
            var tab = _hostTab;
            _hostTab  = null;
            if (tab is null) return;

            // Same wait-for-Unloaded discipline going back to the tab,
            // for the same reason: the popped Window's XamlRoot
            // ownership doesn't release until our Unloaded event fires.
            void OnUnloadedForReAttach(object sender, RoutedEventArgs e)
            {
                Unloaded -= OnUnloadedForReAttach;
                DispatcherQueue.TryEnqueue(() => tab.Content = this);
            }
            Unloaded += OnUnloadedForReAttach;
            win.Content = null;
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
