using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Patterns;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NexusRDM.DemoRecorder;

// Drive NexusRDM through the demo flow and snap PNGs (+ a demo GIF)
// at named milestones. Output filenames match the placeholder paths
// in the README so dropping them under docs/screenshots/ wires up
// the embedded images automatically.
//
// Usage:
//   NexusRDM.DemoRecorder.exe [output-dir]
//     output-dir : where PNGs and the GIF land. Default
//                  "docs/screenshots" relative to the repo root.

string outDir = "docs/screenshots";
// Default Debug — that's what developers actually iterate on, and
// running the recorder against a stale Release build led to "newest
// exe wins" picking the wrong copy. Override with --release if you
// really want to capture the optimised binary.
string config = "Debug";
for (int i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if      (arg == "--release") config = "Release";
    else if (arg == "--debug")   config = "Debug";
    else if (arg == "--config" && i + 1 < args.Length) config = args[++i];
    else if (!arg.StartsWith("--")) outDir = arg;
}

outDir = Path.GetFullPath(outDir);
Directory.CreateDirectory(outDir);
Console.WriteLine($"Output → {outDir}");

var exe = LocateNexusRdmExe(config)
    ?? throw new FileNotFoundException(
        $"Couldn't locate a built NexusRDM.exe under bin\\x64\\{config}. " +
        $"Build the app first: msbuild src\\NexusRDM\\NexusRDM.csproj /p:Configuration={config} /p:Platform=x64");
Console.WriteLine($"App    → {exe}");
Console.WriteLine($"Config → {config}");

using var app = Application.Launch(new ProcessStartInfo(exe) { UseShellExecute = false });
using var ua  = new UIA3Automation();
var win = app.GetMainWindow(ua, TimeSpan.FromSeconds(30))
    ?? throw new InvalidOperationException("Main window didn't appear within 30s.");

// Resize to a stable, README-friendly aspect ratio. Larger than the
// runner default so screenshots don't look cramped.
TrySetSize(win, 1280, 800);
win.Focus();
Thread.Sleep(800);

// Resolve the demo button up-front. We need it twice: once to force
// the app into a known OFF state for the pre-demo screenshot, then
// again to turn demo on for the rest of the captures.
Console.WriteLine("Locating demo button…");
Button? demoBtn = null;
for (int attempt = 0; attempt < 20 && demoBtn is null; attempt++)
{
    demoBtn = win.FindFirstDescendant(cf => cf.ByAutomationId("BtnStartDemo")) as Button;
    if (demoBtn is null)
    {
        // Fallbacks if the build doesn't have the AutomationId yet
        // (older builds, or in case it gets stripped during release).
        demoBtn = win.FindFirstDescendant(cf => cf.ByName("Start demo")) as Button;
        demoBtn ??= win.FindFirstDescendant(cf => cf.ByName("Exit demo")) as Button;
        demoBtn ??= FindButtonByLabel(win, "Start demo");
        demoBtn ??= FindButtonByLabel(win, "Exit demo");
    }
    if (demoBtn is null) Thread.Sleep(500);
}
if (demoBtn is null)
{
    DumpButtons(win);
    throw new InvalidOperationException(
        "Couldn't find the demo toggle button after 10s. " +
        "Make sure NexusRDM was rebuilt with the AutomationId change. " +
        "See stderr above for the list of visible buttons.");
}

// Make sure demo mode is ON before any screenshot. The recorder
// must NEVER capture the user's real environment — every PNG and
// the GIF should show synthetic data only.
//
// Detection is fiddly because the demo button's UIA Name is fixed
// ("Start demo") regardless of state, and the connections tree is
// virtualised — even with demo active, UIA may not see "pi-hole"
// until the tree is expanded. So we use a tolerant flow:
//   1. Expand the tree (cheap, no-op if nothing's there yet).
//   2. Probe for pi-hole. If we see it, demo is already on.
//   3. Otherwise click the toggle, expand again, re-probe.
//   4. If still missing, click ONCE more (we may have toggled OFF
//      a state we couldn't detect) and re-probe.
// Anything past that is a real failure and we refuse to snap.
ExpandAllTreeItems(win);
Thread.Sleep(300);

if (TryFindRow(win, "pi-hole") is null)
{
    Console.WriteLine("Activating demo mode…");
    demoBtn.Click();
    Thread.Sleep(900);
    SkipTourDialogs(ua, win, maxSteps: 8);
    Thread.Sleep(500);
    ExpandAllTreeItems(win);

    if (!WaitForRow(win, "pi-hole", TimeSpan.FromSeconds(6)))
    {
        Console.WriteLine("  (no rows yet — click may have toggled OFF; flipping again)");
        demoBtn.Click();
        Thread.Sleep(900);
        SkipTourDialogs(ua, win, maxSteps: 8);
        Thread.Sleep(500);
        ExpandAllTreeItems(win);
    }
}
else
{
    Console.WriteLine("Demo mode already active — keeping it on.");
    SkipTourDialogs(ua, win, maxSteps: 4);
}

if (!WaitForRow(win, "pi-hole", TimeSpan.FromSeconds(8)))
    throw new InvalidOperationException(
        "Demo rows didn't appear after activating demo mode. " +
        "Refusing to take screenshots — they would expose your real connections.");

