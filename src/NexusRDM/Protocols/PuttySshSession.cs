using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Services;

namespace NexusRDM.Protocols;

/// <summary>
/// PuTTYNG-backed SSH session. Launches <c>PuTTYNG.exe</c> as a child
/// process and pins its top-level window into a host panel inside our
/// WinUI session tab via the same owner-window technique
/// <see cref="NexusRDM.RdpAx.MstscAxRdpSession"/> uses for the RDP OCX.
///
/// Why PuTTYNG instead of stock PuTTY: PuTTYNG accepts a
/// <c>-hwndparent &lt;hwnd&gt;</c> command-line argument and overrides
/// <c>IsZoomed</c> internally so it lays out cleanly inside an
/// arbitrary host rect. It also reroutes fatal-error popups into the
/// terminal stream instead of <c>MessageBox</c>, so an embedded host
/// can't get random modal dialogs mid-session.
///
/// The session implements <see cref="ISshSession"/> for compat with the
/// existing SshHandler dispatcher, but most of the data-stream methods
/// (<c>SendAsync</c>, <c>DataReceived</c>) are no-ops because PuTTYNG
/// owns the protocol — we never see the bytes. Lifecycle events
/// (<c>Connected</c> / <c>Disconnected</c>) come from the process state.
///
/// <see cref="ResizeAsync"/> is repurposed: instead of telling the
/// server about a viewport change, it accepts (cols, rows) which we
/// translate into a window-rect resize via SetWindowPos. The
/// <see cref="Views.SshSessionView"/> drives this from its host panel's
/// SizeChanged.
/// </summary>
public sealed class PuttySshSession : ISshSession
{
    private readonly ConnectionProfile _profile;
    private readonly string            _username;
    private readonly string            _password;

    private Process? _proc;
    private nint     _ownerHwnd;
    private nint     _puttyHwnd;
    private (int X, int Y, int W, int H) _lastBounds;
    private CancellationTokenSource? _followCts;

    public Guid ConnectionId => _profile.Id;
    public bool IsConnected  => _proc is { HasExited: false };

    // ISshSession surface — DataReceived never fires (PuTTY owns the
    // protocol). Disconnected fires when the process exits.
    public event EventHandler<byte[]>? DataReceived { add { } remove { } }
    public event EventHandler?         Disconnected;

    // Stats — PuTTY owns the SSH channel, so we have no visibility into
    // cipher / banner / bytes. Surface what we DO know (PTY size from
    // the host panel, process start time) and stub the rest.
    private DateTimeOffset? _connectedAt;
    public DateTimeOffset? ConnectedAt   => _connectedAt;
    public long            BytesReceived => 0;
    public long            BytesSent     => 0;
    public string          ServerVersion => string.Empty;
    public string          CipherInfo    => "(PuTTY backend)";
    public int             PtyCols       => 0;
    public int             PtyRows       => 0;
    public string          ConnectedUsername => _username ?? string.Empty;

    public Task<string> ExecAsync(string command, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Host stats panel needs a programmable SSH channel, which the " +
            "PuTTY backend doesn't expose. Switch to the embedded terminal " +
            "(Settings → SSH mode) to enable host stats.");

    public PuttySshSession(ConnectionProfile profile, string username, string password)
    {
        _profile  = profile;
        _username = username;
        _password = password;
    }

    /// <summary>Resolve PuTTYNG.exe + launch the session. Optionally
    /// pins the window to a host HWND when the WinUI view has rendered
    /// its host panel; if <paramref name="hwndParent"/> is 0, PuTTYNG
    /// runs as a standalone window.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Capture the bootstrap's progress messages so a download
        // failure (404, network issue, AV intercept) shows up in the
        // exception text instead of a generic "couldn't resolve".
        var trail = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var progress = new Progress<string>(msg => trail.Enqueue(msg));

        var exe = await PuttyNgBootstrap.ResolveAsync(progress, ct);
        if (exe is null)
        {
            var details = trail.Count > 0
                ? string.Join(" | ", trail)
                : "no diagnostic messages from bootstrap.";
            throw new InvalidOperationException(
                "Couldn't resolve PuTTYNG.exe. " +
                "Set Settings → SSH → PuTTYNG path manually, or pick the Embedded terminal backend.\n" +
                "Bootstrap trail: " + details);
        }

        // Build the command line. For password auth we use PuTTY's
        // -pwfile (added in 0.77) instead of -pw — the password no
        // longer appears in `tasklist /v`. Only the path does, and
        // the file itself is per-user-readable, deleted within seconds.
        // Stock PuTTY (and mRemoteNG) still ships the leakier -pw;
        // there's no reason for us to.
        string? pwFile = null;
        if (!string.IsNullOrEmpty(_password))
            pwFile = WriteRestrictedPasswordFile(_password);

