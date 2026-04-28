using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NexusRDM.DemoRecorder;

// Drive NexusRDM through the demo flow and snap PNGs at named
// milestones. Output filenames match the placeholder paths in the
// README so dropping them under docs/screenshots/ wires up the
// embedded images automatically.
//
// Usage:
//   NexusRDM.DemoRecorder.exe [output-dir] [--gif]
//     output-dir : where PNGs (and optional GIFs) land. Default
//                  "docs/screenshots" relative to the repo root.
//     --gif      : also record short GIFs of key flows. Requires
//                  ffmpeg on PATH; skipped silently if missing.

string outDir = "docs/screenshots";
bool   wantGifs = false;
foreach (var arg in args)
{
    if      (arg == "--gif") wantGifs = true;
    else if (!arg.StartsWith("--")) outDir = arg;
}

outDir = Path.GetFullPath(outDir);
Directory.CreateDirectory(outDir);
Console.WriteLine($"Output → {outDir}");

var exe = LocateNexusRdmExe()
    ?? throw new FileNotFoundException(
        "Couldn't locate a built NexusRDM.exe. Build the app once before running the recorder.");
Console.WriteLine($"App    → {exe}");

using var app = Application.Launch(new ProcessStartInfo(exe) { UseShellExecute = false });
using var ua  = new UIA3Automation();
var win = app.GetMainWindow(ua, TimeSpan.FromSeconds(30))
    ?? throw new InvalidOperationException("Main window didn't appear within 30s.");

// Resize to a stable, README-friendly aspect ratio. Larger than the
// runner default so screenshots don't look cramped.
TrySetSize(win, 1280, 800);
win.Focus();
Thread.Sleep(800);

// 1. main-window.png — Home tab + sidebar (no demo yet).
Snap.Window(win, Path.Combine(outDir, "main-window.png"));

// 2. Activate demo. Click "Start demo" on the Home page.
Console.WriteLine("Activating demo mode…");
var demoBtn = win.FindFirstDescendant(cf => cf.ByName("Start demo")) as Button
              ?? throw new InvalidOperationException("Couldn't find the 'Start demo' button.");
demoBtn.Click();
Thread.Sleep(800);

// Skip the multi-step tour by clicking Skip-tour on each dialog. We
// run the recorder unattended; the tour is for end users.
SkipTourDialogs(win, maxSteps: 8);
Thread.Sleep(1000);

// Force the demo tree to expand fully (mirrors what ConnectionsPane
// does on demo-active, but we can't always assume timing).
Thread.Sleep(500);

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

// 5. proxmox-sync.gif (optional). Records a short clip of the
// Settings → Proxmox sources panel for the README.
if (wantGifs)
{
    Console.WriteLine("Recording GIFs (requires ffmpeg)…");
    if (!FfmpegRecorder.IsAvailable())
        Console.WriteLine("  ffmpeg not on PATH — skipping all GIFs.");
    else
    {
        // Demo flow GIF: tree expansion already happened. Record a
        // 6-second clip of the user clicking around the demo tree.
        await FfmpegRecorder.RecordWindow(
            win, Path.Combine(outDir, "demo-tour.gif"), durationSeconds: 6);
    }
}

// 6. Settings → Proxmox sources screenshot. Navigate, snap, return.
Console.WriteLine("Navigating to Settings → Proxmox sources…");
var settingsBtn = win.FindFirstDescendant(cf => cf.ByAutomationId("BtnNavSettings")) as Button;
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
}
else
{
    Console.WriteLine("  (no Settings nav button found — skipping settings shots)");
}

// Exit demo + close.
Console.WriteLine("Done. Closing app…");
try { app.Close(); } catch { }
try { app.Kill();  } catch { }

return 0;

// ── helpers ─────────────────────────────────────────────────────────

static string? LocateNexusRdmExe()
{
    // Walk up from this exe to the repo root, then look for any
    // built NexusRDM.exe under src/NexusRDM/bin. Newest wins.
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 10 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
    {
        var src = Path.Combine(dir, "src", "NexusRDM", "bin");
        if (!Directory.Exists(src)) continue;
        return Directory.EnumerateFiles(src, "NexusRDM.exe", SearchOption.AllDirectories)
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

static void SkipTourDialogs(Window root, int maxSteps)
{
    // Each tour step is a ContentDialog with a "Skip tour" close
    // button. We click it up to maxSteps times to walk through any
    // remaining dialogs the recorder may have triggered.
    for (int i = 0; i < maxSteps; i++)
    {
        Thread.Sleep(400);
        var skip = root.FindFirstDescendant(cf => cf.ByName("Skip tour")) as Button;
        if (skip is null) return;
        skip.Click();
    }
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
