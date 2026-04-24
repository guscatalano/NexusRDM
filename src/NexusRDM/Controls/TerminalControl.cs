using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VtNetCore.VirtualTerminal;
using VtNetCore.XTermParser;
using Windows.System;
using Windows.UI;

namespace NexusRDM.Controls;

/// <summary>
/// A WinUI 3 UserControl that renders a VT100/xterm terminal using VtNetCore.
/// Receives raw VT bytes via Feed(), renders to a Canvas, and fires UserInput
/// with raw bytes when the user types.
///
/// Rendering approach: Canvas with DrawingSession (Win2D) is the production path,
/// but that requires the Win2D NuGet. For M2 we use a TextBlock grid — fast to
/// ship, easy to replace with Win2D once the rest of M2 is stable.
/// </summary>
public sealed partial class TerminalControl : UserControl
{
    // ── VtNetCore state ───────────────────────────────────────────────────────
    private readonly VirtualTerminalController _vtc = new();
    private readonly DataConsumer              _parser;

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>Fires when the user presses a key — caller sends bytes to SSH.</summary>
    public event EventHandler<byte[]>? UserInput;

    /// <summary>Current terminal dimensions in character cells.</summary>
    public (int Cols, int Rows) TerminalSize => (_cols, _rows);

    private int _cols = 220;
    private int _rows = 50;

    // ── Constructor ───────────────────────────────────────────────────────────

    public TerminalControl()
    {
        _parser = new DataConsumer(_vtc);
        InitializeComponent();

        Background = new SolidColorBrush(Color.FromArgb(255, 12, 12, 12));
        IsTabStop  = true;
        KeyDown   += OnKeyDown;
        SizeChanged += OnSizeChanged;

        _vtc.SendData += (_, e) => UserInput?.Invoke(this, e.Data);
    }

    // ── Feed VT data from SSH ─────────────────────────────────────────────────

    public void Feed(byte[] data)
    {
        _parser.Push(data);
        Render();
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Render()
    {
        // Simple TextBlock renderer — replace with Win2D SwapChainPanel for
        // proper colour, bold, underline support in M2 polish phase.
        TermCanvas.Children.Clear();

        var screen   = _vtc.ViewPort;
        double charW = CharWidth;
        double charH = CharHeight;

        for (int row = 0; row < screen.Count; row++)
        {
            var line = screen[row];
            for (int col = 0; col < line.Count; col++)
            {
                var cell = line[col];
                if (cell.Char == '\0' || cell.Char == ' ') continue;

                var tb = new TextBlock
                {
                    Text       = cell.Char.ToString(),
                    FontFamily = FontFamily,
                    FontSize   = FontSize,
                    Foreground = new SolidColorBrush(VtColorToWinUI(cell.ForegroundColor)),
                };
                Canvas.SetLeft(tb, col * charW);
                Canvas.SetTop(tb,  row * charH);
                TermCanvas.Children.Add(tb);
            }
        }

        // Cursor
        var cur = _vtc.CursorState;
        var cursorRect = new Windows.UI.Xaml.Shapes.Rectangle
        {
            Width   = charW,
            Height  = charH,
            Fill    = new SolidColorBrush(Colors.White),
            Opacity = 0.7
        };
        Canvas.SetLeft(cursorRect, cur.CurrentColumn * charW);
        Canvas.SetTop(cursorRect,  cur.CurrentRow    * charH);
        TermCanvas.Children.Add(cursorRect);
    }

    // ── Keyboard input ────────────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var bytes = TranslateKey(e.Key, IsCtrlDown(), IsShiftDown());
        if (bytes is { Length: > 0 })
        {
            UserInput?.Invoke(this, bytes);
            e.Handled = true;
        }
    }

    private static bool IsCtrlDown()  =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static byte[] TranslateKey(VirtualKey key, bool ctrl, bool shift) => key switch
    {
        VirtualKey.Enter     => "\r"u8.ToArray(),
        VirtualKey.Back      => "\x7f"u8.ToArray(),
        VirtualKey.Tab       => "\t"u8.ToArray(),
        VirtualKey.Escape    => "\x1b"u8.ToArray(),
        VirtualKey.Up        => "\x1b[A"u8.ToArray(),
        VirtualKey.Down      => "\x1b[B"u8.ToArray(),
        VirtualKey.Right     => "\x1b[C"u8.ToArray(),
        VirtualKey.Left      => "\x1b[D"u8.ToArray(),
        VirtualKey.Home      => "\x1b[H"u8.ToArray(),
        VirtualKey.End       => "\x1b[F"u8.ToArray(),
        VirtualKey.Delete    => "\x1b[3~"u8.ToArray(),
        VirtualKey.PageUp    => "\x1b[5~"u8.ToArray(),
        VirtualKey.PageDown  => "\x1b[6~"u8.ToArray(),
        VirtualKey k when ctrl && k >= VirtualKey.A && k <= VirtualKey.Z
                         => [(byte)(k - VirtualKey.A + 1)],
        VirtualKey k when k >= VirtualKey.A && k <= VirtualKey.Z
                         => [shift ? (byte)('A' + (k - VirtualKey.A))
                                   : (byte)('a' + (k - VirtualKey.A))],
        VirtualKey k when k >= VirtualKey.Number0 && k <= VirtualKey.Number9
                         => [(byte)('0' + (k - VirtualKey.Number0))],
        _ => []
    };

    // ── Sizing ────────────────────────────────────────────────────────────────

    private double CharWidth  => FontSize * 0.601;   // approximate for monospace
    private double CharHeight => FontSize * 1.4;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        int newCols = Math.Max(10, (int)(e.NewSize.Width  / CharWidth));
        int newRows = Math.Max(4,  (int)(e.NewSize.Height / CharHeight));
        if (newCols == _cols && newRows == _rows) return;
        _cols = newCols;
        _rows = newRows;
        _vtc.ResizeView(_cols, _rows);
    }

    // ── Color helper ──────────────────────────────────────────────────────────

    private static Color VtColorToWinUI(VtNetCore.VirtualTerminal.Model.TerminalColor color)
    {
        // VtNetCore exposes ARGB as an int
        int argb = color.Argb;
        return Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >>  8) & 0xFF),
            (byte)( argb        & 0xFF));
    }
}
