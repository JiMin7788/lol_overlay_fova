using Overlay.Core.ChampionDb;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 551) Base AD in the champion summary. <see cref="ChampionSummaryStats"/> never parsed
/// <c>attackdamage</c>/<c>attackdamageperlevel</c>, so every summary-built champion (everyone
/// without a per-champion detail file — ~172 of 173) carried BaseStats.Ad == 0. Two symptoms:
/// the out-of-game preview showed AA = 0 (the user's "일라오이 평타 0" report), and LIVE
/// bonus-AD ratios over-counted by the whole base (bonus = total − 0). These rows pin the parse.
/// </summary>
public class SummaryBaseAdTests
{
    [Theory]
    [InlineData("Illaoi", 65, 5)]
    [InlineData("Camille", 68, 3.8)]
    [InlineData("Garen", 69, 4.5)]
    public void Summary_CarriesBaseAdAndGrowth(string champion, double ad, double adPerLevel)
    {
        var stats = ChampionSummary.Get(champion);
        Assert.NotNull(stats);
        Assert.Equal(ad, stats!.Ad, 1);
        Assert.Equal(adPerLevel, stats.AdPerLevel, 1);
    }
}
