using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Xunit;

namespace NexusRDM.Tests.UiSmoke;

/// <summary>
/// End-to-end smoke test: launches the real WinUI app pointed at an embedded
/// SSH server, drives the connection tree + credential prompt via FlaUI, then
/// types a key and verifies the byte arrived at the SSH server. Proves the
/// keyboard → ViewModel → ShellStream pipeline works headed.
/// </summary>
[Collection("UI smoke")]
public sealed class SshTypingSmokeTests : IClassFixture<SshSessionFixture>
{
    private readonly SshSessionFixture _fx;
    public SshTypingSmokeTests(SshSessionFixture fx) => _fx = fx;

    [SkippableFact]
    public void Connect_To_Embedded_Ssh_Then_Type_Reaches_Server()
    {
        Skip.IfNot(_fx.Available, "NexusRDM.exe not built — run `dotnet build src/NexusRDM` first.");
        var win = _fx.MainWindow!;
        // Explicit Win32 foregrounding — UIA Focus() alone isn't enough when a
        // prior UI smoke fixture left another window in the foreground.
        BringToForeground(win);
        win.Focus();
        Thread.Sleep(250);

        // 1. Wait for the connection to populate, then invoke it. Prefer the
        // actual TreeViewItem (which raises ItemInvoked) over its inner
        // TextBlock — clicks on the latter sometimes don't bubble as an
        // invocation.
        var node = WaitFor(() =>
            win.FindAllDescendants(c => c.ByControlType(ControlType.TreeItem))
               .FirstOrDefault(it => (it.Name ?? "").Contains("Embedded SSH"))
            ?? win.FindFirstDescendant(c => c.ByName("Embedded SSH")),
            TimeSpan.FromSeconds(15));
        Assert.True(node is not null, "Seeded 'Embedded SSH' connection not visible in tree.");

        // 2. Click the node, then watch for the credential dialog. WinUI3
        // TreeView raises ItemInvoked on either single or double click depending
        // on selection mode and platform version, so try a single click first
        // and fall back to a double-click + keyboard Enter if no dialog opens.
        var connectBtn = TryInvokeAndWaitForConnectButton(win, node!);
        Assert.True(connectBtn is not null,
            "Credential prompt did not appear after clicking the connection node.");

        // The dialog hosts one TextBox (username) + one PasswordBox (password) —
        // both expose ControlType.Edit, in document order.
        var edits = WaitFor(() =>
        {
            var found = win.FindAllDescendants(c => c.ByControlType(ControlType.Edit));
            return found.Length >= 2 ? found : null;
        }, TimeSpan.FromSeconds(5));
        Assert.True(edits is not null && edits.Length >= 2, "Username/password fields not found.");

        var userBox = edits![0].AsTextBox();
        userBox.Focus();
        userBox.Text = _fx.Server.Username;

        // PasswordBox doesn't expose .Text via UIA, so type instead.
        edits[1].Focus();
        Keyboard.Type(_fx.Server.Password);

        connectBtn!.Click();

        // 3. Wait for the SSH handshake to complete by polling the
        // SshSessionViewModel.StatusMessage (rendered as a read-only TextBox).
        var connected = WaitForCondition(
            () => TryReadStatusMessage(win).StartsWith("Connected", StringComparison.Ordinal),
            TimeSpan.FromSeconds(20));
        Assert.True(connected,
            $"SSH session never reached 'Connected' state. Status: '{TryReadStatusMessage(win)}'\n" +
            _fx.DumpAppDiagnostics());

        // 4. Closing the credential dialog leaves keyboard focus on whatever
        // had it before — usually the tree node, not the new SSH tab. Click
        // the tab header to refocus the SshSessionView, which on PointerPressed
        // claims keyboard focus.
        var sshTab = WaitFor(() =>
            win.FindAllDescendants(c => c.ByControlType(ControlType.TabItem))
               .FirstOrDefault(t => (t.Name ?? "").Contains("Embedded SSH")),
            TimeSpan.FromSeconds(5));
        sshTab?.Click();
        Thread.Sleep(200);

        // Force the WinUI window to foreground — Keyboard.Type uses Win32
        // SendInput, which delivers to the foreground HWND. App.Launch doesn't
        // necessarily put the new window in front of the test runner.
        BringToForeground(win);
        Thread.Sleep(150);

        // Click into the terminal area to give the TerminalControl pointer focus
        // (its PointerPressed handler claims focus). We click the StatusMessage
        // bar's row to avoid hitting any focusable child by accident — actually
        // the terminal Canvas covers the middle of the view, so click there.
        ClickIntoTerminalArea(win);
        Thread.Sleep(200);

        // 5. Type a distinctive character. WinUI routes printable keys through
        // CharacterReceived → SshSessionViewModel.SendInputAsync → ShellStream.
        var beforeLen = _fx.Server.ReceivedBytes.Length;
        Keyboard.Type("z");

        // 5. Wait for 'z' to land on the server side — exactly one byte. The
        // SshSessionView and TerminalControl both subscribe to keyboard input,
        // so a regression on the dedup logic shows up as duplicate bytes
        // ("zz" instead of "z"). Asserting an exact match catches that.
        var oneByteArrived = WaitForCondition(
            () => _fx.Server.ReceivedBytes.Length >= beforeLen + 1,
            TimeSpan.FromSeconds(10));
        Assert.True(oneByteArrived,
            $"Server never received 'z' from the UI. Total received so far: " +
            $"{_fx.Server.ReceivedBytes.Length} bytes ('{Sanitize(_fx.Server.ReceivedText)}').\n" +
            $"App status: '{TryReadStatusMessage(win)}'\n" +
            _fx.DumpAppDiagnostics());
        // Settle: give any duplicate sends a chance to land before we measure.
        Thread.Sleep(300);
        var afterZ = _fx.Server.ReceivedBytes.Length - beforeLen;
        Assert.True(afterZ == 1,
            $"Expected exactly 1 byte for 'z' keystroke, got {afterZ}. " +
            $"Likely cause: SshSessionView + TerminalControl both forwarding the same event.");

        // 6. Click around inside the SshSessionView — toolbar status box, then
        // back into the terminal — and type "abc". Each keystroke must produce
        // exactly one byte regardless of focus shuffling. This is the
        // regression test for "clicking re-subscribes and 'a' types as 'aa'".
        var checkpoint = _fx.Server.ReceivedBytes.Length;
        var statusBox = win.FindAllDescendants(c => c.ByControlType(ControlType.Edit))
            .FirstOrDefault(e => (e.AsTextBox().Text ?? "").StartsWith("Connected"));
        statusBox?.Click();
        Thread.Sleep(150);
        ClickIntoTerminalArea(win);
        Thread.Sleep(200);

        Keyboard.Type("abc");
        var threeArrived = WaitForCondition(
            () => _fx.Server.ReceivedBytes.Length >= checkpoint + 3,
            TimeSpan.FromSeconds(5));
        Assert.True(threeArrived,
            $"Only got {_fx.Server.ReceivedBytes.Length - checkpoint} bytes after typing 'abc'.");
        Thread.Sleep(300);  // catch any late duplicates
        var typed = _fx.Server.ReceivedBytes.Skip(checkpoint).ToArray();
        Assert.Equal("abc", Encoding.UTF8.GetString(typed));
    }

