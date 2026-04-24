using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VtNetCore.VirtualTerminal;
using VtNetCore.XTermParser;
using Windows.System;
using Windows.UI;

namespace NexusRDM.Controls;

/// <summary>
/// WinUI 3 terminal control backed by VtNetCore.
/// Receives raw VT bytes via Feed(), renders to a Canvas using TextBlocks,
/// and raises UserInput with raw bytes when the user types.
///
/// Rendering note: TextBlock-per-glyph is intentionally simple for M2.
/// Replace the Render() body with a Win2D SwapChainPanel for GPU-accelerated
/// colour/bold/underline in a future iteration.
/// </summary>
public sealed partial class TerminalControl : UserControl
{
    private readonly VirtualTerminalController _vtc  = new();
    private readonly DataConsumer              _parser;

    private int _cols = 220;
    private int _rows = 50;

    /// <summary>Raw bytes to send back to the SSH channel.</summary>
    public event EventHandler<byte[]>? UserInput;

    public (int Cols, int Rows) TerminalSize => (_cols, _rows);

    public TerminalControl()
    {
        _parser = new DataConsumer(_vtc);
        InitializeComponent();

        Background = new SolidColorBrush(Color.FromArgb(255, 12, 12, 12));
        IsTabStop  = true;
        KeyDown   += OnKeyDown;
        SizeChanged += OnSizeChanged;

        // VtNetCore can ask us to send data (e.g. device attribute responses)
        _vtc.SendData += (_, e) => UserInput?.Invoke(this, e.Data);
    }

    // ── Feed VT data ──────────────────────────────────────────────────────────

    public void Feed(byte[] data)
    {
        _parser.Push(data);
        DispatcherQueue.TryEnqueue(Render);
    }

    // ── Render ────────────────────────────────────────────────────────────────

    private void Render()
    {
        TermCanvas.Children.Clear();

        double cw = CharWidth;
        double ch = CharHeight;
        var vp    = _vtc.ViewPort;

        for (int row = 0; row < _rows; row++)
        {
            var line = vp.GetVisibleLine(row);
            if (line is null) continue;

            for (int col = 0; col < line.Count; col++)
            {
                var cell = line[col];
                if (cell.Char is '\0' or ' ') continue;

                var fg = cell.Attributes.ForegroundRgb;
                var tb = new TextBlock
                {
                    Text       = cell.Char.ToString(),
                    FontFamily = FontFamily,
                    FontSize   = FontSize,
                    Foreground = new SolidColorBrush(ArgbToColor(fg.ARGB))
                };

                // Background cell (only paint non-black backgrounds)
                var bg = cell.Attributes.BackgroundRgb;
                if (bg.ARGB != 0)
                {
                    var bgRect = new Rectangle
                    {
                        Width  = cw,
                        Height = ch,
                        Fill   = new SolidColorBrush(ArgbToColor(bg.ARGB))
                    };
                    Canvas.SetLeft(bgRect, col * cw);
                    Canvas.SetTop(bgRect,  row * ch);
                    TermCanvas.Children.Add(bgRect);
                }

                Canvas.SetLeft(tb, col * cw);
                Canvas.SetTop(tb,  row * ch);
                TermCanvas.Children.Add(tb);
            }
        }

        // Cursor block
        var cur = vp.CursorPosition;
        var cursor = new Rectangle
        {
            Width   = cw,
            Height  = ch,
            Fill    = new SolidColorBrush(Colors.White),
            Opacity = 0.65
        };
        Canvas.SetLeft(cursor, cur.Column * cw);
        Canvas.SetTop(cursor,  cur.Row    * ch);
        TermCanvas.Children.Add(cursor);
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var bytes = TranslateKey(e.Key, IsModifierDown(VirtualKey.Control),
                                        IsModifierDown(VirtualKey.Shift));
        if (bytes is { Length: > 0 })
        {
            UserInput?.Invoke(this, bytes);
            e.Handled = true;
        }
    }

    private static bool IsModifierDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static byte[] TranslateKey(VirtualKey key, bool ctrl, bool shift) => key switch
    {
        VirtualKey.Enter    => "\r"u8.ToArray(),
        VirtualKey.Back     => "\x7f"u8.ToArray(),
        VirtualKey.Tab      => "\t"u8.ToArray(),
        VirtualKey.Escape   => "\x1b"u8.ToArray(),
        VirtualKey.Up       => "\x1b[A"u8.ToArray(),
        VirtualKey.Down     => "\x1b[B"u8.ToArray(),
        VirtualKey.Right    => "\x1b[C"u8.ToArray(),
        VirtualKey.Left     => "\x1b[D"u8.ToArray(),
        VirtualKey.Home     => "\x1b[H"u8.ToArray(),
        VirtualKey.End      => "\x1b[F"u8.ToArray(),
        VirtualKey.Delete   => "\x1b[3~"u8.ToArray(),
        VirtualKey.PageUp   => "\x1b[5~"u8.ToArray(),
        VirtualKey.PageDown => "\x1b[6~"u8.ToArray(),
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

    private double CharWidth  => FontSize * 0.601;
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Color ArgbToColor(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >>  8) & 0xFF),
        (byte)( argb        & 0xFF));
}
