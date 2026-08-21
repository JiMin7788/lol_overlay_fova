using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// §43-C — the measurement behind <see cref="EnemyTemplate.MatchRadiusFraction"/>.
///
/// <para>A captured minimap icon carries the team-colored ring over its outer annulus, while the
/// template is the raw DDragon square with portrait art in that same region. Those pixels can never
/// agree for ANY champion, so including them adds a near-constant error to every comparison and
/// pushes correct and wrong matches together — which is what the live log showed as a squashed
/// 0.75-0.88 confidence band.</para>
///
/// <para>This reproduces that with synthetic portraits and asserts the property that matters: the
/// interior-only mask separates the correct champion from the best wrong one by more than the full
/// circle does. Synthetic rather than real art so the test carries no DDragon asset dependency and
/// no patch coupling.</para>
/// </summary>
public class TemplateMaskSeparationTests
{
    private const int Src = 32;
    private static readonly (byte B, byte G, byte R)[] Ring = { (40, 40, 210) };

    /// <summary>A portrait: a distinct flat color per champion, plus a lighter off-center patch so
    /// the interior is not perfectly uniform (a uniform interior would make the comparison trivial).</summary>
    private static byte[] Portrait(byte b, byte g, byte r)
    {
        var px = new byte[Src * Src * 4];
        for (int y = 0; y < Src; y++)
            for (int x = 0; x < Src; x++)
            {
                bool patch = x > Src / 2 && y < Src / 2;
                int i = (y * Src + x) * 4;
                px[i] = (byte)Math.Min(255, b + (patch ? 60 : 0));
                px[i + 1] = (byte)Math.Min(255, g + (patch ? 60 : 0));
                px[i + 2] = (byte)Math.Min(255, r + (patch ? 60 : 0));
                px[i + 3] = 255;
            }
        return px;
    }

    /// <summary>The same portrait as the game draws it: ring painted over the outer annulus.</summary>
    private static byte[] WithRing(byte[] portrait)
    {
        var px = (byte[])portrait.Clone();
        double c = (Src - 1) / 2.0, rad = Src / 2.0;
        for (int y = 0; y < Src; y++)
            for (int x = 0; x < Src; x++)
            {
                if (Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) < rad * 0.72) continue;
                int i = (y * Src + x) * 4;
                px[i] = Ring[0].B; px[i + 1] = Ring[0].G; px[i + 2] = Ring[0].R; px[i + 3] = 255;
            }
        return px;
    }

    private static double Similarity(byte[] candidate16, EnemyTemplate t)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < t.CircleMask.Length; i++)
        {
            if (!t.CircleMask[i]) continue;
            int o = i * 4;
            double db = candidate16[o] - t.Bgra[o];
            double dg = candidate16[o + 1] - t.Bgra[o + 1];
            double dr = candidate16[o + 2] - t.Bgra[o + 2];
            sum += Math.Sqrt(dr * dr + dg * dg + db * db); n++;
        }
        return Math.Clamp(1.0 - (sum / n) / 441.6729559300637, 0, 1);
    }

    private static readonly (string Id, byte B, byte G, byte R)[] Roster =
    {
        ("A", 30, 60, 150), ("B", 140, 90, 40), ("C", 60, 140, 90),
        ("D", 120, 40, 120), ("E", 40, 130, 140),
    };

    /// <summary>Worst-case gap between the correct champion's score and the best wrong one, using a
    /// mask built at the given radius fraction.</summary>
    private static double WorstSeparation(double radiusFraction)
    {
        var templates = Roster.Select(c => BuildTemplate(c.Id, c.B, c.G, c.R, radiusFraction)).ToList();
        double worst = double.MaxValue;

        foreach (var c in Roster)
        {
            byte[] captured = MinimapPixelSampling.NearestNeighborDownscale(
                WithRing(Portrait(c.B, c.G, c.R)), Src * 4, 0, 0, Src, EnemyTemplate.Size);

            double correct = templates.Where(t => t.ChampionId == c.Id).Select(t => Similarity(captured, t)).Single();
            double bestWrong = templates.Where(t => t.ChampionId != c.Id).Max(t => Similarity(captured, t));
            worst = Math.Min(worst, correct - bestWrong);
        }
        return worst;
    }

    /// <summary>Template with an explicitly-sized mask, so this test can compare mask choices
    /// without depending on whichever fraction production currently ships.</summary>
    private static EnemyTemplate BuildTemplate(string id, byte b, byte g, byte r, double radiusFraction)
    {
        var t = EnemyTemplate.FromSquareIcon(Portrait(b, g, r), Src, Src, id);
        var mask = MinimapPixelSampling.BuildCircleMask(EnemyTemplate.Size, radiusFraction);
        Array.Copy(mask, t.CircleMask, mask.Length);
        return t;
    }

    [Fact]
    public void DroppingTheRingAnnulus_WidensTheGapBetweenRightAndWrongChampion()
    {
        double full = WorstSeparation(1.0);
        double interior = WorstSeparation(EnemyTemplate.MatchRadiusFraction);

        Assert.True(interior > full,
            $"interior mask should separate better: full={full:0.###}, interior={interior:0.###}");
    }

    /// <summary>
    /// The mask has to trim the ring WITHOUT eating the band that distinguishes champions.
    /// (2026-07-20) The lower bound is the correction: 0.68 shipped briefly and reported one
    /// champion in place of another, because their identifying colour lives outside that radius
    /// while their centres look alike. On the real crop that exposed it, the winner only becomes
    /// correct at 0.85. The upper bound keeps some ring trimming, which measurably helped.
    /// </summary>
    [Fact]
    public void TheShippedFraction_TrimsTheRingWithoutEatingTheIdentifyingBand()
    {
        Assert.InRange(EnemyTemplate.MatchRadiusFraction, 0.85, 0.95);
    }

    [Fact]
    public void EveryChampionIsStillIdentifiedCorrectly_WithTheShippedMask()
    {
        var templates = Roster
            .Select(c => BuildTemplate(c.Id, c.B, c.G, c.R, EnemyTemplate.MatchRadiusFraction)).ToList();

        foreach (var c in Roster)
        {
            byte[] captured = MinimapPixelSampling.NearestNeighborDownscale(
                WithRing(Portrait(c.B, c.G, c.R)), Src * 4, 0, 0, Src, EnemyTemplate.Size);
            var best = templates.OrderByDescending(t => Similarity(captured, t)).First();
            Assert.Equal(c.Id, best.ChampionId);
        }
    }
}
