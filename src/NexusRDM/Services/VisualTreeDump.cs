using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace NexusRDM.Services;

/// <summary>
/// Formats the live XAML visual tree under a given root into a compact
/// indented text representation. Used by the "Copy visual tree" diagnostic
/// button on the Settings page so problems with layout / hosted controls
/// can be shared with whoever's looking at the bug.
/// </summary>
internal static class VisualTreeDump
{
    public static string Build(DependencyObject? root)
    {
        if (root is null) return "(no root)";
        var sb = new StringBuilder();
        Walk(root, sb, depth: 0);
        return sb.ToString();
    }

    private static void Walk(DependencyObject node, StringBuilder sb, int depth)
    {
        sb.Append(' ', depth * 2)
          .Append(node.GetType().Name);

        AppendKeyProps(node, sb);
        sb.AppendLine();

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            Walk(VisualTreeHelper.GetChild(node, i), sb, depth + 1);
    }

    private static void AppendKeyProps(DependencyObject node, StringBuilder sb)
    {
        if (node is FrameworkElement fe)
        {
            if (!string.IsNullOrEmpty(fe.Name))
                sb.Append("  Name=\"").Append(fe.Name).Append('"');
            sb.Append("  ").Append((int)fe.ActualWidth).Append('x').Append((int)fe.ActualHeight);
            if (fe.Visibility != Visibility.Visible)
                sb.Append("  [").Append(fe.Visibility).Append(']');
        }
        if (node is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
        {
            var t = tb.Text.Length > 60 ? tb.Text[..60] + "…" : tb.Text;
            sb.Append("  Text=\"").Append(t).Append('"');
        }
        else if (node is TextBox tbx && !string.IsNullOrWhiteSpace(tbx.Text))
        {
            var t = tbx.Text.Length > 60 ? tbx.Text[..60] + "…" : tbx.Text;
            sb.Append("  Text=\"").Append(t).Append('"');
        }
        else if (node is ContentControl cc && cc.Content is string s && !string.IsNullOrWhiteSpace(s))
        {
            sb.Append("  Content=\"").Append(s).Append('"');
        }
    }

    /// <summary>Enumerates top-level windows owned by the current process
    /// that match the embedded-RDP form titles (<c>NexusRDM-MstscAx-…</c>
    /// while docked and <c>Nexus RDM — …</c> when popped out) and reports
    /// each one's screen rect, visibility flags, and owner HWND. Useful
    /// when the RDP tab looks empty and we need to know whether the form
    /// is offscreen, behind the WinUI window, or simply zero-sized.</summary>
    public static string DumpRdpWindows()
    {
        var pid = (uint)Environment.ProcessId;
        var sb  = new StringBuilder();
        sb.AppendLine("RDP windows:");

        var found = 0;
        EnumWindows((hwnd, _) =>
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out var wpid);
                if (wpid != pid) return true;
                var title = GetWindowTitle(hwnd);
                if (string.IsNullOrEmpty(title)) return true;
                if (!title.StartsWith("NexusRDM-MstscAx", StringComparison.Ordinal)
                 && !title.StartsWith("Nexus RDM —",  StringComparison.Ordinal))
                    return true;

                found++;
                GetWindowRect(hwnd, out var rect);
                var visible = IsWindowVisible(hwnd);
                var owner   = GetWindow(hwnd, GW_OWNER);
                var styles  = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
                var minimized = (styles & WS_MINIMIZE) != 0;

                sb.Append("  hwnd=0x").Append(hwnd.ToString("X"))
                  .Append("  title=\"").Append(title).Append('"')
                  .Append("  rect=(").Append(rect.Left).Append(',').Append(rect.Top)
                  .Append(' ').Append(rect.Right - rect.Left).Append('x').Append(rect.Bottom - rect.Top).Append(')')
                  .Append("  client=").Append(GetClientSize(hwnd))
                  .Append("  visible=").Append(visible)
                  .Append("  minimized=").Append(minimized)
                  .Append("  owner=0x").Append(owner.ToString("X"))
                  .AppendLine();

                DumpChildren(hwnd, sb, depth: 2);
            }
            catch { /* skip windows that race away */ }
            return true;
        }, IntPtr.Zero);

        if (found == 0) sb.AppendLine("  (none)");
        return sb.ToString();
    }

    private static string GetWindowTitle(nint hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var buf = new StringBuilder(len + 1);
        GetWindowText(hwnd, buf, buf.Capacity);
        return buf.ToString();
    }

    private static string GetClassName(nint hwnd)
    {
        var buf = new StringBuilder(256);
        var n = GetClassName(hwnd, buf, buf.Capacity);
        return n > 0 ? buf.ToString() : string.Empty;
    }

    private static string GetClientSize(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var r)) return "?";
        return (r.Right - r.Left) + "x" + (r.Bottom - r.Top);
    }

    /// <summary>Walks the child HWND tree under <paramref name="parent"/>
    /// and emits class, screen rect, client size, and visibility for each.
    /// The mstscax OCX nests several Win32 children (the OCX host, the
    /// rendering surface, etc.) — if the visible region is smaller than
    /// the form, one of these children is the actual culprit.</summary>
    private static void DumpChildren(nint parent, StringBuilder sb, int depth)
    {
        var children = new List<nint>();
        EnumChildWindows(parent, (h, _) =>
        {
            // Direct children only — recurse manually so we can indent.
            if (GetParent(h) == parent) children.Add(h);
            return true;
        }, IntPtr.Zero);

        foreach (var ch in children)
        {
            try
            {
                GetWindowRect(ch, out var rect);
                var cls     = GetClassName(ch);
                var title   = GetWindowTitle(ch);
                var visible = IsWindowVisible(ch);
                sb.Append(' ', depth * 2)
                  .Append("hwnd=0x").Append(ch.ToString("X"))
                  .Append("  class=\"").Append(cls).Append('"');
                if (!string.IsNullOrEmpty(title))
                    sb.Append("  title=\"").Append(title).Append('"');
                sb.Append("  rect=(").Append(rect.Left).Append(',').Append(rect.Top)
                  .Append(' ').Append(rect.Right - rect.Left).Append('x').Append(rect.Bottom - rect.Top).Append(')')
                  .Append("  client=").Append(GetClientSize(ch))
                  .Append("  visible=").Append(visible)
                  .AppendLine();
                DumpChildren(ch, sb, depth + 1);
            }
            catch { /* skip races */ }
        }
    }

    // ── Win32 ──────────────────────────────────────────────────────────────

    private const int  GW_OWNER     = 4;
    private const int  GWL_STYLE    = -16;
    private const long WS_MINIMIZE  = 0x20000000L;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, int cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc proc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder name, int count);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint hWnd);
}
