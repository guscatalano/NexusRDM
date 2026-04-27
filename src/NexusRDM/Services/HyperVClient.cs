using System.Management;
using System.Security.Principal;
using System.Xml.Linq;

namespace NexusRDM.Services;

/// <summary>
/// Thin wrapper over the local Hyper-V WMI surface
/// (<c>\\.\root\virtualization\v2</c>). All calls run on a worker
/// thread because <see cref="ManagementObjectSearcher"/> is
/// synchronous; the public methods take a CT for cancellation
/// at the dispatch level even though the underlying WMI work
/// is uninterruptible mid-query.
///
/// Requires the user to be in the local <c>Hyper-V Administrators</c>
/// group (or running elevated). Without that, queries silently return
/// empty — same UX trap as Proxmox's Privsep=1 tokens. The
/// <see cref="DiagnoseAccessAsync"/> helper distinguishes "no VMs"
/// from "no access" so the Test button can surface the right message.
/// </summary>
public sealed class HyperVClient
{
    private const string ScopePath = @"\\.\root\virtualization\v2";

    /// <summary>WMI scope with explicit impersonation + packet
    /// privacy. Default <see cref="ManagementScope"/> options run with
    /// <c>ImpersonationLevel.Default</c>, which under UAC's filtered-
    /// token model authenticates without the user's "Hyper-V
    /// Administrators" membership — the query connects but returns
    /// zero rows. Mirroring the options that PowerShell's
    /// <c>Get-VM</c> and Hyper-V Manager use makes the call work as a
    /// regular (non-elevated) member of that group.</summary>
    private static ManagementScope BuildScope()
    {
        var options = new ConnectionOptions
        {
            Impersonation  = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            EnablePrivileges = true,
        };
        var scope = new ManagementScope(ScopePath, options);
        scope.Connect();
        return scope;
    }

