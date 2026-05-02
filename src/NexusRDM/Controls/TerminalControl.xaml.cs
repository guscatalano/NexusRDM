using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Text;
using VtNetCore.VirtualTerminal;
using VtNetCore.XTermParser;
using Windows.ApplicationModel.DataTransfer;
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
///
/// Selection / copy: pointer-drag selects a rectangular range in cell
/// coordinates. Ctrl+C copies + clears selection (sends SIGINT only when
/// nothing is selected, matching Windows Terminal). Ctrl+Shift+C always
/// copies. Right-click and Ctrl+Shift+V paste from the clipboard.
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

    // ── Selection state ───────────────────────────────────────────────
    // Stored in cell coordinates. _isSelecting indicates an in-progress
    // drag; the selection becomes "live" (highlighted, copyable) only
    // after the drag covers at least one cell.
    private (int Row, int Col) _selAnchor;
    private (int Row, int Col) _selFocus;
    private bool _isSelecting;
    private bool _hasSelection;

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
        PointerPressed     += OnPointerPressed;
        PointerMoved       += OnPointerMoved;
        PointerReleased    += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        RightTapped        += OnRightTapped;
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

    private static readonly Color DefaultFg     = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xF0);
    private static readonly Color SelectionFill = Color.FromArgb(0x80, 0x4D, 0xA6, 0xFF);

    private void Render()
    {
        TermCanvas.Children.Clear();

        double cw = CharWidth;
        double ch = CharHeight;
        var vp    = _vtc.ViewPort;

        // Selection highlight goes UNDER the glyphs so text stays readable.
        if (_hasSelection || _isSelecting)
        {
            var (s, e) = NormalisedSelection();
            for (int row = s.Row; row <= e.Row; row++)
            {
                int startCol = (row == s.Row) ? s.Col : 0;
                int endCol   = (row == e.Row) ? e.Col : _cols;
                if (endCol <= startCol) continue;
                var hl = new Rectangle
                {
                    Width  = (endCol - startCol) * cw,
                    Height = ch,
                    Fill   = new SolidColorBrush(SelectionFill),
                };
                Canvas.SetLeft(hl, startCol * cw);
                Canvas.SetTop(hl,  row * ch);
                TermCanvas.Children.Add(hl);
            }
        }

        for (int row = 0; row < _rows; row++)
        {
            var line = vp.GetVisibleLine(row);
            if (line is null) continue;

            for (int col = 0; col < line.Count; col++)
            {
                var cell = line[col];
                // Skip uninitialised cells. We DO render real spaces now —
                // `top`/`htop`/`vim` clear regions by writing spaces with
                // attributes, and skipping them used to leave stale glyphs
                // bleeding through redraws.
                if (cell.Char is '\0') continue;

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

                if (cell.Char == ' ') continue; // bg is drawn; no glyph needed

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
                Canvas.SetLeft(tb, col * cw);
                Canvas.SetTop(tb,  row * ch);
                TermCanvas.Children.Add(tb);
            }
        }

        // Cursor block — suppressed during a selection drag so the user
        // can see what they're highlighting without it strobing.
        if (!_isSelecting)
        {
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
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private (int Row, int Col) PointToCell(Windows.Foundation.Point p)
    {
        int col = Math.Clamp((int)(p.X / CharWidth), 0, _cols);
        int row = Math.Clamp((int)(p.Y / CharHeight), 0, _rows - 1);
        return (row, col);
    }

    /// <summary>Selection in line-major order (start ≤ end).</summary>
    private ((int Row, int Col) Start, (int Row, int Col) End) NormalisedSelection()
    {
        var a = _selAnchor;
        var b = _selFocus;
        bool aFirst = a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col);
        return aFirst ? (a, b) : (b, a);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var p = e.GetCurrentPoint(this);

        // Right-click is handled by RightTapped (paste); ignore here.
        if (p.Properties.IsRightButtonPressed) return;

        // Clear any old selection on a fresh left-click.
        var cell = PointToCell(p.Position);
        _selAnchor    = cell;
        _selFocus     = cell;
        _isSelecting  = true;
        _hasSelection = false;
        CapturePointer(e.Pointer);
        Render();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSelecting) return;
        var p = e.GetCurrentPoint(this);
        var cell = PointToCell(p.Position);
        if (cell == _selFocus) return;
        _selFocus = cell;
        _hasSelection = (cell != _selAnchor);
        Render();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSelecting) return;
        _isSelecting = false;
        var p = e.GetCurrentPoint(this);
        _selFocus = PointToCell(p.Position);
        _hasSelection = (_selFocus != _selAnchor);
        ReleasePointerCapture(e.Pointer);
        Render();
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // Drag interrupted (e.g. window lost focus). Treat as a normal release.
        _isSelecting = false;
        Render();
    }

    private async void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Right-click pastes. PuTTY-style middle-click is overkill;
        // right-click matches Windows Terminal's default behaviour.
        e.Handled = true;
        await PasteFromClipboardAsync();
    }

    private void ClearSelection()
    {
        if (!_hasSelection && !_isSelecting) return;
        _hasSelection = false;
        _isSelecting  = false;
        Render();
    }

    private string GetSelectedText()
    {
        if (!_hasSelection) return string.Empty;
        var (s, e) = NormalisedSelection();
        var vp     = _vtc.ViewPort;
        var sb     = new StringBuilder();

        for (int row = s.Row; row <= e.Row; row++)
        {
            var line = vp.GetVisibleLine(row);
            if (line is null) { if (row < e.Row) sb.Append('\n'); continue; }

            int startCol = (row == s.Row) ? s.Col : 0;
            int endCol   = (row == e.Row) ? e.Col : line.Count;
            endCol = Math.Min(endCol, line.Count);

            // Build the row's slice. \0 cells become spaces; trailing
            // whitespace is trimmed per row so a wide selection over a
            // mostly-empty line doesn't paste a wall of spaces.
            var rowSb = new StringBuilder(endCol - startCol);
            for (int col = startCol; col < endCol; col++)
            {
                var c = line[col].Char;
                rowSb.Append(c == '\0' ? ' ' : c);
            }
            sb.Append(rowSb.ToString().TrimEnd());
            if (row < e.Row) sb.Append('\n');
        }
        return sb.ToString();
    }

    private void CopySelectionToClipboard()
    {
        var text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    private async Task PasteFromClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content is null || !content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            // Normalise Windows CRLF to bare CR — that's what Enter
            // sends in the terminal protocol; LF alone gets ignored
            // by most shells.
            text = text.Replace("\r\n", "\r").Replace('\n', '\r');
            UserInput?.Invoke(this, Encoding.UTF8.GetBytes(text));
        }
        catch { /* clipboard format unavailable / access denied */ }
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl  = IsModifierDown(VirtualKey.Control);
        bool shift = IsModifierDown(VirtualKey.Shift);

        // Ctrl+Shift+C / Ctrl+Shift+V — explicit copy/paste, never sent
        // as control bytes. Matches Windows Terminal default bindings.
        if (ctrl && shift && e.Key == VirtualKey.C)
        {
            CopySelectionToClipboard();
            ClearSelection();
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == VirtualKey.V)
        {
            await PasteFromClipboardAsync();
            e.Handled = true;
            return;
        }

        // Ctrl+C: copy if a selection exists, otherwise let it fall
        // through as SIGINT (^C, byte 0x03) via TranslateSpecialKey.
        if (ctrl && !shift && e.Key == VirtualKey.C && _hasSelection)
        {
            CopySelectionToClipboard();
            ClearSelection();
            e.Handled = true;
            return;
        }

        var bytes = TranslateSpecialKey(e.Key, ctrl, shift);
        if (bytes is { Length: > 0 })
        {
            // Any keystroke that produces output dismisses a selection —
            // matches PuTTY / xterm muscle memory.
            ClearSelection();
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

        ClearSelection();
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
