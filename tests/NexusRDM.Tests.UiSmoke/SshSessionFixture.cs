using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.EntityFrameworkCore;
using NexusRDM.Core.Models;
using NexusRDM.Data.Context;

namespace NexusRDM.Tests.UiSmoke;

/// <summary>
/// Spins up an embedded SSH server, seeds a fresh SQLite DB with a connection
/// profile pointing at it, then launches NexusRDM against an isolated data
/// directory (NEXUSRDM_DATA_DIR) so the test never touches the user's real DB
/// or credential vault.
/// </summary>
public sealed class SshSessionFixture : IDisposable
{
    public Application?       App        { get; }
    public UIA3Automation?    Automation { get; }
    public Window?            MainWindow { get; }
    public EmbeddedSshServer  Server     { get; }
    public ConnectionProfile  Profile    { get; }
    public string             DataDir    { get; }
    public bool               Available  { get; }

    public SshSessionFixture()
    {
        DataDir = Path.Combine(Path.GetTempPath(), "NexusRDM-uismoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDir);

        Server = new EmbeddedSshServer();
        Server.Start();

        Profile = SeedProfile(DataDir, Server.Port);

        var exe = LocateAppExe();
        if (exe is null) { Available = false; return; }

        // Wait for any leftover NexusRDM.exe processes from prior fixtures to
        // exit before spawning a new one — competing instances race for
        // foreground focus and break UIA element resolution.
        WaitForNoNexusRDM(TimeSpan.FromSeconds(5));

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        // Only set on the child process — keeps other test fixtures (which
        // launch their own NexusRDM.exe) running against the user's real data
        // dir, so we don't have to coordinate env-var ownership across xunit
        // collections.
        psi.EnvironmentVariables["NEXUSRDM_DATA_DIR"] = DataDir;

        App        = Application.Launch(psi);
        Automation = new UIA3Automation();
        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(20));
        Available  = MainWindow is not null;
    }

    private static ConnectionProfile SeedProfile(string dataDir, int port)
    {
        var dbPath = Path.Combine(dataDir, "connections.db");
        var opts   = new DbContextOptionsBuilder<NexusDbContext>()
                        .UseSqlite($"Data Source={dbPath}")
                        .Options;

        using var ctx = new NexusDbContext(opts);
        ctx.Database.Migrate();

        var profile = new ConnectionProfile
        {
            DisplayName     = "Embedded SSH",
            Host            = "127.0.0.1",
            Port            = port,
            Protocol        = ConnectionProtocol.Ssh,
            CredentialKey   = null, // forces the credential prompt — drives the dialog from the test
            SshSettingsJson = JsonSerializer.Serialize(new SshOptions
            {
                AuthMethod       = SshAuthMethod.Password,
                KeepAliveSeconds = 30
            }),
        };
        ctx.Connections.Add(profile);
        ctx.SaveChanges();
        return profile;
    }

    /// <summary>Block (briefly) until no NexusRDM.exe is still running.
    /// Each fixture's Dispose force-kills its child, but Windows takes a
    /// moment to release the process; a fresh launch on top of a dying one
    /// breaks UIA window resolution.</summary>
    internal static void WaitForNoNexusRDM(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("NexusRDM").Length == 0) return;
            Thread.Sleep(200);
        }
    }

    private static string? LocateAppExe()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var src = Path.Combine(dir, "src", "NexusRDM");
            if (!Directory.Exists(src)) continue;
            return Directory.EnumerateFiles(
                Path.Combine(src, "bin"),
                "NexusRDM.exe",
                SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        return null;
    }

    /// <summary>Snapshot the app's crash + serilog files into a string so the
    /// test can include them in an assertion message before the data dir
    /// is deleted.</summary>
    public string DumpAppDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        var crash = Path.Combine(DataDir, "crash.log");
        if (File.Exists(crash))
            sb.AppendLine("---- crash.log ----").AppendLine(SafeRead(crash));

        var logsDir = Path.Combine(DataDir, "logs");
        if (Directory.Exists(logsDir))
        {
            var newest = Directory.EnumerateFiles(logsDir, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is not null)
                sb.AppendLine($"---- {Path.GetFileName(newest)} (tail) ----")
                  .AppendLine(SafeRead(newest, tailLines: 80));
        }
        return sb.Length == 0 ? "(no app diagnostic files written)" : sb.ToString();
    }

    private static string SafeRead(string path, int tailLines = 200)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = new List<string>();
            while (sr.ReadLine() is { } line) lines.Add(line);
            var start = Math.Max(0, lines.Count - tailLines);
            return string.Join(Environment.NewLine, lines.Skip(start));
        }
        catch (Exception ex) { return $"(read failed: {ex.Message})"; }
    }

    public void Dispose()
    {
        // Force-kill, not graceful: graceful close can hang behind TabView
        // teardown / async session disposal, leaving NexusRDM.exe alive
        // long enough to interfere with the next fixture's app launch.
        if (App is not null)
        {
            int pid = App.ProcessId;
            try { App.Kill(); } catch { /* may already be exiting */ }
            try { Process.GetProcessById(pid)?.WaitForExit(5000); } catch { /* gone */ }
        }
        Automation?.Dispose();
        App?.Dispose();
        Server.Dispose();
        try { Directory.Delete(DataDir, recursive: true); } catch { /* tolerate locks */ }
    }
}