// Force every TreeViewItem to the Expanded state. The data binding
// sets IsExpanded=true but virtualization can render collapsed; we
// invoke ExpandCollapse via UIA so screenshots show the full tree.
ExpandAllTreeItems(win);
Thread.Sleep(400);
// Second pass — expanding parents reveals children that weren't in
// the UIA tree on the first sweep.
ExpandAllTreeItems(win);
Thread.Sleep(400);

// Switch to the Light theme as the recorder's baseline. Light has
// the highest text/background contrast of the bundled themes and
// embeds well in any README context (light-mode GitHub, slide
// decks, printouts). Dark themes look great in dark mode but
// render as a heavy black block when the host page is light.
//
// Theme switch happens BEFORE the first screenshot so every
// capture (PNGs + GIF + MP4) shares the same palette. We restore
// the original theme at the end of the run so re-launching the
// app interactively keeps whatever the user had picked.
Console.WriteLine("Switching to Light theme for the recording…");
{
    var settingsBtnInit = win.FindFirstDescendant(cf => cf.ByAutomationId("BtnNavSettings")) as Button
                       ?? win.FindFirstDescendant(cf => cf.ByName("Settings")) as Button;
    if (settingsBtnInit is not null)
    {
        settingsBtnInit.Click();
        Thread.Sleep(500);
        (win.FindFirstDescendant(cf => cf.ByName("Appearance")))?.Click();
        Thread.Sleep(500);
        if (!SelectTheme(win, "Light"))
            Console.WriteLine("  (couldn't pick Light — recording will use whatever theme is active)");
        Thread.Sleep(500);
        // Back to Connections so the first screenshot frames the tree.
        var connBtnInit = win.FindFirstDescendant(cf => cf.ByAutomationId("BtnNavConn")) as Button
                       ?? win.FindFirstDescendant(cf => cf.ByName("Connections")) as Button;
        connBtnInit?.Click();
        Thread.Sleep(600);
        ExpandAllTreeItems(win);
        Thread.Sleep(300);
    }
}

// 1. main-window.png — Home tab + sidebar with the demo tree.
Snap.Window(win, Path.Combine(outDir, "main-window.png"));

// 3. context-menu.png — right-click on a managed VM to show the
// power / detach actions. We pick a known demo row by its name.
Console.WriteLine("Capturing context menu…");
var sample = TryFindRow(win, "pi-hole") ?? TryFindRow(win, "web-prod-01");
if (sample is not null)
{
    var bounds = sample.BoundingRectangle;
    var center = new System.Drawing.Point(
        (int)(bounds.X + bounds.Width / 2),
        (int)(bounds.Y + bounds.Height / 2));
    Mouse.RightClick(center);
    Thread.Sleep(700);
    Snap.Window(win, Path.Combine(outDir, "context-menu.png"));
    // Dismiss the flyout.
    Keyboard.Press(VirtualKeyShort.ESCAPE);
    Thread.Sleep(300);
}
else
{
    Console.WriteLine("  (skipped — no demo row found)");
}

// 4. power-icons.png — a wide screenshot showing the tree with all
// the badge variations (PVE, HV, AUTO, power glyph). Just snap the
// whole window after demo expansion.
Snap.Window(win, Path.Combine(outDir, "power-icons.png"));

// 4. ssh-session.png — open a real SSH session on a demo row. The
// app is in demo mode, so DemoSshHandler returns a DemoSshSession
// that emits canned bash output. Type a few commands to populate
// the terminal, then close the tab so the GIF flow can reopen
// fresh later.
Console.WriteLine("Capturing SSH session…");
if (ConnectViaContextMenu(win, "pi-hole"))
{
    Thread.Sleep(1500); // let banner + prompt land
    Keyboard.Type("ls");
    Keyboard.Press(VirtualKeyShort.RETURN);
    Thread.Sleep(400);
    Keyboard.Type("uptime");
    Keyboard.Press(VirtualKeyShort.RETURN);
    Thread.Sleep(400);
    Keyboard.Type("whoami");
    Keyboard.Press(VirtualKeyShort.RETURN);
    Thread.Sleep(500);
    Snap.Window(win, Path.Combine(outDir, "ssh-session.png"));
    CloseActiveTab(ua, win);
    Thread.Sleep(500);
}
else
{
    Console.WriteLine("  (skipped — couldn't connect via context menu)");
}

// rdp-session.png — open a "real" RDP session on a demo row. The
// app is in demo mode, so DemoRdpHandler returns a DemoRdpSession;
// the view renders the fake-desktop placeholder (DEMO watermark +
// fake taskbar) into the host panel because no Win32 mstscax window
// is hosted. Snap, close.
Console.WriteLine("Capturing RDP session…");
if (ConnectViaContextMenu(win, "rdp-jumpbox") || ConnectViaContextMenu(win, "dev-windows-11"))
{
    Thread.Sleep(1500); // let the placeholder render + Connected fire
    Snap.Window(win, Path.Combine(outDir, "rdp-session.png"));
    CloseActiveTab(ua, win);
    Thread.Sleep(500);
}
else
{
    Console.WriteLine("  (skipped — couldn't connect to an RDP demo row)");
}

// 4a. edit-connection.png — right-click row → click "Edit…" → snap
// the slide-over panel → close. The edit panel is an in-window
// overlay (not a separate ContentDialog), so a normal Window snap
// captures it correctly. Snap once with the row's natural protocol
// (SSH for pi-hole), then switch to RDP and snap a second shot so
// the README can show both protocol-specific sections.
Console.WriteLine("Capturing edit-connection panel (SSH)…");
if (OpenEditPanel(win, "pi-hole") || OpenEditPanel(win, "web-prod-01"))
{
    Thread.Sleep(900);
    Snap.Window(win, Path.Combine(outDir, "edit-connection.png"));
    CloseEditPanel(win);
    Thread.Sleep(500);
    if (IsEditPanelOpen(win))
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
    }
}
else
{
    Console.WriteLine("  (skipped — couldn't open Edit… for any SSH demo row)");
}

