using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;

namespace NexusRDM.Protocols;

/// <summary>
/// FreeRDP-backed <see cref="IRdpSession"/>. Spawns wfreerdp.exe as
/// a child process; the binary is resolved by
/// <see cref="FreeRdpBootstrap"/> (cached download into
/// %LocalAppData%, falling back to system PATH or downloading a
/// FreeRDP MSI on first use).
///
/// Two operating modes:
///   • Phase A — separate window. wfreerdp owns its own top-level
///     window; we just track the process for lifecycle events. Used
///     when <c>hwndParent</c> in <see cref="Connect"/> is zero
///     (typically the standalone-window UX path).
///   • Phase B — owner-window pin. wfreerdp's main window gets
///     GWLP_HWNDPARENT set to the WinUI window's HWND, then we
///     SetWindowPos it over the XAML host panel rect on every
///     <see cref="Resize"/>. Mirrors the technique
///     <see cref="MstscAxRdpSession"/> uses for its embedded form.
///     Engaged when hwndParent is non-zero.
///
/// Live resize / SmartSizing / SetResolution: FreeRDP doesn't
/// support runtime renegotiation of display size like the mstsc
/// OCX does, so those calls are best-effort no-ops in this backend.
/// Pop-out: under the owner-window model, "popping out" is just
/// clearing GWLP_HWNDPARENT and giving the wfreerdp window a normal
/// title bar — matches the MstscAx pop-out flow.
/// </summary>
public sealed class FreeRdpSession : IRdpSession
{
    private readonly ConnectionProfile _profile;
    private readonly string            _username;
    private readonly string            _password;

    private Process? _proc;
    private string?  _rdpFilePath;
    private nint     _ownerHwnd;
    private nint     _wfreerdpHwnd;     // wfreerdp's main top-level window after we pin it
    private (int X, int Y, int W, int H) _lastBounds;
    private bool     _embedded;
    private CancellationTokenSource? _followCts;

    public Guid ConnectionId { get; }
    public bool IsConnected  => _proc is { HasExited: false };

    public event EventHandler?               Connected;
    public event EventHandler<string>?       Disconnected;
    public event EventHandler<string>?       FatalError;
    public event EventHandler<RdpEventEntry>? RdpEvent;
    public event EventHandler?               ReAttached;

    private void Log(string kind, string detail = "") =>
        RdpEvent?.Invoke(this, new RdpEventEntry(DateTime.Now, kind, detail));

    public FreeRdpSession(ConnectionProfile profile, string username, string password)
    {
        ConnectionId = profile.Id;
        _profile     = profile;
        _username    = username;
        _password    = password;
    }

    public void Connect(nint hwndParent, int x, int y, int width, int height)
    {
        _ownerHwnd  = hwndParent;
        _embedded   = hwndParent != 0;
        _lastBounds = (x, y, width, height);

        // Resolve wfreerdp.exe asynchronously, then launch from a
        // background task so Connect returns quickly (the IRdpSession
        // contract is fire-and-forget — Connected/Disconnected events
        // carry the actual outcome).
        _ = Task.Run(async () =>
        {
            try
            {
                var exe = await FreeRdpBootstrap.ResolveAsync(
                    new Progress<string>(msg => Log("FreeRdpBootstrap", msg)));
                if (exe is null)
                {
                    var err = "Couldn't resolve wfreerdp.exe (no cached binary, no system install, download failed). " +
                              "Set Settings → RDP → FreeRDP exe path manually, or pick a different RDP backend.";
                    FatalError?.Invoke(this, err);
                    Log("FatalError", err);
                    return;
                }

                LaunchProcess(exe, width, height);

                if (_embedded)
                {
                    // Wait for wfreerdp's main window to appear,
                    // then re-parent it (owner-window relation) and
                    // pin to the panel rect.
                    var hwnd = await WaitForWfreerdpWindowAsync(_proc!, TimeSpan.FromSeconds(15));
                    if (hwnd != 0)
                    {
                        _wfreerdpHwnd = hwnd;
                        AttachToOwner(hwnd, _ownerHwnd);
                        Resize(_lastBounds.X, _lastBounds.Y, _lastBounds.W, _lastBounds.H);
                        StartFollowLoop();
                    }
                    else
                    {
                        Log("FreeRdpEmbed", "wfreerdp window didn't appear within 15s — leaving standalone.");
                    }
                }

                Connected?.Invoke(this, EventArgs.Empty);
                Log("Connected", $"{_profile.Host}:{_profile.Port} (FreeRDP, {(_embedded ? "embedded" : "standalone")})");

                await _proc!.WaitForExitAsync();
                Disconnected?.Invoke(this, "wfreerdp exited");
                Log("Disconnected", $"exit code {_proc.ExitCode}");
            }
            catch (Exception ex)
            {
                FatalError?.Invoke(this, ex.Message);
                Log("FatalError", ex.Message);
            }
            finally
            {
                _followCts?.Cancel();
                if (_rdpFilePath is not null) { try { File.Delete(_rdpFilePath); } catch { } }
            }
        });
    }

