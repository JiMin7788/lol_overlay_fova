using Overlay.Core.Render;
using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// M31 P1 calibration layer 2: <see cref="MinimapAutoCalibrator"/> geometry over synthetic BGRA
/// frames with a border drawn at KNOWN pixel coordinates (M31 §7 "synthetic frames, border at
/// known px"). The border color lives in an injected predicate, so these tests verify the
/// rectangle-finding math, not any live color tuning.
/// </summary>
public class MinimapAutoCalibratorTests
{
    // Border = near-white; anything else is background.
    private static bool IsWhite(byte b, byte g, byte r, byte a) => b > 200 && g > 200 && r > 200;

    private static MinimapFrame BlankFrame(int w, int h, int extraStride = 0)
    {
        int stride = w * 4 + extraStride;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                buf[y * stride + x * 4 + 3] = 255; // opaque black background
        return new MinimapFrame(buf, w, h, stride, timestampMs: 0, flipped: false);
    }

    private static void SetWhite(MinimapFrame f, int x, int y)
    {
        int o = f.PixelOffset(x, y);
        f.Bgra[o] = f.Bgra[o + 1] = f.Bgra[o + 2] = 255;
        f.Bgra[o + 3] = 255;
    }

    private static void DrawBorder(MinimapFrame f, int x0, int y0, int x1, int y1)
    {
        for (int x = x0; x <= x1; x++) { SetWhite(f, x, y0); SetWhite(f, x, y1); }
        for (int y = y0; y <= y1; y++) { SetWhite(f, x0, y); SetWhite(f, x1, y); }
    }

    [Fact]
    public void Refine_FindsBorderRect_ExactlyAtKnownPixels()
    {
        var frame = BlankFrame(100, 80);
        DrawBorder(frame, 10, 8, 70, 60);

        var result = MinimapAutoCalibrator.Refine(frame, searchRegion: default, IsWhite);

        Assert.True(result.Refined);
        // Inclusive edges → width = 70-10+1 = 61, height = 60-8+1 = 53. 0 px error (≤2 accept).
        Assert.Equal(10, result.Rect.X, precision: 6);
        Assert.Equal(8, result.Rect.Y, precision: 6);
        Assert.Equal(61, result.Rect.Width, precision: 6);
        Assert.Equal(53, result.Rect.Height, precision: 6);
    }

    [Fact]
    public void Refine_WorksWithPaddedStride()
    {
        var frame = BlankFrame(100, 80, extraStride: 20); // rows padded past width*4
        DrawBorder(frame, 12, 10, 64, 55);

        var result = MinimapAutoCalibrator.Refine(frame, searchRegion: default, IsWhite);

        Assert.True(result.Refined);
        Assert.Equal(12, result.Rect.X, precision: 6);
        Assert.Equal(10, result.Rect.Y, precision: 6);
        Assert.Equal(64 - 12 + 1, result.Rect.Width, precision: 6);
        Assert.Equal(55 - 10 + 1, result.Rect.Height, precision: 6);
    }

    [Fact]
    public void Refine_RejectsStraySpecks_ThatDoNotFillARowOrColumn()
    {
        var frame = BlankFrame(100, 80);
        DrawBorder(frame, 10, 8, 70, 60);
        // A handful of isolated white pixels outside the border — must not move the result.
        SetWhite(frame, 2, 2);
        SetWhite(frame, 95, 3);
        SetWhite(frame, 90, 75);

        var result = MinimapAutoCalibrator.Refine(frame, searchRegion: default, IsWhite);

        Assert.True(result.Refined);
        Assert.Equal(10, result.Rect.X, precision: 6);
        Assert.Equal(70 - 10 + 1, result.Rect.Width, precision: 6);
    }

    [Fact]
    public void Refine_NoBorder_ReturnsNotRefined()
    {
        var frame = BlankFrame(60, 40); // blank, no border

        var result = MinimapAutoCalibrator.Refine(frame, searchRegion: default, IsWhite);

        Assert.False(result.Refined);
    }

    [Fact]
    public void Refine_RespectsAnExplicitSearchRegion()
    {
        var frame = BlankFrame(120, 100);
        DrawBorder(frame, 30, 25, 90, 80);

        // Search region loosely around the border still finds it exactly.
        var region = new RenderBounds(20, 15, 85, 75);
        var result = MinimapAutoCalibrator.Refine(frame, region, IsWhite);

        Assert.True(result.Refined);
        Assert.Equal(30, result.Rect.X, precision: 6);
        Assert.Equal(25, result.Rect.Y, precision: 6);
    }

    [Fact]
    public void Refine_NullPredicate_Throws()
    {
        var frame = BlankFrame(20, 20);
        Assert.Throws<ArgumentNullException>(
            () => MinimapAutoCalibrator.Refine(frame, default, null!));
    }
}
