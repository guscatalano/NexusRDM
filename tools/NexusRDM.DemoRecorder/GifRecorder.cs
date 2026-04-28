using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core.AutomationElements;

namespace NexusRDM.DemoRecorder;

/// <summary>
/// Captures a window region into in-memory frames, then encodes
/// multiple output qualities from the same capture pass.
///
/// Workflow:
///   1. <see cref="RecordAsync"/> grabs full-resolution frames at
///      <c>captureFps</c> while the caller-supplied <c>driveUi</c>
///      action runs.
///   2. <see cref="Capture.SaveGif"/> can be called repeatedly to
///      produce GIFs at different scales/frame-rates from those
///      frames (one capture, many outputs).
///   3. <see cref="Capture.SaveMp4"/> pipes the frames to ffmpeg if
///      it's on PATH; otherwise it's a no-op with a warning. MP4 is
///      hugely smaller than GIF for the same visual quality, which
///      is why GitHub READMEs prefer it.
///
/// Encoding notes:
/// - GIF assembly uses GDI+'s built-in animated-GIF encoder
///   (<see cref="Image.SaveAdd(EncoderParameters)"/> + multi-frame
///   <see cref="FrameDimension.Time"/>). 256-colour palette per
///   frame; quality is fine for UI walkthroughs.
/// - Per-frame delay is set via PropertyItem 0x5100 (FrameDelay) in
///   1/100ths of a second. Loop count via 0x5101 on the first frame.
/// </summary>
internal static class GifRecorder
{
    /// <summary>
    /// Capture handle returned by <see cref="RecordAsync"/>. Owns
    /// the in-memory frame list; encoding helpers are members.
    /// </summary>
    public sealed class Capture : IDisposable
    {
        public List<Bitmap> Frames { get; }
        public int CaptureFps { get; }
        public int CaptureWidth { get; }
        public int CaptureHeight { get; }

        internal Capture(List<Bitmap> frames, int fps, int w, int h)
        {
            Frames = frames; CaptureFps = fps; CaptureWidth = w; CaptureHeight = h;
        }

        public void Dispose()
        {
            foreach (var f in Frames) f.Dispose();
            Frames.Clear();
        }

        /// <summary>
        /// Encode an animated GIF from the captured frames.
        /// </summary>
        /// <param name="outPath">Output file path.</param>
        /// <param name="maxLongSide">Cap the longer dimension at this many
        /// pixels. Use the captured size when the source is already smaller.</param>
        /// <param name="outFps">Target frame rate. Sub-samples the captured
        /// frames; can't exceed the capture fps.</param>
        public void SaveGif(string outPath, int maxLongSide, int outFps)
        {
            if (Frames.Count == 0)
            {
                Console.WriteLine($"  (no frames for {Path.GetFileName(outPath)})");
                return;
            }

            // Round capture area down to even dimensions — some GIF
            // viewers misrender odd-sized frames.
            double scale = Math.Min(1.0, (double)maxLongSide / Math.Max(CaptureWidth, CaptureHeight));
            int outW = Math.Max(2, (int)(CaptureWidth  * scale) & ~1);
            int outH = Math.Max(2, (int)(CaptureHeight * scale) & ~1);

            int outFrameMs = Math.Max(20, 1000 / Math.Max(1, outFps));
            int captureFrameMs = 1000 / Math.Max(1, CaptureFps);

            // Build the down-sampled list. Walk the captured timeline
            // in real ms and pick the nearest captured frame.
            var picked = new List<Bitmap>();
            try
            {
                int totalMs = (Frames.Count - 1) * captureFrameMs;
                for (int t = 0; t <= totalMs; t += outFrameMs)
                {
                    int idx = Math.Min(Frames.Count - 1, t / captureFrameMs);
                    var src = Frames[idx];
                    var dst = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new Rectangle(0, 0, outW, outH));
                    }
                    picked.Add(dst);
                }
                if (picked.Count == 0) picked.Add((Bitmap)Frames[0].Clone());

