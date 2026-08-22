using System.IO;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN — Sylas P (Petricite Burst). After Sylas casts an ability, his next basic attack is
/// EMPOWERED: instead of a normal physical auto it strikes in an arc for MAGIC damage, so the
/// basic-attack damage is REPLACED (not added to) by the passive's value — curated as the
/// <c>skill_damage/Sylas.json</c> "P" node, one Magic hit on calc <c>PassiveDamage</c>.
///
/// <para>The BIN formula (<c>sylas.bin.json</c> PassiveDamage, read 2026-08-22) is two
/// <c>StatByCoefficientCalculationPart</c>s: <c>mStat=2</c> (TOTAL AD) × 1.3 and a coefficient-only
/// part × 0.3 (default <c>mStat=0</c> = AP). So the empowered attack = <b>1.3 × total AD + 0.3 ×
/// AP</b>, as MAGIC. No rank scaling (a passive), so the value depends only on AD and AP.</para>
///
/// <para>Three points pin both coefficients independently rather than through one combined number:
/// AD-only reads the 1.3, AP-only reads the 0.3, and the combined row is the number a real Sylas
/// sees. If the engine ever regressed the default-mStat (AP) handling or the total-vs-bonus AD
/// split, exactly one of these rows would move.</para>
/// </summary>
public class GoldenSylasTests
{
    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{championId.ToLowerInvariant()}.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = championId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = championId, Name = championId, Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static double Passive(ChampionData sylas, double totalAd, double ap)
        => SkillDamage.ComputeCalcDamage(
               sylas, "P", "PassiveDamage",
               new ActivePlayerStats { AttackDamage = totalAd, AbilityPower = ap }, level: 18)!.Value;

    private static void AssertWithinOne(double expected, double actual, string what)
        => Assert.True(System.Math.Abs(actual - expected) <= 1.0,
            $"{what}: expected {expected} ±1, engine gave {actual:0.##} (Δ={actual - expected:0.##})");

    [Fact]
    public void PetriciteBurst_EmpoweredAttack_Is130PercentTotalADPlus30PercentAP()
    {
        var sylas = LoadChampionFromBin("Sylas");

        // AD only → 1.3 × total AD. 200 AD, 0 AP → 260.
        AssertWithinOne(260, Passive(sylas, totalAd: 200, ap: 0), "P: 1.3 × total AD");

        // AP only → 0.3 × AP. 0 AD, 100 AP → 30. (Pins the coefficient-only, default-mStat=AP part.)
        AssertWithinOne(30, Passive(sylas, totalAd: 0, ap: 100), "P: 0.3 × AP");

        // Combined — the empowered auto a real Sylas throws: 200 AD + 100 AP → 260 + 30 = 290.
        AssertWithinOne(290, Passive(sylas, totalAd: 200, ap: 100), "P: 1.3 AD + 0.3 AP");
    }
}
