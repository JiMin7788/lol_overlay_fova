using Overlay.Core.ChampionDb;

namespace Overlay.Core.Tests;

/// <summary>
/// Covers <see cref="ChampionRepository.InitializeFromCache"/>: the all-champion offline
/// load must serve the WHOLE cached roster (every champion with a bundled CommunityDragon
/// BIN), not just the originally sampled 5 — while keeping those 5's richer Data Dragon
/// skill metadata intact. Paths mirror the runtime layout (AppContext.BaseDirectory/data/*),
/// which the Overlay.Core content globs deploy into the test output.
/// </summary>
public class ChampionRepositoryTests : IDisposable
{
    private static readonly string DDragonDir =
        Path.Combine(AppContext.BaseDirectory, "data", "ddragon", "16.13.1");
    private static readonly string CDragonDir =
        Path.Combine(AppContext.BaseDirectory, "data", "communitydragon");

    public ChampionRepositoryTests()
    {
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        ChampionLocalizationRepository.ResetForTests();
    }

    public void Dispose()
    {
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        ChampionLocalizationRepository.ResetForTests();
    }

    [Theory]
    [InlineData("Lux")]
    [InlineData("Garen")]
    public void InitializeFromCache_ResolvesNonSampledChampion_ToRealSkillData(string championId)
    {
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);

        var champion = ChampionRepository.Get(championId);
        Assert.NotNull(champion);

        // All four active-slot skills extract from the BIN with usable damage data.
        foreach (var slot in new[] { "Q", "W", "E", "R" })
        {
            var skill = ChampionRepository.GetSkill(championId, slot);
            Assert.NotNull(skill);
            Assert.True(
                skill!.DataValues.Count > 0 || skill.SpellCalculations.Count > 0,
                $"{championId} {slot} carried no BIN DataValues/SpellCalculations");
        }

