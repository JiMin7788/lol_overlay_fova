using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (TODO §11.C) Stat-coefficient interpretation audit: proves <c>StatByCoefficientCalculationPart</c>
/// resolves each coefficient against the CORRECT live stat, with no AD/AP confusion inflating a
/// number. The trap: a DEFAULT-mStat coefficient means AP (0 = AP), an explicit mStat=2 means AD —
/// swapping them silently mis-scales every affected champion.
///
/// Method (base-agnostic, so it can't be fooled by a wrong base DataValue): for each calc, vary ONE
/// stat while holding the rest fixed and assert the DELTA equals the coefficient times that stat —
/// and that the OTHER stat has zero effect. This isolates the coefficient's stat + rate exactly,
/// against the wiki formula cited in each champion's own curation note. Real BIN fixtures (frozen
/// test data) via ChampionBinParser, same loader as ComboDamageModelTests.
/// </summary>
public class StatCoefficientAuditTests
{
    private static string FixturePath(string champion)
        => Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{champion}.bin.json");

    private static ChampionData Load(string championId)
    {
        var json = File.ReadAllText(FixturePath(championId.ToLowerInvariant()));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = championId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = championId, Name = championId, Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static double Calc(ChampionData c, string slot, string calc, ActivePlayerStats stats, int level)
        => SkillDamage.ComputeCalcDamage(c, slot, calc, stats, level)
           ?? throw new Xunit.Sdk.XunitException($"{c.Id} {slot}/{calc} did not resolve");

    // ── default mStat == AP (the core "not AD-confused" check) ─────────────────────

    [Fact]
    public void IvernW_DefaultMStatCoefficient_ScalesWithAp_NotAd()
    {
        // Ivern W (Brushmaker) TotalDamage = BaseDamage[rank] + StatByCoefficient(0.20, no mStat = AP).
        // Wiki: "(+20% AP)". No AD term at all.
        var ivern = Load("Ivern");
        ActivePlayerStats S(double ap, double ad) => new() { AbilityPower = ap, AttackDamage = ad, AbilityW = 5 };

        // Vary AP (hold AD): delta must be exactly 0.20 * ΔAP.
        double apDelta = Calc(ivern, "W", "TotalDamage", S(100, 300), 1) - Calc(ivern, "W", "TotalDamage", S(0, 300), 1);
        Assert.Equal(0.20 * 100, apDelta, 3);

        // Vary AD (hold AP at 0): ZERO change — the default coefficient is AP, so AD must not leak in.
        Assert.Equal(Calc(ivern, "W", "TotalDamage", S(0, 50), 1), Calc(ivern, "W", "TotalDamage", S(0, 300), 1), 3);
    }

    [Fact]
    public void LockeP_DefaultMStatCoefficient_ScalesWithAp_NotAd()
    {
        // Locke P (Silver Stake) MinOnHitDamage = ByCharLevelInterpolation(5..40) + StatByCoefficient(0.1, = AP).
        // Re-verifies the champion from the §10 bug directly: its on-hit scales with AP at 0.10, not AD.
        var locke = Load("Locke");
        ActivePlayerStats S(double ap, double ad) => new() { AbilityPower = ap, AttackDamage = ad };

        double apDelta = Calc(locke, "P", "MinOnHitDamage", S(200, 300), 7) - Calc(locke, "P", "MinOnHitDamage", S(0, 300), 7);
        Assert.Equal(0.10 * 200, apDelta, 3);

        Assert.Equal(Calc(locke, "P", "MinOnHitDamage", S(0, 50), 7), Calc(locke, "P", "MinOnHitDamage", S(0, 300), 7), 3);
    }

    // ── explicit mStat=2 == AD (the AD branch is distinct from AP) ─────────────────

    [Fact]
    public void RengarR_MStat2Coefficient_ScalesWithAd_NotAp()
    {
        // Rengar R (Thrill of the Hunt) BonusDamage = StatByCoefficient(1.0, mStat=2 = total AD).
        // Wiki: "100% AD bonus physical damage". No AP term. Proves mStat=2 resolves to AD, so the
        // AP-default and AD branches are genuinely distinguished (not both silently one stat).
        var rengar = Load("Rengar");
        ActivePlayerStats S(double ad, double ap) => new() { AttackDamage = ad, AbilityPower = ap, AbilityR = 1 };

        // Vary AD (hold AP): delta must be exactly 1.0 * ΔAD.
        double adDelta = Calc(rengar, "R", "BonusDamage", S(200, 0), 6) - Calc(rengar, "R", "BonusDamage", S(100, 0), 6);
        Assert.Equal(1.0 * 100, adDelta, 3);

        // Vary AP (hold AD): ZERO change — this coefficient is AD, AP must not leak in.
        Assert.Equal(Calc(rengar, "R", "BonusDamage", S(100, 0), 6), Calc(rengar, "R", "BonusDamage", S(100, 200), 6), 3);
    }
}