// Open Edit on an actual RDP demo row so the panel header and
// fields read as RDP from the start (we used to open SSH and then
// swap the Protocol dropdown — the header still said the SSH host
// name, which was confusing). Scroll to bring the RDP-specific
// options (resolution, colour depth, audio, gateway, etc.) into
// view rather than hidden below the General fields.
Console.WriteLine("Capturing edit-connection panel (RDP)…");
if (OpenEditPanel(win, "rdp-jumpbox") || OpenEditPanel(win, "dev-windows-11"))
{
    Thread.Sleep(900);
    ScrollEditPanel(win, pageDownTimes: 2);
    Thread.Sleep(400);
    Snap.Window(win, Path.Combine(outDir, "edit-connection-rdp.png"));
    CloseEditPanel(win);
    Thread.Sleep(500);
    if (IsEditPanelOpen(win))
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
    }
}
else
{
    Console.WriteLine("  (skipped — couldn't open Edit… for any RDP demo row)");
}

// 5. demo-tour.gif. Pure-managed GIF recorder — captures frames via
// GDI and assembles via GDI+'s built-in animated-GIF encoder. No
// ffmpeg dependency.
//
// Driver intentionally avoids clicking row entries. Demo connections
// are real protocol entries (SSH/RDP) — single-clicking one in the
// user's normal click-mode triggers an auth dialog that pegs the
// recorder. Instead we show motion via right-click context menus
// (which we want in the README anyway) and the Edit panel.
// Defer disposal of the capture handle until after the Settings
// shots so we can encode the GIF/MP4 LAST — encoding the hi-res GIF
// can take 30+ seconds on the fallback ladder and there's no point
// holding the WinUI app open while it runs.
GifRecorder.Capture? capture = null;
{
    Console.WriteLine("Recording demo tour…");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    void Step(string label) =>
        Console.WriteLine($"  [+{sw.Elapsed.TotalSeconds,5:0.0}s] {label}");

    capture = await GifRecorder.RecordAsync(
        win,
        durationSeconds: 32,
        captureFps: 15,
        driveUi: async () =>
        {
            Step("starting tour");
            await Task.Delay(400);

            // Right-click a row → context menu, then dismiss.
            var pi = TryFindRow(win, "pi-hole");
            if (pi is not null)
            {
                Step("right-click pi-hole → context menu");
                var b = pi.BoundingRectangle;
                Mouse.RightClick(new System.Drawing.Point(
                    (int)(b.X + b.Width / 2), (int)(b.Y + b.Height / 2)));
                await Task.Delay(1100);
                Step("dismiss context menu");
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                await Task.Delay(400);
            }

            // SSH demo: connect to pi-hole (DemoSshHandler returns a
            // DemoSshSession), type a couple of commands so the
            // terminal animates, then close the tab.
            Step("connect SSH (pi-hole)");
            if (ConnectViaContextMenu(win, "pi-hole"))
            {
                await Task.Delay(1700); // banner + prompt land
                Step("type 'ls'");
                Keyboard.Type("ls");
                Keyboard.Press(VirtualKeyShort.RETURN);
                await Task.Delay(700);
                Step("type 'uptime'");
                Keyboard.Type("uptime");
                Keyboard.Press(VirtualKeyShort.RETURN);
                await Task.Delay(700);
                Step("type 'whoami'");
                Keyboard.Type("whoami");
                Keyboard.Press(VirtualKeyShort.RETURN);
                await Task.Delay(900);
                Step("close SSH tab");
                CloseActiveTab(ua, win);
                await Task.Delay(500);
            }

            // RDP demo: connect to an RDP row (DemoRdpHandler returns
            // a DemoRdpSession), linger on the fake-desktop overlay
            // so the viewer sees the placeholder, then close the tab.
            Step("connect RDP (rdp-jumpbox)");
            if (ConnectViaContextMenu(win, "rdp-jumpbox") || ConnectViaContextMenu(win, "dev-windows-11"))
            {
                await Task.Delay(2200); // placeholder render + read time
                Step("close RDP tab");
                CloseActiveTab(ua, win);
                await Task.Delay(500);
            }

            // Briefly show the Edit panel — just hold it open so the
            // viewer sees the slide-over surface, then close. We
            // deliberately don't toggle the Protocol dropdown here:
            // PageDown after a SetEditProtocol drives the focused
            // ComboBox open instead of scrolling the body, which
            // looks broken on tape.
            Step("open Edit panel (web-prod-01)");
            if (OpenEditPanel(win, "web-prod-01"))
            {
                await Task.Delay(1800);
                Step("Edit: close panel");
                CloseEditPanel(win);
                await Task.Delay(600);
                if (IsEditPanelOpen(win))
                {
                    Step("Edit: still open, sending ESC");
                    Keyboard.Press(VirtualKeyShort.ESCAPE);
                    await Task.Delay(400);
                }
            }

            // Settings tour. Click Settings nav, walk through a few
            // sections (Proxmox sources, Hyper-V, Appearance), so the
            // viewer sees the whole app, not just the connections
            // tree. Each section has visible content so the GIF
            // doesn't fall flat into static frames.
            Step("nav: Settings");
            var settingsBtn = win.FindFirstDescendant(cf => cf.ByAutomationId("BtnNavSettings")) as Button
                            ?? win.FindFirstDescendant(cf => cf.ByName("Settings")) as Button;
            if (settingsBtn is not null)
            {
                settingsBtn.Click();
                await Task.Delay(700);

                Step("Settings: Proxmox sources");
                (win.FindFirstDescendant(cf => cf.ByName("Proxmox sources")))?.Click();
                await Task.Delay(900);

                Step("Settings: Hyper-V");
                (win.FindFirstDescendant(cf => cf.ByName("Hyper-V")))?.Click();
                await Task.Delay(900);

                Step("Settings: Appearance");
                (win.FindFirstDescendant(cf => cf.ByName("Appearance")))?.Click();
                await Task.Delay(1100);

                // Back to Connections so the closing frame shows the
                // tree (matches the opening shot — bookends the GIF).
                Step("nav: Connections");
                var connBtn = win.FindFirstDescendant(cf => cf.ByAutomationId("BtnNavConn")) as Button
                            ?? win.FindFirstDescendant(cf => cf.ByName("Connections")) as Button;
                connBtn?.Click();
                await Task.Delay(700);
            }

            // Final beat — hover db-prod-01 so the GIF closes on a
            // composed frame instead of a stray cursor location.
            var db = TryFindRow(win, "db-prod-01");
            if (db is not null)
            {
                Step("hover db-prod-01 (closing frame)");
                var b = db.BoundingRectangle;
                Mouse.MoveTo(new System.Drawing.Point(
                    (int)(b.X + b.Width / 2), (int)(b.Y + b.Height / 2)));
                await Task.Delay(500);
            }
            Step("driver done — capture continues to fill duration");
        });

    // Belt-and-suspenders: the edit panel may still be up if the
    // GIF driver bailed early. Force-close before the next step so
    // the Settings nav isn't blocked by the modal scrim.
    if (IsEditPanelOpen(win))
    {
        Console.WriteLine("  (edit panel still open after GIF — closing)");
        CloseEditPanel(win);
    }

    // Belt-and-suspenders: if a connection auth dialog DID slip
    // through (e.g. user has SingleClick mode + we hit a row by
    // accident), close it before continuing.
    DismissAuthDialogs(ua, win);
}

