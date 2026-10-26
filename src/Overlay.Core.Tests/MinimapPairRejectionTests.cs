using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// lolF04 t5d (TODO:411): <see cref="PairRejectionSettings"/> and its opt-in hook in
/// <see cref="MinimapDetector.Detect"/>. One icon drawn in ChampA's colour; ChampA's template matches it
/// exactly, so the margin over a runner-up equals the pair's bound. The values here are test values only.
/// </summary>
public class MinimapPairRejectionTests
{
    private static readonly (byte R, byte G, byte B) Background = (30, 30, 30);
    private static readonly (byte R, byte G, byte B) ChampA = (220, 20, 20);
    // 26.9 RGB units from ChampA: bound = margin ≈ 0.061, above the 0.03 margin rule.
    private static readonly (byte R, byte G, byte B) ChampANear = (220, 39, 39);

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

    private static EnemyTemplate MakeSolidTemplate(string championId, (byte R, byte G, byte B) color, bool isEnemy = true)
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
        return EnemyTemplate.FromSquareIcon(bgra, size, size, championId, isEnemy: isEnemy);
    }

    private static MinimapFrame OneIcon()
    {
        var frame = MakeFrame(120, 120);
        DrawFilledDisc(frame, 60, 60, 11, ChampA);
        return frame;
    }

    private static readonly EnemyTemplate A = MakeSolidTemplate("ChampA", ChampA);
    private static readonly EnemyTemplate Near = MakeSolidTemplate("ChampNear", ChampANear);
    private static readonly EnemyTemplate[] RiskyPair = { A, Near };

    private static IReadOnlyList<MinimapSighting> Detect(IReadOnlyList<EnemyTemplate> templates, PairRejectionSettings? pr)
        => new MinimapDetector().Detect(OneIcon(), templates, DetectionOptions.Default with { PairRejection = pr });

    [Fact]
    public void Fixture_MarginEqualsTheBound_AndClearsTheMarginRule()
    {
        double bound = MinimapDetector.MaxAchievableMargin(A, Near);
        Assert.InRange(bound, 0.055, 0.065);
        var s = Assert.Single(Detect(RiskyPair, null));
        Assert.Equal("ChampA", s.ChampionId);
        Assert.Equal(1.0, s.Confidence, precision: 6);
    }

    [Fact]
    public void RiskyPair_BelowTheMargin_NoIdentity()
        => Assert.Empty(Detect(RiskyPair, new PairRejectionSettings(0.10, 0.08)));

    [Fact]
    public void RiskyPair_AboveTheMargin_Identity()
        => Assert.Equal("ChampA", Assert.Single(Detect(RiskyPair, new PairRejectionSettings(0.10, 0.05))).ChampionId);

    /// <summary>Same templates, same margin ≈ 0.061 &lt; 0.08, but the pair's bound is not below 0.05.</summary>
    [Fact]
    public void NonRiskyPair_SameSmallMargin_Unchanged()
        => Assert.Equal(Detect(RiskyPair, null), Detect(RiskyPair, new PairRejectionSettings(0.05, 0.08)));

    /// <summary>An ally template is never a runner-up (MatchBestTemplate skips allies), so one enemy template
    /// has no pair to reject even when every pair would count as risky.</summary>
    [Fact]
    public void SingleEnemyTemplate_Unchanged()
    {
        var templates = new[] { A, MakeSolidTemplate("AllyNear", ChampANear, isEnemy: false) };
        var baseline = Detect(templates, null);
        Assert.Equal("ChampA", Assert.Single(baseline).ChampionId);
        Assert.Equal(baseline, Detect(templates, new PairRejectionSettings(1.0, 1.0)));
    }

    [Fact]
    public void NullSettings_Unchanged()
    {
        Assert.Null(DetectionOptions.Default.PairRejection);
        Assert.Equal(new MinimapDetector().Detect(OneIcon(), RiskyPair, DetectionOptions.Default), Detect(RiskyPair, null));
    }

    [Fact]
    public void Rejects_NeedsBothTheRiskyBoundAndTheSmallMargin()
    {
        var pr = new PairRejectionSettings(0.10, 0.08);
        Assert.True(pr.Rejects(A, Near, 0.07));
        Assert.False(pr.Rejects(A, Near, 0.08));                              // margin at the edge: kept
        Assert.False(new PairRejectionSettings(0.06, 0.08).Rejects(A, Near, 0.07));   // bound ≈ 0.061 ≥ 0.06
    }

    /// <summary>docs/reports/minimap_lolF04_variants.md §TODO:411 ends "Result: measured, not adopted".</summary>
    [Fact]
    public void Measured_IsNull_NotAdopted()
        => Assert.Null(PairRejectionSettings.Measured);

    // AI-NOTE: pinned to the R15 wording; an adopted variant is logged at start as "minimap: detector variant on — " + this.
    [Fact]
    public void Describe_IsTheR15Text()
        => Assert.Equal("pair-rejection (bound < 0.30, margin ≥ 0.08)", new PairRejectionSettings(0.30, 0.08).Describe());

    public static TheoryData<double, double> InvalidSettings => new()
    {
        { 0, 0.05 },
        { -0.2, 0.05 },
        { double.NaN, 0.05 },
        { 1.01, 0.05 },
        { 0.25, 0 },
        { 0.25, -0.05 },
        { 0.25, double.NaN },
        { 0.25, 1.01 },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Constructor_RejectsInvalidValues(double bound, double margin)
        => Assert.Throws<ArgumentException>(() => new PairRejectionSettings(bound, margin));

    [Fact]
    public void Constructor_KeepsValidValues()
    {
        var pr = new PairRejectionSettings(0.35, 0.12);
        Assert.Equal(0.35, pr.MaxPairBound);
        Assert.Equal(0.12, pr.MinPairMargin);
    }
}
