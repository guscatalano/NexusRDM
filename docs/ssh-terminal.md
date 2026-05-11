# SSH Terminal Pipeline

How the embedded SSH terminal is wired today — from raw bytes off the network to glyphs on a swap chain — plus known gaps against the relevant specs (RFC 4254, ECMA-48, xterm ctlseqs, terminfo).

## Data flow

```
┌───────────────────────────┐
│ SSH.NET ShellStream       │  ← src/NexusRDM.Core/Protocols/SshSession.cs
│ blocking sync I/O,        │     ReadLoopAsync wraps Read() in Task.Run
│ bounced via Task.Run      │     SendAsync   wraps Write() in Task.Run
└────────────┬──────────────┘
             │ DataReceived event (threadpool thread)
             ▼
┌───────────────────────────┐
│ SshSessionView            │  ← src/NexusRDM/Views/SshSessionView.xaml.cs
│ DispatcherQueue.TryEnqueue│     Marshals to UI thread; routes
│ Terminal.Feed(data)       │     UserInput → ViewModel.SendInputAsync
└────────────┬──────────────┘
             │ UI thread
             ▼
┌───────────────────────────────────────┐
│ TerminalControl.Feed                  │  ← src/NexusRDM/Controls/TerminalControl.xaml.cs:169
│  ├─ snapshot pre (normal buffer only) │
│  ├─ PushWithPseudoAltDetection(data)  │  scans bytes; on ESC[?25l/h
│  │   ├─ slice + parser.Push           │  injects DECSET/DECRESET 1049
│  │   └─ injected DECSET 1049 +        │  to trigger VtNetCore's real
│  │       _vtc.ResizeView              │  alt-buffer machinery
│  ├─ check IsInAlternateBuffer()       │
│  ├─ if flipped: reset snapshot, snap  │
│  │   to live, log transition          │
│  ├─ else (normal): snapshot post,     │
│  │   CaptureScrollOff(pre, post)      │
│  └─ Render() = SyncScrollThumb +      │
│                CanvasControl.Invalidate
└────────────┬──────────────────────────┘
             │
             ▼
┌───────────────────────────────────────┐
│ Win2D CanvasControl                   │  ← src/NexusRDM/Controls/TerminalControl.xaml
│ Draw event → OnDraw(args)             │     :413
│  ├─ EnsureTextFormat (lazy, once)     │     measure advance from 100×"M"
│  ├─ Selection rects (FillRectangle)   │
│  ├─ For each visible row:             │
│  │   scrollback string → DrawText,    │
│  │   live VtNetCore line → coalesce   │
│  │   into fg/bg colour spans:         │
│  │     FillRectangle + DrawText       │
│  └─ Cursor (FillRectangle)            │
└───────────────────────────────────────┘
```

## What each piece is doing

### Render: Win2D, not XAML

`TermCanvas` is a `Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl` (Direct2D + DirectWrite under the hood). `OnDraw` walks the cell grid and emits one `FillRectangle` + one `DrawText` per *colour span* — adjacent cells sharing fg+bg collapse into a single glyph run. Typical default-colour terminal output is ~1–3 draw calls per row, total ~50 per frame.

This replaced the previous `TextBlock`-per-cell renderer, which allocated ~11,000 XAML elements per frame and stalled the UI thread during heavy output.

Cell metrics (`_charWidth`, `_charHeight`) are measured by laying out a 100-character `"MMM…M"` and dividing — that gives DirectWrite's true *advance* width, not the ink-box width of a single glyph (the two differ enough that single-glyph measurement caused 20-column cursor drift over a line of input).

`FontFamily` is split on comma (`"Cascadia Code, Consolas, Courier New"` → `"Cascadia Code"`) because XAML's fallback-list syntax is interpreted by WinUI's text infra, not DirectWrite — passing the whole comma-list to `CanvasTextFormat` would have DirectWrite fall back to Segoe UI (proportional, catastrophic for a monospace grid).

### VT byte handling: VtNetCore + pseudo-alt heuristic

