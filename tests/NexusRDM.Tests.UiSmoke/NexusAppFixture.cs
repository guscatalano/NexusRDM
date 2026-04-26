using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace NexusRDM.Tests.UiSmoke;

/// <summary>
/// Launches the NexusRDM exe once for the whole test class and tears it down at the end.
/// Locate the built artifact relative to the test bin dir; tests skip themselves if the
/// app exe can't be found (so CI without a built app doesn't red-flag).
/// </summary>
public sealed class NexusAppFixture : IDisposable
{
    public Application?    App        { get; }
    public UIA3Automation? Automation { get; }
    public Window?         MainWindow { get; }
    public bool            AppAvailable => App is not null;

    public NexusAppFixture()
    {
        var exe = LocateAppExe();
        if (exe is null) return;

        // Wait for any leftover NexusRDM.exe from prior fixtures to exit.
        SshSessionFixture.WaitForNoNexusRDM(TimeSpan.FromSeconds(5));

        App        = Application.Launch(new ProcessStartInfo(exe) { UseShellExecute = false });
        Automation = new UIA3Automation();
        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(20));
    }

    private static string? LocateAppExe()
    {
        // Walk up from the test assembly to the repo root, then look for the
        // built NexusRDM.exe under any bin/x64/{Debug,Release}/<tfm>/win-x64/.
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
        // Force-kill instead of graceful close — keeps subsequent UI smoke
        // fixtures (SSH / MstscAx) from racing with a still-shutting-down
        // NexusRDM.exe for foreground focus and the WinUI dispatcher.
        if (App is not null)
        {
            int pid = App.ProcessId;
            try { App.Kill(); } catch { /* may already be exiting */ }
            try { Process.GetProcessById(pid)?.WaitForExit(5000); } catch { /* gone */ }
        }
        Automation?.Dispose();
        App?.Dispose();
    }
}