    private void LaunchProcess(string exe, int width, int height)
    {
        // Write a temp .rdp file with credentials + display settings
        // and pass it via /load. Avoids putting the password on the
        // command line (visible in `tasklist /v`) while still
        // passing all the tunables the RdpSettings provide.
        _rdpFilePath = Path.Combine(Path.GetTempPath(), $"nexus-freerdp-{ConnectionId:N}.rdp");
        File.WriteAllText(_rdpFilePath, BuildRdpFile(width, height));

        // FreeRDP CLI: /v + /u/p (we still need /p for now — wfreerdp
        // doesn't read passwords from .rdp files reliably across
        // versions). /size sets the negotiated resolution.
        // /cert:ignore disables the cert prompt for self-signed
        // hosts (typical in homelab / Hyper-V scenarios).
        // /from-stdin would be more secure for the password but
        // requires async stdin pumping; punt for now.
        var args = new StringBuilder();
        args.Append($"/v:{_profile.Host}:{_profile.Port} ");
        args.Append($"/u:\"{_username}\" ");
        if (!string.IsNullOrEmpty(_password))
            args.Append($"/p:\"{_password.Replace("\"", "\\\"")}\" ");
        args.Append($"/size:{width}x{height} ");
        args.Append("/cert:ignore ");

        var rdp = _profile.RdpSettings();
        if (!string.IsNullOrEmpty(rdp.Domain))
            args.Append($"/d:\"{rdp.Domain}\" ");
        if (rdp.RedirectClipboard) args.Append("+clipboard ");
        if (rdp.RedirectDrives)    args.Append("/drive:home,%USERPROFILE% ");

        var psi = new ProcessStartInfo(exe, args.ToString().TrimEnd())
        {
            UseShellExecute        = false,
            CreateNoWindow         = false,
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
        };
        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start wfreerdp.exe");
        Log("Launching", $"{exe} (pid {_proc.Id})");
    }

    public void Disconnect()
    {
        _followCts?.Cancel();
        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    public void Resize(int x, int y, int width, int height)
    {
        _lastBounds = (x, y, width, height);
        if (!_embedded || _wfreerdpHwnd == 0) return;
        try
        {
            SetWindowPos(_wfreerdpHwnd, _ownerHwnd,
                x, y, width, height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch { /* best effort — wfreerdp may have exited mid-call */ }
    }

    public void SendCtrlAltDel()
    {
        // wfreerdp's built-in shortcut is Ctrl+Alt+Home → menu, then
        // Send Ctrl+Alt+Del. Programmatic keystroke posting into
        // another process's RDP session is unreliable; document the
        // shortcut and skip the API.
    }

    public void BringToFront()
    {
        if (_wfreerdpHwnd != 0)
        {
            try { SetForegroundWindow(_wfreerdpHwnd); } catch { }
        }
    }

    public void SetVisible(bool visible)
    {
        if (_wfreerdpHwnd == 0) return;
        try { ShowWindow(_wfreerdpHwnd, visible ? SW_SHOW : SW_HIDE); } catch { }
    }

    public void ToggleFullScreen()
    {
        // FreeRDP's full-screen toggle is /f on launch + Ctrl+Alt+Enter
        // at runtime — same shortcut as mstsc. Best to teach the user
        // the shortcut rather than synthesise keystrokes.
    }

    public void PopOut()
    {
        // Detach from the owner window so wfreerdp becomes a normal
        // top-level window the user can drag freely. One-way for now,
        // matching the MstscAx pop-out semantics.
        if (_wfreerdpHwnd == 0 || _ownerHwnd == 0) return;
        try
        {
            DetachFromOwner(_wfreerdpHwnd);
            _embedded  = false;
            _ownerHwnd = 0;
            _followCts?.Cancel();
            ReAttached?.Invoke(this, EventArgs.Empty); // signal "no longer embedded"
        }
        catch { }
    }

    public void SetSmartSizing(bool enabled)         { /* not supported on FreeRDP at runtime */ }
    public void SetResolution(int width, int height) { /* not supported on FreeRDP at runtime */ }
    public void SetRightInset(int rightInsetPx)      { /* edit-panel slide-over inset — keep Resize-driven */ }

    public void Dispose()
    {
        try { Disconnect(); } catch { }
        _proc?.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private string BuildRdpFile(int w, int h)
    {
        var rdp = _profile.RdpSettings();
        var sb  = new StringBuilder();
        sb.AppendLine($"full address:s:{_profile.Host}:{_profile.Port}");
        sb.AppendLine($"username:s:{_username}");
        sb.AppendLine($"desktopwidth:i:{w}");
        sb.AppendLine($"desktopheight:i:{h}");
        sb.AppendLine($"session bpp:i:{(int)rdp.ColorDepth}");
        if (!string.IsNullOrEmpty(rdp.Domain))
            sb.AppendLine($"domain:s:{rdp.Domain}");
        return sb.ToString();
    }

    private static async Task<nint> WaitForWfreerdpWindowAsync(Process proc, TimeSpan timeout)
    {
        // Poll EnumWindows for a top-level window owned by our
        // child process whose dimensions look like an RDP session
        // (≥ 600x400) and that is visible. Filters out the auth
        // prompt + any transient splashes.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !proc.HasExited)
        {
            var hwnd = FindMainWindowFor(proc.Id);
            if (hwnd != 0) return hwnd;
            await Task.Delay(150);
        }
        return 0;
    }

    private static nint FindMainWindowFor(int pid)
    {
        nint result = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (GetWindowThreadProcessId(h, out int wpid) == 0) return true;
            if (wpid != pid) return true;
            if (!GetWindowRect(h, out var rect)) return true;
            int w = rect.Right - rect.Left;
            int hgt = rect.Bottom - rect.Top;
            if (w < 600 || hgt < 400) return true;
            result = h;
            return false; // stop enumerating
        }, 0);
        return result;
    }

    private static void AttachToOwner(nint child, nint owner)
    {
        // Owner window relationship via GWLP_HWNDPARENT — same
        // technique MstscAxRdpSession uses. Strip the title bar /
        // resize border so the wfreerdp chrome doesn't poke out of
        // the host panel rect.
        var style = GetWindowLong(child, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME);
        SetWindowLong(child, GWL_STYLE, style);

        if (Environment.Is64BitProcess)
            SetWindowLongPtr64(child, GWLP_HWNDPARENT, owner);
        else
            SetWindowLong(child, GWLP_HWNDPARENT, (int)owner);
    }

    private static void DetachFromOwner(nint child)
    {
        var style = GetWindowLong(child, GWL_STYLE);
        style |= WS_CAPTION | WS_THICKFRAME;
        SetWindowLong(child, GWL_STYLE, style);

        if (Environment.Is64BitProcess)
            SetWindowLongPtr64(child, GWLP_HWNDPARENT, 0);
        else
            SetWindowLong(child, GWLP_HWNDPARENT, 0);
    }

    private void StartFollowLoop()
    {
        _followCts = new CancellationTokenSource();
        var token = _followCts.Token;
        _ = Task.Run(async () =>
        {
            // Re-pin the wfreerdp window on a 50ms cadence. Cheap;
            // matches the mstscax follow-the-panel cadence so the
            // visual feel is identical between backends.
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_proc?.HasExited == true) break;
                    if (_wfreerdpHwnd == 0 || _ownerHwnd == 0) break;
                    SetWindowPos(_wfreerdpHwnd, _ownerHwnd,
                        _lastBounds.X, _lastBounds.Y,
                        _lastBounds.W, _lastBounds.H,
                        SWP_NOZORDER | SWP_NOACTIVATE);
                }
                catch { }
                try { await Task.Delay(50, token); } catch { break; }
            }
        }, token);
    }

    // ── Win32 ─────────────────────────────────────────────────────────

    private const int  GWL_STYLE        = -16;
    private const int  GWLP_HWNDPARENT  = -8;
    private const int  WS_CAPTION       = 0x00C00000;
    private const int  WS_THICKFRAME    = 0x00040000;
    private const uint SWP_NOZORDER     = 0x0004;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_SHOWWINDOW   = 0x0040;
    private const int  SW_HIDE          = 0;
    private const int  SW_SHOW          = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
}
