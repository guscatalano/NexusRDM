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
        UseSystemFocusVisuals = false;
        KeyDown            += OnKeyDown;
        CharacterReceived  += OnCharacterReceived;
        SizeChanged        += OnSizeChanged;
        PointerPressed     += (_, e) => { Focus(FocusState.Pointer); e.Handled = true; };
        Loaded             += (_, _) => Focus(FocusState.Programmatic);

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

    private static readonly Color DefaultFg = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xF0);

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

                // VtNetCore reports ARGB=0 (fully transparent) for the default
                // foreground; fall back to a visible light gray in that case.
                var fgArgb = cell.Attributes.ForegroundRgb?.ARGB ?? 0u;
                var fgColor = (fgArgb >> 24) == 0 ? DefaultFg : ArgbToColor(fgArgb);

                var tb = new TextBlock
                {
                    Text       = cell.Char.ToString(),
                    FontFamily = FontFamily,
                    FontSize   = FontSize,
                    Foreground = new SolidColorBrush(fgColor)
                };

                var bgArgb = cell.Attributes.BackgroundRgb?.ARGB ?? 0u;
                if ((bgArgb >> 24) != 0)
                {
                    var bgRect = new Rectangle
                    {
                        Width  = cw,
                        Height = ch,
                        Fill   = new SolidColorBrush(ArgbToColor(bgArgb))
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
        bool ctrl  = IsModifierDown(VirtualKey.Control);
        bool shift = IsModifierDown(VirtualKey.Shift);

        var bytes = TranslateSpecialKey(e.Key, ctrl, shift);
        if (bytes is { Length: > 0 })
        {
            UserInput?.Invoke(this, bytes);
            e.Handled = true;
        }
        // Printable keys are intentionally *not* handled here — they flow into
        // CharacterReceived which gives us the OS-translated unicode (handles
        // Shift, AltGr, IME, dead keys, layout differences, symbols, etc.).
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        // Skip control-range chars; OnKeyDown already handled them
        // (Enter=0x0D, Backspace=0x08, Tab=0x09, Escape=0x1B, etc.).
        if (e.Character < 0x20 || e.Character == 0x7F) return;

        UserInput?.Invoke(this, System.Text.Encoding.UTF8.GetBytes(new[] { e.Character }));
        e.Handled = true;
    }

    private static bool IsModifierDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Public wrapper so the parent view can route keys when this control isn't focused.</summary>
    public byte[] TranslateSpecialKeyForView(VirtualKey key) =>
        TranslateSpecialKey(key, IsModifierDown(VirtualKey.Control), IsModifierDown(VirtualKey.Shift));

    /// <summary>Maps non-printable / control keys to VT byte sequences. Returns [] for printable keys (handled by CharacterReceived). Public-static so unit tests can call it without instantiating the control.</summary>
    public static byte[] TranslateSpecialKey(VirtualKey key, bool ctrl, bool shift) => key switch
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
        _ => []
    };

    // ── Sizing ────────────────────────────────────────────────────────────────

    private double CharWidth  => FontSize * 0.601;
    private double CharHeight => FontSize * 1.4;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        int newCols = Math.Max(10, (int)(e.NewSize.Width  / CharWidth));
        int newRows = Math.Max(4,  (int)(e.NewSize.Height / CharHeight));

        // Canvas needs explicit dimensions or it stays 0×0 and clips its children.
        TermCanvas.Width  = e.NewSize.Width;
        TermCanvas.Height = e.NewSize.Height;

        if (newCols == _cols && newRows == _rows) return;
        _cols = newCols;
        _rows = newRows;
        _vtc.ResizeView(_cols, _rows);
        DispatcherQueue.TryEnqueue(Render);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Color ArgbToColor(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >>  8) & 0xFF),
        (byte)( argb        & 0xFF));
}
