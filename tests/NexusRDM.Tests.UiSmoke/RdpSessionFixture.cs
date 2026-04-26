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
/// RDP-flavoured counterpart of <see cref="SshSessionFixture"/>: pre-writes a
/// settings.json that selects the MstscAx backend, seeds a
/// <see cref="ConnectionProtocol.Rdp"/> profile, and launches NexusRDM
/// against an isolated data dir. The seeded profile points at an unreachable
/// loopback port — the connection will fail, but the Forms HWND lifecycle
/// runs first, which is what the smoke test exercises.
/// </summary>
public sealed class RdpSessionFixture : IDisposable
{
    public Application?      App        { get; }
    public UIA3Automation?   Automation { get; }
    public Window?           MainWindow { get; }
    public ConnectionProfile Profile    { get; }
    public string            DataDir    { get; }
    public bool              Available  { get; }

    public string Username { get; } = "tester";
    public string Password { get; } = "doesnt-matter";

    public RdpSessionFixture()
    {
        DataDir = Path.Combine(Path.GetTempPath(), "NexusRDM-rdpsmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDir);

        // Force MstscAx mode (RdpLaunchMode.MstscAx == 1) before launch.
        File.WriteAllText(
            Path.Combine(DataDir, "settings.json"),
            JsonSerializer.Serialize(new Dictionary<string, object> { ["RdpMode"] = 1 }));

        Profile = SeedProfile(DataDir);

        var exe = LocateAppExe();
        if (exe is null) { Available = false; return; }

        // Wait for any leftover NexusRDM.exe from prior fixtures to exit.
        SshSessionFixture.WaitForNoNexusRDM(TimeSpan.FromSeconds(5));

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.EnvironmentVariables["NEXUSRDM_DATA_DIR"]      = DataDir;
        // Skip the live MsRdpClient ActiveX in tests — keeps the windowing
        // assertions deterministic (no modal error dialogs from a failed
        // RDP connect, no machine-licence prompts).
        psi.EnvironmentVariables["NEXUSRDM_RDP_TEST_FAKE"] = "1";
        App        = Application.Launch(psi);
        Automation = new UIA3Automation();
        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(20));
        Available  = MainWindow is not null;
    }

    private static ConnectionProfile SeedProfile(string dataDir)
    {
        var dbPath = Path.Combine(dataDir, "connections.db");
        var opts   = new DbContextOptionsBuilder<NexusDbContext>()
                        .UseSqlite($"Data Source={dbPath}")
                        .Options;

        using var ctx = new NexusDbContext(opts);
        ctx.Database.Migrate();

        var profile = new ConnectionProfile
        {
            DisplayName     = "Embedded RDP Test",
            // Loopback + an unused port: the TCP connect either refuses
            // immediately or hangs, but the AxHost form has been instantiated
            // and reparented by the time the connect attempt fails — that's
            // what the test checks.
            Host            = "127.0.0.1",
            Port            = 13389,
            Protocol        = ConnectionProtocol.Rdp,
            CredentialKey   = null,
            RdpSettingsJson = JsonSerializer.Serialize(new RdpOptions
            {
                ColorDepth        = RdpColorDepth.Colors24Bit,
                RedirectClipboard = false,
                RedirectDrives    = false,
            }),
        };
        ctx.Connections.Add(profile);
        ctx.SaveChanges();
        return profile;
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

    public void Dispose()
    {
        // Force-kill, not graceful close: the MstscAxRdpSession's STA Forms
        // thread keeps the process alive past WM_CLOSE on its own,
        // poisoning any subsequent fixture (focus, foreground races).
        if (App is not null)
        {
            int pid = App.ProcessId;
            try { App.Kill(); } catch { /* may already be exiting */ }
            try { Process.GetProcessById(pid)?.WaitForExit(5000); } catch { /* gone */ }
        }
        Automation?.Dispose();
        App?.Dispose();
        try { Directory.Delete(DataDir, recursive: true); } catch { /* tolerate locks */ }
    }
}
