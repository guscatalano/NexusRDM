using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using NexusRDM.Core.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Text;
using System.Reflection;
using VtNetCore.VirtualTerminal;
using VtNetCore.VirtualTerminal.Enums;
using VtNetCore.XTermParser;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace NexusRDM.Controls;

/// <summary>
/// WinUI 3 terminal control backed by VtNetCore and rendered through
/// Win2D (Direct2D + DirectWrite). Receives raw VT bytes via Feed(),
/// updates the VtNetCore controller state, then invalidates the
/// CanvasControl so OnDraw emits glyph runs and filled rectangles
/// straight to a swap chain. The previous TextBlock-per-cell renderer
/// allocated ~11,000 XAML elements per frame and could not keep up
/// with high-throughput output; this path stays flat at one draw
/// session per frame regardless of viewport size.
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

    /// <summary>How many rows above the current viewport the user has
    /// scrolled. 0 = following live output. While this is non-zero we
    /// render lines from <see cref="_scrollback"/> and ignore the
    /// cursor, so incoming data doesn't yank the user back to the
    /// bottom.</summary>
    private int _scrollOffset;

    /// <summary>Cap on retained history. ~5000 lines is enough for a
    /// typical "scroll up to read what just flew by" workflow without
    /// blowing memory on long-running sessions.</summary>
    private const int HistoryRows = 5000;

    /// <summary>Our own scrollback buffer. VtNetCore in this version
    /// doesn't reliably retain lines that scroll off the visible
    /// area (vp.TopRow stays at 0, vp.GetLine throws on negative
    /// indices), so we capture rows ourselves: snapshot the visible
    /// area before each Feed, snapshot after, diff to find how many
    /// rows shifted up, and push the disappearing rows here.
    /// Stored as plain strings — colour/attribute fidelity is lost
    /// in scrollback for now (live rendering still has full colour).
    /// Bounded to <see cref="HistoryRows"/>; oldest entries drop off
    /// the front when we hit the cap.</summary>
    private readonly List<string> _scrollback = new();
    private List<string>? _lastSnapshot;

    /// <summary>True while the VtNetCore controller is in the alternate
    /// screen buffer (DECSET 1049). Programs like <c>top</c>, <c>vim</c>,
    /// <c>less</c> enter alt-buffer mode, repaint in place every frame,
    /// then DECRESET on exit and the original buffer is restored. We
    /// suppress scrollback capture and scrollback navigation while this
    /// is set — matches xterm / Windows Terminal / PuTTY behaviour, and
    /// avoids polluting history with transient TUI redraws.</summary>
    private bool _inAltBuffer;

    /// <summary>VtNetCore 1.0.9 marks <c>ActiveBuffer</c> as a private
    /// property with no public accessor (the <c>Enable*Buffer</c> methods
    /// are sealed-final on the interface so we can't override them
    /// either). Read it via cached reflection. If a future version of
    /// VtNetCore renames or removes the property this returns null and
    /// we fall back to "always normal buffer" — equivalent to the
    /// pre-fix behaviour, no crash.</summary>
    private static readonly PropertyInfo? ActiveBufferProp =
        typeof(VirtualTerminalController).GetProperty(
            "ActiveBuffer",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private bool IsInAlternateBuffer() =>
        ActiveBufferProp?.GetValue(_vtc) is EActiveBuffer.Alternative;

    // ── Win2D state ───────────────────────────────────────────────────
    // CanvasTextFormat is the DirectWrite handle that owns the font face
    // and rendering settings (size, alignment, hinting). Constructed once,
    // reused for every glyph run. _charWidth/_charHeight are measured
    // from this format and drive cell-to-pixel math everywhere (cursor,
    // selection, pointer hit-testing). They MUST stay in sync with the
    // format — call MeasureCellMetrics whenever the format changes.
    private CanvasTextFormat? _textFormat;
    private CanvasTextFormat? _textFormatBold;
    private float _charWidth  = 8f;
    private float _charHeight = 18f;
    // Reused per row to compose color-run strings without allocating
    // a new StringBuilder for every span on every frame.
    private readonly StringBuilder _rowBuf = new(256);

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

        // Tell VtNetCore to retain history. Default is 0 — it discards
        // every line that scrolls off the visible area, so PgUp can't
        // see anything. With this set, ViewPort.GetLine(absRow) returns
        // historical rows and we can scroll back into them.
        _vtc.MaximumHistoryLines = HistoryRows;

        Background = new SolidColorBrush(Color.FromArgb(255, 12, 12, 12));
        IsTabStop  = true;
        UseSystemFocusVisuals = false;
        KeyDown             += OnKeyDown;
        CharacterReceived   += OnCharacterReceived;
        SizeChanged += OnSizeChanged;

        // Win2D draw pipeline. The control raises Draw whenever it needs
        // to paint (initial display, after Invalidate(), after resize).
        // We construct the text format lazily on the first Draw because
        // FontFamily / FontSize on UserControl are not yet resolved at
        // ctor time (the XAML parser sets them after InitializeComponent
        // returns).
        TermCanvas.Draw += OnDraw;

        // Custom scrollbar pointer handling — drag the thumb to
        // change scroll offset.
        VScrollTrack.PointerPressed     += OnScrollTrackPointerPressed;
        VScrollTrack.PointerMoved       += OnScrollTrackPointerMoved;
        VScrollTrack.PointerReleased    += OnScrollTrackPointerReleased;
        VScrollTrack.PointerCaptureLost += OnScrollTrackPointerLost;
        PointerPressed      += OnPointerPressed;
        PointerMoved        += OnPointerMoved;
        PointerReleased     += OnPointerReleased;
        PointerCaptureLost  += OnPointerCaptureLost;
        PointerWheelChanged += OnPointerWheelChanged;
        RightTapped         += OnRightTapped;
        Loaded              += (_, _) => Focus(FocusState.Programmatic);

        // Focus reporting (xterm ESC[?1004h). Programs like vim opt
        // in and expect ESC[I when the terminal gains focus, ESC[O
        // when it loses it (used to auto-save, refresh git status,
        // etc.). VtNetCore parses the enable/disable and surfaces
        // it as SendFocusInAndFocusOutEvents — gate emission on that.
        GotFocus  += (_, _) => SendFocusEvent(true);
        LostFocus += (_, _) => SendFocusEvent(false);

        // VtNetCore can ask us to send data (e.g. device attribute responses)
        _vtc.SendData += (_, e) => UserInput?.Invoke(this, e.Data);
    }

    private static readonly byte[] FocusInBytes  = { 0x1B, 0x5B, 0x49 }; // ESC[I
    private static readonly byte[] FocusOutBytes = { 0x1B, 0x5B, 0x4F }; // ESC[O

    private void SendFocusEvent(bool gained)
    {
        if (!_vtc.SendFocusInAndFocusOutEvents) return;
        UserInput?.Invoke(this, gained ? FocusInBytes : FocusOutBytes);
    }

    // ── Feed VT data ──────────────────────────────────────────────────────────

    public void Feed(byte[] data)
    {
        bool wasAlt = _inAltBuffer;
        // Only snapshot pre-state if we're going to diff (normal buffer).
        // In alt mode the snapshot would be wasted work — top redraws
        // the visible rows on every refresh and we'd never use the diff.
        var pre = wasAlt ? null : (_lastSnapshot ?? SnapshotVisibleRows());

        PushWithPseudoAltDetection(data);

        bool nowAlt = IsInAlternateBuffer();

        if (nowAlt != wasAlt)
        {
            // Buffer flipped (entered/exited top/vim/less). Diffing
            // across buffers would treat the wholesale content swap as
            // a giant "scroll" and dump garbage into history. Reset.
            // Snap to live: top users want their viewport pinned to the
            // current frame; after exiting, they want to see the
            // restored shell, not be parked mid-scrollback.
            _inAltBuffer  = nowAlt;
            _lastSnapshot = null;
            _scrollOffset = 0;
            SshLog.Info($"Alt-buffer flip: {(nowAlt ? "ENTER" : "EXIT")} scrollback={_scrollback.Count} cols={_cols} rows={_rows}");
        }
        else if (!nowAlt)
        {
            // Stayed in normal buffer — diff and append scroll-off rows.
            var post = SnapshotVisibleRows();
            if (pre is not null) CaptureScrollOff(pre, post);
            _lastSnapshot = post;
        }
        // Else: stayed in alt buffer — no capture, no snapshot tracking.

        // Win2D internally coalesces multiple Invalidate() calls between
        // frames into a single Draw, so we don't need our own pending
        // flag any more. Going through Render() also keeps the
        // scrollbar thumb in sync — streaming output grows _scrollback
        // and the thumb size should shrink correspondingly.
        Render();
    }

    /// <summary>Push <paramref name="data"/> into the VtNetCore parser,
    /// but pre-scan for cursor-visibility toggles (<c>ESC[?25l</c> /
    /// <c>ESC[?25h</c>) and synthesize the corresponding <c>DECSET 1049</c>
    /// / <c>DECRESET 1049</c> sequences around them.
    ///
    /// Background: many full-screen TUIs (<c>top</c>, <c>htop</c>) issue
    /// <c>hide-cursor</c> + clear-screen on entry and <c>show-cursor</c>
    /// on exit, but only call <c>smcup</c> / <c>rmcup</c> when terminfo
    /// is correctly configured. On servers where TERM has been
    /// downgraded (e.g. <c>xterm</c> instead of <c>xterm-256color</c>,
    /// or <c>infocmp</c> stripped of smcup), the program writes its
    /// fullscreen frame straight into the normal screen buffer. On exit
    /// the frame stays painted and the user can't tell they're back at
    /// the shell prompt.
    ///
    /// We treat cursor-hide as an alt-buffer enter signal (and
    /// cursor-show as exit). The visible behaviour matches a proper
    /// <c>smcup</c>/<c>rmcup</c> terminal: top runs in the alt buffer,
    /// and on exit the pre-top shell history is restored.
    ///
    /// The synthesized sequences go through VtNetCore's normal parser
    /// path, which means save/restore of cursor state, attribute
    /// reset, and buffer switching all happen exactly as if the server
    /// had sent the standard DECSET 1049.</summary>
    private void PushWithPseudoAltDetection(byte[] data)
    {
        // OSC 52 clipboard-set sequences must be intercepted BEFORE the
        // bytes reach VtNetCore, because VtNetCore 1.0.9 has no handler
        // for them and would just drop the parser into an "unknown OSC"
        // state. The scanner returns spans of bytes that should pass
        // through plus side-effects (clipboard sets) to perform.
        data = HandleOsc52(data);

        // ESC[3J = "erase saved lines" (xterm extension, terminfo E3).
        // Linux `clear` and Windows Terminal's "clear buffer" emit this
        // after the visible-area erase. VtNetCore 1.0.9 doesn't handle
        // it, and our scrollback list lives outside VtNetCore anyway —
        // so detect it ourselves and drop _scrollback. The sequence is
        // still passed through (harmless if VtNetCore can't parse it;
        // future versions may).
        HandleEraseScrollback(data);

        int len = data.Length;
        int i = 0;
        while (i < len)
        {
            int markerIdx = FindCursorVisibilityMarker(data, i, out bool isHide);
            int sliceEnd = markerIdx < 0 ? len : markerIdx;

            if (sliceEnd > i)
            {
                if (i == 0 && sliceEnd == len)
                {
                    // Common fast path: no marker in the chunk, push the
                    // original array without copying.
                    _parser.Push(data);
                }
                else
                {
                    var slice = new byte[sliceEnd - i];
                    Buffer.BlockCopy(data, i, slice, 0, slice.Length);
                    _parser.Push(slice);
                }
            }

            if (markerIdx < 0) break;

            // Decide whether to inject DECSET / DECRESET 1049. We only
            // do it on transitions — if the buffer state already matches
            // the marker's implication, the program is just toggling
            // cursor visibility within an already-known mode.
            bool currentlyAlt = IsInAlternateBuffer();
            if (isHide && !currentlyAlt)
            {
                _parser.Push(DecSet1049Enter);
                // The alt buffer is allocated lazily by VtNetCore the
                // first time DECSET 1049 fires, and it's created at the
                // controller's CONSTRUCTOR dimensions (24×80) — not the
                // current viewport size. Force a resize right after the
                // switch so top's frame bytes (which follow immediately
                // in this same chunk) land in a buffer of the correct
                // height. Without this, top only fills the top ~24 rows
                // regardless of how tall the viewport actually is.
                _vtc.ResizeView(_cols, _rows);
                SshLog.Info($"Pseudo-alt ENTER: synthesized DECSET 1049 from ESC[?25l; resized alt to {_cols}×{_rows}");
            }
            else if (!isHide && currentlyAlt)
            {
                _parser.Push(DecSet1049Exit);
                // On exit, ensure the now-active normal buffer is at
                // the right size too (defensive: keeps both buffers in
                // sync regardless of which one was last resized).
                _vtc.ResizeView(_cols, _rows);
                SshLog.Info($"Pseudo-alt EXIT: synthesized DECRESET 1049 from ESC[?25h; resized normal to {_cols}×{_rows}");
            }

            // Push the original hide/show bytes so VtNetCore still
            // updates its cursor-visible flag — the program's intent
            // (hide / show cursor) is preserved on top of the buffer flip.
            const int markerLen = 6;
            var markerBytes = new byte[markerLen];
            Buffer.BlockCopy(data, markerIdx, markerBytes, 0, markerLen);
            _parser.Push(markerBytes);
            i = markerIdx + markerLen;
        }
    }

    private static readonly byte[] DecSet1049Enter = { 0x1B, 0x5B, 0x3F, 0x31, 0x30, 0x34, 0x39, 0x68 };
    private static readonly byte[] DecSet1049Exit  = { 0x1B, 0x5B, 0x3F, 0x31, 0x30, 0x34, 0x39, 0x6C };

    /// <summary>Scan for <c>ESC[3J</c> (CSI 3 J = "erase saved lines"
    /// / "erase scrollback", xterm extension). If found, drop our
    /// scrollback buffer so <c>clear</c> actually deletes history
    /// instead of just clearing the visible area.</summary>
    private void HandleEraseScrollback(byte[] data)
    {
        int end = data.Length - 3;
        for (int i = 0; i <= end; i++)
        {
            if (data[i] == 0x1B && data[i + 1] == 0x5B
             && data[i + 2] == 0x33 && data[i + 3] == 0x4A)
            {
                _scrollback.Clear();
                _lastSnapshot = null;
                _scrollOffset = 0;
                SshLog.Info("ESC[3J detected — scrollback cleared");
                return;
            }
        }
    }

    /// <summary>Scan <paramref name="data"/> for OSC 52 clipboard-set
    /// sequences (<c>ESC ] 52 ; &lt;params&gt; ; &lt;base64&gt; ST</c>
    /// where ST is BEL <c>0x07</c> or <c>ESC \</c>). For each one
    /// found, base64-decode the payload and push it to the Windows
    /// clipboard. Return a new byte array with the OSC 52 sequences
    /// removed so VtNetCore's parser doesn't see them — VtNetCore 1.0.9
    /// has no handler and would treat the bytes as text.
    ///
    /// Query form (<c>ESC]52;c;?ST</c>) is dropped silently; we don't
    /// publish clipboard contents back over the channel for security
    /// (would let a malicious server exfiltrate whatever's on the host
    /// clipboard the moment the user opens an SSH session).</summary>
    private byte[] HandleOsc52(byte[] data)
    {
        // Fast path: no ESC byte means no OSC anything.
        int firstEsc = Array.IndexOf(data, (byte)0x1B);
        if (firstEsc < 0) return data;

        List<(int start, int endExclusive, string? payload)>? hits = null;
        int i = firstEsc;
        while (i + 4 < data.Length)
        {
            // OSC prefix: ESC ] 5 2 ;  → bytes 0x1B 0x5D 0x35 0x32 0x3B
            if (data[i] == 0x1B && data[i + 1] == 0x5D
             && data[i + 2] == 0x35 && data[i + 3] == 0x32 && data[i + 4] == 0x3B)
            {
                int paramStart = i + 5;
                // Find the terminator: BEL (0x07) or ESC \ (0x1B 0x5C).
                int term = paramStart;
                int termLen = 0;
                while (term < data.Length)
                {
                    if (data[term] == 0x07) { termLen = 1; break; }
                    if (data[term] == 0x1B && term + 1 < data.Length && data[term + 1] == 0x5C)
                    { termLen = 2; break; }
                    term++;
                }
                if (termLen == 0) break; // truncated; let it pass through

                // Body between paramStart and term has the form
                // "<targets>;<base64-or-?>".
                int semi = Array.IndexOf(data, (byte)';', paramStart, term - paramStart);
                string? payload = null;
                if (semi >= 0 && semi + 1 < term)
                {
                    int pStart = semi + 1;
                    int pLen = term - pStart;
                    if (pLen > 0 && data[pStart] != (byte)'?')
                    {
                        try
                        {
                            var b64 = System.Text.Encoding.ASCII.GetString(data, pStart, pLen);
                            var raw = Convert.FromBase64String(b64);
                            payload = System.Text.Encoding.UTF8.GetString(raw);
                        }
                        catch
                        {
                            // Malformed base64 — ignore, don't crash the
                            // session. The hit is still stripped so the
                            // garbage doesn't reach the parser.
                        }
                    }
                }

                int endExclusive = term + termLen;
                (hits ??= new()).Add((i, endExclusive, payload));
                i = endExclusive;
                continue;
            }
            i++;
        }

        if (hits is null) return data;

        // Apply clipboard sets. Coalesce to a single Clipboard.SetContent
        // even if multiple OSC 52 sequences appeared in one chunk —
        // the last one wins, matching xterm behaviour.
        string? lastPayload = null;
        foreach (var hit in hits)
            if (hit.payload is not null) lastPayload = hit.payload;
        if (lastPayload is not null)
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText(lastPayload);
                Clipboard.SetContent(dp);
                SshLog.Info($"OSC 52 clipboard set: {lastPayload.Length} chars");
            }
            catch (Exception ex)
            {
                SshLog.Warn($"OSC 52 clipboard set failed: {ex.Message}");
            }
        }

        // Build a new byte array with the OSC 52 spans removed.
        int totalRemoved = 0;
        foreach (var hit in hits) totalRemoved += hit.endExclusive - hit.start;
        var output = new byte[data.Length - totalRemoved];
        int srcIdx = 0, dstIdx = 0;
        foreach (var hit in hits)
        {
            int keepLen = hit.start - srcIdx;
            if (keepLen > 0)
            {
                Buffer.BlockCopy(data, srcIdx, output, dstIdx, keepLen);
                dstIdx += keepLen;
            }
            srcIdx = hit.endExclusive;
        }
        if (srcIdx < data.Length)
        {
            Buffer.BlockCopy(data, srcIdx, output, dstIdx, data.Length - srcIdx);
        }
        return output;
    }

    /// <summary>Find the next <c>ESC[?25l</c> (hide) or <c>ESC[?25h</c>
    /// (show) sequence in <paramref name="data"/> starting at
    /// <paramref name="startIdx"/>. Returns the index of the ESC byte,
    /// or -1 if none. The five-byte prefix <c>ESC[?25</c> is specific
    /// enough that false positives in non-control text are not a
    /// concern (printable text can't contain an ESC).</summary>
    private static int FindCursorVisibilityMarker(byte[] data, int startIdx, out bool isHide)
    {
        isHide = false;
        int end = data.Length - 5;
        for (int i = startIdx; i <= end; i++)
        {
            if (data[i]     == 0x1B && data[i + 1] == 0x5B
             && data[i + 2] == 0x3F && data[i + 3] == 0x32
             && data[i + 4] == 0x35)
            {
                byte last = data[i + 5];
                if (last == 0x6C) { isHide = true;  return i; }
                if (last == 0x68) { isHide = false; return i; }
            }
        }
        return -1;
    }

    /// <summary>Snapshot every visible row's text content. Cells past
    /// the line's actual width come back as null chars; we substitute
    /// space and trim trailing whitespace per row so a line that only
    /// uses the first 20 columns doesn't compare unequal to "the same
    /// line, padded".</summary>
    private List<string> SnapshotVisibleRows()
    {
        var result = new List<string>(_rows);
        var vp = _vtc.ViewPort;
        for (int row = 0; row < _rows; row++)
        {
            VtNetCore.VirtualTerminal.Model.TerminalLine? line;
            try { line = vp.GetVisibleLine(row); }
            catch (ArgumentOutOfRangeException) { line = null; }
            if (line is null) { result.Add(string.Empty); continue; }
            var sb = new System.Text.StringBuilder(line.Count);
            for (int col = 0; col < line.Count; col++)
            {
                var c = line[col].Char;
                sb.Append(c == '\0' ? ' ' : c);
            }
            result.Add(sb.ToString().TrimEnd());
        }
        return result;
    }

    /// <summary>Compare pre/post visible-area snapshots to find how
    /// many rows scrolled up (i.e. became invisible above the top).
    /// If <c>post[i] == pre[i + shift]</c> for a contiguous range,
    /// then <c>shift</c> rows scrolled off — push them onto the
    /// scrollback buffer in order. Bounded to <see cref="HistoryRows"/>.
    /// Same-content edits without scroll produce shift=0 and nothing
    /// gets captured.</summary>
    private void CaptureScrollOff(List<string> pre, List<string> post)
    {
        if (pre.Count != post.Count || pre.Count == 0) return;
        int rows = pre.Count;

        // Try shift = 1, 2, ..., rows-1. Stop at the first match —
        // that's the smallest shift that explains the post state.
        for (int shift = 1; shift < rows; shift++)
        {
            bool match = true;
            for (int i = 0; i < rows - shift; i++)
            {
                if (pre[i + shift] != post[i]) { match = false; break; }
            }
            if (!match) continue;

            for (int i = 0; i < shift; i++)
            {
                _scrollback.Add(pre[i]);
                if (_scrollback.Count > HistoryRows) _scrollback.RemoveAt(0);
            }
            return;
        }
    }

    // ── Render ────────────────────────────────────────────────────────────────

    private static readonly Color DefaultFg     = Color.FromArgb(0xFF, 0xE8, 0xE8, 0xF0);
    private static readonly Color TerminalBg    = Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);
    private static readonly Color SelectionFill = Color.FromArgb(0x80, 0x4D, 0xA6, 0xFF);

    /// <summary>Thin shim: sync the XAML scrollbar thumb (sibling
    /// element, lives outside the Win2D surface), then invalidate the
    /// CanvasControl so it raises Draw on the next frame. Win2D
    /// coalesces multiple Invalidate calls between frames into one,
    /// so callers can fire this freely without backing off.</summary>
    private void Render()
    {
        SyncScrollThumb();
        TermCanvas.Invalidate();
    }

    /// <summary>Win2D Draw event handler — paints the entire terminal
    /// surface (selection, glyphs, cursor) in one drawing session.
    /// Runs on the UI thread; reads from <see cref="_vtc"/> and
    /// <see cref="_scrollback"/> which are also UI-thread-only.
    ///
    /// Performance: we group adjacent cells with identical fg+bg into
    /// a single <c>FillRectangle</c> + <c>DrawText</c> pair. For a typical
    /// terminal frame (mostly default-colour text), that's ~1–3 draw
    /// calls per row instead of one element per glyph. The whole 50×220
    /// viewport paints in well under a millisecond on a modern GPU.</summary>
    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        EnsureTextFormat();
        if (_textFormat is null) return;

        // Selection highlight — drawn first so glyphs paint on top.
        if (_hasSelection || _isSelecting)
        {
            var (s, e) = NormalisedSelection();
            for (int row = s.Row; row <= e.Row; row++)
            {
                int startCol = (row == s.Row) ? s.Col : 0;
                int endCol   = (row == e.Row) ? e.Col : _cols;
                if (endCol <= startCol) continue;
                ds.FillRectangle(
                    startCol * _charWidth,
                    row      * _charHeight,
                    (endCol - startCol) * _charWidth,
                    _charHeight,
                    SelectionFill);
            }
        }

        var vp = _vtc.ViewPort;

        for (int row = 0; row < _rows; row++)
        {
            int absRow = row + _scrollback.Count - _scrollOffset;
            if (absRow < 0) continue;

            if (absRow < _scrollback.Count)
            {
                // Scrollback: stored as plain strings, render in
                // default foreground in one DrawText per row.
                var text = _scrollback[absRow];
                if (text.Length > 0)
                {
                    ds.DrawText(text, 0, row * _charHeight, DefaultFg, _textFormat);
                }
                continue;
            }

            int liveRow = absRow - _scrollback.Count;
            VtNetCore.VirtualTerminal.Model.TerminalLine? line;
            try { line = vp.GetVisibleLine(liveRow); }
            catch (ArgumentOutOfRangeException) { line = null; }
            if (line is null) continue;

            DrawLiveRow(ds, line, row);
        }

        // Cursor. Suppressed during a selection drag (would strobe
        // under the highlight), while scrolled back (the cursor's true
        // position is at the live bottom; drawing it on a historical
        // view would be misleading), and when DECTCEM has hidden it
        // (programs like top/vim toggle this).
        if (!_isSelecting && _scrollOffset == 0 && _vtc.CursorState.ShowCursor)
        {
            var cur = vp.CursorPosition;
            float cx = cur.Column * _charWidth;
            float cy = cur.Row    * _charHeight;
            // DECSCUSR: cursor shape from VtNetCore (block / underline /
            // bar). VtNetCore parses the `ESC[<n> q` sequence into
            // CursorShape; we just respect it on each render. No blink
            // — most users find it distracting and we don't run a per-
            // frame timer.
            switch (_vtc.CursorState.CursorShape)
            {
                case ECursorShape.Underline:
                    ds.FillRectangle(cx, cy + _charHeight - 2f, _charWidth, 2f, CursorFill);
                    break;
                case ECursorShape.Bar:
                    ds.FillRectangle(cx, cy, 2f, _charHeight, CursorFill);
                    break;
                default: // Block
                    ds.FillRectangle(cx, cy, _charWidth, _charHeight, CursorFill);
                    break;
            }
        }
    }

    private static readonly Color CursorFill = Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF);

    /// <summary>Paint one live VtNetCore row, grouping adjacent cells
    /// with the same fg+bg into a single draw-text + fill-rect pair.
    /// VtNetCore reports per-cell attributes; long runs of the same
    /// colour (the common case) collapse to one DrawText call each.</summary>
    private void DrawLiveRow(
        CanvasDrawingSession ds,
        VtNetCore.VirtualTerminal.Model.TerminalLine line,
        int row)
    {
        int spanStartCol = -1;
        Color spanFg = DefaultFg;
        uint  spanBgArgb = 0;
        bool  spanBold = false;
        bool  spanUnderline = false;
        _rowBuf.Clear();

        int colCount = Math.Min(line.Count, _cols);
        for (int col = 0; col < colCount; col++)
        {
            var cell = line[col];
            // Treat '\0' as a space inside spans so a run of un-set
            // cells in the middle of a coloured region doesn't force
            // a flush; the trailing TrimEnd in DrawText handles them.
            // Real spaces still get rendered when their bg differs.
            char c = cell.Char == '\0' ? ' ' : cell.Char;

            // SGR attributes. VtNetCore exposes Bright (bold), Underscore,
            // Reverse, Blink, Hidden — no Italic in 1.0.9. Reverse swaps
            // fg / bg cell-locally; do that before coalescing so spans
            // group by the actually-rendered colours.
            bool bold      = cell.Attributes.Bright;
            bool underline = cell.Attributes.Underscore;

            // Resolve foreground: prefer the RGB triple (set by SGR 38;5;N
            // or 38;2;R;G;B), fall back to the named-ANSI enum + bright
            // flag (set by SGR 30-37). VtNetCore 1.0.9 doesn't auto-
            // populate ForegroundRgb when only the enum was set, so
            // reading RGB alone misses every ANSI-named cell — that was
            // the htop "no colors" bug.
            uint  fgArgb = cell.Attributes.ForegroundRgb?.ARGB ?? 0u;
            Color fg = (fgArgb >> 24) != 0
                ? ArgbToColor(fgArgb)
                : AnsiColorFg(cell.Attributes.ForegroundColor, bold);

            uint  bgArgb = cell.Attributes.BackgroundRgb?.ARGB ?? 0u;
            // For bg, use the RGB if set, else the named enum if the
            // cell explicitly painted a bg (i.e. enum is not the default
            // Black AND no rgb). VtNetCore reports Black as the default
            // bg even when nothing has been painted, so we can't blindly
            // honour it — that would paint every empty cell solid black
            // over our terminal background. Heuristic: only emit a bg
            // fill when SOME attribute on the cell looks "really set"
            // (rgb provided, or reverse, or non-black bg enum).
            if (cell.Attributes.Reverse)
            {
                // Swap fg and bg. Reversed text on a default-bg cell
                // should still be visible — use the terminal background
                // as the swap source.
                Color origBg = (bgArgb >> 24) != 0
                    ? ArgbToColor(bgArgb)
                    : AnsiColorBg(cell.Attributes.BackgroundColor, defaultIsTransparent: true) ?? TerminalBg;
                Color reversedBg = fg;
                fg = origBg;
                bgArgb = ColorToArgb(reversedBg);
            }
            else if ((bgArgb >> 24) == 0)
            {
                // No RGB bg set. Map the enum if it represents a real
                // user-set colour; null means "leave transparent".
                var enumBg = AnsiColorBg(cell.Attributes.BackgroundColor, defaultIsTransparent: true);
                if (enumBg is Color enumBgVal) bgArgb = ColorToArgb(enumBgVal);
            }

            if (spanStartCol < 0)
            {
                spanStartCol  = col;
                spanFg        = fg;
                spanBgArgb    = bgArgb;
                spanBold      = bold;
                spanUnderline = underline;
                _rowBuf.Append(c);
            }
            else if (fg == spanFg && bgArgb == spanBgArgb && bold == spanBold && underline == spanUnderline)
            {
                _rowBuf.Append(c);
            }
            else
            {
                FlushSpan(ds, row, spanStartCol, _rowBuf.ToString(), spanFg, spanBgArgb, spanBold, spanUnderline);
                _rowBuf.Clear();
                spanStartCol  = col;
                spanFg        = fg;
                spanBgArgb    = bgArgb;
                spanBold      = bold;
                spanUnderline = underline;
                _rowBuf.Append(c);
            }
        }
        if (spanStartCol >= 0 && _rowBuf.Length > 0)
            FlushSpan(ds, row, spanStartCol, _rowBuf.ToString(), spanFg, spanBgArgb, spanBold, spanUnderline);
    }

    /// <summary>Emit one span: optional bg fill, glyph run with the
    /// foreground colour and weight (bold uses a separate cached text
    /// format), optional underline line at the row's baseline + 1px.
    /// Whitespace-only spans skip the DrawText pass.</summary>
    private void FlushSpan(
        CanvasDrawingSession ds,
        int row, int colStart, string text,
        Color fg, uint bgArgb, bool bold, bool underline)
    {
        float x = colStart * _charWidth;
        float y = row      * _charHeight;
        if ((bgArgb >> 24) != 0)
        {
            ds.FillRectangle(
                x, y,
                text.Length * _charWidth, _charHeight,
                ArgbToColor(bgArgb));
        }
        bool hasGlyph = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ') { hasGlyph = true; break; }
        }
        if (hasGlyph)
        {
            var format = bold ? (_textFormatBold ?? _textFormat) : _textFormat;
            ds.DrawText(text, x, y, fg, format);
        }
        if (underline)
        {
            // 1-DIP thick line at the bottom of the cell. Underlines
            // sit at the descender area so they don't collide with
            // glyph ink even on bold/Italic faces.
            float uy = y + _charHeight - 1.5f;
            ds.FillRectangle(x, uy, text.Length * _charWidth, 1.0f, fg);
        }
    }

    private static uint ColorToArgb(Color c) =>
        (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);

    /// <summary>Map a VtNetCore named-ANSI foreground to its RGB. Uses
    /// the xterm-classic palette (compatible with what most TUIs were
    /// authored against). When <paramref name="bright"/> is true the
    /// SGR 1 "bold-brightens-foreground" convention applies — the
    /// intensified variant is used. Falling-through default for an
    /// un-set cell (enum still at default White) gives a slightly
    /// brighter near-white that matches DefaultFg closely.</summary>
    private static Color AnsiColorFg(ETerminalColor c, bool bright) => c switch
    {
        ETerminalColor.Black   => bright ? Color.FromArgb(0xFF, 0x7F, 0x7F, 0x7F) : Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        ETerminalColor.Red     => bright ? Color.FromArgb(0xFF, 0xFF, 0x55, 0x55) : Color.FromArgb(0xFF, 0xCD, 0x00, 0x00),
        ETerminalColor.Green   => bright ? Color.FromArgb(0xFF, 0x55, 0xFF, 0x55) : Color.FromArgb(0xFF, 0x00, 0xCD, 0x00),
        ETerminalColor.Yellow  => bright ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0x55) : Color.FromArgb(0xFF, 0xCD, 0xCD, 0x00),
        ETerminalColor.Blue    => bright ? Color.FromArgb(0xFF, 0x5C, 0x5C, 0xFF) : Color.FromArgb(0xFF, 0x00, 0x00, 0xEE),
        ETerminalColor.Magenta => bright ? Color.FromArgb(0xFF, 0xFF, 0x55, 0xFF) : Color.FromArgb(0xFF, 0xCD, 0x00, 0xCD),
        ETerminalColor.Cyan    => bright ? Color.FromArgb(0xFF, 0x55, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x00, 0xCD, 0xCD),
        ETerminalColor.White   => bright ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : DefaultFg,
        _                       => DefaultFg,
    };

    /// <summary>Background palette. <paramref name="defaultIsTransparent"/>
    /// returns null when the value looks like VtNetCore's default
    /// (Black with no other signal), so the renderer can skip the bg
    /// fill instead of painting every untouched cell solid black over
    /// our terminal background.</summary>
    private static Color? AnsiColorBg(ETerminalColor c, bool defaultIsTransparent) => c switch
    {
        ETerminalColor.Black   => defaultIsTransparent ? null : (Color?)Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        ETerminalColor.Red     => Color.FromArgb(0xFF, 0xCD, 0x00, 0x00),
        ETerminalColor.Green   => Color.FromArgb(0xFF, 0x00, 0xCD, 0x00),
        ETerminalColor.Yellow  => Color.FromArgb(0xFF, 0xCD, 0xCD, 0x00),
        ETerminalColor.Blue    => Color.FromArgb(0xFF, 0x00, 0x00, 0xEE),
        ETerminalColor.Magenta => Color.FromArgb(0xFF, 0xCD, 0x00, 0xCD),
        ETerminalColor.Cyan    => Color.FromArgb(0xFF, 0x00, 0xCD, 0xCD),
        ETerminalColor.White   => Color.FromArgb(0xFF, 0xE5, 0xE5, 0xE5),
        _                       => null,
    };

    /// <summary>Construct the CanvasTextFormat lazily on first draw,
    /// then re-measure cell dimensions. This must happen after XAML
    /// has applied FontFamily / FontSize attributes set on the
    /// containing element (e.g. SshSessionView.xaml sets
    /// FontFamily="Cascadia Code, Consolas, Courier New" FontSize="13").</summary>
    private void EnsureTextFormat()
    {
        if (_textFormat is not null) return;

        // FontFamily.Source may be a comma-separated fallback list — that's
        // XAML's syntax for "try these in order". DirectWrite (via
        // CanvasTextFormat) treats the whole string as a single family
        // name; if it doesn't resolve, DWrite silently substitutes the
        // system default font (Segoe UI on most installs — proportional!).
        // For a terminal that would catastrophically misalign cells
        // against rendered text. Split on comma and take the first entry;
        // if it's not installed DWrite still falls back to a system font,
        // but at least we tried the user's preferred face first.
        var familySource = FontFamily?.Source ?? "Cascadia Mono";
        var family = familySource.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(family)) family = "Cascadia Mono";

        _textFormat = new CanvasTextFormat
        {
            FontFamily        = family,
            FontSize          = (float)FontSize,
            WordWrapping      = CanvasWordWrapping.NoWrap,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
        };
        // Separate bold format. A real monospace font's bold weight has
        // the SAME advance width as regular, so we don't re-measure
        // (it's the contract of monospace). If a non-monospace font
        // slipped through fallback, bold-on-some-runs would still render
        // mostly correctly because we coalesce by attribute and each
        // span draws at its own start x.
        _textFormatBold = new CanvasTextFormat
        {
            FontFamily        = family,
            FontSize          = (float)FontSize,
            FontWeight        = Microsoft.UI.Text.FontWeights.Bold,
            WordWrapping      = CanvasWordWrapping.NoWrap,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
        };
        MeasureCellMetrics();
        // The cell grid may have been guessed using the heuristic
        // metrics from the constructor — re-run sizing now that we
        // have accurate measurements.
        ResizeFromActual();
    }

    /// <summary>Measure the rendered cell box from the current text
    /// format. The width is the per-character *advance* DirectWrite
    /// will use when laying out a run of glyphs — we can't get this
    /// from a single character (LayoutBounds.Width is the ink box,
    /// not the advance; for "M" the two differ by enough that the
    /// cursor drifts ~1 column per ~50 typed characters). Measure
    /// a long monospaced run and divide; the mantissa is a clean
    /// float.
    ///
    /// Height comes from LineMetrics[0].Height — that's the full
    /// line box (ascent + descent + gap), matching DirectWrite's
    /// inter-line spacing.</summary>
    private void MeasureCellMetrics()
    {
        if (_textFormat is null) return;
        var device = CanvasDevice.GetSharedDevice();
        // 100 Ms minimises per-character measurement noise from
        // bearings/kerning. Monospace fonts have constant advance, so
        // total width / count is the exact advance.
        const string SampleRun = "MMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMM" +
                                 "MMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMMM";
        using var layout = new CanvasTextLayout(device, SampleRun, _textFormat, 100000f, 1000f);
        var lm = layout.LineMetrics;
        _charWidth  = (float)(layout.LayoutBounds.Width / SampleRun.Length);
        _charHeight = lm.Length > 0 ? lm[0].Height : (float)layout.LayoutBounds.Height;
        if (_charWidth  <= 0) _charWidth  = (float)FontSize * 0.6f;
        if (_charHeight <= 0) _charHeight = (float)FontSize * 1.4f;
        SshLog.Info($"Cell metrics measured: family={_textFormat.FontFamily} size={FontSize} charW={_charWidth:F3} charH={_charHeight:F3} layoutBoundsH={layout.LayoutBounds.Height:F3} lineMetricCount={lm.Length}");
    }

    /// <summary>Recompute the cell grid (cols, rows) from the
    /// CanvasControl's actual layout size and tell VtNetCore about it.
    /// Called after metrics change or after the control resizes.</summary>
    private void ResizeFromActual()
    {
        double w = TermCanvas.ActualWidth;
        double h = TermCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        int newCols = Math.Max(10, (int)(w / _charWidth));
        int newRows = Math.Max(4,  (int)(h / _charHeight));
        if (newCols == _cols && newRows == _rows) return;
        SshLog.Info($"Resize cell grid: cols {_cols}→{newCols} rows {_rows}→{newRows} (viewportW={w:F1} viewportH={h:F1} charW={_charWidth:F3} charH={_charHeight:F3})");
        _cols = newCols;
        _rows = newRows;
        _vtc.ResizeView(_cols, _rows);
        TermCanvas.Invalidate();
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private (int Row, int Col) PointToCell(Windows.Foundation.Point p)
    {
        int col = Math.Clamp((int)(p.X / _charWidth),  0, _cols);
        int row = Math.Clamp((int)(p.Y / _charHeight), 0, _rows - 1);
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

    // ── Scrollback ────────────────────────────────────────────────────

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // In alt-buffer mode the wheel forwards to the remote app
        // (top/vim/less use it for their own scrolling/paging via
        // mouse-protocol mode-1000 etc). For now we just drop wheel
        // input in alt — sending it as ESC[A / ESC[B would be safer
        // than leaking it into the shell, but that's out of scope here.
        if (_inAltBuffer) { e.Handled = true; return; }

        var p = e.GetCurrentPoint(this);
        int delta = p.Properties.MouseWheelDelta; // 120 per notch
        // Wheel-up (positive delta) increases _scrollOffset → look
        // further back in history → thumb travels up. 3 lines per notch
        // matches Windows Terminal / xterm defaults.
        int lineDelta = delta / 40;
        ScrollByLines(lineDelta);
        e.Handled = true;
    }

    /// <summary>Adjust the scroll offset by <paramref name="lines"/>
    /// (positive = scroll up into history, negative = back toward live).
    /// Clamps to [0, _scrollback.Count] — our captured history is the
    /// authoritative bound (VtNetCore's TopRow stays at 0 in this build,
    /// which is why we maintain the buffer ourselves).</summary>
    private void ScrollByLines(int lines)
    {
        var maxBack = _scrollback.Count;
        var newOffset = Math.Clamp(_scrollOffset + lines, 0, maxBack);
        if (newOffset == _scrollOffset) return;
        _scrollOffset = newOffset;
        Render();
    }

    /// <summary>Snap back to live output. Called from any keystroke
    /// that produces input — matches PuTTY / xterm muscle memory:
    /// scrolling back is read-only, the next keypress brings you home.</summary>
    private void SnapToLive()
    {
        if (_scrollOffset == 0) return;
        _scrollOffset = 0;
        Render();
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Right-click opens a context menu with Copy / Paste /
        // Select all / Clear scrollback. Items grey out when not
        // applicable (Copy without selection, Paste without text on
        // the clipboard).
        e.Handled = true;

        var menu = new MenuFlyout();

        var copy = new MenuFlyoutItem
        {
            Text      = "Copy",
            IsEnabled = _hasSelection,
            Icon      = new SymbolIcon(Symbol.Copy),
        };
        copy.Click += (_, _) =>
        {
            CopySelectionToClipboard();
            ClearSelection();
        };
        menu.Items.Add(copy);

        var paste = new MenuFlyoutItem
        {
            Text      = "Paste",
            IsEnabled = ClipboardHasText(),
            Icon      = new SymbolIcon(Symbol.Paste),
        };
        paste.Click += async (_, _) => await PasteFromClipboardAsync();
        menu.Items.Add(paste);

        menu.Items.Add(new MenuFlyoutSeparator());

        var selectAll = new MenuFlyoutItem
        {
            Text = "Select all visible",
            Icon = new SymbolIcon(Symbol.SelectAll),
        };
        selectAll.Click += (_, _) =>
        {
            _selAnchor   = (0, 0);
            _selFocus    = (_rows - 1, _cols);
            _hasSelection = true;
            _isSelecting  = false;
            Render();
        };
        menu.Items.Add(selectAll);

        var clear = new MenuFlyoutItem { Text = "Clear scrollback" };
        clear.Click += (_, _) =>
        {
            // VtNetCore doesn't expose a "drop history" API; the
            // simplest reset is to flip MaximumHistoryLines to 0
            // (drops all retained rows) and back. The visible
            // viewport is unaffected.
            var keep = _vtc.MaximumHistoryLines;
            _vtc.MaximumHistoryLines = 0;
            _vtc.MaximumHistoryLines = keep;
            _scrollOffset = 0;
            Render();
        };
        menu.Items.Add(clear);

        menu.ShowAt(this, e.GetPosition(this));
    }

    private static bool ClipboardHasText()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content?.Contains(StandardDataFormats.Text) ?? false;
        }
        catch { return false; }
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

        // Shift + PgUp/PgDn/Home/End — scrollback navigation.
        // Without Shift these keys are sent to the remote shell as
        // their VT escape sequences (existing behavior). With Shift
        // they drive the scrollback buffer instead, matching xterm /
        // Windows Terminal. Suppressed in alt-buffer mode — the
        // scrollback isn't accessible there.
        if (shift && (e.Key == VirtualKey.PageUp || e.Key == VirtualKey.PageDown ||
                      e.Key == VirtualKey.Home   || e.Key == VirtualKey.End))
        {
            if (_inAltBuffer) { e.Handled = true; return; }
            switch (e.Key)
            {
                case VirtualKey.PageUp:   ScrollByLines(  _rows - 1 ); break;
                case VirtualKey.PageDown: ScrollByLines(-(_rows - 1)); break;
                case VirtualKey.Home:     ScrollByLines(int.MaxValue);  break;
                case VirtualKey.End:      SnapToLive();                 break;
            }
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
            // Any keystroke that produces output dismisses a selection
            // and snaps the view back to the live bottom — matches
            // PuTTY / xterm muscle memory.
            ClearSelection();
            SnapToLive();
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

        SshLog.Debug($"Key char received: '{(char)e.Character}' (0x{(int)e.Character:X2}) altBuf={_inAltBuffer} scrollOff={_scrollOffset}");
        ClearSelection();
        SnapToLive();
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

    /// <summary>The CanvasControl is sized by its Grid column ("*"), so
    /// we don't set explicit Width/Height. SizeChanged fires once layout
    /// assigns it a final size; we re-derive cols/rows from the actual
    /// rendered metrics measured via DirectWrite and tell VtNetCore.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeFromActual();
    }

    // ── Custom scrollbar ──────────────────────────────────────────────

    private bool _scrollDragging;
    private double _scrollDragOffsetWithinThumb;

    private void SyncScrollThumb()
    {
        var trackH = VScrollTrack.ActualHeight;
        if (trackH <= 0) return;

        // Scrollable range = how many lines we can drag back into.
        // Bounded by what we've actually captured (matches the
        // always-draggable bar's UX without showing fake range past
        // the real history).
        int maxBack = _scrollback.Count;
        const double minThumb = 24;
        // Thumb size scales with what fraction of total content is
        // currently visible: live rows / (live + history).
        double thumbH = Math.Max(minThumb, trackH * _rows / Math.Max(1, maxBack + _rows));
        thumbH = Math.Min(thumbH, trackH);

        // Position: scrollOffset 0 → thumb at bottom (track-thumb).
        // scrollOffset maxBack → thumb at top (0). When maxBack == 0
        // (no scrollback yet) we pin to bottom.
        double frac = maxBack > 0 ? 1.0 - (double)_scrollOffset / maxBack : 0.0;
        frac = Math.Clamp(frac, 0, 1);
        double y = (trackH - thumbH) * frac;

        VScrollThumb.Height = thumbH;
        Canvas.SetTop(VScrollThumb, y);
    }

    private void OnScrollTrackPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // No scrollback while top/vim/less is up; leave the bar inert.
        if (_inAltBuffer) { e.Handled = true; return; }

        var pt = e.GetCurrentPoint(VScrollTrack).Position;
        var thumbY = Canvas.GetTop(VScrollThumb);
        if (double.IsNaN(thumbY)) thumbY = 0;
        var thumbH = VScrollThumb.Height;

        if (pt.Y >= thumbY && pt.Y <= thumbY + thumbH)
        {
            // Drag start — record offset within thumb so the thumb
            // doesn't jump to the cursor on first move.
            _scrollDragging = true;
            _scrollDragOffsetWithinThumb = pt.Y - thumbY;
        }
        else
        {
            // Click on the track but outside the thumb — page up/down.
            int direction = pt.Y < thumbY ? +1 : -1;
            ScrollByLines(direction * (_rows - 1));
            return;
        }
        VScrollTrack.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnScrollTrackPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrollDragging) return;
        var pt = e.GetCurrentPoint(VScrollTrack).Position;
        var trackH = VScrollTrack.ActualHeight;
        var thumbH = VScrollThumb.Height;
        var max = trackH - thumbH;
        if (max <= 0) return;
        double newY = Math.Clamp(pt.Y - _scrollDragOffsetWithinThumb, 0, max);
        double frac = 1.0 - (newY / max);
        // Map drag fraction onto our captured scrollback range, not
        // HistoryRows — otherwise dragging a half-empty bar still jumps
        // to "5000 rows back" and Render asks for nonexistent rows.
        int maxBack = _scrollback.Count;
        _scrollOffset = Math.Clamp((int)Math.Round(frac * maxBack), 0, maxBack);
        Render();
    }

    private void OnScrollTrackPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrollDragging) return;
        _scrollDragging = false;
        VScrollTrack.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnScrollTrackPointerLost(object sender, PointerRoutedEventArgs e)
    {
        _scrollDragging = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Color ArgbToColor(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >>  8) & 0xFF),
        (byte)( argb        & 0xFF));
}
