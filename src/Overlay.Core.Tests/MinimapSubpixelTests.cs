using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// lolF04 t5i (R19, TODO:595/590): <see cref="SubpixelSettings"/>, the bilinear sampler and the opt-in hook in
/// <see cref="MinimapDetector.Detect"/>. The icon is a red ring centred on a half pixel (its seed centroid is exactly
/// x.5) over a grey ramp 8 units per px, so a crop's colours say exactly where it was taken. The values here are test
/// values only.
/// </summary>
public class MinimapSubpixelTests
{
    private const int Size = 96;
    private const double RingCentre = 48.5;
    private const double RadiusPx = 10;        // crop side 20 → ratio 1.25 to the 16 px template
    private static readonly (byte R, byte G, byte B) Red = (220, 30, 30);

    /// <summary>Grey ramp: 8 × (x − 34), clamped to 0-255; linear on every pixel a masked crop sample reads.</summary>
    private static byte Ramp(double x) => (byte)Math.Clamp(Math.Round(8 * (x - 34)), 0, 255);

    private static MinimapFrame IconFrame()
    {
        int stride = Size * 4;
        var bgra = new byte[stride * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int off = y * stride + x * 4;
                double dx = x - RingCentre, dy = y - RingCentre, d2 = dx * dx + dy * dy;
                // Red annulus 12.2-14 px: the 1.30 × 10 probe lands on it; a masked crop sample never reaches it.
                var (r, g, b) = d2 >= 12.2 * 12.2 && d2 <= 14 * 14 ? Red : (Ramp(x), Ramp(x), Ramp(x));
                bgra[off] = b;
                bgra[off + 1] = g;
                bgra[off + 2] = r;
                bgra[off + 3] = 255;
            }
        return new MinimapFrame(bgra, Size, Size, stride, 0, flipped: false);
    }

    /// <summary>The icon's art as an exactly aligned bilinear crop sees it: the ramp at each template pixel's sample
    /// point (origin centre − 10, the R19 fractional origin), shifted by <paramref name="shiftPx"/> in x.</summary>
    private static EnemyTemplate RampTemplate(string id, double centreX, double shiftPx)
    {
        const int n = EnemyTemplate.Size;
        var bgra = new byte[n * n * 4];
        for (int ty = 0; ty < n; ty++)
            for (int tx = 0; tx < n; tx++)
            {
                double sx = centreX - RadiusPx + (tx + 0.5) * (2 * RadiusPx / n) - 0.5 + shiftPx;
                byte v = Ramp(sx);
                int off = (ty * n + tx) * 4;
                bgra[off] = bgra[off + 1] = bgra[off + 2] = v;
                bgra[off + 3] = 255;
            }
        return EnemyTemplate.FromSquareIcon(bgra, n, n, id, isEnemy: true);
    }

    private static readonly DetectionOptions Options = DetectionOptions.Default with { ExpectedIconRadiusPx = RadiusPx };
    private static readonly SubpixelSettings Bilinear = new(true, 0);
    private static readonly SubpixelSettings HalfStep = new(true, 0.5);

    /// <summary>The fixture's one enemy icon, centred on a half pixel in x and y.</summary>
    private static MinimapRingFinder.Icon TheIcon(MinimapFrame frame)
    {
        var icon = Assert.Single(MinimapRingFinder.Find(frame, RadiusPx, Options.RedDominanceMargin));
        Assert.True(icon.IsEnemy);
        Assert.Equal(0.5, icon.CentreX - Math.Floor(icon.CentreX));
        Assert.Equal(0.5, icon.CentreY - Math.Floor(icon.CentreY));
        return icon;
    }

    private static IReadOnlyList<MinimapSighting> Detect(
        MinimapFrame frame, EnemyTemplate[] templates, SubpixelSettings? subpixel,
        AssignmentMode assignment = AssignmentMode.PerCandidate)
        => new MinimapDetector().Detect(frame, templates, Options with { Subpixel = subpixel, Assignment = assignment });

    /// <summary>B = 5x, G = 5y: sampling (2tx + 1.75, 2ty + 3) gives B 10tx + 8.75 → 10tx + 9 and G 10ty + 15.</summary>
    [Fact]
    public void Bilinear_OnAGradient_GivesTheExactValues()
    {
        const int w = 48, h = 48, stride = w * 4;
        var src = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int off = y * stride + x * 4;
                src[off] = (byte)(5 * x);
                src[off + 1] = (byte)(5 * y);
                src[off + 3] = 255;
            }

        byte[] crop = MinimapPixelSampling.BilinearDownscale(src, stride, w, h, 1.25, 2.5, 32, 16);
        Assert.Equal(16 * 16 * 4, crop.Length);
        for (int ty = 0; ty < 16; ty++)
            for (int tx = 0; tx < 16; tx++)
            {
                int off = (ty * 16 + tx) * 4;
                Assert.Equal(10 * tx + 9, crop[off]);
                Assert.Equal(10 * ty + 15, crop[off + 1]);
                Assert.Equal(0, crop[off + 2]);
                Assert.Equal(255, crop[off + 3]);
            }

