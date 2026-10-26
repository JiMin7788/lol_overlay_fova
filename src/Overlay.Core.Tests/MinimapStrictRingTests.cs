using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// lolF04 t5g (TODO:442): <see cref="StrictRingSettings"/> and its opt-in hook in <see cref="MinimapRingFinder.Find"/>
/// and <see cref="MinimapDetector.Detect"/>. A filled red disc passes the plain ring test on every circle inside it;
/// a real ring has no red just outside its edge. The values here are test values only.
/// </summary>
public class MinimapStrictRingTests
{
    private const int RadiusPx = 12;
    private const int RedMargin = 40;
    private const int Size = 96, C = 48;

    private static readonly (byte R, byte G, byte B) Terrain = (60, 70, 45);   // neither red- nor blue-dominant
    private static readonly (byte R, byte G, byte B) Red = (220, 30, 30);
    private static readonly (byte R, byte G, byte B) Blue = (30, 90, 220);
    private static readonly (byte R, byte G, byte B) Portrait = (120, 110, 100);

    private static readonly StrictRingSettings Strict = new(2, 0.3);

    private static MinimapFrame MakeFrame()
    {
        int stride = Size * 4;
        var bgra = new byte[stride * Size];
        for (int i = 0; i < Size * Size; i++)
        {
            int off = i * 4;
            bgra[off] = Terrain.B;
            bgra[off + 1] = Terrain.G;
            bgra[off + 2] = Terrain.R;
            bgra[off + 3] = 255;
        }
        return new MinimapFrame(bgra, Size, Size, stride, 0, flipped: false);
    }

