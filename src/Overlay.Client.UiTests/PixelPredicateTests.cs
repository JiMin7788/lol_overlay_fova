using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Overlay.Client.UiTests;

/// <summary>
/// The <see cref="Screen"/> predicates on synthetic bitmaps, each with a case it must reject. Plain
/// facts (no Fova, no input), so they run on every <c>dotnet test</c>.
/// </summary>
public sealed class PixelPredicateTests
{
    private static readonly Color Background = OverlayPalette.Opaque(OverlayPalette.OverlayPanelBg);
    private static readonly Color Chip = OverlayPalette.Opaque(OverlayPalette.SlotQ);

    private static Bitmap Canvas(int w = 64, int h = 64, Color? fill = null)
    {
        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(fill ?? Background);
        return bitmap;
    }

    private static readonly Rectangle ChipRect = new(16, 16, 32, 32);

    // ── CornersDifferFrom ────────────────────────────────────────────────────────────

    [Fact]
    public void CornersDifferFrom_SquareChip_Passes()
    {
        using var bitmap = Canvas();
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Chip))
            g.FillRectangle(brush, ChipRect);

        Assert.True(Screen.CornersDifferFrom(bitmap, ChipRect, Background));
    }

    [Fact]
    public void CornersDifferFrom_RoundedCornerChip_Fails()
    {
        using var bitmap = Canvas();
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Chip))
        using (var path = RoundedRect(ChipRect, 6))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(brush, path);
        }

        Assert.False(Screen.CornersDifferFrom(bitmap, ChipRect, Background));
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ── MeanSaturation / LumaStdDev (spec R15 thresholds) ────────────────────────────

    private static readonly Point DiscCenter = new(32, 32);
    private const double DiscRadius = 20;

    /// <summary>A disc whose pixels follow <paramref name="colourAt"/> (x, y) inside the radius.</summary>
    private static Bitmap Disc(Func<int, int, Color> colourAt)
    {
        var bitmap = Canvas();
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                double dx = x - DiscCenter.X, dy = y - DiscCenter.Y;
                if (dx * dx + dy * dy <= DiscRadius * DiscRadius) bitmap.SetPixel(x, y, colourAt(x, y));
            }
        return bitmap;
    }

    /// <summary>A grayscale silhouette: gray levels that vary across the face.</summary>
    private static Bitmap GrayDisc() => Disc((x, y) =>
    {
        int v = 40 + (x * 5 + y * 3) % 160;
        return Color.FromArgb(v, v, v);
    });

    /// <summary>A living portrait: saturated colours.</summary>
    private static Bitmap ColourDisc() => Disc((x, y) =>
        (x + y) % 2 == 0 ? Color.FromArgb(200, 60, 40) : Color.FromArgb(40, 160, 220));

    [Fact]
    public void MeanSaturation_GrayDisc_IsWithinTheGrayscaleThreshold()
    {
        using var bitmap = GrayDisc();
        Assert.True(Screen.MeanSaturation(bitmap, DiscCenter, DiscRadius) <= Screen.GrayMaxSaturation);
    }

    [Fact]
    public void MeanSaturation_ColourDisc_FailsTheGrayscaleThreshold()
    {
        using var bitmap = ColourDisc();
        double saturation = Screen.MeanSaturation(bitmap, DiscCenter, DiscRadius);
        Assert.False(saturation <= Screen.GrayMaxSaturation);
        Assert.True(saturation >= Screen.ColourMinSaturation);
    }

    [Fact]
    public void LumaStdDev_TexturedGrayDisc_PassesTheGrayscaleThreshold()
    {
        using var bitmap = GrayDisc();
        Assert.True(Screen.LumaStdDev(bitmap, DiscCenter, DiscRadius) >= Screen.GrayMinLumaStdDev);
    }

    [Fact]
    public void LumaStdDev_FlatGrayDisc_FailsTheGrayscaleThreshold()
    {
        // A flat fill is gray but no silhouette: the luminance threshold rejects it.
        using var bitmap = Disc((_, _) => Color.FromArgb(128, 128, 128));
        Assert.True(Screen.MeanSaturation(bitmap, DiscCenter, DiscRadius) <= Screen.GrayMaxSaturation);
        Assert.False(Screen.LumaStdDev(bitmap, DiscCenter, DiscRadius) >= Screen.GrayMinLumaStdDev);
    }

    [Fact]
    public void ColourDisc_FailsTheCombinedGrayscaleCheck()
    {
        using var bitmap = ColourDisc();
        bool grayscale = Screen.MeanSaturation(bitmap, DiscCenter, DiscRadius) <= Screen.GrayMaxSaturation
            && Screen.LumaStdDev(bitmap, DiscCenter, DiscRadius) >= Screen.GrayMinLumaStdDev;
        Assert.False(grayscale);
    }

    // ── MeanAbsDiff ──────────────────────────────────────────────────────────────────

    /// <summary>An "icon": a diagonal two-colour split.</summary>
    private static Bitmap Icon(Color a, Color b, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                bitmap.SetPixel(x, y, x > y ? a : b);
        return bitmap;
    }

    [Fact]
    public void MeanAbsDiff_SameIconAtAnotherSize_IsSmall()
    {
        using var reference = Icon(Color.Gold, Color.DarkBlue, 64);
        using var screen = Canvas();
        using (var g = Graphics.FromImage(screen))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(reference, ChipRect);
        }

        Assert.True(Screen.MeanAbsDiff(screen, ChipRect, reference) <= 40);
    }

    [Fact]
    public void MeanAbsDiff_DifferentIcon_IsLarge()
    {
        using var reference = Icon(Color.Gold, Color.DarkBlue, 64);
        using var other = Icon(Color.DarkBlue, Color.Gold, 32); // the split swapped
        using var screen = Canvas();
        using (var g = Graphics.FromImage(screen))
            g.DrawImage(other, ChipRect);

        Assert.True(Screen.MeanAbsDiff(screen, ChipRect, reference) > 40);
    }

    // ── Bbox ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bbox_FindsTheColouredRectangle()
    {
        var gold = OverlayPalette.Opaque(OverlayPalette.AccentGold);
        var target = new Rectangle(10, 20, 15, 7);
        using var bitmap = Canvas();
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(gold))
            g.FillRectangle(brush, target);

        Assert.Equal(target, Screen.Bbox(bitmap, gold, 10));
    }

    [Fact]
    public void Bbox_AbsentOrFarColour_IsNull()
    {
        var gold = OverlayPalette.Opaque(OverlayPalette.AccentGold);
        using var bitmap = Canvas();
        using (var g = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(Color.FromArgb(gold.R - 20, gold.G - 20, gold.B - 20)))
            g.FillRectangle(brush, 10, 20, 15, 7);

        Assert.Null(Screen.Bbox(bitmap, gold, 10)); // 60 away, outside the tolerance
        Assert.NotNull(Screen.Bbox(bitmap, gold, 60));
    }

    // ── IsBlank (capture fallback trigger) ───────────────────────────────────────────

    [Fact]
    public void IsBlank_UniformImageOnly()
    {
        using var blank = Canvas(fill: Color.Black);
        Assert.True(Screen.IsBlank(blank));

        using var drawn = Canvas(fill: Color.Black);
        drawn.SetPixel(0, 32, Color.White);
        Assert.False(Screen.IsBlank(drawn));
    }
}
