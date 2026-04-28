# NexusRDM.DemoRecorder

FlaUI-driven walk-through that captures the README's hero PNGs (and optional GIFs) by automating NexusRDM through the demo flow.

## Usage

```powershell
# 1. Build the app once (recorder needs a real NexusRDM.exe to drive).
dotnet build src\NexusRDM\NexusRDM.csproj -c Debug -p:Platform=x64

# 2. Build + run the recorder.
dotnet run --project tools\NexusRDM.DemoRecorder -- docs\screenshots
```

The recorder launches the most-recently-built `NexusRDM.exe`, sizes the window to 1280×800, clicks **Start demo**, skips the tour dialogs, then snaps PNGs at the milestones the README references plus a short GIF of the tour:

| File                  | Captured at                                        |
|-----------------------|----------------------------------------------------|
| `main-window.png`     | Initial Home + sidebar (pre-demo)                  |
| `context-menu.png`    | Right-click on a demo VM row                       |
| `power-icons.png`     | Demo tree expanded (PVE / HV / power glyphs)       |
| `edit-connection.png` | Edit slide-over panel for a demo row               |
| `proxmox-sync.png`    | Settings → Proxmox sources                         |
| `hyperv-sync.png`     | Settings → Hyper-V                                 |
| `discovery.png`       | Settings → Network discovery                       |
| `themes.png`          | Settings → Appearance                              |
| `demo-tour.gif`       | Low-res tour (~800px, 10fps) — README-friendly |
| `demo-tour-hq.gif`    | Hi-res tour (~1280px, 15fps) — tracked via Git LFS |
| `demo-tour.mp4`       | H.264 video (full-res, 30fps) — only if ffmpeg is on PATH |

## Limitations

- **Windows-only**, by design. FlaUI talks to UIA which only exists on Windows.
- **Foreground-focus required.** The recorder uses real mouse/keyboard input, so don't drive your machine for the ~30 seconds it runs. Easiest setup: leave it running on a second desktop or via Remote Desktop with the session disconnected.
- **GIF capture uses GDI screen-grab.** Captures the screen region under the window's bounds — anything that overlaps (a notification toast, a tooltip from a different app) ends up in the frames. Run on a clean desktop.
- **No audio.** Not a thing the recorder cares about.

## Extending

Each capture is one block in `Program.cs`. Pattern:

```csharp
// Drive the UI to the state you want.
var btn = win.FindFirstDescendant(cf => cf.ByName("Some Button")) as Button;
btn?.Click();
Thread.Sleep(500);

// Snap.
Snap.Window(win, Path.Combine(outDir, "my-screenshot.png"));
```

For new GIF clips, call `await GifRecorder.RecordAsync(win, outPath, durationSeconds: N, targetFps: 10, driveUi: async () => { /* clicks here */ });`. Pure-managed encoder — no ffmpeg.

## Not for CI

This tool runs interactively against a real desktop session. Don't add it to `ci.yml` — GitHub Actions' `windows-latest` runners give you a non-interactive session without a visible desktop, so UI automation either fails or produces black screenshots. Run it on a developer box and commit the PNGs.
