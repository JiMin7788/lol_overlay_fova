using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// §43-N — the regression line for the phantom the live pass reported: an enemy marked in our own
/// base while that champion was top lane.
///
/// <para>Fixtures are REAL captured pixels, not synthetic. The negative region is the ally fountain
/// from <c>20260720_092937/frame_049</c>, where two allied champions overlap and the white viewport
/// bracket cuts across them; the matcher once named Sett there at 0.792. The positive region is a
/// genuine enemy from the same frame. Stored as raw BGRA because this project targets net8.0 with
/// no image decoder available.</para>
///
/// <para><b>Both directions are asserted on purpose.</b> A one-sided "no phantom" test passes
/// trivially for a detector that finds nothing at all, which is exactly the failure mode a
/// too-strict threshold produces — and a threshold was raised to fix this very phantom.</para>
/// </summary>
public class MinimapFountainPhantomTests
{
    // The capture this fixture came from was 407px wide; the pipeline derives the icon radius from
    // ROI width, so pin the same value rather than re-deriving it from the cropped region.
    private const double SourceRoiWidth = 407.0;
    private static readonly double ExpectedRadius = SourceRoiWidth * (10.0 / 280.0);

    private static readonly string[] Enemies = { "Kassadin", "MonkeyKing", "Nautilus", "Seraphine", "Sett" };
    private static readonly string[] Allies = { "Karma", "Rumble", "TwistedFate", "Varus", "Ziggs" };

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimap");

    private static (byte[] Bgra, int W, int H) LoadBgra(string name)
    {
        var raw = File.ReadAllBytes(Path.Combine(FixtureDir, name));
        int w = BitConverter.ToInt32(raw, 0);
        int h = BitConverter.ToInt32(raw, 4);
        var px = new byte[w * h * 4];
        Array.Copy(raw, 8, px, 0, px.Length);
        return (px, w, h);
    }

    private static MinimapFrame Frame(string name)
    {
        var (px, w, h) = LoadBgra(name);
        return new MinimapFrame(px, w, h, w * 4, timestampMs: 0, flipped: false);
    }

    /// <summary>The production match set: every champion in the game, allies flagged as decoys.</summary>
    private static List<EnemyTemplate> Templates()
    {
        var list = new List<EnemyTemplate>();
        foreach (var id in Enemies.Concat(Allies))
        {
            var (px, w, h) = LoadBgra($"tmpl_{id}.bgra");
            list.Add(EnemyTemplate.FromSquareIcon(px, w, h, id, EnemyTemplate.Size,
                                                  isEnemy: Enemies.Contains(id)));
        }
        return list;
    }

    private static DetectionOptions Options() => DetectionOptions.Default with
    {
        ExpectedIconRadiusPx = ExpectedRadius,
        ToleranceRadiusPx = Math.Max(2.0, ExpectedRadius * 0.4),
    };

    [Fact]
    public void AllyFountain_YieldsNoEnemy()
    {
        var sightings = new MinimapDetector().Detect(Frame("fountain_region.bgra"), Templates(), Options());

        Assert.True(sightings.Count == 0,
            "no enemy may be reported in our own fountain; got: " +
            string.Join(", ", sightings.Select(s => $"{s.ChampionId}@{s.Confidence:0.###}")));
    }

    [Fact]
    public void ARealEnemy_IsStillFound()
    {
        // Guards the assertion above from being satisfied by a detector that reports nothing.
        var sightings = new MinimapDetector().Detect(Frame("enemy_region.bgra"), Templates(), Options());

        Assert.NotEmpty(sightings);
        Assert.All(sightings, s => Assert.Contains(s.ChampionId, Enemies));
    }

    /// <summary>
    /// The phantom passed at margin 0.015 and was rejected at 0.03 — measured over 89 fountain-corner
    /// candidates, where the five that picked an enemy all scored margins of 0.003–0.015. This pins
    /// the reason the default is where it is, so lowering it again fails loudly here rather than
    /// quietly in a game.
    /// </summary>
    [Fact]
    public void TheMarginDefault_IsAboveWhereThePhantomSlipsThrough()
    {
        Assert.True(DetectionOptions.DefaultMinMatchMargin >= 0.02,
            $"margin {DetectionOptions.DefaultMinMatchMargin} readmits the fountain phantom " +
            "(observed phantom margins ran up to 0.015)");
    }
}
