using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NexusRDM.ViewModels;
using WinRT.Interop;

namespace NexusRDM.Views;

public sealed partial class RdpSessionView : UserControl
{
    public RdpSessionViewModel ViewModel { get; }

    private bool _connected;

    public RdpSessionView(RdpSessionViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();

        // Kick off the connection once the panel is laid out and has a real size
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_connected) return;
        _connected = true;

        var hwnd        = GetPanelHwnd();
        var (x, y, w, h) = GetPanelBoundsInWindow();
        if (hwnd != 0 && w > 0 && h > 0)
            ViewModel.StartConnection(hwnd, x, y, w, h);
    }

    private void RdpHostPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_connected) return;
        var (x, y, w, h) = GetPanelBoundsInWindow();
        ViewModel.Resize(x, y, w, h);
    }

    /// <summary>Convert RdpHostPanel's bounds to coordinates inside the main
    /// window's client area — the mstsc child gets reparented into the window
    /// HWND, so (0,0) of SetWindowPos is the window's top-left, not the
    /// panel's. Without this translation mstsc lands over the title bar /
    /// sidebar and the user sees a black panel and "nothing happening".</summary>
    private (int X, int Y, int W, int H) GetPanelBoundsInWindow()
    {
        try
        {
            var rootContent = App.MainWin.Content as UIElement ?? RdpHostPanel;
            var transform   = RdpHostPanel.TransformToVisual(rootContent);
            var topLeft     = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            return ((int)topLeft.X, (int)topLeft.Y,
                    (int)RdpHostPanel.ActualWidth, (int)RdpHostPanel.ActualHeight);
        }
        catch
        {
            return (0, 0, (int)RdpHostPanel.ActualWidth, (int)RdpHostPanel.ActualHeight);
        }
    }

    private void SendCtrlAltDel_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SendCtrlAltDel();

    // ── HWND helper ───────────────────────────────────────────────────────────

    private nint GetPanelHwnd()
    {
        // WinUI 3: every UIElement lives in a window whose HWND we can get.
        // We return the main window HWND; the RDP handler will size the child
        // window to cover RdpHostPanel's bounds via SetParent + SetWindowPos.
        try
        {
            return WindowNative.GetWindowHandle(App.MainWin);
        }
        catch
        {
            return 0;
        }
    }
}
