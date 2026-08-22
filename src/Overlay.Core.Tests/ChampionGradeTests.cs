using Overlay.Core.Stats;

namespace Overlay.Core.Tests;

/// <summary>
/// (composite PS-style score, loop 538) <see cref="ChampionGrade"/>. The score now combines win, pick
/// and ban rate into one PS-scale number (calibrated to a Korean tier site), graded on its
/// confidence-adjusted lower bound PS★. These pin: the composite rewards presence (a lower-win but
/// everywhere pick can outscore a niche higher-win one), grades are absolute PS★ cutoffs, and a thin
/// sample is pulled down a tier by its own standard error rather than by a separate gate.
/// </summary>
public class ChampionGradeTests
{
    private static TierRow Row(int key, int games, double winRate, double pick = 0.05, double ban = 0.02)
        => new(key, $"C{key}", games, winRate, pick, ban, default, default, default);

    [Fact]
    public void ScoreCombinesWinPickBan_OnTheShrunkWinRate()
    {
        // 3000 games at 53% (pick 5%, ban 2%): the site-calibrated composite lands ≈ 54.2.
        Assert.Equal(54.19, ChampionGrade.Score(Row(1, 3000, 0.53)), 1);
        // Same win rate, but played and banned everywhere → a higher score. Presence is rewarded.
        Assert.True(ChampionGrade.Score(Row(2, 3000, 0.53, pick: 0.20, ban: 0.30))
                  > ChampionGrade.Score(Row(1, 3000, 0.53)) + 2);
        // No games → no claim.
        Assert.Equal(0, ChampionGrade.Score(Row(3, 0, 0.90)));
    }

    [Fact]
    public void PresenceCanOutscoreARawWinRateEdge()
    {
        // A 51% champion picked and banned constantly outscores a 53% niche pick — the whole point of
        // moving off the win-rate-only grade.
        double everywhere = ChampionGrade.Score(Row(1, 20000, 0.51, pick: 0.20, ban: 0.30));
        double niche = ChampionGrade.Score(Row(2, 20000, 0.53, pick: 0.03, ban: 0.01));
        Assert.True(everywhere > niche, $"presence {everywhere:0.0} should beat niche {niche:0.0}");
    }

    [Fact]
    public void AThinSampleIsGradedDown_ByItsOwnStandardError()
    {
        // 30 games at 60% has a point score past the S+ cut, but its standard error is enormous, so
        // PS★ falls two tiers — graded on what the sample supports, and flagged as gated.
        var thin = Row(1, 30, 0.60);
        Assert.True(ChampionGrade.Score(thin) >= ChampionGrade.Bands[0].MinPsStar);
        Assert.Equal("B", ChampionGrade.Of(thin, out bool gated));
        Assert.True(gated);

        // The same strength on a real sample keeps the top grade and is not gated.
        var real = Row(2, 30000, 0.53, pick: 0.12, ban: 0.20);
        Assert.Equal("S+", ChampionGrade.Of(real, out bool realGated));
        Assert.False(realGated);
    }

    [Fact]
    public void GradesAreAbsolute_SoTheyDoNotDependOnWhoElseIsInTheList()
    {
        var champion = Row(1, 3000, 0.53);          // A on its own (point S, gated to A)
        string alone = ChampionGrade.Of(champion);
        Assert.Equal("A", alone);

        var crowd = new List<TierRow> { champion };
        for (int i = 2; i < 40; i++) crowd.Add(Row(i, 200_000, 0.60));   // everyone else far stronger

        var ranked = ChampionGrade.Rank(crowd);
        Assert.Equal(alone, ranked.Single(r => r.Row.ChampionKey == 1).Grade);
        Assert.Equal(1, ranked[^1].Row.ChampionKey);                     // last, same grade regardless
    }

    [Fact]
    public void CutoffsCutOnPsStar()
    {
        // Large samples so the error term is tiny and PS★ ≈ the point score. With pick 5% / ban 2%
        // the composite is ≈ 1.546·WR% − 27.3, so each whole win-rate point crosses about one tier.
        Assert.Equal("S+", ChampionGrade.Of(Row(1, 200_000, 0.54)));
        Assert.Equal("S", ChampionGrade.Of(Row(2, 200_000, 0.53)));
        Assert.Equal("S", ChampionGrade.Of(Row(3, 200_000, 0.52)));
        Assert.Equal("A", ChampionGrade.Of(Row(4, 200_000, 0.51)));
        Assert.Equal("B", ChampionGrade.Of(Row(5, 200_000, 0.50)));
        Assert.Equal("C", ChampionGrade.Of(Row(6, 200_000, 0.48)));
        Assert.Equal("D", ChampionGrade.Of(Row(7, 200_000, 0.47)));
    }

    [Fact]
    public void EveryLetterCanBeEmptyOrCrowded()
    {
        // A flat 50% lane: everyone is B, nobody an S/A. (No peer effect — absolute cutoffs.)
        var flat = new List<TierRow>();
        for (int i = 0; i < 30; i++) flat.Add(Row(i, 200_000, 0.50));
        var flatGrades = new HashSet<string>();
        foreach (var r in ChampionGrade.Rank(flat)) flatGrades.Add(r.Grade);
        Assert.Equal(new[] { "B" }, flatGrades);

        // A lopsided lane where a third clears the S+ cut and the rest are D.
        var lopsided = new List<TierRow>();
        for (int i = 0; i < 10; i++) lopsided.Add(Row(i, 200_000, 0.55));
        for (int i = 10; i < 30; i++) lopsided.Add(Row(i, 200_000, 0.44));
        int top = ChampionGrade.Rank(lopsided).Count(r => r.Grade == "S+");
        Assert.Equal(10, top);
    }

    [Fact]
    public void OrderingIsByGradeFirst_SoAGatedRowLeadsItsOwnBlock()
    {
        // The gated thin row has the HIGHEST point score of all, but it is graded B, so grade-first
        // ordering keeps it out of the S+/S blocks and at the head of the B block instead.
        var gated = Row(1, 30, 0.60);                 // point ≈ 56.2 (S+), PS★ ≈ 48.8 → B, gated
        Assert.Equal("B", ChampionGrade.Of(gated));

        var ranked = ChampionGrade.Rank(new[]
        {
            gated,
            Row(2, 200_000, 0.54),   // S+ outright
            Row(3, 200_000, 0.52),   // S outright
            Row(4, 200_000, 0.49),   // B outright, lower point than the gated row
        });

        Assert.Equal(new[] { "S+", "S", "B", "B" }, ranked.Select(r => r.Grade).ToArray());
        Assert.Equal(new[] { 2, 3, 1, 4 }, ranked.Select(r => r.Row.ChampionKey).ToArray());
        Assert.True(ranked.Single(r => r.Row.ChampionKey == 1).Gated);
        Assert.False(ranked.Single(r => r.Row.ChampionKey == 2).Gated);
    }

    [Fact]
    public void WithinAGrade_ScoreThenSampleSizeDecides()
    {
        var ranked = ChampionGrade.Rank(new[] { Row(1, 200_000, 0.51), Row(2, 200_000, 0.515) });
        Assert.Equal("A", ranked[0].Grade);
        Assert.Equal(2, ranked[0].Row.ChampionKey);   // higher win rate → higher score, listed first

        var tied = ChampionGrade.Rank(new[] { Row(1, 500, 0.52), Row(2, 500, 0.52) });
        Assert.Equal(tied[0].Score, tied[1].Score, 12);
        Assert.Equal(2, tied.Count);
    }
}
