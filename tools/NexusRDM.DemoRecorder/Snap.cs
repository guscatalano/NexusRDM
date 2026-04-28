using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core.AutomationElements;

namespace NexusRDM.DemoRecorder;

/// <summary>
/// PNG snapshot helpers. We grab pixels via GDI <c>BitBlt</c>-style
/// <see cref="Graphics.CopyFromScreen"/> rather than FlaUI's built-in
/// Capture utilities — those rasterise the window's UI tree from
/// the UIA cache and miss WinUI 3's compositor-rendered visuals
/// (XAML islands, the connections tree icons, etc.). Capturing
/// straight from the screen-buffer guarantees what the user actually
/// sees lands in the PNG.
/// </summary>
internal static class Snap
{
    public static void Window(AutomationElement element, string path)
    {
        var rect = element.BoundingRectangle;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            Console.WriteLine($"  (skipped {Path.GetFileName(path)} — element has no bounds)");
            return;
        }
        Region(
            (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height,
            path);
    }

    public static void Region(int x, int y, int width, int height, string path)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"  → {Path.GetFileName(path)} ({width}x{height})");
    }
}
