using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// lolF04 t2a: <see cref="MinimapCandidateGate"/> (size band against E = K x ROI width, fixed-structure
/// rule) and its opt-in hook in <see cref="MinimapDetector.Detect"/>. The values here are test values
/// only; the measured ones are lolF04 t2b's.
/// </summary>
public class MinimapCandidateGateTests
{
    private const double K = 0.04464, Lo = 0.85, Hi = 1.35;
    private static readonly SizeGate Band = new(K, Lo, Hi);
    private static readonly StructureGate Rule = new(Radius01: 0.03, MinScore: 0.85, MinMargin: 0.06, MinSizeOfE: 0.9);

    // One structure at map (0.2, 0.8); a list of one keeps the flip test meaningful (the real table
    // also holds every point's mirror).
    private static readonly MinimapStructurePositions.Entry[] OneStructure = { new(0.2, 0.8, "test") };

    private static GateResult Eval(CandidateGateSettings s, double r, int w, double x01 = 0.5, double y01 = 0.5,
                                   bool flipped = false, double score = 0.9, double margin = 0.1)
        => MinimapCandidateGate.Evaluate(s, r, w, x01, y01, flipped, score, margin, OneStructure);

    private static readonly CandidateGateSettings SizeOnly = new(Band, null);
    private static readonly CandidateGateSettings Both = new(Band, Rule);

    // --- Size band ---

    [Fact]
    public void Size_BandEdges_InsideKeptOutsideRejected_ExactBoundariesKept()
    {
        const int w = 407;
        double e = K * w;
        Assert.Equal(GateResult.Pass, Eval(SizeOnly, Lo * e, w));          // exactly at Lo*E
        Assert.Equal(GateResult.Pass, Eval(SizeOnly, Hi * e, w));          // exactly at Hi*E
        Assert.Equal(GateResult.Pass, Eval(SizeOnly, Lo * e + 0.01, w));
        Assert.Equal(GateResult.Pass, Eval(SizeOnly, Hi * e - 0.01, w));
        Assert.Equal(GateResult.Size, Eval(SizeOnly, Lo * e - 0.01, w));
        Assert.Equal(GateResult.Size, Eval(SizeOnly, Hi * e + 0.01, w));
    }

    /// <summary>The six W-269 Ekko TPs ring at r 12.01 (r/E 1.00): a fixed-px band tuned on W 407
    /// would drop them, a band proportional to W keeps them.</summary>
    [Fact]
    public void Size_EScalesWithFrameWidth_Ekko12_01()
    {
        Assert.Equal(GateResult.Size, Eval(SizeOnly, 12.01, 407));
        Assert.Equal(GateResult.Pass, Eval(SizeOnly, 12.01, 269));
    }

    // --- Structure rule (at the entry: ROI (0.2, 0.8), not flipped; W 407, E 18.17) ---

    [Fact]
    public void Structure_ClearOnScoreMarginAndSize_AtAnEntry_IsKept_TowerDive()
        => Assert.Equal(GateResult.Pass, Eval(Both, 18.17, 407, 0.2, 0.8, score: 0.9, margin: 0.1));

    [Fact]
    public void Structure_FailsScoreOnly_IsRejected()
        => Assert.Equal(GateResult.Structure, Eval(Both, 18.17, 407, 0.2, 0.8, score: 0.80, margin: 0.1));

    [Fact]
    public void Structure_FailsMarginOnly_IsRejected()
        => Assert.Equal(GateResult.Structure, Eval(Both, 18.17, 407, 0.2, 0.8, score: 0.9, margin: 0.04));

    [Fact]
    public void Structure_FailsSizeOnly_IsRejected()
    {
        // 15.5 is inside the size band (>= 0.85 E = 15.44) but under 0.9 E = 16.35.
        Assert.Equal(GateResult.Pass, Eval(SizeOnly, 15.5, 407, 0.2, 0.8));
        Assert.Equal(GateResult.Structure, Eval(Both, 15.5, 407, 0.2, 0.8, score: 0.9, margin: 0.1));
    }