// 6. Settings → Proxmox sources screenshot. Navigate, snap, return.
Console.WriteLine("Navigating to Settings → Proxmox sources…");
var settingsBtn = win.FindFirstDescendant(cf => cf.ByAutomationId("BtnNavSettings")) as Button
                ?? win.FindFirstDescendant(cf => cf.ByName("Settings")) as Button
                ?? FindButtonByLabel(win, "Settings");
if (settingsBtn is not null)
{
    settingsBtn.Click();
    Thread.Sleep(800);
    var proxmoxNav = win.FindFirstDescendant(cf => cf.ByName("Proxmox sources"));
    proxmoxNav?.Click();
    Thread.Sleep(600);
    Snap.Window(win, Path.Combine(outDir, "proxmox-sync.png"));

    // 7. Hyper-V section
    var hyperVNav = win.FindFirstDescendant(cf => cf.ByName("Hyper-V"));
    hyperVNav?.Click();
    Thread.Sleep(600);
    Snap.Window(win, Path.Combine(outDir, "hyperv-sync.png"));

    // 8. Network discovery
    var discoveryNav = win.FindFirstDescendant(cf => cf.ByName("Network discovery"));
    discoveryNav?.Click();
    Thread.Sleep(600);
    Snap.Window(win, Path.Combine(outDir, "discovery.png"));

    // 9. Themes / Appearance
    var apprNav = win.FindFirstDescendant(cf => cf.ByName("Appearance"));
    apprNav?.Click();
    Thread.Sleep(600);
    Snap.Window(win, Path.Combine(outDir, "themes.png"));

    // 9a. Theme tour. Cycle through a representative subset of
    // themes, navigate back to Connections after each pick (so
    // the snapshot captures the tree + sidebar painted in that
    // theme's palette — way more visually informative than the
    // Appearance page itself), then snap. We end the tour by
    // restoring the default ("Dracula") so the next recorder
    // run starts from a known palette.
    Console.WriteLine("Capturing per-theme connection-tree shots…");
    string[] themes = ["Dark (default)", "Light", "Solarized Dark", "Solarized Light", "Nord", "Monokai"];
    string[] themeFileSlugs = ["theme-dark", "theme-light", "theme-solarized-dark", "theme-solarized-light", "theme-nord", "theme-monokai"];
    for (int i = 0; i < themes.Length; i++)
    {
        if (!SelectTheme(win, themes[i]))
        {
            Console.WriteLine($"  (skipped {themes[i]} — picker missed it)");
            continue;
        }
        Thread.Sleep(500); // let the brushes repaint everywhere
        // Switch to Connections so the screenshot shows the tree
        // re-painted in the new palette. Settings nav stays
        // selected on each iteration; clicking Connections then
        // back to Settings would lose our spot in the page list.
        var connBtnTheme = win.FindFirstDescendant(cf => cf.ByAutomationId("BtnNavConn")) as Button
                         ?? win.FindFirstDescendant(cf => cf.ByName("Connections")) as Button;
        connBtnTheme?.Click();
        Thread.Sleep(600);
        Snap.Window(win, Path.Combine(outDir, $"{themeFileSlugs[i]}.png"));

        // Re-enter Settings → Appearance for the next theme pick.
        if (i < themes.Length - 1)
        {
            settingsBtn?.Click();
            Thread.Sleep(500);
            (win.FindFirstDescendant(cf => cf.ByName("Appearance")))?.Click();
            Thread.Sleep(500);
        }
    }
    // Restore the recorder's baseline (Light) so the next
    // recorder run starts from the same persisted theme. The
    // user can manually switch back to Dracula or anything else.
    SelectTheme(win, "Light");
    Thread.Sleep(400);
}
else
{
    Console.WriteLine("  (no Settings nav button found — skipping settings shots)");
    Console.WriteLine("  Diagnostic: every visible button below — the WinUI app is");
    Console.WriteLine("  probably a stale build that doesn't have AutomationProperties");
    Console.WriteLine("  on the sidebar nav buttons. Rebuild the app and try again.");
    DumpButtons(win);
}

