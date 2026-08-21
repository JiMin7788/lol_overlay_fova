using Overlay.Core.Damage;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M05 Damage Engine's 6-step pipeline (docs/modules/M05_DAMAGE_ENGINE.md):
///  - Step 1+2: base damage formula + armor/mr mitigation, hand-computed.
///  - Step 3: shield absorbs first, only the excess hits HP (partial-absorption case).
///  - Step 5: all three execute types (Threshold / CurrentHp / MissingHp) as distinct branches.
///  - Step 6: killThresholdHP is a genuine inverse calculation, not an echo of remainingHP.
///  - breakdown array completeness (every node present exactly once, in order, incl. nodes
///    after a mid-sequence execute kill).
/// </summary>
public class DamageEngineTests
{
    private static readonly AttackerStat NeutralAttacker = new(Ad: 0, BonusAD: 0, Ap: 0, Level: 1, CriticalChance: 0, LifeSteal: 0);

    [Fact]
    public void BaseDamageAndMitigation_MatchHandComputedValue()
    {
        // raw = 20 + 1.0*(60+40) + 0*0 = 120
        // mitigated (armor 50) = 120 * 100/150 = 80
        var attacker = new AttackerStat(Ad: 60, BonusAD: 40, Ap: 0, Level: 11, CriticalChance: 0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 500, MaxHP: 500, Armor: 50, Mr: 0, Shield: 0);
        var node = new ComboNode("Q", Damage: 20, RatioAD: 1.0, RatioAP: 0, DamageType.Physical);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(80.0, result.TotalDamage, 2);
        Assert.Equal(420.0, result.RemainingHP, 2);
        Assert.False(result.IsLethal);
        Assert.Single(result.Breakdown);
        Assert.Equal(80.0, result.Breakdown[0].Damage, 2);
    }

    [Fact]
    public void Shield_AbsorbsFirst_OnlyExcessHitsHP_AcrossTwoNodes()
    {
        // Two True-damage nodes of 40 each (no mitigation, isolates shield logic).
        // Shield = 50: node1's 40 fully absorbed (shield -> 10 left, 0 HP loss).
        // node2's 40: only 10 left in shield absorbs, 30 hits HP.
        // Total HP loss = 30; totalDamage (pre-shield) = 80.
        var defender = new DefenderStat(CurrentHP: 200, MaxHP: 200, Armor: 0, Mr: 0, Shield: 50);
        var nodes = new[]
        {
            new ComboNode("N1", Damage: 40, RatioAD: 0, RatioAP: 0, DamageType.True),
            new ComboNode("N2", Damage: 40, RatioAD: 0, RatioAP: 0, DamageType.True),
        };

        var result = new DamageEngine().Calculate(new DamageCalcInput(nodes, NeutralAttacker, defender));

        Assert.Equal(80.0, result.TotalDamage, 2);
        Assert.Equal(170.0, result.RemainingHP, 2); // 200 - 30
        Assert.False(result.IsLethal);
        Assert.Equal(40.0, result.Breakdown[0].Damage, 2);
        Assert.Equal(40.0, result.Breakdown[1].Damage, 2);
    }

    [Fact]
    public void ExecuteType_Threshold_TriggersInstantKill_BelowThreshold()
    {
        // raw=10 (true, no mitigation), hpLoss=10 -> remainingHP = 45-10 = 35.
        // 35 <= executeThreshold(50) -> instant kill (remainingHP forced to 0),
        // even though 35 > 0 on its own (proves this is the Threshold branch, not a
        // natural zero-HP death).
        var defender = new DefenderStat(CurrentHP: 45, MaxHP: 100, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("R", Damage: 10, RatioAD: 0, RatioAP: 0, DamageType.True,
            ExecuteType: ExecuteType.Threshold, ExecuteThreshold: 50);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, NeutralAttacker, defender));

