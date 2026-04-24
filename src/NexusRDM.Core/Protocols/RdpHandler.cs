using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using System.Diagnostics;

namespace NexusRDM.Core.Protocols;

/// <summary>
/// RDP session implementation.
///
/// Phase 1 (M3): Launches mstsc.exe as a child process and reparents its
///   window into our HWND host. This works on all Windows versions without
///   any extra COM registration.
///
/// Phase 2 (future): Replace with AxMSTSCLib for true in-process embedding,
///   clipboard/drive redirect, and programmatic control.
/// </summary>
public sealed class RdpSession : IRdpSession
{
    private readonly ConnectionProfile _profile;
    private readonly string            _username;
    private Process?                   _proc;
    private nint                       _mstscHwnd;

    public Guid ConnectionId { get; }
    public bool IsConnected  => _proc is { HasExited: false };

    public event EventHandler?         Connected;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<string>? FatalError;

    internal RdpSession(ConnectionProfile profile, string username)
    {
        ConnectionId = profile.Id;
        _profile     = profile;
        _username    = username;
    }

    public void Connect(nint hwndParent, int width, int height)
    {
        // Write a temporary .rdp file so we can pass all settings to mstsc
        var rdpPath = Path.Combine(Path.GetTempPath(), $"nexus_{ConnectionId:N}.rdp");
        File.WriteAllText(rdpPath, BuildRdpFile(width, height));

        var psi = new ProcessStartInfo("mstsc.exe", $"\"{rdpPath}\"")
        {
            UseShellExecute = false
        };

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start mstsc.exe");

        // Wait for the mstsc window to appear, then reparent it
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500);   // give mstsc time to paint its window
                _mstscHwnd = FindMstscWindow(_proc.Id);
                if (_mstscHwnd != 0 && hwndParent != 0)
                    ReparentWindow(_mstscHwnd, hwndParent, width, height);
                Connected?.Invoke(this, EventArgs.Empty);

                await _proc.WaitForExitAsync();
                Disconnected?.Invoke(this, "mstsc exited");
            }
            catch (Exception ex)
            {
                FatalError?.Invoke(this, ex.Message);
            }
            finally
            {
                File.Delete(rdpPath);
            }
        });
    }

    public void Disconnect()
    {
        if (_proc is { HasExited: false }) _proc.Kill();
    }

    public void Resize(int width, int height)
    {
        if (_mstscHwnd != 0)
            NativeMethods.SetWindowPos(_mstscHwnd, 0, 0, 0, width, height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOMOVE);
    }

    public void SendCtrlAltDel()
    {
        // mstsc handles this internally via the toolbar button; no API needed
    }

    public void Dispose() => _proc?.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildRdpFile(int w, int h)
    {
        var rdp = _profile.RdpSettings();
        var sb  = new System.Text.StringBuilder();
        sb.AppendLine($"full address:s:{_profile.Host}:{_profile.Port}");
        sb.AppendLine($"username:s:{_username}");
        sb.AppendLine($"desktopwidth:i:{w}");
        sb.AppendLine($"desktopheight:i:{h}");
        sb.AppendLine($"session bpp:i:{(int)rdp.ColorDepth}");
        sb.AppendLine($"audiomode:i:{(int)rdp.AudioMode}");
        sb.AppendLine($"redirectclipboard:i:{(rdp.RedirectClipboard ? 1 : 0)}");
        sb.AppendLine($"drivestoredirect:s:{(rdp.RedirectDrives ? "*" : string.Empty)}");
        if (!string.IsNullOrEmpty(rdp.Domain))
            sb.AppendLine($"domain:s:{rdp.Domain}");
        sb.AppendLine("screen mode id:i:1");   // windowed
        return sb.ToString();
    }

    private static nint FindMstscWindow(int pid)
    {
        nint found = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint wpid);
            if (wpid == (uint)pid && NativeMethods.IsWindowVisible(hwnd))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    private static void ReparentWindow(nint child, nint parent, int w, int h)
    {
        // Remove title bar / border so mstsc fills our panel
        int style = NativeMethods.GetWindowLong(child, NativeMethods.GWL_STYLE);
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME);
        NativeMethods.SetWindowLong(child, NativeMethods.GWL_STYLE, style);

        NativeMethods.SetParent(child, parent);
        NativeMethods.SetWindowPos(child, 0, 0, 0, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
    }
}

public sealed class RdpHandler : IRdpHandler
{
    public IRdpSession CreateSession(ConnectionProfile profile, string username, string password) =>
        new RdpSession(profile, username);
}

/// <summary>P/Invoke surface for window management.</summary>
internal static class NativeMethods
{
    public const int GWL_STYLE     = -16;
    public const int WS_CAPTION    = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int SWP_NOZORDER  = 0x0004;
    public const int SWP_NOMOVE    = 0x0002;
    public const int SWP_FRAMECHANGED = 0x0020;

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern nint SetParent(nint child, nint newParent);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetWindowLong(nint hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int SetWindowLong(nint hwnd, int index, int value);
}
