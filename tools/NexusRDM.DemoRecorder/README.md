# NexusRDM.DemoRecorder

FlaUI-driven walk-through that captures the README's hero PNGs (and optional GIFs) by automating NexusRDM through the demo flow.

## Usage

```powershell
# 1. Build the app once (recorder needs a real NexusRDM.exe to drive).
msbuild src\NexusRDM\NexusRDM.csproj /p:Configuration=Release /p:Platform=x64

# 2. Build + run the recorder.
dotnet run --project tools\NexusRDM.DemoRecorder -- docs\screenshots

# Optional: also record GIFs (requires ffmpeg on PATH).
dotnet run --project tools\NexusRDM.DemoRecorder -- docs\screenshots --gif
```

The recorder launches the most-recently-built `NexusRDM.exe`, sizes the window to 1280×800, clicks **Start demo**, skips the tour dialogs, then snaps PNGs at the milestones the README references:

| File                       | Captured at                                        |
|----------------------------|----------------------------------------------------|
| `main-window.png`          | Initial Home + sidebar (pre-demo)                  |
| `context-menu.png`         | Right-click on a demo VM row                       |
| `power-icons.png`          | Demo tree expanded (PVE / HV / power glyphs)       |
| `proxmox-sync.png`         | Settings → Proxmox sources                         |
| `hyperv-sync.png`          | Settings → Hyper-V                                 |
| `discovery.png`            | Settings → Network discovery                       |
| `themes.png`               | Settings → Appearance                              |
| `demo-tour.gif` (optional) | 6-second clip of the demo tree                     |

## Limitations

- **Windows-only**, by design. FlaUI talks to UIA which only exists on Windows.
- **Foreground-focus required.** The recorder uses real mouse/keyboard input, so don't drive your machine for the ~30 seconds it runs. Easiest setup: leave it running on a second desktop or via Remote Desktop with the session disconnected.
- **GIF capture uses `ffmpeg gdigrab`.** Captures the screen region under the window's bounds — anything that overlaps (a notification toast, a tooltip from a different app) ends up in the frames. Run on a clean desktop.
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

For new GIF clips, call `await FfmpegRecorder.RecordWindow(win, outPath, durationSeconds: N);` after `IsAvailable()` returns true. The two-stage palette pipeline produces decent quality at modest file sizes.

## Not for CI

This tool runs interactively against a real desktop session. Don't add it to `ci.yml` — GitHub Actions' `windows-latest` runners give you a non-interactive session without a visible desktop, so UI automation either fails or produces black screenshots. Run it on a developer box and commit the PNGs.
