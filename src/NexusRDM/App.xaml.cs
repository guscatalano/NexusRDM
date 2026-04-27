using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Models;
using NexusRDM.Core.Protocols;
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

    private static string AppDataDir =>
        Environment.GetEnvironmentVariable("NEXUSRDM_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusRDM");

    private static string DbPath => Path.Combine(AppDataDir, "connections.db");

    public App()
    {
        // Catch any unhandled exception on the UI thread and log it before crash
        this.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            var msg = $"[{DateTime.Now}] UNHANDLED: {e.Exception}\n";
            File.AppendAllText(Path.Combine(AppDataDir, "crash.log"), msg);
        };

        try
        {
            InitializeComponent();
            Services = BuildServices();
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(AppDataDir);
            File.AppendAllText(Path.Combine(AppDataDir, "crash.log"),
                $"[{DateTime.Now}] CTOR CRASH: {ex}\n");
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            using var scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<NexusDbContext>().Database.Migrate();
            MainWin = new MainWindow();
            // Push the persisted theme onto the freshly-created window
            // so the first frame already reflects the user's choice.
            try { SettingsViewModel.ApplyPersistedTheme(); } catch { /* non-fatal */ }
            try { SettingsStore.ApplyDebugMode(SettingsStore.ReadDebugMode()); }
            catch { /* non-fatal */ }
            MainWin.Closed += (_, _) =>
            {
                // WinUI 3 keeps the process alive while any window is
                // open. When the user closes the primary window, follow
                // through and close every secondary one too — including
                // the embedded mstscax forms, which run their own message
                // loops on STA threads.
                foreach (var w in SecondaryWindows.ToArray())
                {
                    try { w.Close(); } catch { /* already closed */ }
                }
                SecondaryWindows.Clear();

                try { Services.GetRequiredService<Services.SessionManager>().Dispose(); }
                catch { /* best effort */ }
            };
            MainWin.Activate();
        }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(AppDataDir, "crash.log"),
                $"[{DateTime.Now}] LAUNCH CRASH: {ex}\n");
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

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddNexusData(DbPath);

        services.AddSingleton<ICredentialVault, CredentialVault>();
        services.AddScoped<IConnectionService,  ConnectionService>();
        services.AddSingleton<ISshHandler,      SshHandler>();
        // RDP backend is a dispatcher that picks Mstsc / MstscAx / FreeRdp at
        // session-open time based on the user's setting. The MstscAx factory
        // lives in this project (Forms host); Core stays UI-agnostic.
        services.AddSingleton<IRdpHandler>(_ => new RdpHandler(
            modeProvider:    SettingsStore.ReadRdpMode,
            mstscAxFactory:  (profile, user, pass) => new MstscAxRdpSession(
                profile, user, pass,
                resolutionResolver: ResolveDesktopSize)));
        services.AddSingleton<SessionManager>();

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