// Close the app first — encoding doesn't need a live UI, and the
// hi-res GIF re-encode ladder can take ~30s. No reason to keep the
// WinUI app holding RAM and HWND resources during that window.
Console.WriteLine("Closing app before encoding outputs…");
try { app.Close(); } catch { }
try { app.Kill();  } catch { }

// Encode the captured frames into multiple output formats LAST.
// Two GIF qualities (small for README embeds, hi-res for sharper
// playback) plus an MP4 when ffmpeg is on PATH. Each encode walks
// a fallback ladder if the file blows the 10 MB budget, dropping
// fps before resolution / CRF.
if (capture is not null)
{
    // Both GIFs render at the captured resolution — downscaling
    // makes the terminal text and field labels illegible. The
    // small one ships fewer frames (lower fps); the hi-res keeps
    // the full frame rate. Either may overshoot the 10 MB budget
    // at full resolution; that's fine — LFS handles them.
    Console.WriteLine("Encoding outputs…");
    capture.SaveGif(Path.Combine(outDir, "demo-tour.gif"),    maxLongSide: 1280, outFps: 10);
    capture.SaveGif(Path.Combine(outDir, "demo-tour-hq.gif"), maxLongSide: 1280, outFps: 15);
    await capture.SaveMp4Async(Path.Combine(outDir, "demo-tour.mp4"), outFps: 30);
    capture.Dispose();
}

Console.WriteLine("Done.");
return 0;

// ── helpers ─────────────────────────────────────────────────────────

