using Overlay.Core.Stats;

namespace Overlay.Core.Tests;

/// <summary>
/// (tier table, loop 459; filters, loop 462; brackets, loop 463) <see cref="FileTierStatsSource"/>
/// — the dashboard statistics tab's data. Pins the aggregation-output parse (duration buckets
/// included), the empty-bucket contract (0 games → a bucket the renderer shows as a dash, never a
/// measured 0%), the shared patch-resolution/failure posture, and the two filtering contracts:
///
/// <para>A lane filter must RECOMPUTE the rates from that lane's rows (hiding rows while showing
/// whole-match numbers would be a lie), measuring the lane pick rate against that lane's slots and
/// leaving the ban rate champion-wide because bans precede positions.</para>
///
/// <para>A bracket is the SUM of its member tiers' counts. That is why the file stores counts and
/// no rates: summing counts is exact, averaging rates is not, and the difference is visible the
/// moment two tiers have different sample sizes.</para>
/// </summary>
public class FileTierStatsSourceTests : IDisposable
{
    private readonly string _root;

    public FileTierStatsSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private void WritePatch(string patch, int runeChampions, string? tiersJson = null)
    {
        string dir = Path.Combine(_root, patch);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < runeChampions; i++)
            File.WriteAllText(Path.Combine(dir, $"{100 + i}.json"), "[]");
        if (tiersJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "stats"));
            File.WriteAllText(Path.Combine(dir, "stats", "tiers.json"), tiersJson);
        }
    }

    /// <summary>Current aggregation output: counts only, one block per seed tier. Both champions
    /// carry an UNKNOWN role (Riot left teamPosition empty on those games) that the "전체" total
    /// must EXCLUDE: the champion-wide <c>games</c>/<c>duration</c> include it, the classified lanes
    /// do not, and the overall row is the sum of the lanes.</summary>
    private const string SampleJson = """
        {"patch": "16.15", "seedTiers": ["DIAMOND"],
         "byTier": {
          "DIAMOND": {
            "matches": 6903,
            "roleSlots": {"TOP": 13806, "MIDDLE": 13806, "UTILITY": 13806, "UNKNOWN": 500},
            "champions": {
              "37": {"name": "Sona", "games": 72, "wins": 42, "bans": 14,
                     "duration": {"lt25": [32, 16], "b25to32": [22, 14], "gt32": [18, 12]},
                     "roles": {"UTILITY": {"games": 65, "wins": 39,
                                           "duration": {"lt25": [25, 13], "b25to32": [22, 14],
                                                        "gt32": [18, 12]}},
                               "MIDDLE": {"games": 4, "wins": 2, "duration": {"lt25": [4, 2]}},
                               "UNKNOWN": {"games": 3, "wins": 1, "duration": {"lt25": [3, 1]}}}},
              "266": {"name": "Aatrox", "games": 612, "wins": 300, "bans": 759,
                      "duration": {"lt25": [200, 95]},
                      "roles": {"TOP": {"games": 562, "wins": 275,
                                        "duration": {"lt25": [180, 85]}},
                                "UNKNOWN": {"games": 50, "wins": 25,
                                            "duration": {"lt25": [20, 10]}}}}
            }}
         }}
        """;

    [Fact]
    public void ParsesRows_DurationBuckets_AndSampleInfo()
    {
        WritePatch("16.15", runeChampions: 5, tiersJson: SampleJson);
        var source = new FileTierStatsSource(_root);

        Assert.Equal(("16.15", 6903), source.SampleInfo);
        var rows = source.All("diamond_plus");
        Assert.Equal(2, rows.Count);

        // "전체" is the sum of the classified lanes (UTILITY 65 + MIDDLE 4 = 69), NOT the
        // champion-wide 72 that includes 3 unclassified games.
        var sona = rows.Single(r => r.ChampionKey == 37);
        Assert.Equal("Sona", sona.Name);
        Assert.Equal(69, sona.Games);
        Assert.Equal(41.0 / 69, sona.WinRate, 6);
        Assert.Equal(69.0 / 6903, sona.PickRate, 8);
        Assert.Equal(14.0 / 6903, sona.BanRate, 8);       // bans precede positions: champion-wide
        Assert.Equal(29, sona.Under25.Games);             // 25 (UTILITY) + 4 (MIDDLE), not 32
        Assert.Equal(15.0 / 29, sona.Under25.WinRate, 6);
        Assert.Equal(12.0 / 18, sona.Over32.WinRate, 6);

        // Aatrox's TOP lane carries only the lt25 bucket → the others are the EMPTY bucket (0
        // games), which the renderer contract turns into a dash. Its 50 unclassified games are
        // excluded, so the overall row is the 562 TOP games, not 612.
        var aatrox = rows.Single(r => r.ChampionKey == 266);
        Assert.Equal(562, aatrox.Games);
        Assert.Equal(0, aatrox.From25To32.Games);
        Assert.Equal(0, aatrox.Over32.Games);
        Assert.Equal(180, aatrox.Under25.Games);
    }

    [Fact]
    public void OffersOnlyBracketsTheSampleCanAnswer()
    {
        WritePatch("16.15", runeChampions: 5, tiersJson: SampleJson);
        var source = new FileTierStatsSource(_root);

        Assert.Equal(new[] { "DIAMOND" }, source.SeedTiers);
        // Diamond sits in exactly these four; master_plus and gold_minus hold no collected tier
        // and are therefore not offered at all.
        Assert.Equal(new[] { "all", "diamond_plus", "emerald_plus", "platinum_plus", "gold_plus" },
                     source.AvailableBrackets().Select(b => b.Slug));
        // Diamond is in no downward bracket, so those are absent entirely — the sample cannot
        // answer them at all, which is different from answering them thinly.
        Assert.DoesNotContain("gold_minus", source.AvailableBrackets().Select(b => b.Slug));
        Assert.Equal(6903, source.Matches("platinum_plus"));
        Assert.Equal(0, source.Matches("master_plus"));
        Assert.Empty(source.All("master_plus"));
        Assert.Empty(source.All("not_a_bracket"));
    }

    [Fact]
    public void RoleFilter_RecomputesRatesFromThatLaneOnly()
    {
        WritePatch("16.15", runeChampions: 5, tiersJson: SampleJson);
        var source = new FileTierStatsSource(_root);

        var support = source.All("all", "UTILITY");
        var sona = Assert.Single(support);              // Aatrox never played support → absent
        Assert.Equal(37, sona.ChampionKey);
        Assert.Equal("UTILITY", sona.Role);             // role-filtered rows are tagged with the lane
        Assert.Equal(65, sona.Games);                    // the lane's sample, not the champion's 69
        Assert.Equal(39.0 / 65, sona.WinRate, 6);
        Assert.Equal(65.0 / 13806, sona.PickRate, 8);    // denominator = support slots
        Assert.Equal(14.0 / 6903, sona.BanRate, 8);      // bans precede positions: unchanged
        Assert.Equal(25, sona.Under25.Games);            // per-lane duration curve
        Assert.Equal(13.0 / 25, sona.Under25.WinRate, 6);
    }

    [Fact]
    public void RolesAreListedInLaneOrder_OnlyWhatTheSampleHolds()
    {
        WritePatch("16.15", runeChampions: 5, tiersJson: SampleJson);
        var source = new FileTierStatsSource(_root);

        // roleSlots names TOP/MIDDLE/UTILITY/UNKNOWN; JUNGLE and BOTTOM are absent from this sample
        // and must not be offered, and UNKNOWN is the unclassified bucket — never a browsable lane.
        Assert.Equal(new[] { "TOP", "MIDDLE", "UTILITY" }, source.Roles("all"));
    }

    [Fact]
    public void OverallExcludesUnclassifiedGames_AndUnknownIsNotALane()
    {
        WritePatch("16.15", runeChampions: 5, tiersJson: SampleJson);
        var source = new FileTierStatsSource(_root);

        // The overall row sums the real lanes only: Sona's 3 and Aatrox's 50 unclassified games
        // are gone, so the totals equal what the lane tabs add up to.
        var all = source.All("all");
        Assert.Equal(69, all.Single(r => r.ChampionKey == 37).Games);
        Assert.Equal(562, all.Single(r => r.ChampionKey == 266).Games);

        // UNKNOWN is neither listed as a lane nor answerable as one.
        Assert.DoesNotContain("UNKNOWN", source.Roles("all"));
        Assert.Empty(source.All("all", "UNKNOWN"));
    }

    /// <summary>Two tiers with deliberately different sample sizes and win rates, so a bracket
    /// built by averaging rates (60% and 40% → 50%) is visibly wrong against the correct
    /// count-weighted answer (36+16 wins over 60+40 games → 52%).</summary>
    private const string TwoTierJson = """
        {"patch": "16.16", "seedTiers": ["EMERALD", "DIAMOND"],
         "byTier": {
           "DIAMOND": {"matches": 200, "roleSlots": {"TOP": 400},
                       "champions": {"86": {"name": "Garen", "games": 60, "wins": 36, "bans": 4,
                                            "duration": {"lt25": [60, 36]},
                                            "roles": {"TOP": {"games": 60, "wins": 36,
                                                              "duration": {"lt25": [60, 36]}}}}}},
           "EMERALD": {"matches": 100, "roleSlots": {"TOP": 200},
                       "champions": {"86": {"name": "Garen", "games": 40, "wins": 16, "bans": 3,
                                            "duration": {"lt25": [40, 16]},
                                            "roles": {"TOP": {"games": 40, "wins": 16,
                                                              "duration": {"lt25": [40, 16]}}}}}}
         }}
        """;

    [Fact]
    public void Bracket_SumsMemberTiersByCount_NotByAveragingRates()
    {
        WritePatch("16.16", runeChampions: 5, tiersJson: TwoTierJson);
        var source = new FileTierStatsSource(_root);

        Assert.Equal(new[] { "EMERALD", "DIAMOND" }, source.SeedTiers);
        Assert.Equal(300, source.SampleInfo.Matches);
        // Every bracket here covers the same one champion, so none is thin: coverage is what
        // decides, and it saturates rather than scaling with how many tiers a bracket spans.
        var offered = source.AvailableBrackets();
        Assert.Equal(new[] { "all", "diamond_plus", "emerald_plus", "platinum_plus", "gold_plus" },
                     offered.Select(b => b.Slug));
        Assert.All(offered, b => Assert.False(b.Thin));

        // diamond_plus sees only Diamond.
        var diamond = source.All("diamond_plus").Single();
        Assert.Equal(60, diamond.Games);
        Assert.Equal(0.6, diamond.WinRate, 6);
        Assert.Equal(60.0 / 200, diamond.PickRate, 6);

        // emerald_plus pools both. 52%, not the 50% an average of 60% and 40% would give.
        var pooled = source.All("emerald_plus").Single();
        Assert.Equal(100, pooled.Games);
        Assert.Equal(0.52, pooled.WinRate, 6);
        Assert.Equal(100.0 / 300, pooled.PickRate, 6);
        Assert.Equal(7.0 / 300, pooled.BanRate, 6);          // ban COUNTS add too
        Assert.Equal(100, pooled.Under25.Games);             // duration buckets add
        Assert.Equal(0.52, pooled.Under25.WinRate, 6);

        // Lane slots add as well, so a pooled lane pick rate has the pooled denominator.
        var pooledTop = source.All("emerald_plus", "TOP").Single();
        Assert.Equal(100.0 / 600, pooledTop.PickRate, 6);
        Assert.Equal(0.52, pooledTop.WinRate, 6);
    }

    [Fact]
    public void LegacyFile_StillRenders_AndDerivesWhatItCan()
    {
        // Pre-bracket output: a single unlabelled root block, rates instead of some counts, and
        // roles as bare [games, wins] pairs.
        const string legacy = """
            {"patch": "16.14", "seedTiers": ["DIAMOND"], "matches": 1000, "champions": {
              "86": {"name": "Garen", "games": 200, "wins": 110, "winRate": 0.55,
                     "pickRate": 0.2, "banRate": 0.03,
                     "duration": {"lt25": [200, 110]},
                     "roles": {"TOP": [180, 100], "MIDDLE": [20, 10]}},
              "266": {"name": "Aatrox", "games": 320, "wins": 160, "winRate": 0.5,
                      "pickRate": 0.32, "banRate": 0.05,
                      "duration": {"lt25": [320, 160]},
                      "roles": {"TOP": [320, 160]}}
            }}
            """;
        WritePatch("16.14", runeChampions: 5, tiersJson: legacy);
        var source = new FileTierStatsSource(_root);

        Assert.Equal(new[] { "DIAMOND" }, source.SeedTiers);
        var garen = source.All("diamond_plus").Single(r => r.ChampionKey == 86);
        Assert.Equal(200, garen.Games);
        Assert.Equal(0.55, garen.WinRate, 6);
        // The file stored only a ban RATE; the count is recovered so brackets stay summable.
        Assert.Equal(0.03, garen.BanRate, 3);

        var top = source.All("diamond_plus", "TOP").Single(r => r.ChampionKey == 86);
        Assert.Equal(180, top.Games);
        Assert.Equal(100.0 / 180, top.WinRate, 6);
        // roleSlots is missing, so the denominator falls back to the lane's own total summed over
        // the rows the file does contain (180 + 320) — approximate, since champions the aggregation
        // dropped for thin samples are not in that sum, but derived from the file, never invented.
        Assert.Equal(180.0 / 500, top.PickRate, 6);
        // The legacy shape stored no per-lane duration curve; empty buckets, not fabricated ones.
        Assert.Equal(0, top.Under25.Games);
    }

    [Fact]
    public void FollowsPatchResolution_ThinNewPatchIsSkippedWithItsRunes()
    {
        WritePatch("16.14", runeChampions: 10); // resolved patch, no tiers.json
        WritePatch("16.15", runeChampions: 2, tiersJson: SampleJson); // thin: below coverage floor

        var source = new FileTierStatsSource(_root);

        Assert.Empty(source.All("all"));
        Assert.Equal(("", 0), source.SampleInfo);
    }

    [Fact]
    public void MissingOrCorrupt_DegradesToEmpty()
    {
        Assert.Empty(new FileTierStatsSource(Path.Combine(_root, "nope")).All("all"));

        WritePatch("16.15", runeChampions: 3);
        Assert.Empty(new FileTierStatsSource(_root).All("all"));

        WritePatch("16.16", runeChampions: 3, tiersJson: "not json at all");
        Assert.Empty(new FileTierStatsSource(_root).All("all"));

        Assert.Empty(new FileTierStatsSource("").All("all"));
    }
}
