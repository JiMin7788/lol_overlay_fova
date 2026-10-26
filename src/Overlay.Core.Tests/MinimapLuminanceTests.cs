using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// lolF04 t5f (TODO:346): <see cref="LuminanceSettings"/> and its opt-in hook in <see cref="MinimapDetector.Detect"/>.
/// One red ring whose inside is a brighter (or darker) fill than the template, like a red ring over a bright
/// bracket or fountain. The values here are test values only.
/// </summary>
public class MinimapLuminanceTests
{
    private static readonly (byte R, byte G, byte B) Background = (30, 30, 30);
    private static readonly (byte R, byte G, byte B) Ring = (220, 20, 20);
    // Inside the ring: +58 luma over Ring, still within the 0.75 threshold of the Ring-coloured template.
    private static readonly (byte R, byte G, byte B) BrightFill = (250, 90, 90);
    private static readonly (byte R, byte G, byte B) DarkFill = (150, 10, 10);

    private static MinimapFrame MakeFrame(int width, int height)
    {
        int stride = width * 4;
        var bgra = new byte[stride * height];
        for (int i = 0; i < width * height; i++)
        {
            int off = i * 4;
            bgra[off] = Background.B;
            bgra[off + 1] = Background.G;
            bgra[off + 2] = Background.R;
            bgra[off + 3] = 255;
        }
        return new MinimapFrame(bgra, width, height, stride, 0, flipped: false);
    }

    private static void DrawFilledDisc(MinimapFrame frame, int cx, int cy, int radius, (byte R, byte G, byte B) color)
    {
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > radius * radius) continue;
                int off = frame.PixelOffset(x, y);
                frame.Bgra[off] = color.B;
                frame.Bgra[off + 1] = color.G;
                frame.Bgra[off + 2] = color.R;
                frame.Bgra[off + 3] = 255;
            }
    }

    private static byte[] Solid((byte R, byte G, byte B) color)
    {
        const int size = EnemyTemplate.Size;
        var bgra = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            int off = i * 4;
            bgra[off] = color.B;
            bgra[off + 1] = color.G;
            bgra[off + 2] = color.R;
            bgra[off + 3] = 255;
        }
        return bgra;
    }

    private static EnemyTemplate MakeSolidTemplate(string championId, (byte R, byte G, byte B) color)
        => EnemyTemplate.FromSquareIcon(Solid(color), EnemyTemplate.Size, EnemyTemplate.Size, championId, isEnemy: true);

    private static MinimapFrame RingWithFill((byte R, byte G, byte B) fill)
    {
        var frame = MakeFrame(120, 120);
        DrawFilledDisc(frame, 60, 60, 11, Ring);
        DrawFilledDisc(frame, 60, 60, 7, fill);
        return frame;
    }

    private static readonly EnemyTemplate[] Templates = { MakeSolidTemplate("ChampA", Ring) };

    private static IReadOnlyList<MinimapSighting> Detect(MinimapFrame frame, LuminanceSettings? luminance)
        => new MinimapDetector().Detect(frame, Templates, DetectionOptions.Default with { Luminance = luminance });

    [Fact]
    public void Fixture_BrightFill_IsIdentifiedWithoutTheRule()
        => Assert.Equal("ChampA", Assert.Single(Detect(RingWithFill(BrightFill), null)).ChampionId);

    [Fact]
    public void BrightCrop_OverTheLimit_NoIdentity()
        => Assert.Empty(Detect(RingWithFill(BrightFill), new LuminanceSettings(20)));

    [Fact]
    public void BrightCrop_UnderALargerLimit_Identity()
        => Assert.Equal(Detect(RingWithFill(BrightFill), null), Detect(RingWithFill(BrightFill), new LuminanceSettings(100)));

    /// <summary>A crop darker than its template has a negative excess, so no limit removes it.</summary>
    [Fact]
    public void DarkerCrop_Unchanged()
    {
        var baseline = Detect(RingWithFill(DarkFill), null);
        Assert.Equal("ChampA", Assert.Single(baseline).ChampionId);
        Assert.Equal(baseline, Detect(RingWithFill(DarkFill), new LuminanceSettings(1)));
    }

    /// <summary>The global-assignment path applies the same rule.</summary>
    [Fact]
    public void GlobalAssignment_BrightCrop_NoIdentity()
    {
        var options = DetectionOptions.Default with { Assignment = AssignmentMode.Global };
        Assert.Single(new MinimapDetector().Detect(RingWithFill(BrightFill), Templates, options));
        Assert.Empty(new MinimapDetector().Detect(RingWithFill(BrightFill), Templates,
                                                  options with { Luminance = new LuminanceSettings(20) }));
    }

    [Fact]
    public void NullSettings_Unchanged()
    {
        Assert.Null(DetectionOptions.Default.Luminance);
        Assert.Equal(new MinimapDetector().Detect(RingWithFill(BrightFill), Templates, DetectionOptions.Default),
                     Detect(RingWithFill(BrightFill), null));
    }

    /// <summary>Rec.601 luma: grey (120,120,120) over a grey (100,100,100) template is 20 brighter.</summary>
    [Fact]
    public void Excess_IsTheMeanLumaDifferenceOverTheMask()
    {
        var template = MakeSolidTemplate("Grey", (100, 100, 100));
        Assert.Equal(20.0, LuminanceSettings.Excess(Solid((120, 120, 120)), template), precision: 9);
        Assert.Equal(-0.299 * 50, LuminanceSettings.Excess(Solid((50, 100, 100)), template), precision: 9);
    }

    [Fact]
    public void Rejects_OnlyAboveTheLimit()
    {
        var lum = new LuminanceSettings(20);
        Assert.True(lum.Rejects(20.01));
        Assert.False(lum.Rejects(20));      // at the limit: kept
        Assert.False(lum.Rejects(-40));
    }

    /// <summary>docs/reports/minimap_lolF04_variants.md §TODO:346 ends "Result: measured, not adopted".</summary>
    [Fact]
    public void Measured_IsNull_NotAdopted()
        => Assert.Null(LuminanceSettings.Measured);

    // AI-NOTE: pinned to the R17 wording; an adopted variant is logged at start as "minimap: detector variant on — " + this.
    [Fact]
    public void Describe_IsTheR17Text()
        => Assert.Equal("luminance (excess ≤ 20)", new LuminanceSettings(20).Describe());

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void Constructor_RejectsInvalidValues(double maxExcess)
        => Assert.Throws<ArgumentException>(() => new LuminanceSettings(maxExcess));

    [Fact]
    public void Constructor_KeepsTheValue()
        => Assert.Equal(12.5, new LuminanceSettings(12.5).MaxExcess);
}