static string? LocateNexusRdmExe(string configuration)
{
    // Walk up from this exe to the repo root, then look for a
    // NexusRDM.exe under the requested Configuration's bin
    // directory specifically. We deliberately scope to
    // bin\x64\<Configuration>\ rather than picking "newest in any
    // bin folder" — running the Debug recorder against a stale
    // Release build (or vice versa) leads to confusing "AutomationId
    // not found" errors when the built copy is missing recent
    // source changes.
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
    {
        var configBin = Path.Combine(dir, "src", "NexusRDM", "bin", "x64", configuration);
        if (!Directory.Exists(configBin)) continue;
        return Directory.EnumerateFiles(configBin, "NexusRDM.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    return null;
}

static void TrySetSize(Window w, int width, int height)
{
    try
    {
        // FlaUI's Window has a Patterns surface for Transform/Window
        // patterns, but the simplest cross-version approach is the
        // P/Invoke-backed MoveTo/Resize on AutomationElement.
        w.Move(120, 80);
        // FlaUI doesn't ship a Resize helper — fall back to UIA's
        // TransformPattern through the pattern factory.
        var transform = w.Patterns.Transform.PatternOrDefault;
        transform?.Resize((double)width, (double)height);
    }
    catch { /* best effort */ }
}

static void SkipTourDialogs(UIA3Automation ua, Window root, int maxSteps)
{
    // ContentDialogs in WinUI 3 are hosted on a separate top-level
    // popup window owned by the same process — they're NOT children
    // of the main Window's UIA subtree. So we have to walk from the
    // desktop root to find them. We also fall back to scanning all
    // top-level descendants of our own process.
    int pid;
    try { pid = root.Properties.ProcessId.Value; } catch { pid = 0; }

    for (int i = 0; i < maxSteps; i++)
    {
        Thread.Sleep(500);
        AutomationElement? skip = null;
        try
        {
            // Desktop walk: find any element whose Name == "Skip tour"
            // belonging to our process. The desktop has many top-level
            // windows; restrict to our PID to avoid false matches.
            var desktop = ua.GetDesktop();
            foreach (var top in desktop.FindAllChildren())
            {
                int topPid = 0;
                try { topPid = top.Properties.ProcessId.Value; } catch { }
                if (pid != 0 && topPid != pid) continue;
                skip = top.FindFirstDescendant(cf => cf.ByName("Skip tour"));
                if (skip is not null) break;
            }
        }
        catch { /* best effort */ }

        // Last resort: try the main-window subtree too, in case some
        // dialog mode parents back into it.
        skip ??= root.FindFirstDescendant(cf => cf.ByName("Skip tour"));

        if (skip is null) return;
        try { skip.AsButton().Click(); } catch { try { skip.Click(); } catch { } }
    }
}

static void CloseActiveTab(UIA3Automation? ua = null, Window? root = null)
{
    // WinUI 3 TabView ships Ctrl+F4 as the built-in close-tab
    // accelerator. Cleaner than walking the visual tree to find
    // the per-tab X button (which moves around when tabs reflow).
    Keyboard.Pressing(VirtualKeyShort.CONTROL);
    Keyboard.Press(VirtualKeyShort.F4);
    Keyboard.Release(VirtualKeyShort.CONTROL);

    // The app pops a "Close active session?" ContentDialog when an
    // active connection (SSH/RDP) is still up. Confirm it so the
    // recorder doesn't hang waiting on a modal. Skip when the
    // caller didn't supply automation/root (the static-screenshot
    // step's signature predates this branch).
    if (ua is not null && root is not null)
    {
        Thread.Sleep(600);
        ConfirmCloseSessionDialog(ua, root);
    }
}

static void ConfirmCloseSessionDialog(UIA3Automation ua, Window root)
{
    // ContentDialogs in our app use XamlRoot=Content.XamlRoot, which
    // means they render INSIDE the main window's XAML/UIA tree as a
    // popup — NOT on a separate top-level window. So we have to look
    // in two places:
    //   1. The main window's subtree (the common path).
    //   2. The desktop's top-level children of our PID (in case a
    //      future dialog gets re-parented to a separate popup window).
    //
    // We don't just click once and return — the click may register
    // before the dialog has fully animated open, so we keep polling
    // until the "Close active session?" title is gone or we time out.
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    int pid = 0;
    try { pid = root.Properties.ProcessId.Value; } catch { }

    while (DateTime.UtcNow < deadline)
    {
        // Anchor the search on the title TextBlock, then walk up to a
        // parent that contains a "Close" button. This avoids clicking
        // the edit panel's X button (also named "Close") that lives
        // elsewhere in the tree.
        var title = root.FindFirstDescendant(cf => cf.ByName("Close active session?"));
        if (title is not null) ClickPrimaryCloseNear(title);
        // Verify the dialog is actually gone before returning — a
        // first click sometimes lands while the dialog is still
        // animating in and doesn't register.
        Thread.Sleep(250);
        if (root.FindFirstDescendant(cf => cf.ByName("Close active session?")) is null)
            return;

        // Fallback: top-level desktop walk for our PID.
        try
        {
            var desktop = ua.GetDesktop();
            foreach (var top in desktop.FindAllChildren())
            {
                int topPid = 0;
                try { topPid = top.Properties.ProcessId.Value; } catch { }
                if (pid != 0 && topPid != pid) continue;
                var t = top.FindFirstDescendant(cf => cf.ByName("Close active session?"));
                if (t is not null && ClickPrimaryCloseNear(t)) return;
            }
        }
        catch { /* best effort */ }

        Thread.Sleep(200);
    }
}

static bool ClickPrimaryCloseNear(AutomationElement title)
{
    // Walk up from the title TextBlock to the dialog root, then look
    // for the primary action button labeled "Close". ContentDialog's
    // PrimaryButton is a Button with Name == PrimaryButtonText.
    var node = title;
    for (int i = 0; i < 8 && node is not null; i++, node = node.Parent)
    {
        var primary = node.FindFirstDescendant(cf =>
            cf.ByControlType(ControlType.Button).And(cf.ByName("Close")));
        if (primary is null) continue;
        try { primary.AsButton().Click(); return true; }
        catch
        {
            try { primary.Click(); return true; } catch { }
        }
    }
    return false;
}

static bool ConnectViaContextMenu(Window root, string rowName)
{
    // Right-click the row → click "Connect" in the context menu.
    // Goes through the Connect command regardless of the user's
    // single-click vs double-click setting.
    var row = TryFindRow(root, rowName);
    if (row is null) return false;

    var b = row.BoundingRectangle;
    Mouse.RightClick(new System.Drawing.Point(
        (int)(b.X + b.Width / 2), (int)(b.Y + b.Height / 2)));
    Thread.Sleep(700);

    var connect = root.FindFirstDescendant(cf => cf.ByName("Connect"));
    if (connect is null)
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);
        return false;
    }
    try { connect.AsMenuItem().Click(); }
    catch { try { connect.Click(); } catch { Keyboard.Press(VirtualKeyShort.ESCAPE); return false; } }
    return true;
}

static bool OpenEditPanel(Window root, string rowName)
{
    var row = TryFindRow(root, rowName);
    if (row is null) return false;

    var b = row.BoundingRectangle;
    Mouse.RightClick(new System.Drawing.Point(
        (int)(b.X + b.Width / 2), (int)(b.Y + b.Height / 2)));
    Thread.Sleep(700);

    // Context menu items live on a separate popup, but unlike
    // ContentDialogs, MenuFlyout typically attaches into the main
    // window's UIA tree. Try the main subtree first, fall back to
    // a desktop walk.
    var edit = root.FindFirstDescendant(cf => cf.ByName("Edit…"))
            ?? root.FindFirstDescendant(cf => cf.ByName("Edit…"))
            ?? root.FindFirstDescendant(cf => cf.ByName("Edit"));
    if (edit is null)
    {
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);
        return false;
    }
    try { edit.AsMenuItem().Click(); }
    catch { try { edit.Click(); } catch { Keyboard.Press(VirtualKeyShort.ESCAPE); return false; } }
    return true;
}

