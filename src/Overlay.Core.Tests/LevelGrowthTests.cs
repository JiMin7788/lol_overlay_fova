using Overlay.Core.ChampionDb;

namespace Overlay.Core.Tests;

/// <summary>
/// Covers <see cref="LevelGrowth.Stat"/> — the real League per-level growth curve
/// (<c>base + perLevel*n*(0.7025+0.0175*n)</c>, n=level-1), fixing a user-reported overlay-vs-
/// real-game gap (armor 101 shown vs 97 actual at the same build) that traced to
/// <see cref="ChampionSummary"/>/<see cref="Combo.ComboRunner"/> previously using naive linear
/// interpolation (<c>base + perLevel*(level-1)</c>) instead.
/// </summary>
public class LevelGrowthTests
{
    [Fact]
    public void Stat_AtLevel1_ReturnsBaseUnchanged()
    {
        Assert.Equal(21, LevelGrowth.Stat(21, 4.2, 1));
    }

    [Fact]
    public void Stat_AtLevel18_MatchesLinearTotal()
    {
        // By construction the growth curve is calibrated so level 18 (n=17) always reproduces
        // the naive linear total exactly: 0.7025 + 0.0175*17 = 1.0.
        double linear18 = 21 + 4.2 * 17;
        Assert.Equal(linear18, LevelGrowth.Stat(21, 4.2, 18), precision: 6);
    }

    [Fact]
    public void Stat_AtMidLevel_IsLowerThanNaiveLinear()
    {
        // The super-linear curve grows SLOWER than plain linear at low/mid levels (and catches
        // up exactly at 18) — so a naive linear model over-estimates mid-level stats, matching
        // the user's report (overlay showed HIGHER armor/mr than the real in-game values).
        double n = 5; // level 6
        double naiveLinear = 21 + 4.2 * n;
        double real = LevelGrowth.Stat(21, 4.2, 6);
        Assert.True(real < naiveLinear, $"expected real ({real}) < naive linear ({naiveLinear})");
    }
}
