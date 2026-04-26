using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;

namespace NexusRDM.RdpAx;

/// <summary>
/// In-process RDP backend using the Microsoft Remote Desktop ActiveX control
/// (<c>mstscax.dll</c>, <c>MsRdpClient9NotSafeForScripting</c>) hosted on a
/// borderless Windows Forms <see cref="Form"/>.
///
/// Embedding strategy: the form is a top-level window OWNED by the WinUI
/// app via <c>GWLP_HWNDPARENT</c>, but pinned over the XAML tab's panel
/// rect in screen coordinates. <see cref="Views.RdpSessionView"/> drives
/// position via a 50ms poll calling <see cref="Resize"/>. WinUI 3's
/// compositor can't host Win32 child HWNDs (airspace), but a top-level
/// owned window stays above its owner naturally and gives us native RDP
/// rendering, real input, real cursor — no PrintWindow round-trip.
/// </summary>
public sealed class MstscAxRdpSession : IRdpSession
{
    // CLSID for MsRdpClient9NotSafeForScripting — broadest compatibility on
    // modern Windows while still exposing AdvancedSettings9 (clear-text
    // password, SmartSizing). AxHost expects the canonical braced form.
    private const string MsRdpClient9NotSafeClsid = "{A41A4187-5A86-4E26-B40A-856F9035D9CB}";

    private readonly ConnectionProfile _profile;
    private readonly string            _username;
    private readonly string            _password;
    private Thread?                    _thread;
    private Form?                      _form;
    private MsRdpClientAxHost?         _ax;
    private nint                       _ownerHwnd;
    private Rectangle                  _bounds;
    private bool                       _disposed;
    private bool                       _isPoppedOut;
    // Cached HWND — Form.Handle / IsHandleCreated are thread-affine. Set
    // on HandleCreated (form's STA thread), cleared on FormClosed.
    private nint                       _formHwnd;

    public Guid ConnectionId { get; }
    public bool IsConnected   => _formHwnd != 0;

    /// <summary>When set in the environment, skips creation of the
    /// MsRdpClient AxHost — the form opens empty. Used by UI smoke tests
    /// that need to exercise the windowing behaviour (owner, follow, tab
    /// hide/show) without depending on the real ActiveX connecting, which
    /// can pop modal error dialogs that hang automation.</summary>
    private static readonly bool s_skipOcxForTest =
        Environment.GetEnvironmentVariable("NEXUSRDM_RDP_TEST_FAKE") == "1";

    public event EventHandler?         Connected;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<string>? FatalError;

    public MstscAxRdpSession(ConnectionProfile profile, string username, string password)
    {
        ConnectionId = profile.Id;
        _profile     = profile;
        _username    = username;
        _password    = password;
    }

