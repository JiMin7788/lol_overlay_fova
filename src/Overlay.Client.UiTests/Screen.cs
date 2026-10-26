using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Overlay.Client.UiTests;

/// <summary>
/// Window capture, DPI, pixel predicates and OCR. Thresholds given in DIPs must be multiplied by
/// <see cref="Dpi"/> (RECURRING_TRAPS 3.3). Colour distance everywhere is |ΔR|+|ΔG|+|ΔB|.
/// </summary>
public static class Screen
{
    /// <summary>Spec R14: a corner pixel "differs" from the background by more than this.</summary>
    public const int CornerTolerance = 24;

    /// <summary>Spec R15: a grayscale portrait has mean HSV saturation at most this.</summary>
    public const double GrayMaxSaturation = 0.10;

    /// <summary>Spec R15: a grayscale portrait (not a flat fill) has luminance std dev at least this.</summary>
    public const double GrayMinLumaStdDev = 0.06;

    /// <summary>Spec R15 negative control: a living portrait has mean saturation at least this.</summary>
    public const double ColourMinSaturation = 0.15;

    // ── capture ──────────────────────────────────────────────────────────────────────

    /// <summary><c>PrintWindow(PW_RENDERFULLCONTENT)</c> of the whole window; when that fails or comes
    /// back blank (a layered window can), a screen copy of the window's rectangle.</summary>
    public static Bitmap Capture(IntPtr hwnd) => Capture(hwnd, out _);

