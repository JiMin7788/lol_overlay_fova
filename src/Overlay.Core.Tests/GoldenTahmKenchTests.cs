using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN — Tam Kench Q + Passive, and the mStat-12 (bonus-health) BASIS anchor (CLAUDE_CODE_TODO §17).
/// Measured 2026-07-14 (Tam Kench L1 / Q rank 1, target Kennen Armor 36 / MR 32 / HP 755):
///   AP 0,  HP 650 (0 bonus)    → 60      AP 29, HP 650 → 82      AP 29, HP 950 (+300 bonus) → 92
///
/// KEY (why we read BIN, never back-calc the tooltip): the measured number is Q + a CHAMPION-HIT
/// BONUS. Tam Kench has NO on-hit (tooltip-confirmed); instead Q, when it hits a CHAMPION, deals an
/// extra hit whose damage is the SAME formula as the passive An Acquired Taste (5 @ L1 → 60 + 0.04×
/// bonus health (mStat 12, formula 2) + AP×bonusHP×0.000125) — the BIN has exactly ONE such 0.04-
/// mStat12 calc, so the Q champion-bonus REUSES the passive calc, but it is kept as its OWN separate
/// hit (a patch can diverge them). Q's own `TotalDamage` is `BaseDamage + 1.0×AP` with NO health
/// term. Naively back-calculating from the bundled 60/82/92 would wrongly hand Q a ~4.4% health
/// coefficient it does not have — the health scaling is the champion-bonus, not Q.
///
/// CURATION GAP (see §17): the current Tam Q curation is a single `{calc: TotalDamage}` hit — it
/// MISSES the champion-hit bonus, so the engine under-counts Q vs a champion (≈57, not the measured
/// 60). Q needs a second, champion-conditional hit referencing the passive calc.
///
/// This test references communitydragon values via SkillDamage.ComputeCalcDamage (unmitigated raw) —
/// it does NOT hardcode base/coefficients (a patch re-flows automatically). Base HP pinned to Tam's
/// L1 base (650) so bonus = total − base reproduces the measured 0 / +300.
///
/// Cross-check to the live numbers (magic → ×100/132 = 0.7576):
///   A: (Q 75 + P 5)              × 0.7576 = 60.6 ≈ 60
///   B: (Q 104 + P 5)             × 0.7576 = 82.6 ≈ 82
///   C: (Q 104 + P 18.09)         × 0.7576 = 92.5 ≈ 92   ← +10 is the PASSIVE's 0.04×300, not Q
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs.
/// </summary>
public class GoldenTahmKenchTests
{
    private const double BaseHpL1 = 650; // Tam Kench L1 base HP (config-A total = base ⇒ 0 bonus)

    private static ChampionData TahmKench()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "tahmkench.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("TahmKench", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "TahmKench" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "TahmKench", Name = "TahmKench", Skills = skills,
            BaseStats = new ChampionBaseStats { Hp = BaseHpL1 }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ActivePlayerStats Stats(double ap, double totalHp) => new()
    {
        AttackDamage = 0, AbilityPower = ap, MaxHealth = totalHp,
        AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static double Calc(ChampionData c, string slot, string calc, double ap, double hp)
        => SkillDamage.ComputeCalcDamage(c, slot, calc, Stats(ap, hp), level: 1)!.Value;

    // ── Q: base 75 + 1.0×AP, NO health term ──────────────────────────────────────────

    [Fact]
    public void Golden_TahmKench_Q_Base75_ApRatio1_NoHealthTerm()
    {
        var tam = TahmKench();
        Assert.Equal(75, Calc(tam, "Q", "TotalDamage", ap: 0, hp: 650), precision: 1);    // base only
        Assert.Equal(104, Calc(tam, "Q", "TotalDamage", ap: 29, hp: 650), precision: 1);  // + 1.0×29
        // The anchor's negative control: +300 bonus health does NOT change Q (proves Q has no mStat-12).
        Assert.Equal(104, Calc(tam, "Q", "TotalDamage", ap: 29, hp: 950), precision: 1);
    }

    // ── Passive (An Acquired Taste): 5 @ L1 + 0.04×bonus health (mStat 12) — the caster-basis anchor ─

    [Fact]
    public void Golden_TahmKench_Passive_CasterBonusHealth_004Coefficient()
    {
        var tam = TahmKench();
        Assert.Equal(5, Calc(tam, "P", "TotalDamage", ap: 0, hp: 650), precision: 1);   // L1 base, 0 bonus
        // +300 of TAM's OWN bonus health → +0.04×300 = +12 (plus AP×bonusHP term at AP 29 = +1.0875).
        Assert.Equal(18.09, Calc(tam, "P", "TotalDamage", ap: 29, hp: 950), precision: 1);
        // Anchor: the passive scales with the CASTER's bonus health — the engine's attacker-health
        // reading of mStat-12 is correct for Tam Kench. A future TARGET-basis flip must not regress this.
    }

    // ── Cross-check: Q vs a CHAMPION = Q base + champion-hit bonus (reuses the passive calc), ─────
    // ── mitigated = 60 / 82 / 92. NOT an on-hit; the two calcs are summed but kept separate. ──────

    [Theory]
    [InlineData(0, 650, 60)]
    [InlineData(29, 650, 82)]
    [InlineData(29, 950, 92)]
    public void Golden_TahmKench_QVsChampion_BasePlusChampionBonus_Mitigated_MatchesMeasured(double ap, double hp, double measured)
    {
        var tam = TahmKench();
        // Q vs champion = Q.TotalDamage + champion-hit bonus (= the passive calc, applied on champ hit).
        double raw = Calc(tam, "Q", "TotalDamage", ap, hp) + Calc(tam, "P", "TotalDamage", ap, hp);
        double mitigated = raw * (100.0 / 132.0); // magic vs Kennen MR 32
        Assert.True(Math.Abs(mitigated - measured) <= 1.0,
            $"Q-vs-champion mitigated: expected {measured} ±1, got {mitigated:0.##}");
    }

    // ── R (Abyssal Voyage / ult): 100 flat + (15% + 0.07%×AP) × TARGET max health, magic ─────────
    // Measured L6 / AP 29 vs Kennen (755 HP): 100 + (0.15 + 0.0007×29)×755 = 228.6 → ×0.7576 = 173.
    // Confirms R is TARGET-basis %maxHP (755, not the caster's 1407) — the §17 R answer. Distinct from
    // Q/Passive's caster mStat-12. HELD: R is mis-curated as a plain `calc: PercentHPDamage` (the
    // K'Sante-W bug — the 0.15+ fraction is treated as absolute damage and target maxHP is never
    // multiplied in). Curation now fixed for the %maxHP term: TahmKench.json R = hpPercentCalc
    // PercentHPDamage / hpBasis Max, magic (see TahmKench.json _noteR). The flat +100 remains OPEN
    // (no named BIN DataValue — §17), so it is NOT asserted here; this test pins only the raw BIN
    // fraction, which is a pure calc eval INDEPENDENT of the curation and passes on its own.
    [Fact]
    public void Golden_TahmKench_R_TargetPercentMaxHp_Fraction()
    {
        var tam = TahmKench();
        // BIN PercentHPDamage (mDisplayAsPercent) = 0.15 + 0.0007×AP; rank/level-independent.
        Assert.Equal(0.1703, Calc(tam, "R", "PercentHPDamage", ap: 29, hp: 1407), precision: 3);
        // Full model = (100 + 0.1703 × 755 target maxHP) × 100/132 = 173. The %maxHP term is now
        // curated (hpPercentCalc/hpBasis Max); the flat 100 is the remaining §17 sub-item.
    }
}
