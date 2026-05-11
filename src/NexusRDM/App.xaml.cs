using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Core.Protocols;
using NexusRDM.Core.Proxmox;
using NexusRDM.Core.Services;
using NexusRDM.Data;
using NexusRDM.Data.Context;
using NexusRDM.RdpAx;
using NexusRDM.Services;
using NexusRDM.ViewModels;
using Serilog;

namespace NexusRDM;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow        MainWin  { get; private set; } = null!;

    /// <summary>Every secondary <see cref="Microsoft.UI.Xaml.Window"/> the
    /// app opens (currently the RDP-events pop-up) registers itself here
    /// so closing the main window can close them as a group. WinUI 3
    /// doesn't enumerate windows for us.</summary>
    public static readonly List<Microsoft.UI.Xaml.Window> SecondaryWindows = new();

    /// <summary>Set when the main window starts closing. Lets late-firing
    /// Closed handlers on secondary windows (e.g. the SSH pop-out) skip
    /// work that would touch already-disposed XamlRoots.</summary>
    public static bool IsShuttingDown { get; private set; }

    /// <summary>App-data root. Honors <c>NEXUSRDM_DATA_DIR</c> for tests
    /// and isolated profiles; otherwise <c>%LocalAppData%\NexusRDM</c>.</summary>
    public static string AppDataDir =>
        Environment.GetEnvironmentVariable("NEXUSRDM_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusRDM");

    /// <summary>SQLite database location. Public so the Settings page can
    /// surface it and the reset-database flow can delete it.</summary>
    public static string DbPath => Path.Combine(AppDataDir, "connections.db");

    public App()
    {
        CrashLogger.Initialize(AppDataDir);

        // Three layers of catch-all so nothing slips through:
        //   1. WinUI dispatcher exceptions (the UI thread)
        //   2. AppDomain — anything thrown on a non-UI managed thread
        //   3. TaskScheduler — finalizer-thread observation of forgotten Tasks
        this.UnhandledException += (_, e) =>
        {
            CrashLogger.Log(e.Exception, "WinUI UnhandledException", fatal: true);
            e.Handled = true; // keep the app alive when we can
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLogger.Log(e.ExceptionObject as Exception
                ?? new Exception(e.ExceptionObject?.ToString() ?? "non-Exception throw"),
                "AppDomain.UnhandledException", fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.Log(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        try
        {
            InitializeComponent();
            Services = BuildServices();
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "App ctor", fatal: true);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            using var scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<NexusDbContext>().Database.Migrate();

            // Honor the audit-log retention setting — delete entries
            // older than the configured cutoff. Best effort; if the
            // table is missing on first run, the migration above just
            // created it so there's nothing to clean up anyway.
            try
            {
                var days   = SettingsStore.ReadAuditRetentionDays();
                var cutoff = DateTime.UtcNow.AddDays(-days);
                var audit  = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
                _ = audit.DeleteOlderThanAsync(cutoff);
            }
            catch { /* non-fatal */ }
            MainWin = new MainWindow();
            // Push the persisted theme onto the freshly-created window
            // so the first frame already reflects the user's choice.
            try { SettingsViewModel.ApplyPersistedTheme(); } catch { /* non-fatal */ }
            try { SettingsStore.ApplyDebugMode(SettingsStore.ReadDebugMode()); }
            catch { /* non-fatal */ }
            try { SettingsStore.ApplyFontSize(SettingsStore.ReadFontSizeIndex()); }
            catch { /* non-fatal */ }
            // Side-load the user's mstscax.dll (if any) via SxS so
            // CoCreateInstance picks it up on each session's STA thread.
            try { NexusRDM.RdpAx.MstscAxOverride.Configure(SettingsStore.ReadMstscAxPath()); }
            catch { /* non-fatal — falls back to system mstscax */ }
            MainWin.Closed += (_, _) =>
            {
                IsShuttingDown = true;
                // WinUI 3 keeps the process alive while any window is
                // open. When the user closes the primary window, follow
                // through and close every secondary one too — including
                // the embedded mstscax forms, which run their own message
                // loops on STA threads.
                foreach (var w in SecondaryWindows.ToArray())
                {
                    try { w.Close(); }
                    catch (Exception ex) { CrashLogger.Log(ex, "shutdown: close secondary window"); }
                }
                SecondaryWindows.Clear();

                // Dispose long-lived singletons explicitly. The
                // ServiceProvider does this for us when disposed below,
                // but pinging / discovery have in-flight loops with up
                // to 2-second tails — kicking off cancellation here
                // gives those loops a head start while the rest of
                // teardown runs.
                try { Services.GetRequiredService<Services.PingService>().Dispose(); }
                catch (Exception ex) { CrashLogger.Log(ex, "shutdown: PingService.Dispose"); }
                try { Services.GetRequiredService<Services.NetworkDiscoveryService>().Dispose(); }
                catch (Exception ex) { CrashLogger.Log(ex, "shutdown: NetworkDiscoveryService.Dispose"); }
                try { Services.GetRequiredService<Services.SessionManager>().Dispose(); }
                catch (Exception ex) { CrashLogger.Log(ex, "shutdown: SessionManager.Dispose"); }

                // Cascade-dispose every other IDisposable singleton
                // registered with the container (DbContext factories,
                // anything we add later). ServiceProvider is itself
                // IAsyncDisposable / IDisposable.
                try { (Services as IDisposable)?.Dispose(); }
                catch (Exception ex) { CrashLogger.Log(ex, "shutdown: Services.Dispose"); }

                // Safety net: WinUI 3 + the embedded mstscax Forms host
                // routinely leave non-background STA threads alive that
                // .NET won't unwind, leaving the process visible in
                // Task Manager after the UI is gone. Force-exit after
                // a short grace period so the user doesn't have to
                // taskkill.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    Environment.Exit(0);
                });
            };
            MainWin.Activate();

            // Kick off a Proxmox sync of every enabled source on
            // launch so the tree's power-state glyphs reflect reality
            // from the first paint, instead of forcing the user to
            // hit "Sync now" before anything's known. Best-effort and
            // strictly background — failures land in CrashLogger.
            _ = Task.Run(async () =>
            {
                try
                {
                    var sync = Services.GetRequiredService<Services.ProxmoxSyncService>();
                    await sync.SyncAllAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, "startup ProxmoxSyncAll", fatal: false);
                }
            });

            // Same for Hyper-V if the user opted into background sync.
            // Spawns the elevated agent loop once (UAC prompts at this
            // point); from then on it runs silently and updates state
            // on the configured interval.
            if (SettingsStore.ReadHyperVBackgroundSync())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var hv = Services.GetRequiredService<Services.HyperVSyncService>();
                        await hv.StartBackgroundLoopAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log(ex, "startup HyperVBackgroundSync", fatal: false);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "OnLaunched", fatal: true);
            throw;
        }
    }

    private static IServiceProvider BuildServices()
    {
        Directory.CreateDirectory(AppDataDir);
        var logPath = Path.Combine(AppDataDir, "logs", "nexus-.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        // Route Core's terminal/SSH diagnostic events into Serilog.
        // Core can't reference Serilog directly (would force UI deps
        // onto a pure library), so it exposes a static Action sink that
        // we forward here. Tag every message with [ssh] for easy grep.
        NexusRDM.Core.Diagnostics.SshLog.Sink = msg => Log.Debug("[ssh] {Msg}", msg);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddNexusData(DbPath);

        services.AddSingleton<ICredentialVault, CredentialVault>();
        services.AddSingleton<NexusRDM.Core.Services.IAuditNotifier, NexusRDM.Core.Services.AuditNotifier>();
        services.AddScoped<IConnectionService,  ConnectionService>();
        // Real SSH handler is a dispatcher — picks the embedded
        // VtNetCore-backed session by default, switches to PuTTYNG
        // when SettingsStore.ReadSshMode() == PuttyNg. The PuTTYNG
        // factory lives here (UI project) because the session needs
        // an HWND; Core stays UI-agnostic. The handler is then wrapped
        // by DemoSshHandler so demo mode short-circuits to a
        // DemoSshSession regardless of backend.
        services.AddSingleton<SshHandler>(_ => new SshHandler(
            modeProvider:   SettingsStore.ReadSshMode,
            puttyNgFactory: (profile, user, pass) =>
                new NexusRDM.Protocols.PuttySshSession(profile, user, pass)));
        services.AddSingleton<ISshHandler>(sp => new Services.DemoSshHandler(
            sp.GetRequiredService<SshHandler>(),
            sp.GetRequiredService<Services.DemoModeService>()));
        // SFTP handler. Lives in Core, takes no extra factory — the
        // SshHandler's PuTTYNG embedding doesn't apply to SFTP (always
        // uses the SSH.NET-backed SftpClient).
        services.AddSingleton<ISftpHandler>(_ => new NexusRDM.Core.Protocols.SftpHandler());
        // RDP backend is a dispatcher that picks Mstsc / MstscAx / FreeRdp at
        // session-open time based on the user's setting. The MstscAx factory
        // lives in this project (Forms host); Core stays UI-agnostic. We then
        // wrap it in a demo decorator so demo mode returns a no-op
        // DemoRdpSession (the view paints a fake-desktop overlay).
        services.AddSingleton<RdpHandler>(_ => new RdpHandler(
            modeProvider:        SettingsStore.ReadRdpMode,
            mstscExePathProvider: SettingsStore.ReadMstscExePath,
            mstscAxFactory:  (profile, user, pass) => new MstscAxRdpSession(
                profile, user, pass,
                resolutionResolver: ResolveDesktopSize),
            // FreeRDP backend: wfreerdp.exe lifecycle managed by
            // FreeRdpBootstrap (cached download to %LocalAppData%
            // on first use). The session implements both Phase A
            // (separate window when hwndParent is 0) and Phase B
            // (owner-window pin when hwndParent is non-zero).
            freeRdpFactory:  (profile, user, pass) =>
                new NexusRDM.Protocols.FreeRdpSession(profile, user, pass)));
        services.AddSingleton<IRdpHandler>(sp => new Services.DemoRdpHandler(
            sp.GetRequiredService<RdpHandler>(),
            sp.GetRequiredService<Services.DemoModeService>()));
        services.AddSingleton<SessionManager>();
        services.AddSingleton<PingService>();
        services.AddNexusProxmox();
        services.AddSingleton<Services.ProxmoxSyncService>();
        services.AddSingleton<Services.ProxmoxPowerService>();
        services.AddSingleton<Services.ProxmoxConsoleService>();
        services.AddSingleton<Services.NetworkDiscoveryService>();
        services.AddSingleton<Services.DemoModeService>();
        services.AddSingleton<Services.HyperVClient>();
        services.AddSingleton<Services.HyperVSyncService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<ConnectionsViewModel>();
        services.AddTransient<AuditLogViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Translates the user's <see cref="RdpDefaultResolution"/> setting
    /// into concrete pixel dimensions for the OCX. Called from the
    /// MstscAxRdpSession factory at session-open time, so changes from
    /// the Settings page take effect on the next new tab.
    /// </summary>
    /// <param name="ownerHwnd">The owning WinUI HWND — used to locate
    /// the monitor for the "match monitor" option.</param>
    /// <param name="panelSize">The host tab's client size in raw pixels —
    /// returned as-is when the user picked "match panel".</param>
    private static System.Drawing.Size ResolveDesktopSize(nint ownerHwnd, System.Drawing.Size panelSize)
    {
        var pref = SettingsStore.ReadRdpDefaultResolution();
        return pref switch
        {
            RdpDefaultResolution.MatchMonitor => GetMonitorSize(ownerHwnd, panelSize),
            RdpDefaultResolution.MatchPanel   => panelSize,
            RdpDefaultResolution.Res1024x768  => new System.Drawing.Size(1024,  768),
            RdpDefaultResolution.Res1280x720  => new System.Drawing.Size(1280,  720),
            RdpDefaultResolution.Res1366x768  => new System.Drawing.Size(1366,  768),
            RdpDefaultResolution.Res1600x900  => new System.Drawing.Size(1600,  900),
            RdpDefaultResolution.Res1920x1080 => new System.Drawing.Size(1920, 1080),
            RdpDefaultResolution.Res2560x1440 => new System.Drawing.Size(2560, 1440),
            RdpDefaultResolution.Res3840x2160 => new System.Drawing.Size(3840, 2160),
            _                                 => panelSize,
        };
    }

    /// <summary>Resolves the monitor under <paramref name="hwnd"/> and
    /// returns its full pixel dimensions. Falls back to the panel size on
    /// any error so we never hand the OCX a (0, 0) rect.</summary>
    private static System.Drawing.Size GetMonitorSize(nint hwnd, System.Drawing.Size fallback)
    {
        try
        {
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == 0) return fallback;
            var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref info)) return fallback;
            return new System.Drawing.Size(
                info.rcMonitor.right - info.rcMonitor.left,
                info.rcMonitor.bottom - info.rcMonitor.top);
        }
        catch { return fallback; }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO mi);
}