        // We deliberately don't pass -hwndparent. PuTTYNG accepts it
        // (it patches cmdline.c), but stock PuTTY shows a usage popup
        // on unknown options and bails — and the bootstrap cache can
        // accidentally end up holding stock PuTTY (failed download
        // resumed, user pointed override at putty.exe, etc). Our embed
        // works without the flag: we find the window after launch and
        // pin it via owner-window + follow-loop, the same way
        // MstscAxRdpSession and FreeRdpSession do. mRemoteNG uses the
        // identical fallback for stock PuTTY.
        var args = new StringBuilder();
        args.Append("-ssh ");
        args.Append($"-P {_profile.Port} ");
        if (!string.IsNullOrEmpty(_username))
            args.Append($"-l \"{_username}\" ");
        if (pwFile is not null)
            args.Append($"-pwfile \"{pwFile}\" ");
        args.Append(_profile.Host);

        var psi = new ProcessStartInfo(exe, args.ToString())
        {
            UseShellExecute = false,
            CreateNoWindow  = false,
        };
        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PuTTYNG.exe");
        _connectedAt = DateTimeOffset.UtcNow;

        // Schedule pwfile deletion. PuTTY reads the file once at
        // connection-establish time (well within 5s on normal hosts).
        // We delete unconditionally — even if PuTTY exits early, the
        // file is gone. Any race where PuTTY hasn't read it yet is a
        // failed login, not a leak.
        if (pwFile is not null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                try { File.Delete(pwFile); } catch { /* best effort */ }
            });
        }

        // Watch the process for exit so the view can flip back to the
        // disconnected state. Fire-and-forget — we don't have a clean
        // long-lived background-task slot on this class.
        _ = Task.Run(async () =>
        {
            try { await _proc.WaitForExitAsync(); }
            catch { }
            _followCts?.Cancel();
            Disconnected?.Invoke(this, EventArgs.Empty);
        });

        // Find the PuTTYNG window so the embed can pin it. PuTTYNG's
        // main window class is "PuTTY" — same as upstream. We poll
        // briefly because window creation is async wrt process start.
        if (_ownerHwnd != 0)
        {
            var hwnd = await WaitForPuttyWindowAsync(_proc, TimeSpan.FromSeconds(15));
            if (hwnd != 0)
            {
                _puttyHwnd = hwnd;
                AttachToOwner(hwnd, _ownerHwnd);
                if (_lastBounds is { W: > 0, H: > 0 })
                    PinToBounds(_lastBounds);
                StartFollowLoop();
            }
        }
    }

    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        // PuTTYNG owns input. We can't inject keystrokes through the
        // SSH channel because the channel lives in the PuTTYNG process.
        // We *could* PostMessage WM_CHAR into the PuTTY window for
        // synthetic typing, but that's a footgun (race with user input,
        // shell-quoting headaches). Document and skip.
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        // Repurposed: the SshSessionView feeds (cols, rows) on terminal
        // SizeChanged. We don't act on cell counts (PuTTYNG handles its
        // own grid math); host-rect changes go through SetEmbeddedRect.
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        _followCts?.Cancel();
        if (_proc is { HasExited: false })
        {
            try { _proc.Kill(entireProcessTree: true); } catch { }
        }
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { _ = DisconnectAsync(); } catch { }
        _proc?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── PuTTY-specific embed surface ──────────────────────────────────
    // The view calls these directly via a `is PuttySshSession` cast.
    // Not on ISshSession because the embedded VtNetCore session has no
    // window to host.

    /// <summary>Bind to a host window. Must be called BEFORE
    /// <see cref="ConnectAsync"/> — PuTTYNG accepts the parent HWND
    /// only at process launch, not after.</summary>
    public void SetOwnerHwnd(nint hwnd) => _ownerHwnd = hwnd;

    /// <summary>Resize / reposition the embedded PuTTYNG window
    /// inside the parent client area. Coordinates are CLIENT-relative
    /// to the WinUI window's HWND (because PuTTY is now a real child
    /// after AttachToOwner).</summary>
    public void SetEmbeddedRect(int clientX, int clientY, int width, int height)
    {
        _lastBounds = (clientX, clientY, width, height);
        if (_puttyHwnd != 0) PinToBounds(_lastBounds);
    }

    private void PinToBounds((int X, int Y, int W, int H) b)
    {
        if (b.W <= 0 || b.H <= 0) return; // panel layout pending; follow-loop will retry
        try
        {
            // hWndInsertAfter is ignored when the window is a child,
            // so 0 here is fine. SWP_NOACTIVATE keeps focus stable;
            // SWP_SHOWWINDOW is needed because PuTTY may launch hidden.
            SetWindowPos(_puttyHwnd, 0, b.X, b.Y, b.W, b.H,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch { /* PuTTY may have exited mid-call */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>Write the password to a per-user-readable file under
    /// %LocalAppData% and return its path. The ACL is rewritten to
    /// allow only the current user (no inherited Everyone / Users
    /// entries), and the file is scheduled for deletion shortly after
    /// PuTTY launches. Caller is responsible for cleanup.</summary>
    [SupportedOSPlatform("windows")]
    private static string WriteRestrictedPasswordFile(string password)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NexusRDM", "putty-pwd");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"pwd-{Guid.NewGuid():N}.tmp");

        // Write file then tighten ACL. Order matters — File.Create
        // before ACL rewrite means the brief window of inherited
        // permissions exists, but %LocalAppData% defaults to per-user
        // anyway. The explicit ACL adds belt-and-suspenders.
        File.WriteAllText(path, password);

        try
        {
            var fi  = new FileInfo(path);
            var sec = fi.GetAccessControl();

            // Strip inherited rules so only our explicit ACEs apply.
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // Remove every existing rule (the inherited ones we just
            // detached, plus any creator-default).
            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                sec.RemoveAccessRule(rule);

            // Grant the current user read access — that's all we need.
            var me = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Couldn't resolve current user SID.");
            sec.AddAccessRule(new FileSystemAccessRule(
                me, FileSystemRights.Read, AccessControlType.Allow));

            fi.SetAccessControl(sec);
        }
        catch
        {
            // ACL tightening failed — file still has the parent dir's
            // default ACL (per-user) so we proceed. Worst case is a
            // marginally larger exposure window, still strictly better
            // than -pw on the command line.
        }
        return path;
    }

    private static async Task<nint> WaitForPuttyWindowAsync(Process proc, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !proc.HasExited)
        {
            var hwnd = FindPuttyWindow(proc.Id);
            if (hwnd != 0) return hwnd;
            await Task.Delay(150);
        }
        return 0;
    }

    private static nint FindPuttyWindow(int pid)
    {
        nint result = 0;
        var classBuf = new char[64];
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (GetWindowThreadProcessId(h, out int wpid) == 0) return true;
            if (wpid != pid) return true;

            // Match by class name — PuTTY's main window is class "PuTTY".
            int n = GetClassName(h, classBuf, classBuf.Length);
            if (n <= 0) return true;
            var name = new string(classBuf, 0, n);
            if (!string.Equals(name, "PuTTY", StringComparison.Ordinal)) return true;

            result = h;
            return false; // stop enumerating
        }, 0);
        return result;
    }

    private static void AttachToOwner(nint child, nint owner)
    {
        // True child relationship via SetParent — same as mRemoteNG.
        // Clears WS_POPUP / WS_CAPTION / WS_THICKFRAME and adds
        // WS_CHILD so the OS treats PuTTY as a child window of our
        // WinUI HWND: clipped to the parent's client rect, moves
        // with the parent, coordinates become client-relative
        // instead of screen-relative.
        SetParent(child, owner);

        var style = GetWindowLong(child, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_POPUP);
        style |= WS_CHILD;
        SetWindowLong(child, GWL_STYLE, style);

        // SWP_FRAMECHANGED forces the non-client area to recompute
        // after the style flip — without it, the title bar can
        // linger as a phantom band even though it shouldn't be drawn.
        SetWindowPos(child, 0, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private void StartFollowLoop()
    {
        _followCts = new CancellationTokenSource();
        var token = _followCts.Token;
        _ = Task.Run(async () =>
        {
            // Re-pin every 50ms. Cheap, matches the cadence the
            // mstscax host runs at — visual feel stays consistent
            // across backends.
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_proc?.HasExited == true) break;
                    if (_puttyHwnd == 0 || _ownerHwnd == 0) break;
                    SetWindowPos(_puttyHwnd, _ownerHwnd,
                        _lastBounds.X, _lastBounds.Y, _lastBounds.W, _lastBounds.H,
                        SWP_NOZORDER | SWP_NOACTIVATE);
                }
                catch { }
                try { await Task.Delay(50, token); } catch { break; }
            }
        }, token);
    }

    // ── Win32 ─────────────────────────────────────────────────────────

    private const int  GWL_STYLE        = -16;
    private const int  WS_CHILD         = 0x40000000;
    private const int  WS_POPUP         = unchecked((int)0x80000000);
    private const int  WS_CAPTION       = 0x00C00000;
    private const int  WS_THICKFRAME    = 0x00040000;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOZORDER     = 0x0004;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW   = 0x0040;

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint hWndChild, nint hWndNewParent);
}