    /// <inheritdoc cref="Capture(IntPtr)"/>
    /// <param name="method"><c>PrintWindow</c> or <c>screen copy</c>, whichever produced the image.</param>
    public static Bitmap Capture(IntPtr hwnd, out string method)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            throw new InvalidOperationException($"Window 0x{hwnd:X} has no area to capture");

        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppRgb);
        bool printed;
        using (var g = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = g.GetHdc();
            try { printed = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }
        method = "PrintWindow";
        if (printed && !IsBlank(bitmap)) return bitmap;

        using (var g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
        method = "screen copy";
        return bitmap;
    }

    /// <summary>True when every sampled pixel has the same colour.</summary>
    public static bool IsBlank(Bitmap bitmap)
    {
        var first = bitmap.GetPixel(0, 0).ToArgb() & 0xFFFFFF;
        int stepX = Math.Max(1, bitmap.Width / 32), stepY = Math.Max(1, bitmap.Height / 32);
        for (int y = 0; y < bitmap.Height; y += stepY)
            for (int x = 0; x < bitmap.Width; x += stepX)
                if ((bitmap.GetPixel(x, y).ToArgb() & 0xFFFFFF) != first) return false;
        return true;
    }

    /// <summary>The window's DPI scale (1.0 at 96 DPI).</summary>
    public static double Dpi(IntPtr hwnd)
    {
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    // ── predicates ───────────────────────────────────────────────────────────────────

    public static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    /// <summary>True when each of <paramref name="rect"/>'s four outermost corner pixels differs from
    /// <paramref name="background"/> by more than <paramref name="tolerance"/> (a square, not a rounded,
    /// chip).</summary>
    public static bool CornersDifferFrom(Bitmap bitmap, Rectangle rect, Color background, int tolerance = CornerTolerance)
    {
        var corners = new[]
        {
            new Point(rect.Left, rect.Top), new Point(rect.Right - 1, rect.Top),
            new Point(rect.Left, rect.Bottom - 1), new Point(rect.Right - 1, rect.Bottom - 1),
        };
        return corners.All(p => Distance(bitmap.GetPixel(p.X, p.Y), background) > tolerance);
    }

    /// <summary>Mean HSV saturation (0..1) of the pixels inside the circle (outside
    /// <paramref name="holeRadius"/>, when given).</summary>
    public static double MeanSaturation(Bitmap bitmap, Point center, double radius, double holeRadius = 0)
    {
        var values = CirclePixels(bitmap, center, radius, holeRadius).Select(c =>
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max == 0 ? 0.0 : (max - min) / (double)max;
        }).ToList();
        return values.Count == 0 ? 0 : values.Average();
    }

    /// <summary>Standard deviation of luminance (0..1, Rec. 601 weights) inside the circle (outside
    /// <paramref name="holeRadius"/>, when given).</summary>
    public static double LumaStdDev(Bitmap bitmap, Point center, double radius, double holeRadius = 0)
    {
        var luma = CirclePixels(bitmap, center, radius, holeRadius).Select(c => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0).ToList();
        if (luma.Count == 0) return 0;
        double mean = luma.Average();
        return Math.Sqrt(luma.Sum(l => (l - mean) * (l - mean)) / luma.Count);
    }

    private static IEnumerable<Color> CirclePixels(Bitmap bitmap, Point center, double radius, double holeRadius = 0)
    {
        int r = (int)Math.Ceiling(radius);
        for (int y = Math.Max(0, center.Y - r); y <= Math.Min(bitmap.Height - 1, center.Y + r); y++)
            for (int x = Math.Max(0, center.X - r); x <= Math.Min(bitmap.Width - 1, center.X + r); x++)
            {
                double dx = x - center.X, dy = y - center.Y, d2 = dx * dx + dy * dy;
                if (d2 <= radius * radius && d2 >= holeRadius * holeRadius) yield return bitmap.GetPixel(x, y);
            }
    }

    /// <summary>Mean absolute channel difference (0..255) between <paramref name="crop"/> of
    /// <paramref name="bitmap"/> and <paramref name="reference"/> scaled to the crop's size.</summary>
    public static double MeanAbsDiff(Bitmap bitmap, Rectangle crop, Image reference)
    {
        using var scaled = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(reference, new Rectangle(0, 0, crop.Width, crop.Height));
        }
        long sum = 0;
        for (int y = 0; y < crop.Height; y++)
            for (int x = 0; x < crop.Width; x++)
                sum += Distance(bitmap.GetPixel(crop.X + x, crop.Y + y), scaled.GetPixel(x, y));
        return sum / (3.0 * crop.Width * crop.Height);
    }

    /// <summary>Same, against a PNG file (e.g. a cached Data Dragon icon).</summary>
    public static double MeanAbsDiff(Bitmap bitmap, Rectangle crop, string pngPath)
    {
        using var reference = Image.FromFile(pngPath);
        return MeanAbsDiff(bitmap, crop, reference);
    }

    /// <summary>Bounding box of the pixels within <paramref name="tolerance"/> of
    /// <paramref name="color"/>, searched inside <paramref name="within"/> (default: the whole
    /// bitmap); null when there is none.</summary>
    public static Rectangle? Bbox(Bitmap bitmap, Color color, int tolerance, Rectangle? within = null)
    {
        var area = within ?? new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        area.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (Distance(bitmap.GetPixel(x, y), color) <= tolerance)
                {
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
        return maxX < 0 ? null : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    // ── OCR ──────────────────────────────────────────────────────────────────────────

    /// <summary>Windows OCR of <paramref name="rect"/>, upscaled ×3 first (small HUD text). Throws
    /// when the language's OCR engine is not installed.</summary>
    public static string Ocr(Bitmap bitmap, Rectangle rect, string language = "ko")
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language(language))
            ?? throw new InvalidOperationException($"Windows OCR language '{language}' is not installed");

        using var upscaled = new Bitmap(rect.Width * 3, rect.Height * 3, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(upscaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bitmap, new Rectangle(0, 0, upscaled.Width, upscaled.Height), rect, GraphicsUnit.Pixel);
        }

        using var software = ToSoftwareBitmap(upscaled);
        var result = engine.RecognizeAsync(software).AsTask().GetAwaiter().GetResult();
        return result.Text;
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var software = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Premultiplied);
            software.CopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            return software;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
