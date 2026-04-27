using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Xunit;

namespace NexusRDM.Tests.UiSmoke;

/// <summary>
/// End-to-end coverage for the SSH Pop Out button: clicking it must
/// detach the session into its own top-level window, and closing that
/// window must restore the session inside the original tab.
///
/// Regression: the previous implementation tried to reparent the same
/// UserControl across two WinUI 3 Windows, tripping the cross-XamlRoot
/// "Value does not fall within the expected range" error. The current
/// implementation creates a fresh SshSessionView in the popped window
/// sharing the original SshSessionViewModel — verified end-to-end here.
/// </summary>
[Collection("UI smoke")]
public sealed class SshPopOutSmokeTests : IClassFixture<SshSessionFixture>
{
    private readonly SshSessionFixture _fx;
    public SshPopOutSmokeTests(SshSessionFixture fx) => _fx = fx;

    [SkippableFact]
    public void PopOut_OpensSecondWindow_AndReAttachesOnClose()
    {
        Skip.IfNot(_fx.Available, "NexusRDM.exe not built — run `dotnet build src/NexusRDM` first.");
        var win = _fx.MainWindow!;
        BringToForeground(win);
        win.Focus();
        Thread.Sleep(250);

        // ── Connect: identical bootstrap to SshTypingSmokeTests ──────────
        var node = WaitFor(() =>
            win.FindAllDescendants(c => c.ByControlType(ControlType.TreeItem))
               .FirstOrDefault(it => (it.Name ?? "").Contains("Embedded SSH"))
            ?? win.FindFirstDescendant(c => c.ByName("Embedded SSH")),
            TimeSpan.FromSeconds(15));
        Assert.True(node is not null, "Seeded 'Embedded SSH' connection not visible in tree.");

        var connectBtn = TryInvokeAndWaitForConnectButton(win, node!);
        Assert.True(connectBtn is not null,
            "Credential prompt did not appear after invoking the connection node.");

        var edits = WaitFor(() =>
        {
            var found = win.FindAllDescendants(c => c.ByControlType(ControlType.Edit));
            return found.Length >= 2 ? found : null;
        }, TimeSpan.FromSeconds(5));
        Assert.True(edits is not null && edits.Length >= 2, "Username/password fields not found.");

        edits![0].AsTextBox().Focus();
        edits[0].AsTextBox().Text = _fx.Server.Username;
        edits[1].Focus();
        Keyboard.Type(_fx.Server.Password);
        connectBtn!.Click();

        var connected = WaitForCondition(
            () => TryReadStatusMessage(win).StartsWith("Connected", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20));
        Assert.True(connected,
            $"SSH session never reached 'Connected'. Status: '{TryReadStatusMessage(win)}'\n" +
            _fx.DumpAppDiagnostics());

        // Refocus into the SSH tab so the toolbar buttons are reachable.
        var sshTab = WaitFor(() =>
            win.FindAllDescendants(c => c.ByControlType(ControlType.TabItem))
               .FirstOrDefault(t => (t.Name ?? "").Contains("Embedded SSH")),
            TimeSpan.FromSeconds(5));
        sshTab?.Click();
        Thread.Sleep(200);

        // ── Click "Pop out" ──────────────────────────────────────────────
        var popOutBtn = WaitFor(() =>
            win.FindFirstDescendant(c => c.ByControlType(ControlType.Button).And(c.ByName("Pop out"))),
            TimeSpan.FromSeconds(5));
        Assert.True(popOutBtn is not null, "Pop out toolbar button not found in SSH tab.");
        popOutBtn!.Click();

        // ── Wait for the popped Window to materialise ────────────────────
        // Title is "Nexus RDM — Embedded SSH" (em-dash + connection name);
        // we look across all top-level windows owned by NexusRDM.exe.
        var poppedHwnd = WaitForCondition(
            () => FindPoppedWindowByTitle("Nexus RDM — Embedded SSH") != IntPtr.Zero,
            TimeSpan.FromSeconds(8));
        Assert.True(poppedHwnd, "Popped SSH window did not appear after clicking Pop out.\n"
                              + _fx.DumpAppDiagnostics());

        // ── Close the popped window via Win32 (UIA on a popped Window in
        //     a different XamlRoot is flaky from another process) ────────
        var hwnd = FindPoppedWindowByTitle("Nexus RDM — Embedded SSH");
        Assert.NotEqual(IntPtr.Zero, hwnd);
        SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        // ── Verify the popped window is gone AND the tab content
        //     re-attached (no more "Session is popped out." placeholder) ─
        var popClosed = WaitForCondition(
            () => FindPoppedWindowByTitle("Nexus RDM — Embedded SSH") == IntPtr.Zero,
            TimeSpan.FromSeconds(5));
        Assert.True(popClosed, "Popped window did not close after WM_CLOSE.");

        BringToForeground(win);
        sshTab?.Click();
        Thread.Sleep(200);

        var placeholderGone = WaitForCondition(
            () => win.FindFirstDescendant(c => c.ByName("Session is popped out.")) is null,
            TimeSpan.FromSeconds(5));
        Assert.True(placeholderGone,
            "After closing the popped window, the placeholder is still showing — "
            + "the original SshSessionView didn't re-attach to its tab.");

        // The status message must still read "Connected" — proves the
        // underlying ISshSession survived the pop-out round-trip.
        Assert.StartsWith("Connected", TryReadStatusMessage(win));
    }

