using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #7 — Varus, the multi-axis shape lock. Measured 2026-07-14 (training bot Armor/MR 20,
/// 1000 HP; Varus L6, AD 78 (bonus ≈ 5.3), AP 0, all abilities rank 1). Mitigation ×100/120 = 0.8333.
/// Five engine paths, all cross-checked to the measured HP deltas:
///
///  1. Q charge min/max (Piercing Arrow): base 53.334→80 + 80%→120% bonus AD; min = ⅔×max.
///     min = (53.334 + 0.8×5.33)×0.8333 = 48 ; max = (80 + 1.2×5.33)×0.8333 = 72 ✓
///  2. W passive on-hit (AlwaysOn, loop-104 class): 4 + 25%AP + 15% bonus AD.
///     (4 + 0 + 0.15×5.33)×0.8333 = 4.0 ✓  (AA row: 64 AD + 4 W)
///  3. Blight detonation (onAbility %maxHP hit, hpBasis Max): 3%/stack × target maxHP.
///     3 stacks × 3% × 1000 × 0.8333 = 75 ✓  (E-detonate case)
///  4. Q-charge amplifies the blight up to +50%: full-charge Q detonate = 75×1.5 = 112.5 ≈ 112 ✓
///  5. W-active empowers next Q with 6%→9% (charge) of MISSING health, DYNAMICALLY re-evaluated
///     (reads HP AFTER the same-cast Q base + blight land — the Garen-R MissingHp path):
///     at 518/1000, full charge: HP after Q(72)+blight(112) = 334, missing 666 → 9%×666×0.8333 = 49.9 ≈ 49 ✓
///  (R base rank1 = 150 → ×0.8333 = 125, seen consistently in the R rows.)
///
/// Curation (skill_damage/Varus.json) already models these: Q min/max (TotalDamageMinTooltip/Max via
/// UserDistanceFraction), W onHit alwaysOn (OnHitDamage) + onAbility blight (MaxPercentHPPerStack,
/// hpBasis Max), E/R TotalDamage. Composition machinery = the Varus_MultiAxis integration test.
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs. The robust single-hit
/// axes are asserted here; the stack/charge/W-active COMPOSITION rows are marked for Claude Code to
/// wire through the existing multi-axis path (they need stack-count + charge-fraction + W-active setup).
/// BaseStats.Ad pinned to 72.67 (Varus L6 base) so bonus AD = 78−72.67 = 5.33 reproduces the measured.
/// </summary>
public class GoldenVarusTests
{
    private const double BaseAdL6 = 72.67; // so bonus AD = 78 − 72.67 = 5.33 (drives Q/W ratios)

    private static ChampionData Varus()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "varus.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Varus", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Varus" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Varus", Name = "Varus", Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = BaseAdL6 }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 78, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static double Calc(string slot, string calc)
        => SkillDamage.ComputeCalcDamage(Varus(), slot, calc, Stats(), level: 6)!.Value;

    // ── Axis 1: Q charge min/max (raw calc, from BIN) ────────────────────────────────
    [Fact]
    public void Golden_Varus_Q_ChargeMinMax_RawFromBin()
    {
        // Raw (pre-mitigation): min = 53.334 + 0.8×5.33 = 57.6 ; max = 80 + 1.2×5.33 = 86.4.
        // Live (×0.8333 vs resist 20) = 48 / 72.
        AssertWithinOne(57.6, Calc("Q", "TotalDamageMinTooltip"), "Varus Q min-charge raw");
        AssertWithinOne(86.4, Calc("Q", "TotalDamageMax"), "Varus Q full-charge raw");
    }

    // ── Axis 2: W passive on-hit (AlwaysOn) raw = 4 + 25%AP + 15% bonus AD ────────────
    [Fact]
    public void Golden_Varus_W_OnHit_Raw()
    {
        // 4 + 0.25×0 + 0.15×5.33 = 4.8 raw → ×0.8333 = 4 live.
        AssertWithinOne(4.8, Calc("W", "OnHitDamage"), "Varus W on-hit raw (4 + 25%AP + 15% bonus AD)");
    }

    // ── Axis 3: blight FULL-detonation %maxHP fraction (3 stacks pre-multiplied) ──────
    [Fact]
    public void Golden_Varus_Blight_FullDetonationIsNinePercentMaxHp()
    {
        // Blight is 3% max HP PER STACK and caps at 3 stacks, so the MAXIMUM detonation is 9%.
        // MaxPercentHPPerStack is that capped maximum (ModifiedGameCalculation = PercentHPPerStack×3,
        // per Varus.json _noteW) — read the name as "Max PercentHPPerStack", not "per stack".
        // rank1 = 0.09, confirmed by the measured live 75 on the 1000 HP / MR 20 bot:
        // 75 ÷ 1000 ÷ 0.8333 = 0.09.
        //
        // Curating the cap means every composed blight row is the FULL-STACK UPPER BOUND; the engine
        // does not live-track stack count, so a 1- or 2-stack detonation is not expressible today
        // (_noteW's own honesty caveat).
        //
        // (2026-07-20) This row previously read 0.03 and called it "3% per stack". That was wrong on
        // both counts, and it PASSED only because AssertWithinOne's ±1.5 absolute tolerance is
        // meaningless for a fraction — it accepted anything in [-1.47, 1.53]. Fractions get a
        // proportional tolerance so this assertion actually constrains something; the composition
        // row Varus_MultiAxis_BlightDetonates_OnEveryAbility pins the same value end-to-end at 75.
        Assert.Equal(0.09, Calc("W", "MaxPercentHPPerStack"), 3);
    }

    // ── R base ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void Golden_Varus_R_Base150_Raw()
    {
        AssertWithinOne(150, Calc("R", "TotalDamage"), "Varus R raw (rank1 base 150)");
    }

    // ── Composition rows → GoldenVarusCompositionTests (Varus_MultiAxis), wired 2026-07-20 ───
    // DONE there: Q charge min→max interpolation, and blight = 75 detonating on every ability.
    // NOT wired, because there is no curation to drive them (they are a curation/engine decision,
    // not a missing test — logged as CLAUDE_CODE_TODO §40):
    //   blight via full-charge Q = 112 (75 × 1.5 charge amp) — UserDistanceFraction only interpolates
    //     Q's OWN min/max hit; it never reaches the separate onAbility blight bonus effect.
    //   W-active full-charge Q missing bonus = 49 (9% of DYNAMIC missing 666 at 518/1000) — the
    //     WQHealthDamage/QEmpowerPercentHP terms are "Deliberately NOT curated" per Varus.json _noteW.
    //     Would need a Missing-basis hit ordered AFTER Q base + blight (the Garen-R MissingHp path).
    private static void AssertWithinOne(double expected, double actual, string what)
        => Assert.True(Math.Abs(actual - expected) <= 1.5,
            $"{what}: expected {expected} ±1.5, engine gave {actual:0.###} (Δ={actual - expected:0.###})");
}
