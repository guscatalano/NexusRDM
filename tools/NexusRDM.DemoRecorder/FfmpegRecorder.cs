using System.Diagnostics;
using FlaUI.Core.AutomationElements;

namespace NexusRDM.DemoRecorder;

/// <summary>
/// Optional GIF capture via ffmpeg's <c>gdigrab</c> input. The
/// pure-managed <see cref="GifRecorder"/> is the default path; this
/// is kept as an alternative for users who already have ffmpeg on
/// PATH and want its higher-quality palette pipeline. Two-stage
/// palette generation (mp4 → palettegen → paletteuse) produces
/// noticeably less dithering than ffmpeg's adaptive default.
/// </summary>
internal static class FfmpegRecorder
{
    public static bool IsAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            });
            if (p is null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task RecordWindow(AutomationElement element, string outPath, int durationSeconds)
    {
        var rect = element.BoundingRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Step 1: capture an mp4 of the window's bounding box.
        var tmpMp4 = Path.Combine(Path.GetTempPath(), $"nexusrdm-rec-{Guid.NewGuid():N}.mp4");
        var args1 = string.Join(' ',
            "-y",                              // overwrite
            "-f gdigrab",
            $"-framerate 15",
            $"-offset_x {(int)rect.X}",
            $"-offset_y {(int)rect.Y}",
            $"-video_size {(int)rect.Width}x{(int)rect.Height}",
            "-i desktop",
            $"-t {durationSeconds}",
            "-pix_fmt yuv420p",
            $"\"{tmpMp4}\"");
        await Run("ffmpeg", args1);

        try
        {
            // Step 2: extract a palette.
            var palette = Path.Combine(Path.GetTempPath(), $"nexusrdm-pal-{Guid.NewGuid():N}.png");
            await Run("ffmpeg", $"-y -i \"{tmpMp4}\" -vf \"fps=12,scale=1024:-1:flags=lanczos,palettegen\" \"{palette}\"");

            // Step 3: encode the GIF using that palette.
            await Run("ffmpeg",
                $"-y -i \"{tmpMp4}\" -i \"{palette}\" -filter_complex \"fps=12,scale=1024:-1:flags=lanczos[x];[x][1:v]paletteuse\" \"{outPath}\"");

            try { File.Delete(palette); } catch { }
            Console.WriteLine($"  → {Path.GetFileName(outPath)}");
        }
        finally
        {
            try { File.Delete(tmpMp4); } catch { }
        }
    }

    private static async Task Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        }) ?? throw new InvalidOperationException($"Failed to start {exe}.");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"{exe} {args}\nfailed ({p.ExitCode}):\n{err}");
        }
    }
}
