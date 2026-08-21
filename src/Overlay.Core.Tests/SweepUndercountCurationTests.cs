using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// §41 — the four undercounts found by <c>tools/sweep_unreferenced_damage.py</c> (the Zac-W risk class:
/// a damage term whose DataValue no GameCalculation references, therefore invisible to calc-name-driven
/// curation). Each was confirmed against the live wiki tooltip before being curated.
///
/// <para>These pin the RESOLVED value, not the curation text, because the whole failure mode here is
/// silent: a term can be present in the JSON and still resolve to the wrong rank. The BIN arrays carry
/// a rank-0 placeholder at index 0, so index1 must be rank 1 — that offset is exactly what these rows
/// prove, and it is the reason the BIN-vs-tooltip match counts as corroboration.</para>
///
/// <para>Values are BIN-resolved live (M11 dynamic-lookup rule); the expectations below are the current
/// tooltip's rank-1 numbers. If a patch changes them these rows go red on purpose — that is the signal
/// to re-verify, not to loosen the assertion.</para>
/// </summary>
public class SweepUndercountCurationTests
{
    private static ChampionData Load(string id)
    {
        var json = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "data", "communitydragon", id.ToLowerInvariant() + ".bin.json"));
        var binSkills = ChampionBinParser.ParseChampion(id, json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = id + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = id, Name = id, Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ActivePlayerStats Rank1() => new()
    {
        AttackDamage = 0, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    // ── flat terms that were missing entirely ────────────────────────────────────────

    [Theory]
    // Evelynn E (Whiplash), empowered: tooltip "Empowered Magic Damage: 80/120/160/200/240".
    [InlineData("Evelynn", "E", "EmpoweredDamage", 80.0)]
    // Kled W (Violent Tendencies): tooltip "Additional Physical Damage: 20/30/40/50/60".
    [InlineData("Kled", "W", "BaseFlatDamage", 20.0)]
    public void Sweep_MissingFlatTerm_ResolvesToTooltipRank1(
        string champ, string slot, string dataValue, double expected)
    {
        double? actual = SkillDamage.ComputeFlatDataValue(Load(champ), slot, dataValue, Rank1(), level: 1);
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value, 2);
    }

    // ── %maxHP terms that were missing entirely ──────────────────────────────────────

    [Theory]
    // Gnar W (Hyper) mini third-hit: tooltip "+ 6/8/10/12/14% of target's maximum health".
    // Worst case found: MiniBaseDamage is 0 at rank 1, so before this the proc reported ZERO damage.
    [InlineData("Gnar", "W", "MiniPercentHPDamage", 0.06)]
    // Yone W (Spirit Cleave): tooltip "+ 8/9/10/11/12% of target's maximum health".
    [InlineData("Yone", "W", "MaxHealthDamage", 0.08)]
    public void Sweep_MissingHpPercentTerm_ResolvesToTooltipRank1(
        string champ, string slot, string dataValue, double expected)
    {
        double? actual = SkillDamage.ResolveHpPercent(Load(champ), slot, dataValue, Rank1(), level: 1);
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value, 4);
    }

    /// <summary>
    /// The Gnar case is the one that justifies the whole sweep, so it gets its own row: the flat term
    /// this curation ALREADY had is 0 at rank 1, which means the previously reported damage for a
    /// rank-1 mini-W proc was zero. A missing term is not always a partial undercount.
    /// </summary>
    [Fact]
    public void Sweep_GnarW_FlatTermIsZeroAtRank1_SoTheHpPercentTermWasTheEntireDamage()
    {
        double? flat = SkillDamage.ComputeCalcDamage(Load("Gnar"), "W", "MiniTotalDamage", Rank1(), level: 1);
        Assert.NotNull(flat);
        Assert.Equal(0.0, flat!.Value, 2);
    }
}
