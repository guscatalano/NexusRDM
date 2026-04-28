using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core.AutomationElements;

namespace NexusRDM.DemoRecorder;

/// <summary>
/// Pure-managed animated GIF recorder. Captures frames via
/// <see cref="Graphics.CopyFromScreen"/> on a background timer, then
/// assembles them with GDI+'s built-in GIF encoder
/// (<c>Image.SaveAdd</c> + multi-frame <see cref="FrameDimension.Time"/>).
/// No ffmpeg, no extra NuGet packages.
///
/// Quirks worth knowing about:
/// 1. GDI+ writes one frame at a time and quantises to a 256-colour
///    palette per frame. Quality is fine for UI walkthroughs but a
///    photo-style source would dither badly.
/// 2. Per-frame delay is set via PropertyItem 0x5100 (FrameDelay),
///    expressed in 1/100ths of a second. A loop count of 0 (infinite)
///    is set via 0x5101 (LoopCount) on the first frame.
/// 3. Recording-region capture is done in a background timer thread
///    so the foreground thread can still drive UIA actions.
/// </summary>
internal static class GifRecorder
{
    public static async Task RecordAsync(
        AutomationElement element,
        string outPath,
        int durationSeconds,
        int targetFps = 10,
        Func<Task>? driveUi = null)
    {
        var rect = element.BoundingRectangle;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            Console.WriteLine($"  (skipped {Path.GetFileName(outPath)} — element has no bounds)");
            return;
        }

        // Round capture area down to even dimensions — some GIF
        // viewers misrender odd-sized frames.
        int x = (int)rect.X;
        int y = (int)rect.Y;
        int w = (int)rect.Width  & ~1;
        int h = (int)rect.Height & ~1;

        // Downscale if huge — keeps the GIF under a few MB. Cap the
        // longer side at 960px which is plenty for README embeds.
        int maxDim = 960;
        double scale = Math.Min(1.0, (double)maxDim / Math.Max(w, h));
        int outW = Math.Max(2, (int)(w * scale) & ~1);
        int outH = Math.Max(2, (int)(h * scale) & ~1);

        int frameMs = Math.Max(50, 1000 / Math.Max(1, targetFps));
        int frameCount = Math.Max(1, durationSeconds * 1000 / frameMs);

        var frames = new List<Bitmap>(frameCount);
        var captureCts = new CancellationTokenSource();

        var captureTask = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int next = 0;
            while (!captureCts.IsCancellationRequested && frames.Count < frameCount)
            {
                int target = next * frameMs;
                int wait = target - (int)sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep(wait);

                var bmp = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    if (outW == w && outH == h)
                        g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                    else
                    {
                        using var raw = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        using (var rg = Graphics.FromImage(raw))
                            rg.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                        g.DrawImage(raw, new Rectangle(0, 0, outW, outH));
                    }
                }
                lock (frames) frames.Add(bmp);
                next++;
            }
        });

        // Drive the UI in parallel with capture, if the caller gave us
        // an action. Otherwise just sit out the duration.
        if (driveUi is not null)
        {
            try { await driveUi(); } catch (Exception ex)
            {
                Console.Error.WriteLine($"  (driveUi threw: {ex.Message})");
            }
        }

        await captureTask;
        captureCts.Cancel();

        if (frames.Count == 0)
        {
            Console.WriteLine($"  (no frames captured for {Path.GetFileName(outPath)})");
            return;
        }

        // Stitch frames into an animated GIF via GDI+.
        var gifEncoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Gif.Guid)
            ?? throw new InvalidOperationException("GIF encoder not available.");

        var encoderParams = new EncoderParameters(1);
        // MultiFrame on the first SaveAdd, FrameDimensionTime on the rest.
        encoderParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);

        var firstFrame = frames[0];
        SetFrameDelay(firstFrame, frameMs / 10);   // 1/100ths sec
        SetLoopCount(firstFrame, 0);                // 0 = loop forever

        firstFrame.Save(outPath, gifEncoder, encoderParams);

        var addParams = new EncoderParameters(1);
        addParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);

        for (int i = 1; i < frames.Count; i++)
        {
            SetFrameDelay(frames[i], frameMs / 10);
            firstFrame.SaveAdd(frames[i], addParams);
        }

        var finishParams = new EncoderParameters(1);
        finishParams.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
        firstFrame.SaveAdd(finishParams);

        foreach (var f in frames) f.Dispose();
        var size = new FileInfo(outPath).Length;
        Console.WriteLine($"  → {Path.GetFileName(outPath)} ({outW}x{outH}, {frames.Count} frames, {size / 1024} KB)");
    }

    private static void SetFrameDelay(Image img, int hundredths)
    {
        // PropertyItem ctor is internal, so we steal one from the image
        // itself and overwrite its fields. PropertyTagFrameDelay = 0x5100,
        // and the value is a 4-byte little-endian int per frame.
        var item = img.PropertyItems.Length > 0
            ? img.PropertyItems[0]
            : (PropertyItem)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(PropertyItem));
        item.Id = 0x5100;
        item.Type = 4; // PropertyTagTypeLong
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
        item.Type = 3; // PropertyTagTypeShort
        item.Len = 2;
        item.Value = BitConverter.GetBytes(loops);
        img.SetPropertyItem(item);
    }
}