static void CloseEditPanel(Window root)
{
    // Multi-strategy close. Each strategy is scoped to the edit
    // panel — we deliberately don't grep ByName("Close") globally
    // because the title-bar Close shares that name and clicking it
    // terminates the app.
    //
    // Strategy 1: anchor on the panel-only "Save" button, walk up
    // to a parent that has both Save and Cancel, then Invoke the
    // Cancel button via UIA's InvokePattern. Invoke doesn't rely on
    // synthetic mouse positioning, which is brittle when previous
    // steps (protocol-switch, scroll) have shifted focus or moved
    // the button's bounding rect off-screen.
    var anchor = root.FindFirstDescendant(cf => cf.ByName("Save"));
    if (anchor is not null)
    {
        var node = anchor;
        for (int i = 0; i < 8 && node is not null; i++, node = node.Parent)
        {
            var cancel = node.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.Button).And(cf.ByName("Cancel")));
            if (cancel is null) continue;
            try
            {
                var inv = cancel.Patterns.Invoke.PatternOrDefault;
                if (inv is not null) inv.Invoke();
                else                 cancel.AsButton().Click();
            }
            catch { try { cancel.Click(); } catch { } }
            if (WaitForEditPanelClosed(root, TimeSpan.FromSeconds(2))) return;
            break; // Invoke didn't take it down — fall through to scrim tap.
        }
    }

    // Strategy 2: tap the scrim. Edit panel is 440 DIPs wide,
    // anchored right; the rest of the window is the dim scrim
    // wired to OnScrimTapped → close. We aim for the lower part of
    // the scrim band to avoid hitting the toolbar / sidebar nav
    // and any tab content that sits above the scroll viewport.
    var b = root.BoundingRectangle;
    int scrimBandWidth = Math.Max(0, (int)b.Width - 440);
    int scrimX = (int)(b.X + scrimBandWidth / 2 + 60);
    int scrimY = (int)(b.Y + b.Height * 3 / 4);
    Mouse.Click(new System.Drawing.Point(scrimX, scrimY));
    if (WaitForEditPanelClosed(root, TimeSpan.FromSeconds(1.5))) return;

    Console.WriteLine("  (warning: edit panel still appears open after close attempts)");
}

static bool WaitForEditPanelClosed(Window root, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (!IsEditPanelOpen(root)) return true;
        Thread.Sleep(100);
    }
    return !IsEditPanelOpen(root);
}

static bool IsEditPanelOpen(Window root)
{
    // The footer Save button is unique to the edit panel. If it's
    // present in the UIA tree, the panel is still showing.
    return root.FindFirstDescendant(cf => cf.ByName("Save")) is not null;
}

static bool SelectTheme(Window root, string displayName)
{
    // Locate the theme picker. Three fallbacks because UIA's
    // ComboBox projection is inconsistent across WinUI builds:
    //   1. AutomationId="ThemePicker" (set explicitly in XAML).
    //   2. Name="Theme" (AutomationProperties.Name on the same combo).
    //   3. Any ComboBox on the page whose items list contains a
    //      known theme — this rescues stale builds that don't yet
    //      have the AutomationId/Name properties baked in.
    AutomationElement? combo =
        root.FindFirstDescendant(cf => cf.ByAutomationId("ThemePicker"))
        ?? root.FindFirstDescendant(cf =>
               cf.ByControlType(ControlType.ComboBox).And(cf.ByName("Theme")));

    if (combo is null)
    {
        // Last resort: scan every visible ComboBox, open it briefly,
        // and check whether its items include a known theme name.
        // We do this on the candidate that's most likely (visible,
        // non-zero size) before falling back further.
        foreach (var c in root.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox)))
        {
            try
            {
                c.AsComboBox().Expand();
                Thread.Sleep(200);
                var probe = root.FindFirstDescendant(cf => cf.ByName("Dracula"));
                c.AsComboBox().Collapse();
                if (probe is not null) { combo = c; break; }
            }
            catch { /* not a ComboBox we can drive */ }
        }
    }
    if (combo is null) return false;

    try
    {
        // Prefer the ExpandCollapse pattern over a Click — it works
        // even if the bounding rect is partially scrolled out.
        try { combo.AsComboBox().Expand(); }
        catch { combo.Click(); }
        Thread.Sleep(450);

        // Items appear as ListItem (data-bound ComboBox) — not
        // ComboBoxItem. Match by exact Name; fall back to a
        // case-insensitive substring scan if the displayName has
        // ornamental text (e.g. "Dark (default)").
        AutomationElement? item =
            root.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.ListItem).And(cf.ByName(displayName)))
            ?? root.FindFirstDescendant(cf => cf.ByName(displayName));

        if (item is null)
        {
            foreach (var li in root.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
            {
                string name; try { name = li.Name ?? ""; } catch { continue; }
                if (name.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                { item = li; break; }
            }
        }

        if (item is null)
        {
            try { combo.AsComboBox().Collapse(); } catch { Keyboard.Press(VirtualKeyShort.ESCAPE); }
            return false;
        }

        try { item.AsListBoxItem().Select(); }
        catch
        {
            try { item.Click(); }
            catch { Keyboard.Press(VirtualKeyShort.ESCAPE); return false; }
        }
        return true;
    }
    catch { return false; }
}

static void ScrollEditPanel(Window root, int pageDownTimes)
{
    // Pump PageDown into whatever has focus inside the panel. The
    // ScrollViewer scrolls when the focused element is inside it.
    // Click the panel body first to make sure focus is there.
    var b = root.BoundingRectangle;
    Mouse.Click(new System.Drawing.Point(
        (int)(b.X + b.Width - 220),
        (int)(b.Y + b.Height / 2)));
    Thread.Sleep(200);
    for (int i = 0; i < pageDownTimes; i++)
    {
        Keyboard.Press(VirtualKeyShort.NEXT); // PageDown
        Thread.Sleep(450);
    }
}

