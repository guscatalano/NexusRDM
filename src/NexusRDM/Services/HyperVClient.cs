using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;

namespace NexusRDM.Services;

/// <summary>
/// Hyper-V access from the WinUI host. All WMI work lives in the
/// elevated <c>NexusRDM.HyperVAgent.exe</c> sidekick — the
/// <c>System.Management</c> package can't load inside a WinUI 3
/// process (its <see cref="System.Management.ManagementOptions"/>
/// ctor throws <see cref="System.PlatformNotSupportedException"/>
/// for anything that isn't a WPF / WinForms / console "desktop
/// application"). Every public method here launches the agent with
/// <c>Verb="runas"</c>, which triggers a UAC consent prompt; on
/// approval the agent runs unrestricted (its manifest declares
/// <c>requireAdministrator</c>) and writes JSON results to a temp
/// file we read back.
///
/// Practical implications:
///   - One UAC prompt per public call. Test, Sync, Power actions
///     each prompt once.
///   - There's no scheduled / silent path — periodic timers would
///     hammer the user with prompts, so the manual Sync button is
///     the only invocation point.
///   - <see cref="TryExtractIp"/> stays exported (and tested) because
///     it's pure XML parsing with no WMI dependency.
/// </summary>
public sealed class HyperVClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>True when the current process is running with an
    /// elevated (admin) token. ShellExecute with <c>Verb="runas"</c>
    /// from an elevated parent skips the UAC consent prompt entirely
    /// — the child inherits the parent's integrity level. That's the
    /// only way scheduled / silent Hyper-V syncs are possible; an
    /// unelevated NexusRDM would prompt UAC every timer tick, which
    /// is the no-go path the timer gates against.</summary>
    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public Task<IReadOnlyList<HyperVVm>> ListVmsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<HyperVVm>>(async () =>
        {
            var outPath = Path.Combine(Path.GetTempPath(), $"nexusrdm-hv-list-{Guid.NewGuid():N}.json");
            try
            {
                await RunAgentAsync(new[] { "list", outPath }, ct).ConfigureAwait(false);
                if (!File.Exists(outPath)) return Array.Empty<HyperVVm>();

                using var stream = File.OpenRead(outPath);
                var dtos = await JsonSerializer.DeserializeAsync<List<AgentVmDto>>(stream, JsonOpts, ct)
                    .ConfigureAwait(false);
                if (dtos is null) return Array.Empty<HyperVVm>();

                return dtos.Select(d => new HyperVVm(
                    d.Id ?? "",
                    d.Name ?? d.Id ?? "",
                    Enum.TryParse<HyperVVmState>(d.State, ignoreCase: true, out var s) ? s : HyperVVmState.Unknown,
                    d.Ip)).ToList();
            }
            finally
            {
                try { File.Delete(outPath); } catch { /* best effort */ }
            }
        }, ct);

    public Task<uint> RequestStateChangeAsync(
        string vmId, HyperVPowerAction action, CancellationToken ct = default) =>
        Task.Run<uint>(async () =>
        {
            var outPath = Path.Combine(Path.GetTempPath(), $"nexusrdm-hv-power-{Guid.NewGuid():N}.json");
            try
            {
                await RunAgentAsync(new[] { "power", vmId, action.ToString().ToLowerInvariant(), outPath }, ct)
                    .ConfigureAwait(false);
                if (!File.Exists(outPath)) throw new InvalidOperationException("Agent produced no output.");

                using var stream = File.OpenRead(outPath);
                var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new InvalidOperationException(err.GetString());
                return doc.RootElement.GetProperty("returnValue").GetUInt32();
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }, ct);

    /// <summary>"Test connection" — runs a list, reports success or
    /// the cancelled-prompt / error case. Implemented on top of
    /// <see cref="ListVmsAsync"/> so it triggers the same UAC dance
    /// the real Sync would; success here means the real Sync will
    /// also work.</summary>
    public async Task<HyperVDiagnosis> DiagnoseAccessAsync(CancellationToken ct = default)
    {
        try
        {
            var vms = await ListVmsAsync(ct).ConfigureAwait(false);
            return new HyperVDiagnosis(true, $"Connected. {vms.Count} VM(s) visible.");
        }
        catch (OperationCanceledException)
        {
            return new HyperVDiagnosis(false, "UAC prompt was cancelled. Approve the prompt to test access.");
        }
        catch (FileNotFoundException ex) when (ex.FileName?.Contains("HyperVAgent") == true)
        {
            return new HyperVDiagnosis(false,
                "Hyper-V agent (NexusRDM.HyperVAgent.exe) is missing from the install folder. " +
                "Reinstall or rebuild the app.");
        }
        catch (Exception ex)
        {
            return new HyperVDiagnosis(false, $"Failed: {ex.Message}");
        }
    }

    /// <summary>Spawn the elevated agent. <c>UseShellExecute=true</c>
    /// is required for <c>Verb="runas"</c> to trigger the UAC prompt;
    /// the trade-off is no redirected stdout / stderr, which is why
    /// we hand the agent a temp-file path. UAC denial returns
    /// <see cref="System.ComponentModel.Win32Exception"/> 1223 — we
    /// translate that to a clean
    /// <see cref="OperationCanceledException"/> so callers can show
    /// "Cancelled at UAC prompt." instead of a stack trace.</summary>
    private static async Task RunAgentAsync(string[] args, CancellationToken ct)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "NexusRDM.HyperVAgent.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                "Hyper-V agent missing — expected next to NexusRDM.exe. Did the build copy NexusRDM.HyperVAgent.exe?",
                exe);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName        = exe,
            UseShellExecute = true,
            Verb            = "runas",
            CreateNoWindow  = true,
            WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Hyper-V agent.");
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode == 2)
                throw new InvalidOperationException("Hyper-V agent: bad arguments.");
            // Exit code 1 still produces a JSON file with {error: …};
            // the caller surfaces it during deserialization.
            if (proc.ExitCode != 0 && proc.ExitCode != 1)
                throw new InvalidOperationException($"Hyper-V agent exited with code {proc.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("UAC prompt was cancelled.", wex);
        }
    }

    /// <summary>Pure XML helper for the KVP exchange items the agent
    /// hands back. Stays in this assembly (and gets unit-tested)
    /// because it's the only chunk of the original WMI-side code that
    /// doesn't touch <c>System.Management</c>.</summary>
    internal static string? TryExtractIp(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            string? key = null, data = null;
            foreach (var prop in doc.Descendants("PROPERTY"))
            {
                var pname = prop.Attribute("NAME")?.Value;
                var value = prop.Element("VALUE")?.Value;
                if (pname == "Name") key = value;
                else if (pname == "Data") data = value;
            }
            if (key != "NetworkAddressIPv4" || string.IsNullOrEmpty(data)) return null;

            foreach (var addr in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = addr.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (trimmed.StartsWith("127.") || trimmed.StartsWith("169.254.")) continue;
                return trimmed;
            }
        }
        catch { /* malformed item; skip */ }
        return null;
    }

    private sealed class AgentVmDto
    {
        public string?  Id    { get; set; }
        public string?  Name  { get; set; }
        public string?  State { get; set; }
        public string?  Ip    { get; set; }
    }
}

public sealed record HyperVVm(string Id, string Name, HyperVVmState State, string? Ip);

public enum HyperVVmState { Unknown, Off, Starting, Running, Pausing, Paused, Saving, Saved, Stopping, Snapshotting }

public enum HyperVPowerAction { Start, Shutdown, Stop, Reboot, Save }

public sealed record HyperVDiagnosis(bool IsSuccess, string Message);
