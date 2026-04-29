using System.Diagnostics;
using Xabe.FFmpeg.Downloader;

namespace NexusRDM.DemoRecorder;

/// <summary>
/// Resolves a usable ffmpeg.exe path. Probe order:
///   1. Cached download under <c>{recorder bin}\ffmpeg\</c> from a
///      previous run.
///   2. System PATH (existing developer install).
///   3. Last-resort: download via <c>Xabe.FFmpeg.Downloader</c>
///      into the cache directory and use that.
///
/// First-time download is ~80 MB and takes ~30s on a typical
/// connection; subsequent runs reuse the cached binary instantly.
/// We deliberately don't use Xabe's full conversion API — the
/// recorder still spawns ffmpeg via <see cref="Process"/> directly,
/// because piping PNG frames over stdin is more flexible than
/// what Xabe's high-level API exposes.
/// </summary>
internal static class FfmpegBootstrap
{
    private static string? _resolved;
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public static async Task<string?> ResolveAsync()
    {
        if (_resolved is not null) return _resolved;
        await _gate.WaitAsync();
        try
        {
            if (_resolved is not null) return _resolved;

            // 1. Cached download.
            var cacheDir = CacheDirectory();
            var cached = Path.Combine(cacheDir, "ffmpeg.exe");
            if (File.Exists(cached))
            {
                _resolved = cached;
                return _resolved;
            }

            // 2. System PATH.
            if (TryProbe("ffmpeg"))
            {
                _resolved = "ffmpeg";
                return _resolved;
            }

            // 3. Download via Xabe.
            try
            {
                Console.WriteLine($"ffmpeg not found — downloading to {cacheDir}…");
                Directory.CreateDirectory(cacheDir);
                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, cacheDir);
                if (File.Exists(cached))
                {
                    Console.WriteLine($"  ffmpeg ready: {cached}");
                    _resolved = cached;
                    return _resolved;
                }
                Console.Error.WriteLine($"  ffmpeg download finished but {cached} is missing.");
                return null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ffmpeg download failed: {ex.Message}");
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CacheDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    private static bool TryProbe(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "-version")
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
        catch { return false; }
    }
}