        // Base resistances came from the all-champion summary, not the zeroed fallback.
        Assert.True(champion!.BaseStats.Hp > 0);
        Assert.True(champion.BaseStats.Armor > 0);
    }

    [Fact]
    public void InitializeFromCache_KeepsSampledChampionRichData()
    {
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);

        // Aatrox has a cached Data Dragon detail file, so it keeps rich metadata
        // (skill display name + cooldowns) AND gets BIN spell numbers merged in.
        var q = ChampionRepository.GetSkill("Aatrox", "Q");
        Assert.NotNull(q);
        Assert.False(string.IsNullOrEmpty(q!.Name));
        Assert.NotEmpty(q.Cooldown);
        Assert.NotEmpty(q.DataValues);
    }

    /// <summary>Loop-38 gap fix: <see cref="InitializeFromCache_ResolvesNonSampledChampion_ToRealSkillData"/>
    /// only ever asserted Armor&gt;0 for the NON-sampled (ChampionSummary-only) path (Lux/Garen) — the
    /// rich Data-Dragon-detail-file path (<see cref="DDragonParser.ParseChampion"/>, used for the 5
    /// originally sampled champions incl. Ahri) had NO equivalent base-resistance assertion anywhere,
    /// which is exactly the kind of untested path a user's live "target resolves but armor/mr read 0/0"
    /// report could slip through. `data/ddragon/16.13.1/champion/Ahri.json` has `"armor":21,
    /// "armorperlevel":4.2` (hand-verified against the raw file) — this test proves that value actually
    /// makes it all the way through <c>ChampionRepository.InitializeFromCache</c> into
    /// <c>ChampionData.BaseStats</c>, not just that Data Dragon's own file has a real number.</summary>
    [Fact]
    public void InitializeFromCache_SampledChampion_HasRealBaseArmor()
    {
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);

        var ahri = ChampionRepository.Get("Ahri");
        Assert.NotNull(ahri);
        Assert.Equal(21, ahri!.BaseStats.Armor, precision: 1);
        Assert.Equal(4.2, ahri.StatsPerLevel.Armor, precision: 1);
        Assert.True(ahri.BaseStats.Mr > 0, "Ahri base MR should be a real non-zero Data Dragon value");
    }

    /// <summary>Loop-38 continuation 6: covers <see cref="ChampionSummary.ResolveKoreanName"/>, the
    /// fallback that translates a Korean client-language display name (e.g. "아리") back to its
    /// English Data Dragon id ("Ahri") so <c>ComboRunner.TryResolveBase</c> can still match it.</summary>
    [Theory]
    [InlineData("아리", "Ahri")]
    [InlineData("아트록스", "Aatrox")]
    [InlineData("애니", "Annie")]
    [InlineData("제드", "Zed")]
    [InlineData("징크스", "Jinx")]
    public void ResolveKoreanName_TranslatesKnownKoreanDisplayName_ToEnglishId(string ko, string expectedId)
    {
        Assert.Equal(expectedId, ChampionSummary.ResolveKoreanName(ko));
    }

    [Fact]
    public void ResolveKoreanName_ReturnsNull_ForAlreadyEnglishOrUnknownName()
    {
        Assert.Null(ChampionSummary.ResolveKoreanName("Ahri"));
        Assert.Null(ChampionSummary.ResolveKoreanName("NotAChampion"));
        Assert.Null(ChampionSummary.ResolveKoreanName(""));
    }

    /// <summary>Loop-43 continuation: the real bug — a live retest showed EVERY enemy failing
    /// armor/MR lookup because the Live Client API returns Korean names for the FULL roster, not
    /// just the 5 covered by the static <c>KoreanNameToId</c> table (e.g. "베인"/Vayne, "마오카이"/
    /// Maokai — both far outside that table). This proves the fix: once
    /// <see cref="ChampionLocalizationRepository"/> is initialized (as it is at real app startup,
    /// from the same CommunityDragon ko_kr data already used for M04 display localization),
    /// <c>ResolveKoreanName</c> must resolve names beyond the static 5-entry table.</summary>
    [Fact]
    public void ResolveKoreanName_UsesDynamicRepository_ForChampionsBeyondStaticTable()
    {
        ChampionLocalizationRepository.Initialize(new Dictionary<string, string>
        {
            ["Vayne"] = "베인",
            ["Maokai"] = "마오카이",
        });

        Assert.Equal("Vayne", ChampionSummary.ResolveKoreanName("베인"));
        Assert.Equal("Maokai", ChampionSummary.ResolveKoreanName("마오카이"));
    }

    /// <summary>The dynamic repository must take priority over the static table when both could
    /// answer, so a stale/incorrect hand-typed entry can never shadow the live CommunityDragon data.</summary>
    [Fact]
    public void ResolveKoreanName_PrefersDynamicRepository_OverStaticTable()
    {
        ChampionLocalizationRepository.Initialize(new Dictionary<string, string>
        {
            ["Ahri"] = "아리",
        });

        Assert.Equal("Ahri", ChampionSummary.ResolveKoreanName("아리"));
    }

    [Fact]
    public void InitializeFromCache_LoadsWholeRoster()
    {
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);

        // A representative spread of champions well beyond the original 5.
        foreach (var id in new[] { "Aatrox", "Ahri", "Lux", "Garen", "MonkeyKing", "Yasuo", "Zoe" })
            Assert.NotNull(ChampionRepository.Get(id));
    }

    [Fact]
    public void LoadedIds_ExposesWholeRoster_SortedForTheComboPicker()
    {
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);

        var ids = ChampionRepository.LoadedIds;

        // The combo editor's champion picker binds to this (via AppComposition.ChampionIds),
        // so it must expose the FULL cached roster (far more than the sampled 5), not a subset.
        Assert.True(ids.Count > 100, $"expected the full roster, got {ids.Count}");
        Assert.Contains("Lux", ids);
        Assert.Contains("Mordekaiser", ids);
        Assert.Equal(ids.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), ids);
    }
}