    public Task<IReadOnlyList<HyperVVm>> ListVmsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<HyperVVm>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var list = new List<HyperVVm>();
            var scope = BuildScope();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'"));
            foreach (ManagementObject vm in searcher.Get())
            {
                var id     = vm["Name"]        as string ?? "";
                var name   = vm["ElementName"] as string ?? id;
                var state  = Convert.ToUInt16(vm["EnabledState"]);
                list.Add(new HyperVVm(id, name, MapState(state), TryReadIp(vm, scope)));
            }
            return list;
        }, ct);

    /// <summary>True when the WMI namespace exists and we can list at
    /// least the root provider — distinguishes "Hyper-V isn't installed"
    /// or "user lacks permission" from "no VMs configured".
    ///
    /// Also checks whether the current process is elevated and whether
    /// the user is in the local <c>Hyper-V Administrators</c> group
    /// (SID <c>S-1-5-32-578</c>). Without one of those, WMI's connect
    /// usually succeeds but enumeration silently returns zero rows —
    /// the most common "looks fine, finds nothing" trap. Surfacing
    /// both up front saves a debugging round-trip.</summary>
    public Task<HyperVDiagnosis> DiagnoseAccessAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var (isElevated, isHvAdmin) = IdentityChecks();

            try
            {
                var scope = BuildScope();
                if (!scope.IsConnected) return new HyperVDiagnosis(false, "WMI scope did not connect.");

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Name FROM Msvm_ComputerSystem WHERE Caption='Virtual Machine'"));
                var count = searcher.Get().Count;

                if (count > 0)
                    return new HyperVDiagnosis(true, $"Connected. {count} VM(s) visible.");

                // Zero VMs returned with Impersonate + PacketPrivacy.
                // We CAN'T tell from here whether the user is in
                // Hyper-V Administrators: WindowsPrincipal.IsInRole
                // reports the FILTERED token (UAC strips admin-
                // equivalent group memberships from non-elevated
                // processes), so a non-elevated user in the group
                // shows up as not-in-the-group. Don't make a
                // confident claim either way — list the real
                // possibilities so the user can self-diagnose with
                // `whoami /groups`.
                var msg =
                    "Connected, but 0 VMs visible. Most common causes:\n" +
                    "  1. The host genuinely has no VMs (try `Get-VM` in PowerShell).\n" +
                    "  2. You're in 'Hyper-V Administrators' but haven't signed out since " +
                    "being added — group memberships only land in the access token at logon.\n" +
                    "  3. You're not in 'Hyper-V Administrators' at all. Run `whoami /groups | findstr Hyper-V` " +
                    "to check; add yourself with `Add-LocalGroupMember -Group \"Hyper-V Administrators\" -Member $env:USERNAME` " +
                    "and sign out + back in.";
                if (!isElevated && !isHvAdmin)
                    msg += "\n\n(Note: NexusRDM is running unelevated, so we can't reliably " +
                           "verify Hyper-V Administrators membership from here — the value above " +
                           "may be a UAC-filtered-token false negative. PowerShell's `whoami /groups` " +
                           "is authoritative.)";
                return new HyperVDiagnosis(false, msg);
            }
            catch (UnauthorizedAccessException ex)
            {
                return new HyperVDiagnosis(false,
                    BuildAccessDeniedMessage(isElevated, isHvAdmin) +
                    $" ({ex.Message})");
            }
            catch (ManagementException ex)
            {
                return new HyperVDiagnosis(false,
                    "WMI error — Hyper-V may not be installed or the service isn't running. " +
                    $"({ex.Message})");
            }
            catch (Exception ex)
            {
                return new HyperVDiagnosis(false, $"Failed: {ex.Message}");
            }
        }, ct);

    /// <summary>Returns whether the current process is running
    /// elevated and whether the user is in the local
    /// <c>Hyper-V Administrators</c> group. Both flags can be true
    /// independently — elevation gives admin rights without group
    /// membership, group membership gives Hyper-V access without
    /// elevation.</summary>
    private static (bool IsElevated, bool IsInHyperVAdmins) IdentityChecks()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var elevated  = principal.IsInRole(WindowsBuiltInRole.Administrator);
            // Well-known SID for "Hyper-V Administrators" (Vista+).
            var hvAdmins  = principal.IsInRole(new SecurityIdentifier("S-1-5-32-578"));
            return (elevated, hvAdmins);
        }
        catch
        {
            return (false, false);
        }
    }

    private static string BuildAccessDeniedMessage(bool isElevated, bool isHvAdmin)
    {
        if (isHvAdmin)
            return "Access denied despite Hyper-V Administrators membership — Hyper-V service may be stopped.";
        if (isElevated)
            return "Access denied even though running elevated. Hyper-V service may not be installed or is stopped.";
        return "Access denied. Add your user to the local 'Hyper-V Administrators' group " +
               "(then sign out / sign in), or right-click NexusRDM → Run as administrator.";
    }

    /// <summary>Invokes <c>Msvm_ComputerSystem.RequestStateChange</c>.
    /// Returns the WMI return value: 0 = completed, 4096 = job started,
    /// anything else is an error.</summary>
    public Task<uint> RequestStateChangeAsync(string vmId, HyperVPowerAction action, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var searcher = new ManagementObjectSearcher(
                BuildScope(),
                new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name='{vmId.Replace("'", "''")}'"));
            var vm = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
                     ?? throw new InvalidOperationException($"Hyper-V VM '{vmId}' not found.");

            using (vm)
            {
                var args = vm.GetMethodParameters("RequestStateChange");
                args["RequestedState"] = (ushort)RequestedStateValue(action);
                using var result = vm.InvokeMethod("RequestStateChange", args, null);
                return Convert.ToUInt32(result["ReturnValue"]);
            }
        }, ct);

    private static int RequestedStateValue(HyperVPowerAction a) => a switch
    {
        // Per Msvm_ComputerSystem.RequestStateChange documentation.
        HyperVPowerAction.Start    => 2,      // Enabled
        HyperVPowerAction.Shutdown => 3,      // Disabled (graceful via integration services)
        HyperVPowerAction.Stop     => 32769,  // Off (hard)
        HyperVPowerAction.Reboot   => 11,     // Reset (hard cycle); Hyper-V has no graceful "reboot" state
        HyperVPowerAction.Save     => 32773,  // Saved
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    private static HyperVVmState MapState(ushort enabledState) => enabledState switch
    {
        2     => HyperVVmState.Running,
        3     => HyperVVmState.Off,
        9     => HyperVVmState.Paused,
        10    => HyperVVmState.Pausing,
        32768 => HyperVVmState.Saved,
        32769 => HyperVVmState.Starting,
        32770 => HyperVVmState.Snapshotting,
        32773 => HyperVVmState.Saving,
        32774 => HyperVVmState.Stopping,
        _     => HyperVVmState.Unknown,
    };

    /// <summary>Reads the VM's KVP exchange items and returns the first
    /// non-loopback IPv4 the integration services have published. Returns
    /// null when KVP isn't available (guest agent missing / off / Linux
    /// without hv_kvp_daemon). Errors bubble up as null too — IP is
    /// best-effort here, like Proxmox's qemu-agent path.</summary>
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

    /// <summary>Each KVP item is an XML <c>INSTANCE</c> with Name + Data
    /// PROPERTY children. We parse looking for <c>NetworkAddressIPv4</c>
    /// whose Data is a semicolon-separated address list and pick the
    /// first non-loopback / non-link-local IPv4.</summary>
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
                var trimmed = addr.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (trimmed.StartsWith("127.") || trimmed.StartsWith("169.254.")) continue;
                return trimmed;
            }
        }
        catch { /* malformed item; skip */ }
        return null;
    }
}

public sealed record HyperVVm(string Id, string Name, HyperVVmState State, string? Ip);

public enum HyperVVmState { Unknown, Off, Starting, Running, Pausing, Paused, Saving, Saved, Stopping, Snapshotting }

public enum HyperVPowerAction { Start, Shutdown, Stop, Reboot, Save }

public sealed record HyperVDiagnosis(bool IsSuccess, string Message);