        Assert.True(result.IsLethal);
        Assert.Equal(0.0, result.RemainingHP, 2);
    }

    [Fact]
    public void ExecuteType_CurrentHp_RecalculatesAsPercentOfRemainingHP()
    {
        // Node's flat/ratio fields are irrelevant (Damage=0): CurrentHp replaces raw
        // damage with ExecutePercent * remainingHP-at-cast-time = 0.5 * 100 = 50.
        var defender = new DefenderStat(CurrentHP: 100, MaxHP: 100, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("R_tick", Damage: 0, RatioAD: 0, RatioAP: 0, DamageType.True,
            ExecuteType: ExecuteType.CurrentHp, ExecutePercent: 0.5);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, NeutralAttacker, defender));

        Assert.Equal(50.0, result.TotalDamage, 2);
        Assert.Equal(50.0, result.RemainingHP, 2);
        Assert.False(result.IsLethal);
    }

    [Fact]
    public void HpBelowGate_HitApplies_OnlyAfterRunningHpFallsBelowThreshold()
    {
        // §27 (B): a hit tagged with HpBelowGateFraction (Ekko-W-passive-style low-HP on-hit) contributes
        // damage ONLY once the combo's OWN prior damage has depleted the (per-track) running HP to/below
        // the threshold. True damage + no crit → one deterministic track, so the gate is unambiguous.
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var gated = new ComboNode("G", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.True, HpBelowGateFraction: 0.30);
        var big = new ComboNode("B", Damage: 800, RatioAD: 0, RatioAP: 0, DamageType.True);

        // Order [big, gated]: big depletes 1000→200 (below 30% = 300), so the gated hit APPLIES (+100).
        var applied = new DamageEngine().Calculate(new DamageCalcInput(new[] { big, gated }, NeutralAttacker, defender));
        Assert.Equal(900.0, applied.TotalDamage, 2);

        // Order [gated, big]: running HP is still 1000 (> 300) when the gated hit lands → gated OUT (0),
        // so only the big hit's 800 counts. Same two hits, different order → the low-HP proc turns on
        // only after the HP has actually fallen, exactly the dynamic within-combo model.
        var gatedOut = new DamageEngine().Calculate(new DamageCalcInput(new[] { gated, big }, NeutralAttacker, defender));
        Assert.Equal(800.0, gatedOut.TotalDamage, 2);
    }

    [Fact]
    public void ExecuteType_MissingHp_AddsBonusBasedOnMissingHealth()
    {
        // missing = maxHP(100) - currentHP(60) = 40; bonus = 0.5*40 = 20.
        // raw = base(10) + bonus(20) = 30 (true, no mitigation) -> hpLoss = 30.
        var defender = new DefenderStat(CurrentHP: 60, MaxHP: 100, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("Passive", Damage: 10, RatioAD: 0, RatioAP: 0, DamageType.True,
            ExecuteType: ExecuteType.MissingHp, ExecutePercent: 0.5);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, NeutralAttacker, defender));

        Assert.Equal(30.0, result.TotalDamage, 2);
        Assert.Equal(30.0, result.RemainingHP, 2);
    }

    [Fact]
    public void ExecuteType_BaseWithMissingHpBonus_FullHealthTarget_BonusIsNearZero()
    {
        // Kraken Slayer's real "Bring It Down" shape (wiki-confirmed, see item_effects.json's
        // 3095 _note): base * (1 + scalar * missingHpFraction), scalar = 0.75. At full HP
        // (currentHP == maxHP) missingHpFraction = 0, so raw stays exactly at base (150).
        var defender = new DefenderStat(CurrentHP: 2000, MaxHP: 2000, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("KrakenProc", Damage: 150, RatioAD: 0, RatioAP: 0, DamageType.Physical,
            ExecuteType: ExecuteType.BaseWithMissingHpBonus, ExecutePercent: 0.75);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, NeutralAttacker, defender));

        Assert.Equal(150.0, result.TotalDamage, 2);
    }

    [Fact]
    public void ExecuteType_BaseWithMissingHpBonus_NearDeadTarget_BonusApproachesMax()
    {
        // Near-dead target (currentHP=1 of maxHP=2000): missingHpFraction ~= 0.9995, so the
        // multiplier approaches its cap of 1.75 (1 + 0.75*1.0), matching the wiki's confirmed
        // "up to 262.5-350 (melee)" endpoint being exactly base*1.75.
        var defender = new DefenderStat(CurrentHP: 1, MaxHP: 2000, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("KrakenProc", Damage: 150, RatioAD: 0, RatioAP: 0, DamageType.Physical,
            ExecuteType: ExecuteType.BaseWithMissingHpBonus, ExecutePercent: 0.75);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, NeutralAttacker, defender));

        // missingFrac = 1999/2000 = 0.9995; raw = 150 * (1 + 0.75*0.9995) = 262.44375.
        Assert.Equal(262.44, result.TotalDamage, 2);
        Assert.True(result.TotalDamage < 150.0 * 1.75); // never exceeds the theoretical 100%-missing cap
    }

    [Fact]
    public void ExecuteType_BaseWithMissingHpBonus_ReEvaluatesLiveAsHpDropsWithinSequence()
    {
        // Two identical proc nodes back-to-back on a target that's already lost HP to the first
        // one: the second must see the LOWER remainingHP (dynamic re-evaluation), not a frozen
        // pre-combo snapshot — same convention as CurrentHp/MissingHp above.
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            new ComboNode("Proc1", Damage: 150, RatioAD: 0, RatioAP: 0, DamageType.Physical,
                ExecuteType: ExecuteType.BaseWithMissingHpBonus, ExecutePercent: 0.75),
            new ComboNode("Proc2", Damage: 150, RatioAD: 0, RatioAP: 0, DamageType.Physical,
                ExecuteType: ExecuteType.BaseWithMissingHpBonus, ExecutePercent: 0.75),
        };

        var result = new DamageEngine().Calculate(new DamageCalcInput(nodes, NeutralAttacker, defender));

        // Proc1: missingFrac=0 -> 150. Proc2: remainingHp=850, missingFrac=150/1000=0.15 ->
        // 150*(1+0.75*0.15) = 166.875.
        Assert.Equal(150.0, result.Breakdown[0].Damage, 2);
        Assert.Equal(166.88, result.Breakdown[1].Damage, 2);
    }

    [Fact]
    public void SequentialCombo_MissingHpNode_ReadsRemainingHpAlreadyReducedByEarlierNodesInSameCombo()
    {
        // loop 38 continuation 21: proves the user's Garen Q->E->W->R->AA scenario is already
        // correctly handled by the engine's single running remainingHp variable — a combo starts
        // the target at MAX HP (100%), and a later MissingHp node (the "R") reads the ALREADY-
        // REDUCED remainingHp left by the earlier Q/E/W nodes in the SAME combo, not a frozen
        // pre-combo snapshot or any cross-combo estimate. Round numbers, hand-verified below.
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            new ComboNode("Q", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.Physical),
            new ComboNode("E", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.Magic),
            new ComboNode("W", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.True),
            // "R": base 100 true + 30% of missing HP at cast time (Garen R's real 25/30/35% shape).
            new ComboNode("R", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.True,
                ExecuteType: ExecuteType.MissingHp, ExecutePercent: 0.3),
            new ComboNode("AA", Damage: 50, RatioAD: 0, RatioAP: 0, DamageType.True),
        };

        var result = new DamageEngine().Calculate(new DamageCalcInput(nodes, NeutralAttacker, defender));

        // Q: 100 (armor 0) -> remainingHp 900.
        // E: 100 (mr 0) -> remainingHp 800.
        // W: 100 (true) -> remainingHp 700.
        // R: missing = 1000-700 = 300; bonus = 0.3*300 = 90; raw = 100+90 = 190 (true) ->
        //    remainingHp = 700-190 = 510.
        // AA: 50 (true) -> remainingHp 460.
        Assert.Equal(100.0, result.Breakdown[0].Damage, 2);
        Assert.Equal(100.0, result.Breakdown[1].Damage, 2);
        Assert.Equal(100.0, result.Breakdown[2].Damage, 2);
        Assert.Equal(190.0, result.Breakdown[3].Damage, 2);
        Assert.Equal(50.0, result.Breakdown[4].Damage, 2);
        Assert.Equal(540.0, result.TotalDamage, 2);
        Assert.Equal(460.0, result.RemainingHP, 2);
        Assert.False(result.IsLethal);
    }

    [Fact]
    public void KillThresholdHP_IsGenuineInverseCalculation_NotAnEchoOfRemainingHP()
    {
        // Same fixed-damage scenario as BaseDamageAndMitigation test: mitigated
        // damage per run is a constant 80 regardless of defender.currentHP (no
        // HP-dependent execute types), so the true max-HP-that-dies is exactly 80.
        var attacker = new AttackerStat(Ad: 60, BonusAD: 40, Ap: 0, Level: 11, CriticalChance: 0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 500, MaxHP: 500, Armor: 50, Mr: 0, Shield: 0);
        var node = new ComboNode("Q", Damage: 20, RatioAD: 1.0, RatioAP: 0, DamageType.Physical);
        var engine = new DamageEngine();

        var result = engine.Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        // Not an echo: killThresholdHP (80) differs from both remainingHP (420) and
        // the original target's currentHP (500).
        Assert.Equal(80.0, result.KillThresholdHP, 1);
        Assert.NotEqual(result.RemainingHP, result.KillThresholdHP);
        Assert.NotEqual(defender.CurrentHP, result.KillThresholdHP);

        // Boundary proof: a defender with LOWER currentHP than the killThresholdHP
        // value actually dies to this same combo; one with higher HP survives it.
        var dies = engine.Calculate(new DamageCalcInput(new[] { node }, attacker,
            defender with { CurrentHP = 79 }));
        var survives = engine.Calculate(new DamageCalcInput(new[] { node }, attacker,
            defender with { CurrentHP = 81 }));

        Assert.True(dies.IsLethal);
        Assert.False(survives.IsLethal);
    }

    [Fact]
    public void Breakdown_ListsEveryNodeExactlyOnce_InOrder_IncludingAfterAnExecuteKill()
    {
        // N1 (Threshold) kills outright; N2/N3 must still appear in breakdown with 0
        // damage (completeness contract for M02's UI), in original order.
        var defender = new DefenderStat(CurrentHP: 10, MaxHP: 10, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            new ComboNode("N1", Damage: 10, RatioAD: 0, RatioAP: 0, DamageType.True,
                ExecuteType: ExecuteType.Threshold, ExecuteThreshold: 5),
            new ComboNode("N2", Damage: 15, RatioAD: 0, RatioAP: 0, DamageType.True),
            new ComboNode("N3", Damage: 25, RatioAD: 0, RatioAP: 0, DamageType.True),
        };

        var result = new DamageEngine().Calculate(new DamageCalcInput(nodes, NeutralAttacker, defender));

        Assert.Equal(3, result.Breakdown.Count);
        Assert.Equal("N1", result.Breakdown[0].NodeId);
        Assert.Equal("N2", result.Breakdown[1].NodeId);
        Assert.Equal("N3", result.Breakdown[2].NodeId);
        Assert.Equal(10.0, result.Breakdown[0].Damage, 2);
        Assert.Equal(0.0, result.Breakdown[1].Damage, 2);
        Assert.Equal(0.0, result.Breakdown[2].Damage, 2);
        Assert.True(result.IsLethal);
    }

    [Fact]
    public void ArmorBonus_ChangesResultCompared_ToBaseStatsOnly()
    {
        // Acceptance Criteria: base-stat-only armor vs base+item-bonus armor must
        // produce different (and correct) mitigation — proves item bonus isn't dropped.
        var attacker = new AttackerStat(Ad: 100, BonusAD: 0, Ap: 0, Level: 11, CriticalChance: 0, LifeSteal: 0);
        var node = new ComboNode("AA", Damage: 0, RatioAD: 1.0, RatioAP: 0, DamageType.Physical);
        var engine = new DamageEngine();

        // Base armor only: 30. mitigated = 100 * 100/130 = 76.923...
        var baseOnly = engine.Calculate(new DamageCalcInput(new[] { node }, attacker,
            new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 30, Mr: 0, Shield: 0)));
        // Base(30) + item bonus(70) = 100 armor. mitigated = 100 * 100/200 = 50.
        var withItems = engine.Calculate(new DamageCalcInput(new[] { node }, attacker,
            new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 100, Mr: 0, Shield: 0)));

        Assert.Equal(76.92, baseOnly.TotalDamage, 2);
        Assert.Equal(50.0, withItems.TotalDamage, 2);
        Assert.NotEqual(baseOnly.TotalDamage, withItems.TotalDamage);
    }

    [Fact]
    public void ArmorPenFlat_ReducesEffectiveArmor()
    {
        // armor 100, flat pen 30 -> R=70, mult = 100/170. raw = 100 -> 58.8235...
        var attacker = NeutralAttacker with { ArmorPenFlat = 30 };
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 100, Mr: 0, Shield: 0);
        var node = new ComboNode("N", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.Physical);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(58.82, result.TotalDamage, 2); // 100 * 100/170
    }

    [Fact]
    public void ArmorPenPercent_ReducesEffectiveArmor()
    {
        // armor 100, 30% pen -> R=70, mult = 100/170. raw = 100 -> 58.8235...
        var attacker = NeutralAttacker with { ArmorPenPercent = 0.30 };
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 100, Mr: 0, Shield: 0);
        var node = new ComboNode("N", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.Physical);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(58.82, result.TotalDamage, 2); // 100 * 100/170
    }

    [Fact]
    public void ArmorReductionFlat_CanDriveArmorNegative_AmplifiesDamage()
    {
        // base armor 20, flat reduction 50 -> R=-30. Amplified branch: mult = 2 - 100/130.
        // raw = 100 -> 123.0769...
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 20, Mr: 0, Shield: 0,
            ArmorReductionFlat: 50);
        var node = new ComboNode("N", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.Physical);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, NeutralAttacker, defender));

        Assert.Equal(123.08, result.TotalDamage, 2); // 100 * (2 - 100/130)
    }

    [Fact]
    public void FlatPen_CanDriveArmorNegative_AmplifiesDamage()
    {
        // armor 10, flat pen 50 -> R = 10-50 = -40 (loop 38 fix: flat pen is NOT clamped
        // at 0, unlike percent pen — matches live League lethality over-penetrating a
        // low-armor target). Amplified branch: mult = 2 - 100/140. raw = 100 -> 128.57...
        var attacker = NeutralAttacker with { ArmorPenFlat = 50 };
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 10, Mr: 0, Shield: 0);
        var node = new ComboNode("N", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.Physical);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(128.57, result.TotalDamage, 2); // 100 * (2 - 100/140)
    }

    [Fact]
    public void PercentPen_NeverDrivesArmorBelowZero()
    {
        // armor 10, 100% pen -> R = max(0, 10*(1-1.0)) = 0, mult = 100/100 = 1.0.
        // Percent pen (unlike flat pen) never amplifies past the R=0 floor.
        var attacker = NeutralAttacker with { ArmorPenPercent = 1.0 };
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 10, Mr: 0, Shield: 0);
        var node = new ComboNode("N", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.Physical);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(100.0, result.TotalDamage, 2);
    }

    [Fact]
    public void MagicPath_MirrorsArmor_ForPenetration()
    {
        // Mr 100, magic flat pen 30 -> R=70, mult = 100/170. raw = 100 -> 58.8235...
        var attacker = NeutralAttacker with { MagicPenFlat = 30 };
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 100, Shield: 0);
        var node = new ComboNode("N", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.Magic);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(58.82, result.TotalDamage, 2); // 100 * 100/170
    }

    [Fact]
    public void TrueDamage_IgnoresPenetrationAndReduction()
    {
        // True damage is unmitigated regardless of pen/reduction. raw = 100 -> 100.
        var attacker = NeutralAttacker with { ArmorPenFlat = 999, ArmorPenPercent = 0.9, MagicPenFlat = 999 };
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 100, Mr: 100, Shield: 0,
            ArmorReductionFlat: 200, MrReductionFlat: 200);
        var node = new ComboNode("N", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.True);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(100.0, result.TotalDamage, 2);
    }

    [Fact]
    public void CritRange_ZeroCritChance_MinEqualsMaxEqualsBlend_RegressionSafe()
    {
        // M05 v2.8: with CriticalChance=0, a CanCrit node must never diverge — Min/Max both
        // collapse to the same value the pre-existing blend formula already produces.
        var attacker = new AttackerStat(Ad: 100, BonusAD: 0, Ap: 0, Level: 11, CriticalChance: 0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("AA", Damage: 0, RatioAD: 1.0, RatioAP: 0, DamageType.True, CanCrit: true);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(100.0, result.TotalDamage, 2);
        Assert.Equal(100.0, result.TotalDamageMin, 2);
        Assert.Equal(100.0, result.TotalDamageMax, 2);
        Assert.Equal(result.RemainingHP, result.RemainingHPMin, 2);
        Assert.Equal(result.RemainingHP, result.RemainingHPMax, 2);
    }

    [Fact]
    public void CritRange_HundredPercentCritChance_MinIsNoCrit_MaxIsGuaranteedCrit()
    {
        // 100% crit chance, CritDamageMultiplier defaults to DamageEngine's own 1.75 constant
        // (AttackerStat.CritDamageMultiplier left at 0 = "not reported by the API").
        // raw = 100 (true, no mitigation). Min: no crit -> 100. Max: guaranteed crit -> 175.
        var attacker = new AttackerStat(Ad: 100, BonusAD: 0, Ap: 0, Level: 11, CriticalChance: 1.0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("AA", Damage: 0, RatioAD: 1.0, RatioAP: 0, DamageType.True, CanCrit: true);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(100.0, result.TotalDamageMin, 2);
        Assert.Equal(175.0, result.TotalDamageMax, 2);
        Assert.Equal(900.0, result.RemainingHPMin, 2);
        Assert.Equal(825.0, result.RemainingHPMax, 2);
        // Blend track (expected value) is unchanged/independent from Min/Max.
        Assert.Equal(175.0, result.TotalDamage, 2); // 100 * (1 + 1.0*(1.75-1)) = 175
    }

    [Fact]
    public void CritRange_ParallelRemainingHpThreading_CritOnEarlierNodeShiftsLaterMissingHpNode()
    {
        // Mirrors SequentialCombo_MissingHpNode_... but with 100% crit on the FIRST node
        // (Q, CanCrit) so Min and Max diverge in remainingHp BEFORE the MissingHp node (R)
        // reads it — proving the two remainingHp accumulators are threaded independently
        // and in parallel, not sharing a single running value.
        var attacker = new AttackerStat(Ad: 0, BonusAD: 0, Ap: 0, Level: 1, CriticalChance: 1.0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            // Q: flat 100 true damage, CanCrit -> Min stays 100, Max becomes 100*1.75=175.
            new ComboNode("Q", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.True, CanCrit: true),
            // R: base 100 true + 30% of missing HP at cast time (no crit on this node itself).
            new ComboNode("R", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.True,
                ExecuteType: ExecuteType.MissingHp, ExecutePercent: 0.3),
        };

        var result = new DamageEngine().Calculate(new DamageCalcInput(nodes, attacker, defender));

        // Min track: Q=100 -> remainingHpMin=900. R: missing=1000-900=100; bonus=0.3*100=30;
        // raw=130 -> remainingHpMin = 900-130 = 770.
        // Max track: Q=175 -> remainingHpMax=825. R: missing=1000-825=175; bonus=0.3*175=52.5;
        // raw=152.5 -> remainingHpMax = 825-152.5 = 672.5.
        Assert.Equal(100.0 + 130.0, result.TotalDamageMin, 2); // 230
        Assert.Equal(175.0 + 152.5, result.TotalDamageMax, 2); // 327.5
        Assert.Equal(770.0, result.RemainingHPMin, 2);
        Assert.Equal(672.5, result.RemainingHPMax, 2);
        Assert.True(result.TotalDamageMax > result.TotalDamageMin);
        Assert.True(result.RemainingHPMax < result.RemainingHPMin);
    }

    [Fact]
    public void CritRange_RealCritDamageMultiplierFromApi_OverridesFallbackConstant()
    {
        // When AttackerStat.CritDamageMultiplier is a real reported value (e.g. 2.0 from an
        // Infinity Edge build), the Max track must use it instead of the 1.75 fallback constant.
        var attacker = new AttackerStat(Ad: 100, BonusAD: 0, Ap: 0, Level: 11, CriticalChance: 1.0,
            LifeSteal: 0, CritDamageMultiplier: 2.0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("AA", Damage: 0, RatioAD: 1.0, RatioAP: 0, DamageType.True, CanCrit: true);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(200.0, result.TotalDamageMax, 2); // 100 * 2.0, not 100 * 1.75
    }

    // ── M27: per-hit expected-crit scalar (docs/modules/M27_ABILITY_EXPECTED_CRIT.md) ──────────

    [Fact]
    public void CritScalar_DefaultOne_IsByteIdenticalToPreExistingAaFormula_ZeroRegression()
    {
        // Explicit CritDamageScalar: 1.0 (the default every pre-existing AA node carries, whether
        // set explicitly or not) must reproduce the exact pre-M27 numbers: Min=100 (no crit assumed),
        // Max=175 (100*1.75, guaranteed crit at the fallback constant), Blend=175 (CriticalChance=1.0
        // collapses Blend to the same 1+1.0*(1.75-1) term as Max here) — see
        // CritRange_HundredPercentCritChance_MinIsNoCrit_MaxIsGuaranteedCrit, unchanged.
        var attacker = new AttackerStat(Ad: 100, BonusAD: 0, Ap: 0, Level: 11, CriticalChance: 1.0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("AA", Damage: 0, RatioAD: 1.0, RatioAP: 0, DamageType.True,
            CanCrit: true, CritDamageScalar: 1.0);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        Assert.Equal(100.0, result.TotalDamageMin, 2);
        Assert.Equal(175.0, result.TotalDamageMax, 2);
        Assert.Equal(175.0, result.TotalDamage, 2);
    }

    [Fact]
    public void CritScalar_PartialScalar040_ScalesBlendAndMaxBonus_MinNeverAffected()
    {
        // s=0.40 (Garen E's curated value), 50% crit chance, no IE (CritDamageMultiplier unreported
        // -> falls back to the 1.75 constant). raw = 100 true, no mitigation.
        var attacker = new AttackerStat(Ad: 100, BonusAD: 0, Ap: 0, Level: 11, CriticalChance: 0.5, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("E", Damage: 0, RatioAD: 1.0, RatioAP: 0, DamageType.True,
            CanCrit: true, CritDamageScalar: 0.40);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        // Min: P2-safe floor — the scalar/crit-chance widen Blend/Max only, never the displayed floor.
        Assert.Equal(100.0, result.TotalDamageMin, 2);
        // Blend = 100 * (1 + 0.40*0.5*0.75) = 100 * 1.15 = 115 (crit-chance-weighted average).
        Assert.Equal(115.0, result.TotalDamage, 2);
        // Max = 100 * (1 + 0.40*(1.75-1)) = 100 * 1.30 = 130 (guaranteed crit assumed regardless of
        // the real crit CHANCE — only the crit-damage BONUS is scaled by s).
        Assert.Equal(130.0, result.TotalDamageMax, 2);
    }

    [Fact]
    public void CritScalar_PartialScalar040_WithRealCritDamageMultiplier_AmplifiesMaxLikeAaIe()
    {
        // s=0.40 with a real reported CritDamageMultiplier (e.g. Infinity Edge, 2.0) — M27 §3.3: no
        // new item logic needed, the scalar multiplies the SAME IE-raised bonus term Max already uses.
        var attacker = new AttackerStat(Ad: 100, BonusAD: 0, Ap: 0, Level: 11, CriticalChance: 1.0,
            LifeSteal: 0, CritDamageMultiplier: 2.0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("E", Damage: 0, RatioAD: 1.0, RatioAP: 0, DamageType.True,
            CanCrit: true, CritDamageScalar: 0.40);

        var result = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));

        // Max = 100 * (1 + 0.40*(2.0-1)) = 100 * 1.40 = 140, NOT the un-amplified 1.75-based 130.
        Assert.Equal(140.0, result.TotalDamageMax, 2);
        Assert.Equal(100.0, result.TotalDamageMin, 2);
        // Blend stays on the 1.75 constant (M27 §7, pre-existing asymmetry, unchanged by this
        // feature): 100 * (1 + 0.40*1.0*0.75) = 130, independent of the real CritDamageMultiplier.
        Assert.Equal(130.0, result.TotalDamage, 2);
    }

    [Fact]
    public void Lifesteal_IsSeparateField_AndDoesNotAffectDamageTotal()
    {
        var attacker = new AttackerStat(Ad: 100, BonusAD: 0, Ap: 0, Level: 11, CriticalChance: 0, LifeSteal: 0.2);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var node = new ComboNode("AA", Damage: 0, RatioAD: 1.0, RatioAP: 0, DamageType.Physical);

        var withLifesteal = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));
        var withoutLifesteal = new DamageEngine().Calculate(new DamageCalcInput(new[] { node },
            attacker with { LifeSteal = 0 }, defender));

        Assert.Equal(20.0, withLifesteal.LifestealHeal, 2); // 0.2 * 100
        Assert.Equal(withoutLifesteal.TotalDamage, withLifesteal.TotalDamage); // unaffected
        Assert.Equal(withoutLifesteal.RemainingHP, withLifesteal.RemainingHP); // unaffected
    }
}