static void DismissAuthDialogs(UIA3Automation ua, Window root)
{
    // Walk top-level windows owned by our process and click a
    // "Cancel" / "Close" / "Dismiss" button if we find one. Two
    // hard requirements:
    //   1. NEVER touch the main window itself — its title-bar
    //      Close button is also named "Close" and clicking it
    //      terminates the app. Skip by comparing native handles.
    //   2. Only act on windows that look like dialogs (small,
    //      modal-ish). We approximate this by skipping any window
    //      whose bounds match the main window — same window from a
    //      different angle.
    int pid;
    nint mainHandle;
    try
    {
        pid = root.Properties.ProcessId.Value;
        mainHandle = root.Properties.NativeWindowHandle.ValueOrDefault;
    }
    catch { return; }

    string[] cancelLabels = ["Cancel", "Close", "Dismiss"];
    for (int round = 0; round < 3; round++)
    {
        AutomationElement? cancel = null;
        try
        {
            var desktop = ua.GetDesktop();
            foreach (var top in desktop.FindAllChildren())
            {
                int topPid = 0;
                try { topPid = top.Properties.ProcessId.Value; } catch { }
                if (topPid != pid) continue;

                // Skip the main app window — clicking "Close" on it
                // means the title-bar X, which kills the process.
                nint hwnd = 0;
                try { hwnd = top.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
                if (hwnd == mainHandle && hwnd != 0) continue;

                foreach (var label in cancelLabels)
                {
                    cancel = top.FindFirstDescendant(cf => cf.ByName(label));
                    if (cancel is not null) break;
                }
                if (cancel is not null) break;
            }
        }
        catch { return; }
        if (cancel is null) return;
        try { cancel.AsButton().Click(); } catch { try { cancel.Click(); } catch { } }
        Thread.Sleep(400);
    }
}

static void ExpandAllTreeItems(Window root)
{
    // Connections pane uses a WinUI 3 TreeView. Synthetic demo nodes
    // bind IsExpanded=true, but on cold activation the realized
    // TreeViewItems often render collapsed because the binding is
    // applied before the item is materialized. Fix it by walking the
    // tree and invoking the ExpandCollapse pattern on every item.
    foreach (var item in root.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem)))
    {
        try
        {
            var ec = item.Patterns.ExpandCollapse.PatternOrDefault;
            if (ec is null) continue;
            if (ec.ExpandCollapseState.Value != ExpandCollapseState.Expanded)
                ec.Expand();
        }
        catch { /* skip items that don't support the pattern */ }
    }
}

/// <summary>Last-resort lookup: enumerate every Button in the
/// window and return the first one whose Name OR descendant text
/// matches <paramref name="label"/>. Catches the WinUI 3 case where
/// a code-built Button's string Content doesn't get projected into
/// the UIA Name property — neither ByName nor ByText finds it from
/// the root, but the TextBlock that renders the content IS in the
/// subtree under the Button.</summary>
static Button? FindButtonByLabel(Window root, string label)
{
    var allButtons = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
    foreach (var b in allButtons)
    {
        if (string.Equals(SafeName(b), label, StringComparison.Ordinal))
            return b.AsButton();

        // Search the button's subtree for any element whose Name
        // matches — TextBlocks render Content text and usually
        // expose it as their Name in WinUI 3 even when the parent
        // Button's Name is empty.
        try
        {
            var match = b.FindFirstDescendant(cf => cf.ByName(label));
            if (match is not null) return b.AsButton();
        }
        catch { /* some elements don't support Name lookup; skip */ }
    }
    return null;
}

// Safe property getter — WinUI 3 surfaces some Button-typed elements
// (icon-only buttons, bare ToggleButtons) that don't implement the
// Name pattern. Reading .Name on those throws PropertyNotSupportedException
// from FlaUI; we want a missing name to mean "skip this candidate",
// not "abort the whole search".
static string SafeName(AutomationElement e)
{
    try { return e.Name ?? ""; } catch { return ""; }
}

static string SafeAutomationId(AutomationElement e)
{
    try { return e.AutomationId ?? ""; } catch { return ""; }
}

/// <summary>Dump every visible Button's Name + AutomationId to
/// stderr — handy diagnostic if the search heuristics all fail.</summary>
static void DumpButtons(Window root)
{
    Console.Error.WriteLine("Visible buttons under main window:");
    foreach (var b in root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
    {
        Console.Error.WriteLine($"  AutomationId='{SafeAutomationId(b)}'  Name='{SafeName(b)}'");
    }
}

static bool WaitForRow(Window root, string displayName, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (TryFindRow(root, displayName) is not null) return true;
        Thread.Sleep(250);
    }
    return false;
}

static AutomationElement? TryFindRow(Window root, string displayName)
{
    // The connections tree binds DisplayName to a TextBlock per row.
    // Find the TextBlock by Name, then walk up to the TreeViewItem
    // ancestor — that's the clickable thing.
    var text = root.FindFirstDescendant(cf => cf.ByName(displayName));
    if (text is null) return null;
    var anc = text.Parent;
    while (anc is not null && anc.ControlType != ControlType.TreeItem)
        anc = anc.Parent;
    return anc ?? text;
}
