using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using NexusRDM.Core.Interfaces;
using NexusRDM.Core.Protocols;
using NexusRDM.Core.Services;
using NexusRDM.Data;
using NexusRDM.Data.Context;
using NexusRDM.Services;
using NexusRDM.ViewModels;
using Serilog;

namespace NexusRDM;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow        MainWin  { get; private set; } = null!;

    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusRDM");

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
        services.AddSingleton<IRdpHandler,      RdpHandler>();
        services.AddSingleton<SessionManager>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<ConnectionsViewModel>();
        services.AddTransient<AuditLogViewModel>();
        services.AddTransient<SettingsViewModel>();

        return services.BuildServiceProvider();
    }
}