    private static void DrawDisc(MinimapFrame frame, double radius, (byte R, byte G, byte B) c)
    {
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                double dx = x - C, dy = y - C;
                if (dx * dx + dy * dy > radius * radius) continue;
                int off = frame.PixelOffset(x, y);
                frame.Bgra[off] = c.B;
                frame.Bgra[off + 1] = c.G;
                frame.Bgra[off + 2] = c.R;
                frame.Bgra[off + 3] = 255;
            }
    }

    /// <summary>Red from the centre out to 30 px: past the largest probe (1.30 × 12 = 15.6) plus any offset tested,
    /// so the outer circle is red too — a red portrait running into red surroundings.</summary>
    private static MinimapFrame FilledDisc()
    {
        var frame = MakeFrame();
        DrawDisc(frame, 30, Red);
        return frame;
    }

    /// <summary>A true ring: red from <paramref name="inner"/> to 13 px, portrait inside, terrain outside.</summary>
    private static MinimapFrame TrueRing(double inner)
    {
        var frame = MakeFrame();
        DrawDisc(frame, 13, Red);
        DrawDisc(frame, inner, Portrait);
        return frame;
    }

    /// <summary>The 2 px ring of <see cref="TrueRing"/> plus a red half band (right half, 15.5-17.5 px): clean 2 px
    /// outside the ring (14 px), about half red 4 px outside it (16 px).</summary>
    private static MinimapFrame RingWithOuterBand()
    {
        var frame = TrueRing(11);
        for (int y = 0; y < frame.Height; y++)
            for (int x = C; x < frame.Width; x++)
            {
                double d2 = (x - C) * (x - C) + (y - C) * (y - C);
                if (d2 <= 15.5 * 15.5 || d2 > 17.5 * 17.5) continue;
                int off = frame.PixelOffset(x, y);
                frame.Bgra[off] = Red.B;
                frame.Bgra[off + 1] = Red.G;
                frame.Bgra[off + 2] = Red.R;
            }
        return frame;
    }

    private static IReadOnlyList<MinimapRingFinder.Icon> Find(MinimapFrame frame, StrictRingSettings? strict)
        => MinimapRingFinder.Find(frame, RadiusPx, RedMargin, strict);

    private static double OuterRedShare(MinimapFrame frame, MinimapRingFinder.Icon icon, int offset)
    {
        Assert.True(MinimapRingFinder.RingCoverage(frame, icon.CentreX, icon.CentreY, icon.RadiusPx + offset, RedMargin,
                                                   out double red, out _));
        return red;
    }

    [Fact]
    public void FilledDisc_FoundWithoutStrict()
        => Assert.True(Assert.Single(Find(FilledDisc(), null)).IsEnemy);

    [Fact]
    public void FilledDisc_RejectedUnderStrict()
    {
        Assert.Empty(Find(FilledDisc(), Strict));
        Assert.Empty(Find(FilledDisc(), new StrictRingSettings(4, 0.99)));   // outer share 1.0 > 0.99
    }

    [Theory]
    [InlineData(11.0)]   // 2 px ring
    [InlineData(12.0)]   // 1 px ring
    public void TrueRing_FoundUnderStrict_AsWithout(double inner)
    {
        var plain = Assert.Single(Find(TrueRing(inner), null));
        Assert.True(plain.IsEnemy);
        Assert.Equal(plain, Assert.Single(Find(TrueRing(inner), Strict)));
    }

    /// <summary>The offset decides where "outside" is: 2 px out is terrain, 4 px out is the red band.</summary>
    [Fact]
    public void Offset_SamplesThatFarOutside()
    {
        var frame = RingWithOuterBand();
        var plain = Assert.Single(Find(frame, null));
        Assert.Equal(plain, Assert.Single(Find(frame, new StrictRingSettings(2, 0.3))));
        Assert.Empty(Find(frame, new StrictRingSettings(4, 0.3)));
    }

    /// <summary>A share exactly at MaxOuterShare is kept; any lower limit rejects it. MaxOuterShare 0 keeps a ring whose
    /// outside carries no red at all.</summary>
    [Fact]
    public void Share_AtTheLimitKept_BelowRejected()
    {
        var frame = RingWithOuterBand();
        var plain = Assert.Single(Find(frame, null));
        double share = OuterRedShare(frame, plain, 4);
        Assert.InRange(share, 0.31, 0.99);   // the band is partly on the 16 px circle

        Assert.Equal(plain, Assert.Single(Find(frame, new StrictRingSettings(4, share))));
        Assert.Empty(Find(frame, new StrictRingSettings(4, share - 1e-9)));

        var ring = TrueRing(11);
        Assert.Equal(0.0, OuterRedShare(ring, Assert.Single(Find(ring, null)), 2));
        Assert.Single(Find(ring, new StrictRingSettings(2, 0)));
    }

    [Fact]
    public void Rejects_OnlyAboveTheLimit()
    {
        Assert.False(Strict.Rejects(0.3));
        Assert.True(Strict.Rejects(0.3000001));
        Assert.False(Strict.Rejects(0));
    }

    /// <summary>The team cases of <see cref="MinimapRingFinderTeamTests"/> (ally Gragas: red portrait inside a blue
    /// edge; enemy Kassadin: blue portrait inside a red edge) keep their team under strict: the check reads the
    /// chosen ring's own colour.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TeamCases_KeepTheirTeamUnderStrict(bool enemy)
    {
        var frame = MakeFrame();
        DrawDisc(frame, 13, enemy ? Red : Blue);
        DrawDisc(frame, 11, enemy ? Blue : Red);
        var plain = Assert.Single(Find(frame, null));
        Assert.Equal(enemy, plain.IsEnemy);
        Assert.Equal(plain, Assert.Single(Find(frame, Strict)));
    }

    [Fact]
    public void NullSettings_Unchanged()
    {
        Assert.Null(DetectionOptions.Default.StrictRing);
        foreach (var frame in new[] { FilledDisc(), TrueRing(11), RingWithOuterBand() })
            Assert.Equal(MinimapRingFinder.Find(frame, RadiusPx, RedMargin), Find(frame, null));
    }

    private static readonly EnemyTemplate[] Templates =
    {
        EnemyTemplate.FromSquareIcon(Solid(Red), EnemyTemplate.Size, EnemyTemplate.Size, "ChampA", isEnemy: true),
    };

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

    /// <summary>Detect passes <see cref="DetectionOptions.StrictRing"/> to the ring finder.</summary>
    [Fact]
    public void Detect_PassesStrictRingToFind()
    {
        var options = DetectionOptions.Default with { ExpectedIconRadiusPx = RadiusPx };
        var detector = new MinimapDetector();
        Assert.Equal("ChampA", Assert.Single(detector.Detect(FilledDisc(), Templates, options)).ChampionId);
        Assert.Equal(detector.Detect(FilledDisc(), Templates, options),
                     detector.Detect(FilledDisc(), Templates, options with { StrictRing = null }));
        Assert.Empty(detector.Detect(FilledDisc(), Templates, options with { StrictRing = Strict }));
    }

    /// <summary>t5g built the variant; t5h measured it and did not adopt it (§TODO:442, no valid grid point).</summary>
    // AI-NOTE: renamed from Measured_IsNull_NotYetMeasured, whose premise ended when t5h measured; same assertion.
    [Fact]
    public void Measured_IsNull_NotAdopted()
        => Assert.Null(StrictRingSettings.Measured);

    // AI-NOTE: pinned to the R18 wording; an adopted variant is logged at start as "minimap: detector variant on — " + this.
    [Fact]
    public void Describe_IsTheR18Text()
    {
        Assert.Equal("strict-ring (outer +2 px share ≤ 0.30)", new StrictRingSettings(2, 0.3).Describe());
        Assert.Equal("strict-ring (outer +3 px share ≤ 0.00)", new StrictRingSettings(3, 0).Describe());
    }

    [Theory]
    [InlineData(0, 0.3)]
    [InlineData(-1, 0.3)]
    [InlineData(2, -0.01)]
    [InlineData(2, 1.0)]
    [InlineData(2, 1.5)]
    [InlineData(2, double.NaN)]
    public void Constructor_RejectsInvalidValues(int offset, double share)
        => Assert.Throws<ArgumentException>(() => new StrictRingSettings(offset, share));

    [Fact]
    public void Constructor_AcceptsTheEdges()
    {
        var s = new StrictRingSettings(1, 0);
        Assert.Equal(1, s.OuterOffsetPx);
        Assert.Equal(0, s.MaxOuterShare);
        Assert.Equal(0.999, new StrictRingSettings(1, 0.999).MaxOuterShare);
    }
}
