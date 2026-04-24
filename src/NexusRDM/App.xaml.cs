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

    private static string DbPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NexusRDM", "connections.db");

    public App() { InitializeComponent(); Services = BuildServices(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<NexusDbContext>().Database.Migrate();
        MainWin = new MainWindow();
        MainWin.Activate();
    }

    private static IServiceProvider BuildServices()
    {
        var dbDir   = Path.GetDirectoryName(DbPath)!;
        var logPath = Path.Combine(dbDir, "logs", "nexus-.log");
        Directory.CreateDirectory(dbDir);

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

        return services.BuildServiceProvider();
    }
}
