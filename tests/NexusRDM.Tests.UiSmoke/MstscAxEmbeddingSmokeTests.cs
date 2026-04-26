using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace NexusRDM.Tests.UiSmoke;

/// <summary>
/// Verifies the MstscAx RDP backend embeds its WinForms host as a child of
/// the WinUI main window — same in-tab UX as the SSH terminal. We don't
/// need a working RDP server: the AxHost form is created and reparented in
/// the HandleCreated phase, well before mstscax's connect attempt finishes
/// (or fails). The structural assertion is "a Forms-classed HWND now lives
/// under the main window's HWND".
/// </summary>
[Collection("UI smoke")]
public sealed class MstscAxEmbeddingSmokeTests : IClassFixture<RdpSessionFixture>
{
    private readonly RdpSessionFixture _fx;
    public MstscAxEmbeddingSmokeTests(RdpSessionFixture fx) => _fx = fx;

    [SkippableFact]
    public void MstscAxMode_Embeds_FormsChild_Inside_Main_Window()
    {
        Skip.IfNot(_fx.Available, "NexusRDM.exe not built — run via VS MSBuild first.");
        var win = _fx.MainWindow!;
        BringWindowToForeground(win);
        win.Focus();
        Thread.Sleep(250);

        // 1. Find and invoke the seeded RDP profile in the connection tree.
        var node = WaitFor(() =>
            win.FindAllDescendants(c => c.ByControlType(ControlType.TreeItem))
               .FirstOrDefault(it => (it.Name ?? "").Contains("Embedded RDP Test"))
            ?? win.FindFirstDescendant(c => c.ByName("Embedded RDP Test")),
            TimeSpan.FromSeconds(15));
        Assert.True(node is not null, "Seeded RDP connection not visible in tree.");

        var connectBtn = TryInvokeAndWaitForConnectButton(win, node!);
        Assert.True(connectBtn is not null,
            "Credential prompt did not appear after clicking the RDP node.");

        // 2. Drive the credential dialog. mstscax doesn't need real creds for
        // the embedding plumbing to run — the AxHost is created and
        // reparented during HandleCreated, before the connect attempt.
        var edits = WaitFor(() =>
        {
            var found = win.FindAllDescendants(c => c.ByControlType(ControlType.Edit));
            return found.Length >= 2 ? found : null;
        }, TimeSpan.FromSeconds(5));
        Assert.True(edits is not null && edits.Length >= 2, "Username/password fields not found.");

        var userBox = edits![0].AsTextBox();
        userBox.Focus();
        userBox.Text = _fx.Username;
        edits[1].Focus();
        Keyboard.Type(_fx.Password);
        connectBtn!.Click();

        // 3. Wait for a Forms-classed HWND to appear inside the NexusRDM
        // process. WinUI 3 spawns several top-level HWNDs per window
        // (compositor, DesktopChildSiteBridge, InputSite…), so checking
        // FlaUI's single "main" HWND can miss the reparent target.
        // Scanning every top-level HWND that belongs to NexusRDM.exe and
        // looking for a "WindowsForms…" child anywhere underneath is more
        // robust.
        var pid = (uint)_fx.App!.ProcessId;
        var embedded = WaitForCondition(
            () => HasFormsChildWindow(pid),
            TimeSpan.FromSeconds(20));

        Assert.True(embedded,
            "MstscAx Forms child window was not found anywhere under the NexusRDM process.\n" +
            $"Top-level windows + their child class names: {DescribeAllWindows(pid)}");
    }