    [Fact]
    public void Structure_WeakCandidate_FartherThanRadiusFromEveryEntry_IsKept()
    {
        Assert.Equal(GateResult.Structure, Eval(Both, 15.5, 407, 0.2, 0.8, score: 0.80, margin: 0.04));
        Assert.Equal(GateResult.Pass, Eval(Both, 15.5, 407, 0.2 + 0.031, 0.8, score: 0.80, margin: 0.04));
        Assert.Equal(GateResult.Pass, Eval(Both, 15.5, 407, 0.5, 0.5, score: 0.80, margin: 0.04));
    }

    [Fact]
    public void Structure_NaNMargin_PassesTheMarginCheck()
    {
        Assert.Equal(GateResult.Pass, Eval(Both, 18.17, 407, 0.2, 0.8, score: 0.9, margin: double.NaN));
        Assert.Equal(GateResult.Structure, Eval(Both, 18.17, 407, 0.2, 0.8, score: 0.80, margin: double.NaN));
    }

    /// <summary>Flipped, ROI (0.8, 0.2) is map (0.2, 0.8) — the entry. Unflipped it is nowhere near it.</summary>
    [Fact]
    public void Structure_FlippedFrame_RecognisesTheMirroredEntry()
    {
        Assert.Equal(GateResult.Structure, Eval(Both, 15.5, 407, 0.8, 0.2, flipped: true, score: 0.80));
        Assert.Equal(GateResult.Pass, Eval(Both, 15.5, 407, 0.8, 0.2, flipped: false, score: 0.80));
    }

    [Fact]
    public void Structure_Null_OnlyTheSizeBandActs()
    {
        Assert.Equal(GateResult.Pass, Eval(SizeOnly, 15.5, 407, 0.2, 0.8, score: 0.76, margin: 0.03));
        Assert.Equal(GateResult.Size, Eval(SizeOnly, 12.01, 407, 0.2, 0.8, score: 0.99, margin: 0.5));
    }

    // --- Settings ---

    // AI-NOTE: replaces t2a's Measured_IsNullUntilT2b, whose premise ended when t2b measured the values.
    // These are the values docs/reports/minimap_lolF04_gates.md names as chosen.
    [Fact]
    public void Measured_HoldsTheT2bChosenValues()
    {
        var m = CandidateGateSettings.Measured;
        Assert.NotNull(m);
        Assert.Equal(new SizeGate(0.0446, 0.75, 1.50), m!.Size);
        Assert.Equal(new StructureGate(0.04, 0.80, 0.10, 0.75), m.Structure);
    }

    // lolF04 t3 (R4): the pipeline's start-up log line, pinned character for character in both forms.
    [Fact]
    public void Describe_StructureOn_IsTheR4Line()
    {
        Assert.Equal(
            "minimap: candidate gates on — size 0.75-1.50 × E (E = 0.0446 × ROI width); " +
            "structure within 0.040 of 34 points needs score ≥ 0.80, margin ≥ 0.10, size ≥ 0.75 × E",
            CandidateGateSettings.Measured!.Describe(34));
    }

    [Fact]
    public void Describe_StructureOff_IsTheR4Line()
    {
        Assert.Equal(
            "minimap: candidate gates on — size 0.75-1.50 × E (E = 0.0446 × ROI width); structure rule off",
            new CandidateGateSettings(new SizeGate(0.0446, 0.75, 1.50), null).Describe(34));
    }