        // A sample point left of the frame reads the clamped edge column: x −1 and 0 are both column 0.
        byte[] edge = MinimapPixelSampling.BilinearDownscale(src, stride, w, h, -1, 0, 16, 16);
        Assert.Equal(new byte[] { 0, 0, 5, 10 }, new[] { edge[0], edge[4], edge[8], edge[12] });
    }

    [Fact]
    public void HalfPixelIcon_ScoresHigherWithBilinearThanDefault()
    {
        var frame = IconFrame();
        var templates = new[] { RampTemplate("ChampA", TheIcon(frame).CentreX, 0) };

        double baseline = Assert.Single(Detect(frame, templates, null)).Confidence;
        double bilinear = Assert.Single(Detect(frame, templates, Bilinear)).Confidence;
        Assert.Equal(1.0, bilinear, 12);   // the fractional origin samples exactly where the art was taken
        Assert.True(bilinear > baseline, $"bilinear {bilinear} <= default {baseline}");
    }

    /// <summary>Art taken half a pixel to the right: bilinear alone is 4 units off on every channel, the ½ px search
    /// finds the +0.5 px crop.</summary>
    [Fact]
    public void OffsetSearch_FindsAOneStepShift()
    {
        var frame = IconFrame();
        var icon = TheIcon(frame);
        var templates = new[] { RampTemplate("ChampA", icon.CentreX, 0.5) };

        Assert.Equal(1 - 4 * Math.Sqrt(3) / 441.6729559300637,
                     Assert.Single(Detect(frame, templates, Bilinear)).Confidence, 12);
        Assert.Equal(1.0, Assert.Single(Detect(frame, templates, HalfStep)).Confidence, 12);

        var (score, crop) = Assert.Single(MinimapDetector.ScoreCandidate(frame, icon.CentreX, icon.CentreY,
                                                                         Options with { Subpixel = HalfStep }, templates));
        Assert.Equal(1.0, score, 12);
        // The ramp runs in x only, so every dy at dx +0.5 scores 1.0; a tie keeps the first crop (dy outer, ascending).
        Assert.Equal(MinimapPixelSampling.BilinearDownscale(frame.Bgra, frame.Stride, Size, Size,
                         icon.CentreX + 0.5 - RadiusPx, icon.CentreY - 1 - RadiusPx, 20, EnemyTemplate.Size), crop);
    }

    /// <summary>The margin is between per-template bests: a template 1 px off loses by 0.031 ≥ 0.03 to the aligned one
    /// under bilinear alone, but the search lifts it to 1.0 as well (max over offsets raises wrong answers too), so the
    /// margin is 0 and no identity is emitted — on both stage-2 paths.</summary>
    [Theory]
    [InlineData(AssignmentMode.PerCandidate)]
    [InlineData(AssignmentMode.Global)]
    public void OffsetSearch_MarginIsBetweenPerTemplateBests(AssignmentMode assignment)
    {
        var frame = IconFrame();
        double cx = TheIcon(frame).CentreX;
        var templates = new[] { RampTemplate("Aligned", cx, 0), RampTemplate("OnePxOff", cx, 1) };

        var bilinear = Assert.Single(Detect(frame, templates, Bilinear, assignment));
        Assert.Equal("Aligned", bilinear.ChampionId);
        Assert.Equal(1.0, bilinear.Confidence, 12);
        Assert.Empty(Detect(frame, templates, HalfStep, assignment));
    }

    [Fact]
    public void NullSettings_Unchanged()
    {
        Assert.Null(DetectionOptions.Default.Subpixel);
        var frame = IconFrame();
        var templates = new[] { RampTemplate("ChampA", TheIcon(frame).CentreX, 0), RampTemplate("ChampB", 48.5, 1) };
        var plain = new MinimapDetector().Detect(frame, templates, DetectionOptions.Default with { ExpectedIconRadiusPx = RadiusPx });
        Assert.Equal(plain, Detect(frame, templates, null));
        // Bilinear false is today's crop as well.
        Assert.Equal(plain, Detect(frame, templates, new SubpixelSettings(false, 0)));
    }

    [Fact]
    public void Offsets_SpanMinusOneToOneAtTheStep()
    {
        Assert.Equal(new[] { 0.0 }, Bilinear.Offsets());
        Assert.Equal(new[] { -1, -0.5, 0, 0.5, 1 }, HalfStep.Offsets());
        double[] third = new SubpixelSettings(true, 1.0 / 3).Offsets();
        Assert.Equal(7, third.Length);
        Assert.Equal(-1.0, third[0]);
        Assert.Equal(0.0, third[3]);
        Assert.Equal(1.0, third[6]);
    }

    /// <summary>The adoption decision (bilinear-only or with offsets) is t5j's, after measure 2.</summary>
    [Fact]
    public void Measured_IsNull_NotYetDecided()
        => Assert.Null(SubpixelSettings.Measured);

    // AI-NOTE: pinned to the R19 wording; an adopted variant is logged at start as "minimap: detector variant on — " + this.
    [Fact]
    public void Describe_IsTheR19Text()
    {
        Assert.Equal("subpixel (bilinear)", Bilinear.Describe());
        Assert.Equal("subpixel (bilinear, offsets ±1 px step 0.50)", HalfStep.Describe());
        Assert.Equal("subpixel (bilinear, offsets ±1 px step 0.33)", new SubpixelSettings(true, 1.0 / 3).Describe());
    }

    [Theory]
    [InlineData(true, 0.25)]
    [InlineData(true, -0.5)]
    [InlineData(true, 1.0)]
    [InlineData(true, 0.333)]
    [InlineData(true, double.NaN)]
    [InlineData(false, 0.5)]           // an offset needs the bilinear sampler (§80 C-3)
    [InlineData(false, 1.0 / 3)]
    public void Constructor_RejectsInvalidValues(bool bilinear, double step)
        => Assert.Throws<ArgumentException>(() => new SubpixelSettings(bilinear, step));

    [Theory]
    [InlineData(true, 0.0)]
    [InlineData(true, 0.5)]
    [InlineData(true, 1.0 / 3)]
    [InlineData(false, 0.0)]
    public void Constructor_KeepsTheValues(bool bilinear, double step)
    {
        var s = new SubpixelSettings(bilinear, step);
        Assert.Equal(bilinear, s.Bilinear);
        Assert.Equal(step, s.OffsetStepPx);
    }
}