We use [VtNetCore](https://github.com/darrenstarr/VtNetCore) 1.0.9 as the VT parser / cell store. It handles CSI, SGR, cursor positioning, scroll regions, alt-buffer save/restore, etc.

The novel piece is `PushWithPseudoAltDetection` (TerminalControl.xaml.cs:235): we pre-scan incoming bytes for `ESC[?25l` (hide cursor) and `ESC[?25h` (show cursor). When we see hide-cursor and the current buffer is normal, we *synthesize* a `DECSET 1049` byte sequence and push it through the parser *before* the program's next bytes arrive. VtNetCore's standard alt-buffer machinery kicks in: save normal-buffer state, switch to the alternate buffer, clear. On the corresponding `ESC[?25h`, we synthesize `DECRESET 1049` and the normal buffer (with pre-program shell history) is restored.

Right after each synthesized flip, we also call `_vtc.ResizeView(_cols, _rows)`. VtNetCore allocates the alternate buffer lazily at its *constructor* dimensions (24×80), not the current viewport — without the explicit resize, `top` paints into a 24-row buffer regardless of the actual window height.

Why we need this heuristic: many servers ship a downgraded `TERM` (e.g. `xterm` overridden in `/etc/profile`) or a procps `top` built without `smcup` support. The program never emits the formal `\E[?1049h\E[22;0;0t` smcup sequence, but it *does* emit `ESC[?25l` on entry and `ESC[?25h` on exit. We treat cursor-visibility-toggle as a fullscreen-program signal and synthesize the missing buffer flip. The visible behaviour matches a proper smcup terminal: `top` runs in an isolated buffer, on exit the pre-`top` shell history is restored.

False-positive risk: a non-fullscreen program that briefly hides/shows the cursor (spinners, banners) will flip into and out of alt buffer. The brief content paints into alt, on the matching show-cursor we restore — net effect is the spinner/banner doesn't appear, but the surrounding shell state is preserved. Conservative trade-off.

### PTY size negotiation

`CreateShellStream` (SshSession.cs:116) opens the channel with `xterm-256color` and the SshSession's hardcoded default `_cols=220, _rows=50`. The constructor doesn't know the real viewport size yet.

Immediately after `ConnectAsync` returns successfully (SshSessionView.xaml.cs:170 area), the view calls `ViewModel.ResizeAsync(Terminal.TerminalSize.cols, .rows)` which fires `SendWindowChangeRequest` (via reflection on SSH.NET's internal `IChannelSession`, since the public `ShellStream` API doesn't expose resize in 2024.2). This tells the server-side PTY the real dimensions. Without this, every server-side `ioctl(TIOCGWINSZ)` returns `220×50` and programs lay out for a non-existent 220-column screen — every long line wraps to a second visual row.

Subsequent resizes flow through the existing `Terminal.SizeChanged` handler.

### Scrollback

VtNetCore's history retention in 1.0.9 is unreliable — `vp.TopRow` stays at 0 even with `MaximumHistoryLines = 5000`, and `vp.GetLine` throws on negative indices. We maintain our own scrollback by snapshot-and-diff: snapshot visible rows before parsing each chunk, snapshot after, find the smallest shift where `post[i] == pre[i + shift]` for all `i`, and push the disappearing pre rows into `_scrollback` (capped at 5000).

Captured as plain strings (no per-cell colour) so we don't hold mutable VtNetCore cell references — known follow-up. Live rendering still has full colour. While in alt buffer we skip capture entirely.

### Cursor / scrollback navigation gating

Wheel, drag of the custom scrollbar thumb, and Shift+PgUp/PgDn/Home/End all check `_inAltBuffer` and no-op while in alt — matches xterm behaviour for fullscreen TUIs.

### SshSession robustness

`SendAsync` catches `ObjectDisposedException` / `IOException` *inside* the `Task.Run` lambda, not after the await. Fire-and-forget callers (e.g. fast keystroke loops via `_ = ViewModel.SendInputAsync(data)`) won't observe the faulted Task, and on certain runtimes the exception was escaping the threadpool worker before any awaiter could see it. The inner-catch makes the worker exit cleanly regardless of whether the caller awaits.

### Diagnostic logging

`NexusRDM.Core.Diagnostics.SshLog` is a static action-sink in Core. App.xaml.cs wires `SshLog.Sink = msg => Log.Debug("[ssh] {Msg}", msg)` at startup. Core can use it without taking a dependency on Serilog. Tagged `[ssh]` in `%LocalAppData%\NexusRDM\logs\nexus-<date>.log`.

Currently logs: connect → shell-opened, PTY resize requests + on-wire sends, read chunks (≤256 bytes verbatim with hex preview, larger chunks one-line summary), every send (with hex preview), every key char received, cell metrics on first measure, every cell-grid resize, alt-buffer flips (real + pseudo-synthesized).

## Spec gaps

Surveyed against [RFC 4254](https://www.rfc-editor.org/rfc/rfc4254) (SSH connection protocol — Interactive Sessions), [ECMA-48](https://ecma-international.org/publications-and-standards/standards/ecma-48/), [xterm ctlseqs](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html), and terminfo conventions.

### High-impact

These are observable to the user and break common workflows.

- **Bracketed paste mode not honoured outbound.** Server enables it (`ESC[?2004h` — visible in every shell prompt response in the logs), but `PasteFromClipboardAsync` sends the raw text. Should wrap with `ESC[200~ ... ESC[201~` when VtNetCore reports `BracketedPasteMode=true`. Without it, pasting code into vim/bash interprets each line as a command boundary, runs `auto-indent`, etc. — the canonical "paste breaks indentation" complaint.

- **DECCKM (application cursor keys) not honoured.** When a program (vim, less, fzf) issues `ESC[?1h`, cursor keys should encode as `ESC O A / B / C / D` instead of `ESC [ A / B / C / D`. Our `TranslateSpecialKey` (TerminalControl.xaml.cs:846) always sends the CSI form. Vim arrow keys are effectively broken in insert mode. VtNetCore tracks the mode (`CursorState.ApplicationCursorKeysMode`) — read it and branch.

- **DECKPAM (application keypad mode) not honoured.** Same shape as DECCKM but for the numeric keypad. Affects ncurses TUIs that use numpad navigation.

- **xterm modifier-encoded keys (modifyOtherKeys / CSI u).** Ctrl+Arrow, Shift+Arrow, Alt+Enter, Ctrl+Tab etc. encode as e.g. `ESC[1;5C` (Ctrl+Right) in xterm. We don't emit these; modifier+special-key combos either drop or send the plain key. Breaks tmux pane navigation, vim window jumps, etc.

- **Mouse tracking modes (1000/1002/1003/1005/1006/1015) not implemented.** Programs that request mouse events (vim, tmux, htop, btop) get nothing. We capture mouse for selection but never forward as VT mouse reports. Requires checking `VirtualTerminalController.{CellMotionMouseTracking, X11SendMouseXYOnButton, SgrMouseMode, UseAllMouseTracking}` and encoding `ESC[M…` / `ESC[<…` sequences on pointer events.

- **SSH host key verification.** We don't surface fingerprint prompts on first connect (TOFU). SSH.NET fires `HostKeyReceived` — we let it through. Real MITM exposure on first connection.

### Medium-impact

User-visible but lower-frequency.

- **OSC 0 / 1 / 2 (window title).** `ESC]0;…\x07` is what `bash` and `tmux` send to set the terminal title (often `user@host: pwd`). VtNetCore raises `WindowTitleChanged` (we wire it nowhere). Could update the tab header dynamically — useful when the connection is the user's main work surface.

- **OSC 52 (clipboard set / get).** Programs (vim, tmux) push selections into the system clipboard via `ESC]52;c;<base64>\x07`. We don't honour it. Frequent ask from vim users on remote machines.

- **DECSCUSR (cursor shape).** `ESC[<n> q` lets programs request block / underline / bar cursor with or without blink (vim uses this to indicate insert/normal mode). We draw a fixed semi-transparent block, ignore the parameter.

- **Focus reporting (ESC[?1004h).** Programs that opt-in want `ESC[I` on focus-in, `ESC[O` on focus-out (vim auto-write, neovim filetype refresh). We don't track WinUI focus changes for the terminal.

- **xterm 8-bit / 24-bit colour requests.** VtNetCore handles SGR 38/48 forms (256-colour and truecolour), but we only emit 24-bit foreground/background in our renderer. Bold / italic / underline attributes from `cell.Attributes` are not honoured — flat-weight rendering only.

- **`pty-req` terminal modes.** `CreateShellStream` passes an empty `modes` dict. RFC 4254 §8 lists encoded modes: IUTF8, ECHO, ICANON, ISIG, etc. Servers fall back to sensible defaults, but explicitly negotiating IUTF8=1 prevents the rare server that gates UTF-8 handling on it.

### Low-impact

Mostly fringe protocols.

- **Sixel / Kitty / iTerm2 image protocols.** Not implementing; out of scope for a session manager.

- **DCS / SOS / PM / APC strings.** VtNetCore handles parsing; we don't act on them.

- **Sgr-pixels / character size reporting (`ESC[8t` family).** Some programs query terminal pixel size. We don't respond. Most fall back to character cells.

- **Scrollback colour fidelity.** Scrollback rows render in default foreground only. Would require capturing per-cell attributes instead of strings.

- **Pseudo-alt marker split across read chunks.** `PushWithPseudoAltDetection` scans within a single chunk; if `ESC[?25l` straddles the boundary between two SSH read chunks (e.g. first chunk ends with `ESC[?2`, next starts with `5l`), we miss it. SSH reads are typically large enough that this hasn't been observed in practice; fix would be a 5-byte tail buffer carried across calls.

- **Resize while in alt buffer.** `_vtc.ResizeView` resizes the *active* buffer. If the user resizes the window while `top` is running, the normal buffer stays at the pre-resize size; on `top` exit, the restored shell history renders at the wrong dimensions until the next size change. Fix: ResizeView, flip, ResizeView, flip back — but flipping mid-stream is intrusive.

### Architectural

- **Render coalescing relies on Win2D.** Every `Feed` calls `CanvasControl.Invalidate()`. Win2D collapses multiple invalidations between frames into one Draw. Works in practice; if we ever want explicit frame-rate control (cap at 60fps regardless of throughput) we'd need our own timer.

- **Single-threaded VT processing.** `Feed` runs on the UI thread; the parser is synchronous and operates on `_vtc` which is UI-thread-only. Heavy output (megabytes/second of `cat`) saturates the UI thread. For terminal-grade throughput we'd want a worker thread parser feeding a lock-free row buffer to the UI thread. Not a current bottleneck.

- **Connection-state opacity.** SshSession exposes `IsConnected` and `Disconnected`, but not "in alt-buffer fullscreen mode" / "in bracketed paste" / "current cursor visible". Sometimes we read VtNetCore state directly via reflection (`ActiveBuffer`). A cleaner abstraction would surface terminal state on a public type the View can data-bind.

## Related

- **SFTP file transfer**: see `docs/sftp.md` for the two-pane SFTP tab. Uses the same `ConnectionProfile` + credential resolution path as this terminal, but opens a separate `SftpClient` on its own TCP connection so big transfers can't stall the interactive session. Cross-launches in both directions: SSH tab → "Files" button → SFTP tab; SFTP tab → "Terminal" button → SSH tab.

## References

- RFC 4254 — SSH Connection Protocol (§6 Interactive Sessions): <https://www.rfc-editor.org/rfc/rfc4254>
- RFC 4250 — SSH codepoint registry (terminal mode bytes): <https://www.rfc-editor.org/rfc/rfc4250>
- xterm Control Sequences: <https://invisible-island.net/xterm/ctlseqs/ctlseqs.html>
- ECMA-48 — Control Functions for Coded Character Sets: <https://ecma-international.org/publications-and-standards/standards/ecma-48/>
- terminfo(5) — capability names (`smcup`, `rmcup`, `cup`, `clear`, etc.)
- VtNetCore upstream: <https://github.com/darrenstarr/VtNetCore>
- Microsoft.Graphics.Win2D (1.3.2): <https://www.nuget.org/packages/Microsoft.Graphics.Win2D/1.3.2>