    public static TheoryData<SizeGate, StructureGate?> InvalidSettings => new()
    {
        { new SizeGate(0, Lo, Hi), null },
        { new SizeGate(-0.04, Lo, Hi), null },
        { new SizeGate(double.NaN, Lo, Hi), null },
        { new SizeGate(K, 0, Hi), null },
        { new SizeGate(K, -0.5, Hi), null },
        { new SizeGate(K, Hi, Lo), null },
        { new SizeGate(K, Lo, Lo), null },
        { Band, Rule with { Radius01 = 0 } },
        { Band, Rule with { Radius01 = -0.01 } },
        { Band, Rule with { MinSizeOfE = 0 } },
        { Band, Rule with { MinSizeOfE = -1 } },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Constructor_RejectsInvalidValues(SizeGate size, StructureGate? structure)
        => Assert.Throws<ArgumentException>(() => new CandidateGateSettings(size, structure));

    // --- Detect hook ---

    private static readonly (byte R, byte G, byte B) Background = (30, 30, 30);
    private static readonly (byte R, byte G, byte B) ChampA = (220, 20, 20);
    private static readonly (byte R, byte G, byte B) ChampANear = (200, 30, 30);   // still ChampA, lower score
    private static readonly (byte R, byte G, byte B) ChampB = (255, 140, 0);

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

    private static EnemyTemplate MakeSolidTemplate(string championId, (byte R, byte G, byte B) color)
    {
        const int size = 16;
        var bgra = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            int off = i * 4;
            bgra[off] = color.B;
            bgra[off + 1] = color.G;
            bgra[off + 2] = color.R;
            bgra[off + 3] = 255;
        }
        return EnemyTemplate.FromSquareIcon(bgra, size, size, championId);
    }

    /// <summary>ChampA is claimed twice: an exact match on a real structure point and a weaker one at
    /// the map centre. Without gates the structure claim wins KeepBestPerChampion; with a structure rule
    /// nothing clears there, the gate runs first, so the weaker valid claim is the one emitted.</summary>
    [Fact]
    public void Detect_StrongestClaimGated_WeakerValidClaimIsEmitted()
    {
        const int w = 400;
        var entry = MinimapStructurePositions.All.First(e => e.X01 is > 0.1 and < 0.9 && e.Y01 is > 0.1 and < 0.9);
        int sx = (int)Math.Round(entry.X01 * w), sy = (int)Math.Round(entry.Y01 * w);
        var frame = MakeFrame(w, w);
        DrawFilledDisc(frame, sx, sy, 11, ChampA);
        DrawFilledDisc(frame, 200, 200, 11, ChampANear);
        var templates = new[] { MakeSolidTemplate("ChampA", ChampA) };
        var detector = new MinimapDetector();

        var ungated = Assert.Single(detector.Detect(frame, templates, DetectionOptions.Default));
        Assert.Equal(sx / (double)w, ungated.MapPos01.X, precision: 2);

        // Size band [5, 20] px around E = 10 keeps both rings; MinScore above 1 means nothing at a
        // structure clears the rule.
        var gates = new CandidateGateSettings(
            new SizeGate(10.0 / w, 0.5, 2.0), new StructureGate(0.03, MinScore: 1.01, MinMargin: 0, MinSizeOfE: 0.01));
        var gated = Assert.Single(detector.Detect(frame, templates, DetectionOptions.Default with { Gates = gates }));
        Assert.Equal("ChampA", gated.ChampionId);
        Assert.Equal(0.5, gated.MapPos01.X, precision: 2);
        Assert.Equal(0.5, gated.MapPos01.Y, precision: 2);
        Assert.True(gated.Confidence < ungated.Confidence);
    }

    [Fact]
    public void Detect_GatesNull_LeavesTheTwoIconFixtureUnchanged()
    {
        Assert.Null(DetectionOptions.Default.Gates);

        var frame = MakeFrame(120, 120);
        DrawFilledDisc(frame, 25, 25, 11, ChampA);
        DrawFilledDisc(frame, 90, 90, 11, ChampB);
        var templates = new[] { MakeSolidTemplate("ChampA", ChampA), MakeSolidTemplate("ChampB", ChampB) };
        var detector = new MinimapDetector();

        var baseline = detector.Detect(frame, templates, DetectionOptions.Default);
        var explicitNull = detector.Detect(frame, templates, DetectionOptions.Default with { Gates = null });
        // A gate that rejects nothing is a no-op too.
        var wide = new CandidateGateSettings(new SizeGate(10.0 / 120, 0.01, 100), null);
        var passAll = detector.Detect(frame, templates, DetectionOptions.Default with { Gates = wide });

        Assert.Equal(2, baseline.Count);
        Assert.Equal(baseline, explicitNull);
        Assert.Equal(baseline, passAll);
    }
}
