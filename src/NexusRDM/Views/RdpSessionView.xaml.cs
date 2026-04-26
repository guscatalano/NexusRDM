using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.ViewModels;
using WinRT.Interop;

namespace NexusRDM.Views;

public sealed partial class RdpSessionView : UserControl
{
    public RdpSessionViewModel ViewModel { get; }

    private bool                            _connected;
    private DispatcherQueueTimer?           _followTimer;
    private (int X, int Y, int W, int H)    _lastBounds;

    public RdpSessionView(RdpSessionViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;

        // WinUI 3 TabView detaches the inactive tab's content from the
        // visual tree, so Loaded/Unloaded is our clean signal for "this
        // tab is/isn't on screen". OnUnloaded hides the embedded form;
        // OnLoaded re-shows it (and re-emits bounds so it lands at the
        // right screen rect after the host window may have moved).
    }

    /// <summary>TabView's content host hugs DesiredSize, so layout-only
    /// Stretch leaves the panel area at the toolbar+status height. Push the
    /// window-content height onto RootGrid so the tab fills the viewport.
    /// Also runs a 50 ms poll that re-emits screen-pixel bounds whenever
    /// they change so the embedded form follows the WinUI window.</summary>
    private void HookSizeTracking()
    {
        if (App.MainWin?.Content is not FrameworkElement root) return;

        void UpdateLayout()
        {
            var available = root.ActualHeight - 80;
            if (available > 200) RootGrid.Height = available;
        }

        UpdateLayout();
        root.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateLayout);

        _followTimer ??= DispatcherQueue.CreateTimer();
        _followTimer.Interval = TimeSpan.FromMilliseconds(50);
        _followTimer.Tick += (_, _) =>
        {
            if (!_connected) return;
            var bounds = GetPanelScreenBounds();
            if (bounds.W <= 0 || bounds.H <= 0) return;
            if (bounds == _lastBounds) return;
            _lastBounds = bounds;
            ViewModel.Resize(bounds.X, bounds.Y, bounds.W, bounds.H);
        };
        _followTimer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookSizeTracking();

        if (!_connected)
        {
            // Defer the initial Connect to the next layout pass — when
            // OnLoaded fires, RdpHostPanel.ActualHeight can still be the
            // pre-stretch DesiredSize. HookSizeTracking pushed RootGrid.Height
            // synchronously but the host panel's row arrangement hasn't been
            // re-measured yet, so we'd hand the form a too-short rect.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_connected) return;
                _connected = true;
                RootGrid.UpdateLayout();
                var hwnd        = WindowNative.GetWindowHandle(App.MainWin);
                var (x, y, w, h) = GetPanelScreenBounds();
                if (hwnd != 0 && w > 0 && h > 0)
                {
                    _lastBounds = (x, y, w, h);
                    ViewModel.StartConnection(hwnd, x, y, w, h);
                }
            });
        }
        else
        {
            // Tab re-activated. Force the next poll-tick to re-emit
            // bounds, then make the form visible again.
            _lastBounds = default;
            ViewModel.SetVisible(true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _followTimer?.Stop();
        _followTimer = null;
        // Tab switched away (or tab is being closed). Hide the embedded
        // form so it doesn't sit on top of the new tab's content. If the
        // tab was actually closed, the session's Dispose will tear the
        // form down too.
        ViewModel.SetVisible(false);
    }

    private void RdpHostPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Push the new panel rect to the form immediately instead of
        // waiting up to 50ms for the follow-poll. Without this, the form
        // could stay sized to whatever stale ActualHeight was captured at
        // initial connect — the poll-tick comparison short-circuits when
        // _lastBounds happens to match the cached value before re-layout.
        if (!_connected) return;
        var bounds = GetPanelScreenBounds();
        if (bounds.W <= 0 || bounds.H <= 0) return;
        if (bounds == _lastBounds) return;
        _lastBounds = bounds;
        ViewModel.Resize(bounds.X, bounds.Y, bounds.W, bounds.H);
    }

    private void ShowWindow_Click(object sender, RoutedEventArgs e) =>
        ViewModel.BringToFront();

    private void SendCtrlAltDel_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SendCtrlAltDel();

    private void FullScreen_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ToggleFullScreen();

    private void PopOut_Click(object sender, RoutedEventArgs e) =>
        ViewModel.PopOut();

    /// <summary>Bounds of <c>RdpHostPanel</c> in absolute SCREEN coordinates,
    /// in raw display pixels. WinUI reports DIPs at 96 dpi; on a 200%
    /// display we have to multiply by 2 before handing the rect to
    /// SetWindowPos, otherwise the embedded form ends up half-size and
    /// offset.</summary>
    private (int X, int Y, int W, int H) GetPanelScreenBounds()
    {
        try
        {
            var rootContent = App.MainWin.Content as UIElement ?? RdpHostPanel;
            var transform   = RdpHostPanel.TransformToVisual(rootContent);
            var topLeft     = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            var mainHwnd = WindowNative.GetWindowHandle(App.MainWin);
            var dpi      = GetDpiForWindow(mainHwnd);
            var scale    = dpi <= 0 ? 1.0 : dpi / 96.0;

            var pt = new POINT
            {
                X = (int)(topLeft.X * scale),
                Y = (int)(topLeft.Y * scale),
            };
            ClientToScreen(mainHwnd, ref pt);
            return (pt.X, pt.Y,
                    (int)(RdpHostPanel.ActualWidth  * scale),
                    (int)(RdpHostPanel.ActualHeight * scale));
        }
        catch
        {
            return (0, 0, (int)RdpHostPanel.ActualWidth, (int)RdpHostPanel.ActualHeight);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
}
