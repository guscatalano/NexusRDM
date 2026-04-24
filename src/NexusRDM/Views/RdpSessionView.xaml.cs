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

        // Get the HWND of our host panel via the window interop helper.
        // The mstsc child window will be reparented into this HWND.
        var hwnd = GetPanelHwnd();
        var w    = (int)RdpHostPanel.ActualWidth;
        var h    = (int)RdpHostPanel.ActualHeight;

        if (hwnd != 0 && w > 0 && h > 0)
            ViewModel.StartConnection(hwnd, w, h);
    }

    private void RdpHostPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_connected) return;
        ViewModel.Resize((int)e.NewSize.Width, (int)e.NewSize.Height);
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
