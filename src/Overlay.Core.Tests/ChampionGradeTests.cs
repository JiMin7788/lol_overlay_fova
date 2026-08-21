using Overlay.Core.Stats;

namespace Overlay.Core.Tests;

/// <summary>
/// (tier list, loop 464; absolute cutoffs, loop 467) <see cref="ChampionGrade"/>. The percentile
/// scheme this replaced fixed how many champions held each letter — one S+ and about three S per
/// lane, always — which described the ranking rather than the champions. These tests pin the
/// properties of the absolute scheme that replaced it: a grade depends only on the champion's own
/// measured edge, the letters can be empty or crowded, and the confidence gate stops a small hot
/// streak from buying a top grade.
/// </summary>
public class ChampionGradeTests
{
    private static TierRow Row(int key, int games, double winRate)
        => new(key, $"C{key}", games, winRate, 0.05, 0.02, default, default, default);

    [Fact]
    public void ScoreIsTheShrunkWinRateEdgeInPercentagePoints()
    {
        // 3000 games at 53%: (1590 + 5) / 3010 = 52.99% -> +2.99
        Assert.Equal(2.99, ChampionGrade.Score(Row(1, 3000, 0.53)), 2);
        // 30 games at 60% shrinks hard: (18 + 5) / 40 = 57.5% -> +7.5
        Assert.Equal(7.5, ChampionGrade.Score(Row(2, 30, 0.60)), 2);
        Assert.Equal(0, ChampionGrade.Score(Row(3, 500, 0.50)), 9);
        Assert.True(ChampionGrade.Score(Row(4, 500, 0.46)) < 0);
        Assert.Equal(0, ChampionGrade.Score(Row(5, 0, 0.90)));   // no games, no claim
    }

    [Fact]
    public void TheConfidenceGateStopsASmallHotStreakFromBuyingATopGrade()
    {
        // 30 games at 60% scores +7.5, far past the S+ cutoff — but one standard error at that
        // sample is about 7.8 points, so the sample does not actually place it above even.
        var fluke = Row(1, 30, 0.60);
        Assert.True(ChampionGrade.Score(fluke) >= ChampionGrade.Bands[0].MinEdge);
        Assert.True(ChampionGrade.LowerEdge(fluke) <= 0);
        Assert.Equal("A", ChampionGrade.Of(fluke));

        // The same +7.5-ish edge on a real sample keeps the top grade.
        var real = Row(2, 3000, 0.575);
        Assert.True(ChampionGrade.LowerEdge(real) > 0);
        Assert.Equal("S+", ChampionGrade.Of(real));
    }

    [Fact]
    public void GradesAreAbsolute_SoTheyDoNotDependOnWhoElseIsInTheList()
    {
        var champion = Row(1, 2000, 0.532);
        string alone = ChampionGrade.Of(champion);

        var crowd = new List<TierRow> { champion };
        for (int i = 2; i < 40; i++) crowd.Add(Row(i, 2000, 0.60));   // everyone else is stronger

        var ranked = ChampionGrade.Rank(crowd);
        Assert.Equal(alone, ranked.Single(r => r.Row.ChampionKey == 1).Grade);
        Assert.Equal(39, ranked.Count);
        Assert.Equal(1, ranked[^1].Row.ChampionKey);                  // last on score, same grade
    }

    [Fact]
    public void EveryLetterCanBeEmptyOrCrowded()
    {
        // A flat patch where nobody stands out: no S+, no S, and no A either.
        var flat = new List<TierRow>();
        for (int i = 0; i < 30; i++) flat.Add(Row(i, 2000, 0.50));
        var flatGrades = new HashSet<string>();
        foreach (var r in ChampionGrade.Rank(flat)) flatGrades.Add(r.Grade);
        Assert.Equal(new[] { "B" }, flatGrades);

        // And a lopsided one where a third of the lane is S+.
        var lopsided = new List<TierRow>();
        for (int i = 0; i < 10; i++) lopsided.Add(Row(i, 2000, 0.56));
        for (int i = 10; i < 30; i++) lopsided.Add(Row(i, 2000, 0.44));
        int top = 0;
        foreach (var r in ChampionGrade.Rank(lopsided)) if (r.Grade == "S+") top++;
        Assert.Equal(10, top);
    }

    [Fact]
    public void CutoffBoundariesLandOnTheStatedSide()
    {
        // A large sample so the confidence gate is never the binding constraint. Note the shrink
        // always pulls a rate a hair toward 50%, so "exactly at the cutoff" is tested as a tenth
        // of a point either side of it rather than on it.
        var bands = ChampionGrade.Bands;
        for (int i = 0; i < bands.Length; i++)
        {
            if (double.IsNegativeInfinity(bands[i].MinEdge)) continue;

            var above = Row(1, 200_000, 0.5 + (bands[i].MinEdge + 0.1) / 100);
            Assert.True(ChampionGrade.Score(above) >= bands[i].MinEdge);
            Assert.Equal(bands[i].Label, ChampionGrade.Of(above));

            var below = Row(1, 200_000, 0.5 + (bands[i].MinEdge - 0.1) / 100);
            Assert.True(ChampionGrade.Score(below) < bands[i].MinEdge);
            Assert.Equal(bands[i + 1].Label, ChampionGrade.Of(below));
        }
    }

    [Fact]
    public void OrderingIsByGradeFirst_SoGatedRowsDoNotSplitTheBlockAbove()
    {
        // The reported symptom: a champion the gate demoted keeps its high score, so ordering on
        // score alone put an A between two S+ rows. 100 games at 55% scores +4.55, past the S+
        // cutoff, but its standard error is 4.75 — gated down to A while still outscoring genuine
        // S and A rows.
        var gated = Row(1, 100, 0.55);
        Assert.True(ChampionGrade.Score(gated) >= ChampionGrade.Bands[0].MinEdge);
        Assert.Equal("A", ChampionGrade.Of(gated));

        var ranked = ChampionGrade.Rank(new[]
        {
            gated,
            Row(2, 4000, 0.552),   // S+ outright
            Row(3, 4000, 0.549),   // S+ outright, slightly lower score than the gated row's
            Row(4, 4000, 0.526),   // S outright (+2.59)
            Row(5, 4000, 0.515),   // A outright (+1.50), below the gated row's score
        });

        Assert.Equal(new[] { "S+", "S+", "S", "A", "A" },
                     ranked.Select(r => r.Grade).ToArray());
        // The gated row leads its own block instead of sitting inside the S+ one.
        Assert.Equal(new[] { 2, 3, 4, 1, 5 }, ranked.Select(r => r.Row.ChampionKey).ToArray());
        Assert.True(ranked.Single(r => r.Row.ChampionKey == 1).Gated);
        Assert.False(ranked.Single(r => r.Row.ChampionKey == 2).Gated);
    }

    [Fact]
    public void WithinAGrade_ScoreThenSampleSizeDecides()
    {
        var ranked = ChampionGrade.Rank(new[] { Row(1, 3000, 0.53), Row(2, 3000, 0.535) });
        Assert.Equal(2, ranked[0].Row.ChampionKey);

        var tied = ChampionGrade.Rank(new[] { Row(1, 500, 0.52), Row(2, 500, 0.52) });
        Assert.Equal(tied[0].Score, tied[1].Score, 12);
        Assert.Equal(2, tied.Count);
    }
}
