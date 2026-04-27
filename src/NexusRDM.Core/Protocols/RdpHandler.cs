using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using System.Diagnostics;

namespace NexusRDM.Core.Protocols;

/// <summary>
/// RDP session implementation.
///
/// Phase 1 (M3): Launches mstsc.exe as a separate top-level window. We tried
///   reparenting mstsc into our XAML panel, but mstsc shows a credential
///   prompt before its main window appears — the reparent grabbed the
///   prompt, stripped its title bar, and locked it inside the panel
///   undismissable. Until Phase 2 lands, the standalone mstsc window is the
///   working UX; the RdpSessionView overlay tells the user where to look.
///
/// Phase 2 (future): Replace with AxMSTSCLib for true in-process embedding,
///   clipboard/drive redirect, and programmatic control.
/// </summary>
public sealed class MstscRdpSession : IRdpSession
{
    private readonly ConnectionProfile _profile;
    private readonly string            _username;
    private Process?                   _proc;

    public Guid ConnectionId { get; }
    public bool IsConnected  => _proc is { HasExited: false };

    public event EventHandler?         Connected;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<string>? FatalError;
    public event EventHandler<RdpEventEntry>? RdpEvent;

    private void Log(string kind, string detail = "") =>
        RdpEvent?.Invoke(this, new RdpEventEntry(DateTime.Now, kind, detail));

    internal MstscRdpSession(ConnectionProfile profile, string username)
    {
        ConnectionId = profile.Id;
        _profile     = profile;
        _username    = username;
    }

    public void Connect(nint hwndParent, int x, int y, int width, int height)
    {
        // Write a temporary .rdp file so we can pass all settings to mstsc
        var rdpPath = Path.Combine(Path.GetTempPath(), $"nexus_{ConnectionId:N}.rdp");
        File.WriteAllText(rdpPath, BuildRdpFile(width, height));

        var psi = new ProcessStartInfo("mstsc.exe", $"\"{rdpPath}\"")
        {
            UseShellExecute = false
        };

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start mstsc.exe");

        // Phase 1: leave mstsc as its own top-level window. The previous code
        // tried to reparent the first visible mstsc window into our panel,
        // but mstsc shows a credential prompt before its main window —
        // FindMstscWindow grabbed *that* dialog, ReparentWindow stripped its
        // title bar and pinned it inside our panel, leaving the user unable
        // to dismiss it. The RdpSessionView overlay tells the user where to
        // find the standalone Remote Desktop window. Phase 2 will replace
        // this with AxMSTSCLib for real in-process embedding.
        Log("Connecting", $"{_profile.Host}:{_profile.Port} (mstsc.exe)");

        _ = Task.Run(async () =>
        {
            try
            {
                Connected?.Invoke(this, EventArgs.Empty);
                Log("Connected", $"{_profile.Host}:{_profile.Port}");
                await _proc.WaitForExitAsync();
                Disconnected?.Invoke(this, "mstsc exited");
                Log("Disconnected", "mstsc exited");
            }
            catch (Exception ex)
            {
                FatalError?.Invoke(this, ex.Message);
                Log("FatalError", ex.Message);
            }
            finally
            {
                try { File.Delete(rdpPath); } catch { /* best effort */ }
            }
        });
    }

    public void Disconnect()
    {
        if (_proc is { HasExited: false }) _proc.Kill();
    }

    public void Resize(int x, int y, int width, int height)
    {
        // No-op: mstsc owns its own window in Phase 1, so it manages its
        // own size. Kept on the interface so a future AxMSTSCLib-based
        // implementation can use it.
    }

    public void SendCtrlAltDel()
    {
        // mstsc handles this internally via the toolbar button; no API needed
    }

    public void BringToFront()
    {
        // Phase 1 mstsc is a child process — we don't track its top-level
        // window, so foregrounding it isn't supported here. The user can
        // alt-tab to it.
    }

    public void SetVisible(bool visible)             { /* mstsc child process — N/A */ }
    public void ToggleFullScreen()                   { /* mstsc child process — N/A */ }
    public void PopOut()                             { /* mstsc child process — N/A */ }
    public void SetSmartSizing(bool enabled)         { /* mstsc child process — N/A */ }
    public void SetResolution(int width, int height) { /* mstsc child process — N/A */ }

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

}

/// <summary>
/// Dispatches <see cref="IRdpSession"/> creation to the configured backend
/// (<see cref="RdpLaunchMode"/>). The mode is resolved at session-open time —
/// switching modes in Settings affects new tabs without a restart. Backends
/// that live in the UI project (e.g. mstscax/AxHost) are injected as
/// factories so this class can stay in Core.
/// </summary>
public sealed class RdpHandler : IRdpHandler
{
    private readonly Func<RdpLaunchMode> _modeProvider;
    private readonly Func<ConnectionProfile, string, string, IRdpSession>? _mstscAxFactory;
    private readonly Func<ConnectionProfile, string, string, IRdpSession>? _freeRdpFactory;

    public RdpHandler(
        Func<RdpLaunchMode>? modeProvider = null,
        Func<ConnectionProfile, string, string, IRdpSession>? mstscAxFactory = null,
        Func<ConnectionProfile, string, string, IRdpSession>? freeRdpFactory = null)
    {
        _modeProvider   = modeProvider   ?? (() => RdpLaunchMode.Mstsc);
        _mstscAxFactory = mstscAxFactory;
        _freeRdpFactory = freeRdpFactory;
    }

    public IRdpSession CreateSession(ConnectionProfile profile, string username, string password)
    {
        var mode = _modeProvider();
        return mode switch
        {
            RdpLaunchMode.MstscAx when _mstscAxFactory is not null
                => _mstscAxFactory(profile, username, password),
            RdpLaunchMode.FreeRdp when _freeRdpFactory is not null
                => _freeRdpFactory(profile, username, password),
            RdpLaunchMode.FreeRdp
                => throw new NotImplementedException("FreeRDP backend is not yet implemented; pick Mstsc or MstscAx in Settings."),
            _   => new MstscRdpSession(profile, username),
        };
    }
}