    // ── Win32 helpers ────────────────────────────────────────────────────

    private const uint WM_CLOSE = 0x0010;
    private const int  SW_RESTORE = 9;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static void BringToForeground(Window win)
    {
        var hwnd = win.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Walk every top-level visible window owned by the SAME
    /// process as the test app, return the first whose title contains
    /// the requested substring. UIA across WinUI 3 windows from another
    /// process is unreliable, so we use raw EnumWindows.</summary>
    private IntPtr FindPoppedWindowByTitle(string substring)
    {
        if (_fx.App is null) return IntPtr.Zero;
        var pid = (uint)_fx.App.ProcessId;
        IntPtr hit = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var wpid);
            if (wpid != pid) return true;
            var len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.IndexOf(substring, StringComparison.Ordinal) >= 0)
            {
                hit = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return hit;
    }

    // ── Shared probe helpers ─────────────────────────────────────────────

    private static string TryReadStatusMessage(Window win)
    {
        try
        {
            // The toolbar status switched from a read-only TextBox
            // (UIA Edit) to a TextBlock (UIA Text) to drop the trailing
            // padding. Probe both control types so the test survives
            // either rendering.
            string? hit = null;
            foreach (var ct in new[] { ControlType.Edit, ControlType.Text })
            {
                hit = win.FindAllDescendants(c => c.ByControlType(ct))
                    .Select(e => e.Name ?? string.Empty)
                    .FirstOrDefault(t => t.StartsWith("Connected") || t.StartsWith("Connecting")
                                      || t.StartsWith("Failed")    || t.StartsWith("Disconnected"));
                if (hit is not null) return hit;
            }
            return "(status not found)";
        }
        catch (Exception ex) { return $"(probe threw: {ex.Message})"; }
    }

    private static AutomationElement? TryInvokeAndWaitForConnectButton(Window win, AutomationElement node)
    {
        node.Click();
        var btn = WaitFor(() => FindConnectButton(win), TimeSpan.FromSeconds(6));
        if (btn is not null) return btn;

        node.DoubleClick();
        btn = WaitFor(() => FindConnectButton(win), TimeSpan.FromSeconds(6));
        if (btn is not null) return btn;

        node.Focus();
        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        return WaitFor(() => FindConnectButton(win), TimeSpan.FromSeconds(6));
    }

    private static AutomationElement? FindConnectButton(Window win) =>
        win.FindFirstDescendant(c =>
            c.ByControlType(ControlType.Button).And(c.ByName("Connect")));

    private static T? WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { if (probe() is { } hit) return hit; } catch { /* element churn */ }
            Thread.Sleep(150);
        }
        return null;
    }

    private static bool WaitForCondition(Func<bool> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { if (probe()) return true; } catch { /* ignore */ }
            Thread.Sleep(100);
        }
        return false;
    }
}