    public void Connect(nint hwndParent, int x, int y, int width, int height)
    {
        if (_thread is not null) return;
        _ownerHwnd = hwndParent;
        _bounds    = new Rectangle(x, y, Math.Max(400, width), Math.Max(300, height));

        _thread = new Thread(RunForm)
        {
            IsBackground = true,
            Name         = $"NexusRDM-MstscAx-{_profile.DisplayName}",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void RunForm()
    {
        try
        {
            // Match the WinUI thread's DPI awareness (per-monitor V2). If
            // we leave this STA thread on its default (often System DPI),
            // SetWindowPos rects sent from the WinUI thread (per-monitor V2,
            // physical pixels) get DWM-virtualized when WinForms reads them
            // back, leaving the form sized to a stale/scaled rect — e.g. a
            // 596-px panel ending up as a 348-px form.
            try { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { /* Win10 1607+ only; older falls through */ }

            using var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar   = false,
                StartPosition   = FormStartPosition.Manual,
                AutoScaleMode   = AutoScaleMode.None,
                // Forms applies font-DPI scaling to Bounds in the
                // constructor; we feed raw pixels in via Win32 instead.
                Width           = _bounds.Width,
                Height          = _bounds.Height,
                // Distinct Text gives smoke tests an unambiguous handle
                // to find the form via FindWindow, regardless of which
                // other Forms-classed windows might exist in the
                // process (AxHost spawns a few internally).
                Text            = $"NexusRDM-MstscAx-{ConnectionId:N}",
            };
            _form = form;

            // Skip the AxHost in test-fake mode — the UI smoke suite drives
            // the windowing flow without depending on a live mstscax
            // connection. The form is still a proper Win32 top-level
            // owned window so all the follow/hide/show plumbing runs.
            MsRdpClientAxHost? ax = null;
            if (!s_skipOcxForTest)
            {
                ax = new MsRdpClientAxHost { Dock = DockStyle.Fill };
                form.Controls.Add(ax);
            }
            else
            {
                form.BackColor = System.Drawing.Color.FromArgb(0x0F, 0x0F, 0x12);
            }
            _ax = ax;

            form.HandleCreated += (_, _) =>
            {
                _formHwnd = form.Handle;
                // Initial placement (raw pixel screen coords from caller).
                // Owner is established in Shown — WinForms overwrites
                // GWLP_HWNDPARENT during its own Show() pipeline if we set
                // it too early.
                SetWindowPos(form.Handle, 0,
                             _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
                             SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            };

            form.Shown += (_, _) =>
            {
                // Set owner here, AFTER WinForms has finished Show()-ing
                // the form. Setting GWLP_HWNDPARENT in HandleCreated gets
                // overwritten by WinForms' internal style management.
                if (_ownerHwnd != 0)
                {
                    if (Environment.Is64BitProcess)
                        SetWindowLongPtr64(form.Handle, GWLP_HWNDPARENT, _ownerHwnd);
                    else
                        SetWindowLong(form.Handle, GWLP_HWNDPARENT, (int)_ownerHwnd);
                    // Re-stack just above the new owner.
                    SetWindowPos(form.Handle, _ownerHwnd, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }

                if (ax is null)
                {
                    Connected?.Invoke(this, EventArgs.Empty);
                    return;
                }
                try
                {
                    ax.CreateControl();
                    dynamic ocx = ax.Ocx
                        ?? throw new InvalidOperationException("MsRdpClient COM object not available.");

                    var rdp = _profile.RdpSettings();
                    ocx.Server                                 = _profile.Host;
                    ocx.UserName                               = _username;
                    ocx.AdvancedSettings9.RDPPort              = _profile.Port;
                    ocx.AdvancedSettings9.ClearTextPassword    = _password;
                    ocx.AdvancedSettings9.SmartSizing          = true;
                    ocx.AdvancedSettings9.EnableCredSspSupport = true;
                    ocx.ColorDepth                             = (int)rdp.ColorDepth;
                    // DesktopWidth/Height set the BASE resolution the
                    // remote session is negotiated at — without them
                    // mstscax defaults to 800×600 and the rendered
                    // desktop ends up looking like ~1/3 of the host
                    // form, even with SmartSizing scaling. Use the form's
                    // current client size so the session matches what
                    // the user actually sees.
                    ocx.DesktopWidth                           = Math.Max(800, form.ClientSize.Width);
                    ocx.DesktopHeight                          = Math.Max(600, form.ClientSize.Height);
                    if (!string.IsNullOrEmpty(rdp.Domain))
                        ocx.Domain = rdp.Domain;

                    ocx.Connect();
                    Connected?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex) { FatalError?.Invoke(this, ex.Message); }
            };

            // When focus moves to the WinUI window, restack the form just
            // above the owner so it stays visible.
            form.Deactivate += (_, _) =>
            {
                if (_formHwnd == 0) return;
                try
                {
                    SetWindowPos(_formHwnd, _ownerHwnd, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch { /* tearing down */ }
            };

            // Closing a popped-out window re-attaches it to the tab rather
            // than killing the session. The user expects "X" in the
            // floating window to dock back, not disconnect — that's what
            // the toolbar's Disconnect button is for.
            form.FormClosing += (_, e) =>
            {
                if (!_isPoppedOut) return;
                e.Cancel = true;
                _isPoppedOut = false;
                _isHidden    = false;
                try
                {
                    form.SuspendLayout();
                    form.FormBorderStyle = FormBorderStyle.None;
                    form.ShowInTaskbar   = false;
                    form.ResumeLayout();
                    // Re-establish the WinUI owner so the form pins above
                    // the app window again (PopOut cleared GWLP_HWNDPARENT
                    // to free it from the owner's z-level).
                    if (_ownerHwnd != 0)
                    {
                        if (Environment.Is64BitProcess)
                            SetWindowLongPtr64(form.Handle, GWLP_HWNDPARENT, _ownerHwnd);
                        else
                            SetWindowLong(form.Handle, GWLP_HWNDPARENT, (int)_ownerHwnd);
                    }
                    // Snap back to the panel rect immediately so there's
                    // no flash at the popped-out coords.
                    SetWindowPos(form.Handle, _ownerHwnd,
                                 _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
                                 SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
                catch { /* tearing down */ }
            };

            form.FormClosed += (_, _) =>
            {
                _formHwnd = 0;
                Disconnected?.Invoke(this, "window closed");
            };

            Application.Run(form);
        }
        catch (Exception ex)
        {
            FatalError?.Invoke(this, ex.Message);
        }
        finally
        {
            _form     = null;
            _formHwnd = 0;
        }
    }

    public void Disconnect()
    {
        if (_form is { IsDisposed: false } f)
        {
            try { f.Invoke(() => f.Close()); }
            catch { /* best effort across STA threads */ }
        }
    }

    public void Resize(int x, int y, int width, int height)
    {
        var hwnd = _formHwnd;
        if (hwnd == 0) return;
        _bounds = new Rectangle(x, y, Math.Max(100, width), Math.Max(100, height));
        // While the host tab is hidden, the form sits offscreen — don't
        // drag it back on screen on every poll-tick. Same when popped
        // out: the user is dragging the window themselves.
        if (_isHidden || _isPoppedOut) return;
        try
        {
            // Re-stack just above the owner each tick — clicking back into
            // the WinUI window doesn't push the form behind. SWP_NOACTIVATE
            // so we don't steal keyboard focus.
            SetWindowPos(hwnd, _ownerHwnd,
                         _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
                         SWP_NOACTIVATE);
        }
        catch { /* tearing down */ }
    }

    public void SendCtrlAltDel()
    {
        // Programmatic send needs IMsRdpClientNonScriptable6 — future work.
    }

    public void ToggleFullScreen()
    {
        // Use the OCX's own FullScreen property — mstscax handles the
        // transition (separate full-screen window with built-in connect
        // bar; Ctrl+Alt+Break exits). Without an OCX (test-fake mode)
        // there's nothing to toggle.
        var f = _form;
        if (f is null) return;
        var ax = _ax;
        if (ax is null) return;
        try
        {
            f.BeginInvoke(() =>
            {
                try
                {
                    dynamic ocx = ax.Ocx ?? throw new InvalidOperationException();
                    ocx.FullScreen = !(bool)ocx.FullScreen;
                }
                catch { /* OCX not yet ready or torn down */ }
            });
        }
        catch { /* tearing down */ }
    }

    public void PopOut()
    {
        var f = _form;
        if (f is null) return;
        if (_isPoppedOut) return;
        _isPoppedOut = true;
        _isHidden    = false; // un-hide if it was offscreen-parked
        try
        {
            f.BeginInvoke(() =>
            {
                try
                {
                    f.SuspendLayout();
                    f.FormBorderStyle = FormBorderStyle.Sizable;
                    f.Text            = $"Nexus RDM — {_profile.DisplayName} ({_profile.Host}:{_profile.Port})";
                    f.ShowInTaskbar   = true;

                    // Drop the WinUI owner — an owned window can never be
                    // brought in front of (or behind) its owner via the
                    // taskbar, so the popped-out form would always sit at
                    // the owner's z-level. Clearing GWLP_HWNDPARENT makes
                    // it a true top-level window the user can alt-tab to
                    // and stack freely. The FormClosing re-attach path
                    // restores the owner.
                    if (Environment.Is64BitProcess)
                        SetWindowLongPtr64(f.Handle, GWLP_HWNDPARENT, 0);
                    else
                        SetWindowLong(f.Handle, GWLP_HWNDPARENT, 0);

                    // Restore on screen at last known size, brought to
                    // front (HWND_TOP) and explicitly activated so it
                    // doesn't open behind the WinUI window.
                    var w = Math.Max(800, _bounds.Width);
                    var h = Math.Max(600, _bounds.Height);
                    SetWindowPos(f.Handle, HWND_TOP, 200, 120, w, h, SWP_SHOWWINDOW);
                    SetForegroundWindow(f.Handle);
                    f.ResumeLayout();
                }
                catch { /* tearing down */ }
            });
        }
        catch { /* tearing down */ }
    }

    public void SetSmartSizing(bool enabled)
    {
        var f  = _form;
        var ax = _ax;
        if (f is null || ax is null) return;
        try
        {
            f.BeginInvoke(() =>
            {
                try
                {
                    dynamic ocx = ax.Ocx ?? throw new InvalidOperationException();
                    ocx.AdvancedSettings9.SmartSizing = enabled;
                }
                catch { /* OCX not yet ready / property unavailable */ }
            });
        }
        catch { /* tearing down */ }
    }

    public void SetResolution(int width, int height)
    {
        var f  = _form;
        var ax = _ax;
        if (f is null || ax is null) return;
        if (width <= 0 || height <= 0) return;
        try
        {
            f.BeginInvoke(() =>
            {
                try
                {
                    dynamic ocx = ax.Ocx ?? throw new InvalidOperationException();
                    // IMsRdpClient9.UpdateSessionDisplaySettings: live-resize
                    // without dropping the connection. physicalWidth/Height = 0
                    // means "same as desktop"; scaleFactor 100, deviceScale 100.
                    ocx.UpdateSessionDisplaySettings(
                        (uint)width, (uint)height,
                        (uint)0, (uint)0,
                        (uint)0,
                        (uint)100, (uint)100);
                }
                catch { /* OCX not yet ready / not connected yet */ }
            });
        }
        catch { /* tearing down */ }
    }

    public void BringToFront()
    {
        var hwnd = _formHwnd;
        if (hwnd == 0) return;
        try
        {
            SetWindowPos(hwnd, _ownerHwnd, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { /* tearing down */ }
    }

    /// <summary>Show / hide the embedded RDP window. Called by
    /// RdpSessionView when the host tab becomes inactive (so the form
    /// doesn't sit on top of the wrong tab's content).</summary>
    public void SetVisible(bool visible)
    {
        var hwnd = _formHwnd;
        if (hwnd == 0) return;
        // Popped-out windows are user-managed; tab switches don't hide them.
        if (_isPoppedOut) return;
        _isHidden = !visible;
        try
        {
            if (visible)
            {
                // Restore to the panel's last known screen rect.
                SetWindowPos(hwnd, _ownerHwnd,
                             _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
                             SWP_NOACTIVATE);
            }
            else
            {
                // Park the form far offscreen instead of fighting WinForms
                // over Form.Visible / ShowWindow — the framework reverts
                // external Visible changes on its next message pump. An
                // offscreen window is functionally hidden (no pixels on
                // any monitor) and Resize() bails while _isHidden, so the
                // 50ms position-poll won't drag it back on screen.
                SetWindowPos(hwnd, _ownerHwnd,
                             OFFSCREEN_X, OFFSCREEN_Y, _bounds.Width, _bounds.Height,
                             SWP_NOACTIVATE);
            }
        }
        catch { /* tearing down */ }
    }

    private bool _isHidden;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    /// <summary>Minimal AxHost subclass — exposes the COM Ocx via dynamic
    /// since we don't ship an aximp-generated typed wrapper.</summary>
    private sealed class MsRdpClientAxHost : AxHost
    {
        public MsRdpClientAxHost() : base(MsRdpClient9NotSafeClsid) { }
        public object? Ocx => GetOcx();
    }

    // ── Win32 ──────────────────────────────────────────────────────────────

    private const int  OFFSCREEN_X      = -32000;
    private const int  OFFSCREEN_Y      = -32000;
    private const int  GWLP_HWNDPARENT  = -8;
    private const int  SW_HIDE          = 0;
    private const int  SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOZORDER     = 0x0004;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_SHOWWINDOW   = 0x0040;
    private static readonly nint HWND_TOP = (nint)0;
    private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (nint)(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
