using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #8 — Nasus, the BuffCounter (stack-scaled) path. Siphoning Strike Q empowers the next
/// basic attack: Q = BonusDamage(rank) + TOTAL AD + stacks × 1.0 (BuffCounterByCoefficient 1.0),
/// physical. Measured 2026-07-14 (dummy Armor 20, ×100/120 = 0.8333):
///   L1, AD 72, 0 stacks   → 93   (raw 111.6 = BonusDamage[1] + 72 + 0)
///   L4, AD 81, 117 stacks → 198  (raw 237.6 = BonusDamage[1] + 81 + 117)
/// Both back-solve to BonusDamage[1] ≈ 39.6 (Q rank 1 in both — the constant BonusDamage proves the
/// rank didn't change), and the 117 stacks account for EXACTLY +117 raw ⇒ per-stack coefficient 1.0.
///
/// BIN-referenced: ComputeCalcDamage reads BonusDamage + resolves total AD + the BuffCounter stack
/// term from communitydragon; the stack count is the ComboNode.UserStackCount knob (here the
/// stackCount arg). No hardcoded coefficients.
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs.
/// </summary>
public class GoldenNasusTests
{
    private static ChampionData Nasus()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "nasus.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Nasus", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Nasus" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Nasus", Name = "Nasus", Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static double Q(ChampionData nasus, double totalAd, int level, int stackCount)
        => SkillDamage.ComputeCalcDamage(
            nasus, "Q", "TotalDamage",
            new ActivePlayerStats { AttackDamage = totalAd, AbilityQ = 1 },
            level, stackCount: stackCount)!.Value;

    private static void AssertWithin(double expected, double actual, double tol, string what)
        => Assert.True(Math.Abs(actual - expected) <= tol,
            $"{what}: expected {expected} ±{tol}, engine gave {actual:0.##} (Δ={actual - expected:0.##})");

    [Fact]
    public void Golden_Nasus_Q_0Stacks_L1_Ad72_Raw111()
    {
        // raw 111.6 → ×0.8333 = 93 measured.
        AssertWithin(111.6, Q(Nasus(), totalAd: 72, level: 1, stackCount: 0), 1.5, "Nasus Q 0-stack raw (BonusDamage + 72)");
    }

    [Fact]
    public void Golden_Nasus_Q_117Stacks_L4_Ad81_Raw237()
    {
        // raw 237.6 → ×0.8333 = 198 measured. 117 stacks add exactly +117.
        AssertWithin(237.6, Q(Nasus(), totalAd: 81, level: 4, stackCount: 117), 1.5, "Nasus Q 117-stack raw (BonusDamage + 81 + 117)");
    }

    [Fact]
    public void Golden_Nasus_Q_PerStackCoefficient_Is1()
    {
        // Same AD/rank, only stacks vary: 100 stacks add exactly +100 ⇒ BuffCounter coefficient 1.0.
        var nasus = Nasus();
        double delta = Q(nasus, 72, 1, stackCount: 100) - Q(nasus, 72, 1, stackCount: 0);
        Assert.Equal(100.0, delta, precision: 2);
    }
}