    private static string TryReadStatusMessage(Window win)
    {
        try
        {
            // Status used to be a read-only TextBox (UIA Edit) but moved
            // to a TextBlock (UIA Text) to drop trailing layout padding.
            // Probe both so this stays robust either way.
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

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private const int SW_RESTORE = 9;

    private static void BringToForeground(Window win)
    {
        var hwnd = win.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    private static void ClickIntoTerminalArea(Window win)
    {
        // The terminal sits in the centre row of the SshSessionView. Click the
        // window's centre — that's reliably inside the terminal canvas.
        var rect = win.BoundingRectangle;
        var cx   = (int)(rect.Left + rect.Width  / 2);
        var cy   = (int)(rect.Top  + rect.Height / 2);
        Mouse.Click(new System.Drawing.Point(cx, cy));
    }

    private static AutomationElement? TryInvokeAndWaitForConnectButton(Window win, AutomationElement node)
    {
        // Be patient — when multiple UI smoke fixtures launch back-to-back,
        // the system is busy and the credential dialog can take a few
        // seconds to materialise after each invocation strategy.

        // Strategy 1: single click. WinUI3 TreeView typically raises ItemInvoked here.
        node.Click();
        var btn = WaitFor(() => FindConnectButton(win), TimeSpan.FromSeconds(6));
        if (btn is not null) return btn;

        // Strategy 2: double-click. Some configurations require it.
        node.DoubleClick();
        btn = WaitFor(() => FindConnectButton(win), TimeSpan.FromSeconds(6));
        if (btn is not null) return btn;

        // Strategy 3: keyboard invoke (Focus + Enter).
        node.Focus();
        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        btn = WaitFor(() => FindConnectButton(win), TimeSpan.FromSeconds(6));
        return btn;
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

    private static string Sanitize(string raw) =>
        new(raw.Select(c => c < 0x20 ? '·' : c).ToArray());
}
