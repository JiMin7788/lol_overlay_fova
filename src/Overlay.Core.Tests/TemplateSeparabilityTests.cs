using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 501) <see cref="MinimapDetector.MaxAchievableMargin"/> claims something stronger than a
/// heuristic: that no candidate crop can EVER separate two templates by more than
/// <c>1 - similarity(a, b)</c>. The roster-separability log ranks pairs on the strength of it, so
/// the claim is worth checking rather than believing.
///
/// <para>These tests cover the MATH. The calibration - how close real icons actually get - lives in
/// <c>tools/measure_icon_separability.py</c>, because this project has no image decoder. It found
/// the bound is loose on real artwork (closest pair 0.157, over 5x the matcher margin), which is
/// why the log ranks pairs instead of declaring them inseparable.</para>
///
/// <para>The argument is that <c>ColorSimilarity</c> is a mean of per-pixel Euclidean distances over
/// a fixed mask, an average of metrics is a metric, and metrics obey the triangle inequality. These
/// tests hammer that with random pixel data, which is the case a hand-picked example would miss.</para>
/// </summary>
public class TemplateSeparabilityTests
{
    private const int Src = 32;   // source icons are downscaled to EnemyTemplate.Size

    private static EnemyTemplate Template(Random rng, string id, bool isEnemy = true)
        => EnemyTemplate.FromSquareIcon(RandomBgra(rng), Src, Src, id, isEnemy: isEnemy);

    private static byte[] RandomBgra(Random rng)
    {
        var bytes = new byte[Src * Src * 4];
        rng.NextBytes(bytes);
        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 255;   // opaque
        return bytes;
    }

    /// <summary>A template compared with itself is a perfect match, so the bound is zero — the
    /// degenerate end of the scale, and the reason a self-pair is never worth logging.</summary>
    [Fact]
    public void ATemplateIsIdenticalToItself()
    {
        var t = Template(new Random(1), "Ahri");
        Assert.Equal(1.0, MinimapDetector.TemplateSimilarity(t, t), 9);
        Assert.Equal(0.0, MinimapDetector.MaxAchievableMargin(t, t), 9);
    }

    /// <summary>Scoring a against b must equal scoring b against a — the log names pairs, not
    /// ordered pairs. This holds because every template shares one Size and one MatchRadiusFraction,
    /// so the two circle masks are the same mask; if that ever stops being true, this catches it.</summary>
    [Fact]
    public void ScoringIsSymmetric()
    {
        var rng = new Random(7);
        for (int i = 0; i < 50; i++)
        {
            var a = Template(rng, "A");
            var b = Template(rng, "B");
            Assert.Equal(MinimapDetector.TemplateSimilarity(a, b),
                         MinimapDetector.TemplateSimilarity(b, a), 9);
        }
    }

    /// <summary>The claim itself: for any crop c, |sim(c,a) - sim(c,b)| never exceeds the bound.</summary>
    [Fact]
    public void NoCandidateEverSeparatesTwoTemplatesByMoreThanTheBound()
    {
        var rng = new Random(20260821);
        double worstSlack = double.MaxValue;

        for (int trial = 0; trial < 400; trial++)
        {
            var a = Template(rng, "A");
            var b = Template(rng, "B");
            double bound = MinimapDetector.MaxAchievableMargin(a, b);

            // A crop is scored as a template here purely to reuse the same downscale + mask path;
            // what matters is that it is an arbitrary image, unrelated to either side.
            var crop = Template(rng, "crop");
            double gap = Math.Abs(MinimapDetector.TemplateSimilarity(crop, a)
                                - MinimapDetector.TemplateSimilarity(crop, b));

            Assert.True(gap <= bound + 1e-9,
                $"trial {trial}: a crop separated the pair by {gap:F6}, which exceeds the bound "
                + $"{bound:F6} — the triangle-inequality argument behind MaxAchievableMargin is wrong, "
                + "and the INSEPARABLE verdict in the roster log is unsound");

            worstSlack = Math.Min(worstSlack, bound - gap);
        }

        // Random 32x32 noise averages out, so every pair lands near the middle of the scale and the
        // bound is nowhere near tight. Recorded so the number is not mistaken for a sharp one.
        Assert.True(worstSlack >= 0, "sanity: the bound was never violated");
    }

    /// <summary>Two templates built from the SAME pixels are inseparable, and the log must say so at
    /// the detector's own margin rather than at some threshold invented for the log.</summary>
    [Fact]
    public void IdenticalArtworkIsTheOneCaseThatFallsUnderTheMatchersMargin()
    {
        var rng = new Random(3);
        byte[] shared = RandomBgra(rng);
        var a = EnemyTemplate.FromSquareIcon(shared, Src, Src, "Twin1", isEnemy: true);
        var b = EnemyTemplate.FromSquareIcon(shared, Src, Src, "Twin2", isEnemy: true);

        double bound = MinimapDetector.MaxAchievableMargin(a, b);
        Assert.Equal(0.0, bound, 9);
        Assert.True(bound < DetectionOptions.Default.MinMatchMargin,
            "identical artwork must fall under the margin the matcher actually applies");
    }
}