                var gifEncoder = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == ImageFormat.Gif.Guid)
                    ?? throw new InvalidOperationException("GIF encoder not available.");

                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);

                var first = picked[0];
                SetFrameDelay(first, outFrameMs / 10);
                SetLoopCount(first, 0);
                first.Save(outPath, gifEncoder, encoderParams);

                var addParams = new EncoderParameters(1);
                addParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);
                for (int i = 1; i < picked.Count; i++)
                {
                    SetFrameDelay(picked[i], outFrameMs / 10);
                    first.SaveAdd(picked[i], addParams);
                }
                var finishParams = new EncoderParameters(1);
                finishParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
                first.SaveAdd(finishParams);

                var size = new FileInfo(outPath).Length;
                Console.WriteLine($"  → {Path.GetFileName(outPath)} ({outW}x{outH}, {picked.Count} frames @ {outFps}fps, {size / 1024} KB)");
            }
            finally
            {
                foreach (var p in picked) p.Dispose();
            }
        }

        /// <summary>
        /// Encode an MP4 from the captured frames using ffmpeg via
        /// stdin (image2pipe → libx264). Skipped silently if ffmpeg
        /// isn't on PATH. MP4 is ~10× smaller than GIF for the same
        /// visual quality and renders inline on GitHub.
        /// </summary>
        public async Task SaveMp4Async(string outPath, int outFps)
        {
            if (Frames.Count == 0) return;
            if (!FfmpegAvailable())
            {
                Console.WriteLine($"  (ffmpeg not on PATH — skipping {Path.GetFileName(outPath)})");
                return;
            }

            int outFrameMs = Math.Max(20, 1000 / Math.Max(1, outFps));
            int captureFrameMs = 1000 / Math.Max(1, CaptureFps);

            // Pipe PNGs to ffmpeg over stdin. image2pipe with
            // -framerate fixes the input clock; -r on the output
            // sets the encoded framerate. We resample the captured
            // timeline to outFps the same way the GIF saver does.
            var psi = new ProcessStartInfo("ffmpeg",
                $"-y -hide_banner -loglevel error " +
                $"-f image2pipe -framerate {outFps} -i - " +
                $"-c:v libx264 -pix_fmt yuv420p -preset medium -crf 18 " +
                $"-movflags +faststart \"{outPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg.");

            try
            {
                var stdin = p.StandardInput.BaseStream;
                int totalMs = (Frames.Count - 1) * captureFrameMs;
                for (int t = 0; t <= totalMs; t += outFrameMs)
                {
                    int idx = Math.Min(Frames.Count - 1, t / captureFrameMs);
                    using var ms = new MemoryStream();
                    Frames[idx].Save(ms, ImageFormat.Png);
                    var bytes = ms.ToArray();
                    await stdin.WriteAsync(bytes);
                }
                stdin.Close();
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    var err = await p.StandardError.ReadToEndAsync();
                    Console.Error.WriteLine($"  ffmpeg failed: {err}");
                    return;
                }
                var size = new FileInfo(outPath).Length;
                Console.WriteLine($"  → {Path.GetFileName(outPath)} ({CaptureWidth}x{CaptureHeight} @ {outFps}fps, {size / 1024} KB)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  mp4 encode threw: {ex.Message}");
            }
        }

        private static bool FfmpegAvailable()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (p is null) return false;
                p.WaitForExit(3000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Capture frames at full resolution and the given fps while
    /// <paramref name="driveUi"/> runs. Returns a <see cref="Capture"/>
    /// the caller can save as one or more output formats.
    /// </summary>
    public static async Task<Capture> RecordAsync(
        AutomationElement element,
        int durationSeconds,
        int captureFps = 15,
        Func<Task>? driveUi = null)
    {
        var rect = element.BoundingRectangle;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            Console.WriteLine("  (recorder: element has no bounds)");
            return new Capture(new List<Bitmap>(), captureFps, 0, 0);
        }

        int x = (int)rect.X;
        int y = (int)rect.Y;
        int w = (int)rect.Width  & ~1;
        int h = (int)rect.Height & ~1;

        int frameMs = Math.Max(20, 1000 / Math.Max(1, captureFps));
        int frameCount = Math.Max(1, durationSeconds * 1000 / frameMs);

        var frames = new List<Bitmap>(frameCount);
        var captureCts = new CancellationTokenSource();

        var captureTask = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            int next = 0;
            while (!captureCts.IsCancellationRequested && frames.Count < frameCount)
            {
                int target = next * frameMs;
                int wait = target - (int)sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep(wait);

                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }
                lock (frames) frames.Add(bmp);
                next++;
            }
        });

        if (driveUi is not null)
        {
            try { await driveUi(); }
            catch (Exception ex) { Console.Error.WriteLine($"  (driveUi threw: {ex.Message})"); }
        }

        await captureTask;
        captureCts.Cancel();

        Console.WriteLine($"  captured {frames.Count} frames @ {captureFps}fps ({w}x{h})");
        return new Capture(frames, captureFps, w, h);
    }

    private static void SetFrameDelay(Image img, int hundredths)
    {
        var item = img.PropertyItems.Length > 0
            ? img.PropertyItems[0]
            : (PropertyItem)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(PropertyItem));
        item.Id = 0x5100;
        item.Type = 4;
        item.Len = 4;
        item.Value = BitConverter.GetBytes(hundredths);
        img.SetPropertyItem(item);
    }

    private static void SetLoopCount(Image img, short loops)
    {
        var item = img.PropertyItems.Length > 0
            ? img.PropertyItems[0]
            : (PropertyItem)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(PropertyItem));
        item.Id = 0x5101;
        item.Type = 3;
        item.Len = 2;
        item.Value = BitConverter.GetBytes(loops);
        img.SetPropertyItem(item);
    }
}
