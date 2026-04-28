using System.Management;
using System.Text.Json;
using System.Xml.Linq;

// Tiny privileged sidekick: enumerates Hyper-V VMs / runs power
// actions and writes the result as JSON to the output path it
// receives. Launched by NexusRDM.exe with Verb="runas" so a UAC
// consent yields a non-filtered token; the parent reads stdout via
// the temp-file handoff (UAC + redirected pipes don't mix when
// UseShellExecute=true is required to trigger elevation).
//
// CLI:
//   NexusRDM.HyperVAgent list  <output.json>
//   NexusRDM.HyperVAgent power <vmid> <start|shutdown|stop|reboot|save> <output.json>
//   NexusRDM.HyperVAgent loop  <interval-seconds> <output.json> <sentinel-pid>
//
// Exit codes: 0 = ok, 1 = handled error (output.json contains an
// {error} payload), 2 = bad args (no JSON written).
//
// `loop` is the long-lived background-sync mode: re-list every
// <interval-seconds>, rewriting the same output file each time.
// <sentinel-pid> is the parent NexusRDM process id; if that process
// disappears we exit cleanly so we don't strand an elevated orphan.

namespace NexusRDM.HyperVAgent;

internal static class Program
{
    private const string Scope = @"\\.\root\virtualization\v2";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        var cmd = args[0].ToLowerInvariant();

        try
        {
            switch (cmd)
            {
                case "list":
                {
                    var output = args[1];
                    var vms = ListVms();
                    File.WriteAllText(output, JsonSerializer.Serialize(vms, JsonOpts));
                    return 0;
                }
                case "power":
                {
                    if (args.Length < 4) return 2;
                    var vmId   = args[1];
                    var action = args[2];
                    var output = args[3];
                    var rv = RequestStateChange(vmId, action);
                    File.WriteAllText(output, JsonSerializer.Serialize(new { returnValue = rv }, JsonOpts));
                    return 0;
                }
                case "loop":
                {
                    if (args.Length < 4) return 2;
                    if (!int.TryParse(args[1], out var seconds) || seconds < 5) return 2;
                    var output = args[2];
                    int.TryParse(args[3], out var sentinelPid);
                    return RunLoop(seconds, output, sentinelPid);
                }
                default:
                    return 2;
            }
        }
        catch (Exception ex)
        {
            try
            {
                // Best effort — the output path is always the LAST arg
                // for both subcommands.
                var output = args[args.Length - 1];
                File.WriteAllText(output, JsonSerializer.Serialize(new
                {
                    error = ex.Message,
                    type  = ex.GetType().FullName,
                }, JsonOpts));
            }
            catch { /* parent will see the non-zero exit + no/partial file */ }
            return 1;
        }
    }

    // ── WMI ──────────────────────────────────────────────────────────────

    private static ManagementScope BuildScope()
    {
        var options = new ConnectionOptions
        {
            Impersonation    = ImpersonationLevel.Impersonate,
            Authentication   = AuthenticationLevel.PacketPrivacy,
            EnablePrivileges = true,
        };
        var scope = new ManagementScope(Scope, options);
        scope.Connect();
        return scope;
    }

    /// <summary>Long-lived list+sleep loop. Writes JSON to
    /// <paramref name="outputPath"/> each pass; transient WMI errors
    /// don't bring the loop down — they're written into the file as
    /// an {error} payload so the parent can surface them. Exits when
    /// the sentinel parent process dies, so we never leak an
    /// elevated orphan.</summary>
    private static int RunLoop(int seconds, string outputPath, int sentinelPid)
    {
        // Write a placeholder immediately so the parent can prove the
        // file was created (vs. UAC denied → no file ever exists).
        try { File.WriteAllText(outputPath, "[]"); } catch { }

        while (true)
        {
            try
            {
                var vms = ListVms();
                var json = JsonSerializer.Serialize(vms, JsonOpts);
                // Atomic-ish write: temp + replace so the parent
                // never reads a half-written buffer.
                var tmp = outputPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, outputPath, overwrite: true);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(outputPath, JsonSerializer.Serialize(new
                    {
                        error = ex.Message,
                        type  = ex.GetType().FullName,
                    }, JsonOpts));
                }
                catch { /* parent will see the staleness in the file mtime */ }
            }

            if (sentinelPid > 0)
            {
                try { using var _ = System.Diagnostics.Process.GetProcessById(sentinelPid); }
                catch { return 0; } // parent gone
            }

            Thread.Sleep(seconds * 1000);
        }
    }

    private static List<HyperVVmDto> ListVms()
    {
        var list = new List<HyperVVmDto>();
        var scope = BuildScope();
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'"));
        foreach (ManagementObject vm in searcher.Get())
        {
            var id    = vm["Name"]        as string ?? "";
            var name  = vm["ElementName"] as string ?? id;
            var state = Convert.ToUInt16(vm["EnabledState"]);
            list.Add(new HyperVVmDto
            {
                Id    = id,
                Name  = name,
                State = MapState(state),
                Ip    = TryReadIp(vm, scope),
            });
        }
        return list;
    }

    private static uint RequestStateChange(string vmId, string action)
    {
        if (string.IsNullOrWhiteSpace(vmId)) throw new ArgumentException("vmid required.");
        var requested = action.ToLowerInvariant() switch
        {
            "start"    => (ushort)2,      // Enabled
            "shutdown" => (ushort)3,      // Disabled (graceful)
            "stop"     => (ushort)32769,  // Off (hard)
            "reboot"   => (ushort)11,     // Reset
            "save"     => (ushort)32773,  // Saved
            _ => throw new ArgumentException($"unknown action '{action}'"),
        };

        using var searcher = new ManagementObjectSearcher(
            BuildScope(),
            new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name='{vmId.Replace("'", "''")}'"));
        var vm = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
                 ?? throw new InvalidOperationException($"VM '{vmId}' not found.");
        using (vm)
        {
            var args = vm.GetMethodParameters("RequestStateChange");
            args["RequestedState"] = requested;
            using var result = vm.InvokeMethod("RequestStateChange", args, null);
            return Convert.ToUInt32(result["ReturnValue"]);
        }
    }

    private static string MapState(ushort enabledState) => enabledState switch
    {
        2     => "Running",
        3     => "Off",
        9     => "Paused",
        10    => "Pausing",
        32768 => "Saved",
        32769 => "Starting",
        32770 => "Snapshotting",
        32773 => "Saving",
        32774 => "Stopping",
        _     => "Unknown",
    };

    private static string? TryReadIp(ManagementObject vm, ManagementScope scope)
    {
        try
        {
            var sysName = vm["Name"] as string ?? "";
            using var kvp = new ManagementObjectSearcher(
                scope,
                new ObjectQuery(
                    "SELECT GuestIntrinsicExchangeItems FROM Msvm_KvpExchangeComponent " +
                    $"WHERE SystemName='{sysName.Replace("'", "''")}'"));
            foreach (ManagementObject k in kvp.Get())
            {
                if (k["GuestIntrinsicExchangeItems"] is not string[] items) continue;
                foreach (var raw in items)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (TryExtractIp(raw) is { } ip) return ip;
                }
            }
        }
        catch { /* best effort */ }
        return null;
    }

    private static string? TryExtractIp(string xml)
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
                var t = addr.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (t.StartsWith("127.") || t.StartsWith("169.254.")) continue;
                return t;
            }
        }
        catch { }
        return null;
    }
}

internal sealed class HyperVVmDto
{
    public string  Id    { get; set; } = "";
    public string  Name  { get; set; } = "";
    public string  State { get; set; } = "Unknown";
    public string? Ip    { get; set; }
}