    private static bool HasFormsChildWindow(uint processId)
    {
        bool found = false;
        EnumWindows((top, _) =>
        {
            GetWindowThreadProcessId(top, out var pid);
            if (pid != processId) return true;

            // The Forms host can be either a top-level owned window or a
            // child HWND of one of the WinUI windows depending on which
            // hosting strategy MstscAxRdpSession is using. Match either
            // shape so the test stays useful when the embedding tactic
            // changes.
            var sb = new StringBuilder(256);
            GetClassName(top, sb, sb.Capacity);
            if (sb.ToString().StartsWith("WindowsForms", StringComparison.Ordinal))
            {
                found = true;
                return false;
            }

            EnumChildWindows(top, (child, _) =>
            {
                var sbChild = new StringBuilder(256);
                GetClassName(child, sbChild, sbChild.Capacity);
                if (sbChild.ToString().StartsWith("WindowsForms", StringComparison.Ordinal))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return !found;
        }, IntPtr.Zero);
        return found;
    }

    private static string DescribeAllWindows(uint processId)
    {
        var lines = new List<string>();
        EnumWindows((top, _) =>
        {
            GetWindowThreadProcessId(top, out var pid);
            if (pid != processId) return true;
            var topClass = ClassOf(top);
            var children = new List<string>();
            EnumChildWindows(top, (child, _) =>
            {
                children.Add(ClassOf(child));
                return true;
            }, IntPtr.Zero);
            lines.Add($"[0x{top:X}] {topClass} -> {(children.Count == 0 ? "(no children)" : string.Join(", ", children.Distinct().Take(15)))}");
            return true;
        }, IntPtr.Zero);
        return lines.Count == 0 ? "(no top-level windows for process)" : "\n" + string.Join("\n", lines);
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [SkippableFact]
    public void MstscAxMode_FormFollowsTab_AndHidesOnTabSwitch()
    {
        Skip.IfNot(_fx.Available, "NexusRDM.exe not built — run via VS MSBuild first.");
        var win = _fx.MainWindow!;
        BringWindowToForeground(win);
        win.Focus();
        Thread.Sleep(250);

        // Make sure an RDP session is open. If a previous test already
        // drove the connect flow (xunit runs class methods in arbitrary
        // order but shares the fixture), the form HWND is still around;
        // otherwise drive the open flow here. We identify the form by
        // its unique Text ("NexusRDM-MstscAx-…") rather than by class —
        // multiple WindowsForms-classed top-levels can exist in the
        // process and class-only matching grabs the wrong one.
        var pid      = (uint)_fx.App!.ProcessId;
        var mainHwnd = win.Properties.NativeWindowHandle.ValueOrDefault;
        var formHwnd = FindFormByTitlePrefix(pid, "NexusRDM-MstscAx-");
        if (formHwnd == IntPtr.Zero)
        {
            OpenRdpConnection(win);
            formHwnd = WaitForCondition(() =>
            {
                var h = FindFormByTitlePrefix(pid, "NexusRDM-MstscAx-");
                return h != IntPtr.Zero ? h : IntPtr.Zero;
            }, IntPtr.Zero, TimeSpan.FromSeconds(20));
        }
        Assert.True(formHwnd != IntPtr.Zero, "MstscAx Forms HWND not found.");

        // 2. Form is on screen (not parked in the offscreen region) while
        //    its host tab is selected.
        Assert.True(WaitForCondition(() => !IsFormOffscreen(formHwnd), TimeSpan.FromSeconds(3)),
            "Form HWND was offscreen on the RDP tab.");

        // 3. Switch to Home tab → MainWindow's SessionTabs_SelectionChanged
        //    moves the form offscreen.
        var homeTab = WaitFor(() =>
            win.FindAllDescendants(c => c.ByControlType(ControlType.TabItem))
               .FirstOrDefault(t => (t.Name ?? "").Equals("Home", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        Assert.True(homeTab is not null, "Home tab not found.");
        SelectTabItem(homeTab!);
        var stillOnscreen = !WaitForCondition(() => IsFormOffscreen(formHwnd), TimeSpan.FromSeconds(5));
        if (stillOnscreen)
        {
            GetWindowRect(formHwnd, out var rcFinal);
            Assert.Fail(
                $"Form HWND did not move offscreen when the host tab was switched away. " +
                $"form=0x{formHwnd:X} finalRect=({rcFinal.Left},{rcFinal.Top},{rcFinal.Right - rcFinal.Left}x{rcFinal.Bottom - rcFinal.Top})");
        }

        // 4. Sanity: ensure the form HWND is still alive at end of test
        //    (didn't get killed off by the tab switch), so we know the
        //    offscreen state above is a "hidden" condition rather than
        //    a "destroyed" one.
        GetWindowRect(formHwnd, out var finalRect);
        Assert.True(finalRect.Right >= finalRect.Left,
            $"Form HWND no longer returns a valid rect after tab switch.");
    }

    /// <summary>True when the form has been parked in the offscreen
    /// region (X/Y < -10000). MstscAxRdpSession.SetVisible(false) moves
    /// the form to (-32000, -32000) instead of fighting WinForms over
    /// Form.Visible.</summary>
    private static bool IsFormOffscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return true;
        return r.Left < -10000 || r.Top < -10000;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>UIA's SelectionItem pattern is the documented way to
    /// activate a TabViewItem; a plain mouse click can land on the tab's
    /// border or close button and not actually change the selection.</summary>
    private static void SelectTabItem(AutomationElement tab)
    {
        try
        {
            var sel = tab.Patterns.SelectionItem.PatternOrDefault;
            if (sel is not null) { sel.Select(); return; }
        }
        catch { /* fall through to click */ }
        tab.Click();
    }

    /// <summary>Stale-element-safe scan for the RDP tab. The tab strip
    /// re-renders on selection change; reading .Name on a now-stale
    /// AutomationElement throws, so we catch per-element and let the
    /// WaitFor retry until UIA settles.</summary>
    private static AutomationElement? FindRdpTabSafely(Window win)
    {
        AutomationElement[] tabs;
        try { tabs = win.FindAllDescendants(c => c.ByControlType(ControlType.TabItem)); }
        catch { return null; }
        foreach (var t in tabs)
        {
            string? name;
            try { name = t.Name; }
            catch { continue; }
            if (name is null) continue;
            if (name.Equals("Home", StringComparison.Ordinal)) continue;
            if (name.Contains("Embedded") || name.Contains("RDP") || name.Contains("Test"))
                return t;
        }
        return null;
    }

    private void OpenRdpConnection(Window win)
    {
        var node = WaitFor(() =>
            win.FindAllDescendants(c => c.ByControlType(ControlType.TreeItem))
               .FirstOrDefault(it => (it.Name ?? "").Contains("Embedded RDP Test"))
            ?? win.FindFirstDescendant(c => c.ByName("Embedded RDP Test")),
            TimeSpan.FromSeconds(15));
        Assert.True(node is not null, "Seeded RDP node not visible.");

        var connectBtn = TryInvokeAndWaitForConnectButton(win, node!);
        Assert.True(connectBtn is not null, "Credential prompt did not appear.");

        var edits = WaitFor(() =>
        {
            var found = win.FindAllDescendants(c => c.ByControlType(ControlType.Edit));
            return found.Length >= 2 ? found : null;
        }, TimeSpan.FromSeconds(5));
        Assert.True(edits is not null && edits.Length >= 2);

        var userBox = edits![0].AsTextBox();
        userBox.Focus();
        userBox.Text = _fx.Username;
        edits[1].Focus();
        Keyboard.Type(_fx.Password);
        connectBtn!.Click();
    }

    private static IntPtr FindFormsTopLevel(uint processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            GetWindowThreadProcessId(top, out var pid);
            if (pid != processId) return true;
            var sb = new StringBuilder(256);
            GetClassName(top, sb, sb.Capacity);
            if (sb.ToString().StartsWith("WindowsForms", StringComparison.Ordinal))
            {
                found = top;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Find a top-level window in <paramref name="processId"/> whose
    /// title begins with <paramref name="titlePrefix"/>. The MstscAx form
    /// uses a unique title ("NexusRDM-MstscAx-…") so the test can pin to
    /// our window even when the process owns other Forms-classed
    /// top-levels (AxHost helpers, WinAppSDK internals, …).</summary>
    private static IntPtr FindFormByTitlePrefix(uint processId, string titlePrefix)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            GetWindowThreadProcessId(top, out var pid);
            if (pid != processId) return true;
            var sb = new StringBuilder(256);
            GetWindowText(top, sb, sb.Capacity);
            if (!sb.ToString().StartsWith(titlePrefix, StringComparison.Ordinal)) return true;
            found = top;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private static T WaitForCondition<T>(Func<T> probe, T defaultValue, TimeSpan timeout) where T : struct, IEquatable<T>
    {
        var deadline = DateTime.UtcNow + timeout;
        T last = defaultValue;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = probe();
                if (!last.Equals(defaultValue)) return last;
            }
            catch { /* transient */ }
            Thread.Sleep(100);
        }
        return last;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    private const uint GW_OWNER = 4;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    private static void BringWindowToForeground(Window win)
    {
        var hwnd = win.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    // ── Helpers (mirrored from SshTypingSmokeTests for consistency) ─────────

    private static AutomationElement? TryInvokeAndWaitForConnectButton(Window win, AutomationElement node)
    {
        // Be patient — when several UI smoke tests launch the app
        // back-to-back, the system is busy and the credential dialog can
        // take a while to appear after each invocation strategy.
        node.Click();
        var btn = WaitFor(() => FindConnectButton(win), TimeSpan.FromSeconds(6));
        if (btn is not null) return btn;

        node.DoubleClick();
        btn = WaitFor(() => FindConnectButton(win), TimeSpan.FromSeconds(6));
        if (btn is not null) return btn;

        node.Focus();
        Keyboard.Press(VirtualKeyShort.RETURN);
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
