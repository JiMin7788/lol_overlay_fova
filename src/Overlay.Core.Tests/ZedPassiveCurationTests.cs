using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 550) Zed P — Contempt for the Weak. The BIN's FinalDamage is a GameCalculationConditional
/// (unsupported wrapper), but the wrapper is a router, not the number: the inner percent calc
/// <c>{6a1a62e9}</c> is a plain ByCharLevelBreakpoints GameCalculation (5% at 1, +2.5% at 7/17/25)
/// and the wrapper's gate (target below 50% HP) is the engine's HpBelow UserAssumed condition.
/// These rows pin that route-around: the fraction resolves per level, and the curated rider
/// surfaces its checkbox on AA nodes (onHit rider, conditional discovery — the user-visible half
/// of "약자멸시가 평타에 안 뜸").
/// </summary>
public class ZedPassiveCurationTests
{
    private static ChampionData Zed()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "zed.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Zed", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Zed" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Zed", Name = "Zed", Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ActivePlayerStats Stats() => new() { AbilityQ = 1, AbilityW = 1, AbilityE = 1 };

    [Theory]
    [InlineData(1, 0.05)]    // breakpoint table: 5% until 7
    [InlineData(6, 0.05)]
    [InlineData(7, 0.075)]   // +2.5% at 7
    [InlineData(16, 0.075)]
    [InlineData(17, 0.10)]   // +2.5% at 17
    [InlineData(18, 0.10)]   // the level-25 breakpoint is unreachable in-game
    public void ZedP_PercentOfTargetMaxHp_FollowsLevelBreakpoints(int level, double expectedFraction)
    {
        var fraction = SkillDamage.ResolveHpPercentCalc(Zed(), "P", "{6a1a62e9}", Stats(), level, 0, rankSlot: "P");
        Assert.NotNull(fraction);
        Assert.Equal(expectedFraction, fraction!.Value, 3);
    }

    [Fact]
    public void ZedP_Rider_OffersItsHpBelowCheckboxOnBasicAttacks()
    {
        SkillDamageDb.ResetForTests();
        var hit = SkillDamageDb.GetConditionalHit("Zed", "AA");
        Assert.NotNull(hit);
        Assert.Equal("HpBelow", hit!.ConditionType);
        Assert.Equal(0.5, hit.ConditionValue, 3);
    }
}
