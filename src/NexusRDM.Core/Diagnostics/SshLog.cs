namespace NexusRDM.Core.Diagnostics;

/// <summary>
/// Lightweight diagnostic sink for SSH / terminal-pipeline events.
/// NexusRDM.Core can't reference Serilog directly without dragging UI
/// dependencies into a pure library, so we expose a static Action and
/// let the WinUI host wire it to its Serilog logger at startup.
///
/// All members are no-ops until <see cref="Sink"/> is set. Production
/// builds where logging isn't wired up pay zero cost beyond the null
/// check on each call.
/// </summary>
public static class SshLog
{
    /// <summary>Set once at app start by the WinUI host (App.xaml.cs):
    /// <c>SshLog.Sink = msg => Log.Debug("[ssh] {M}", msg);</c></summary>
    public static Action<string>? Sink;

    public static void Debug(string msg) => Sink?.Invoke(msg);

    public static void Info(string msg)  => Sink?.Invoke(msg);

    public static void Warn(string msg)  => Sink?.Invoke("WARN: " + msg);
}
