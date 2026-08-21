using Overlay.Core.Damage;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M20's closed-form linear fold (docs/modules/M20_COMBO_DAMAGE_FORMULA.md
/// §5-§7): <see cref="DamageEngine.Fold"/>, <see cref="DamageEngine.SolveKillThresholdHpFold"/>,
/// <see cref="DamageEngine.SolveSuffixThresholdHp"/>, and <see cref="DamageEngine.OrderingDeltaKillThresholdHp"/>.
///
/// All 7 vectors below are M20 §10's own "machine-checked" test contract, transcribed
/// verbatim (hand-derived independently against §5.1's update table before being
/// written here — see Agent Report for the derivation trace). 4-decimal precision is
/// used (matching §10's own stated precision) rather than 3, since some vectors'
/// unrounded values sit close enough to a 3-decimal rounding boundary that comparing
/// two independently-rounded 3-decimal numbers can disagree even though the underlying
/// values agree to well under 0.0001 HP (e.g. vector 1: fold value 790.376458...,
/// naive 3-decimal rounding of the two sides can disagree at the boundary; 4-decimal
/// rounding does not).
/// </summary>
public class DamageEngineFoldTests
{
    private static readonly AttackerStat NeutralAttacker =
        new(Ad: 0, BonusAD: 0, Ap: 0, Level: 1, CriticalChance: 0, LifeSteal: 0);

    // M20 §8 worked example's shared stats: M=2000, armor 100 -> k_phys=0.5, MR 60 -> k_mag=0.625.
    private static readonly DefenderStat GarenShapedDefender =
        new(CurrentHP: 2000, MaxHP: 2000, Armor: 100, Mr: 60, Shield: 0);

    private static ComboNode Q() => new("Q", Damage: 300, RatioAD: 0, RatioAP: 0, DamageType.Physical);
    private static ComboNode W() => new("W", Damage: 200, RatioAD: 0, RatioAP: 0, DamageType.Magic);
    private static ComboNode R() => new("R", Damage: 400, RatioAD: 0, RatioAP: 0, DamageType.Magic,
        ExecuteType: ExecuteType.MissingHp, ExecutePercent: 0.286);
    private static ComboNode A() => new("A", Damage: 250, RatioAD: 0, RatioAP: 0, DamageType.Physical);

    [Fact]
    public void Vector1_QWR_MatchesM20Section10()
    {
        var nodes = new[] { Q(), W(), R() };

        var result = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, GarenShapedDefender);

        Assert.NotNull(result);
        Assert.Equal(790.3765, result!.Value, 4);
    }

    [Fact]
    public void Vector2_QWRA_MatchesM20Section10()
    {
        var nodes = new[] { Q(), W(), R(), A() };

        var result = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, GarenShapedDefender);

        Assert.NotNull(result);
        Assert.Equal(896.4210, result!.Value, 4);
    }

    [Fact]
    public void Vector3_QWAR_MatchesM20Section10_AndExceedsQWRA()
    {
        // Auto moved before R -> higher kill threshold than QWRA (§6.1's ordering rule).
        var nodes = new[] { Q(), W(), A(), R() };

        var result = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, GarenShapedDefender);

        Assert.NotNull(result);
        Assert.Equal(915.3765, result!.Value, 4);
        Assert.True(result.Value > 896.4210);
    }

    [Fact]
    public void Vector4_SuffixRA_OfQWRA_MatchesM20Section10_AndSatisfiesIdentity()
    {
        // Suffix [R, A] of QWRA, starting at R (index 2).
        var nodes = new[] { Q(), W(), R(), A() };

        var suffix = DamageEngine.SolveSuffixThresholdHp(
            nodes, startIndex: 2, NeutralAttacker, GarenShapedDefender, DamageEngine.CritTrack.Blend);

        Assert.NotNull(suffix);
        Assert.Equal(621.4210, suffix!.Value, 4);

        // Identity (M20 §6.3/§8): H*_full(QWRA) - preRFlatDamage(Q+W mitigated) = suffix threshold.
        // preRFlatDamage = 0.5*300 (Q) + 0.625*200 (W) = 150 + 125 = 275.
        var full = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, GarenShapedDefender);
        Assert.NotNull(full);
        Assert.Equal(suffix.Value, full!.Value - 275.0, 4);
    }

    [Fact]
    public void Vector5_UnmitigatedSingleR_MatchesClosedFormFromSection5Point2()
    {
        // M20 §5.2/§10 vector 5: M=2000, flat A=500 before, MissingHp B=300 r=0.3, flat C=200
        // after, all unmitigated (True damage, k=1). H* = A + (B+C+rM)/(1+r) = 1346.1538.
        var defender = new DefenderStat(CurrentHP: 2000, MaxHP: 2000, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            new ComboNode("A", Damage: 500, RatioAD: 0, RatioAP: 0, DamageType.True),
            new ComboNode("R", Damage: 300, RatioAD: 0, RatioAP: 0, DamageType.True,
                ExecuteType: ExecuteType.MissingHp, ExecutePercent: 0.3),
            new ComboNode("C", Damage: 200, RatioAD: 0, RatioAP: 0, DamageType.True),
        };

        var result = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, defender);

        Assert.NotNull(result);
        Assert.Equal(1346.1538, result!.Value, 4);
    }

    [Fact]
    public void Vector6_CurrentHpOnly_MatchesM20Section10()
    {
        // M20 §10 vector 6: flat400 -> CurrentHp s=0.25 -> flat300, k=1 (True). H* = 800.0.
        var defender = new DefenderStat(CurrentHP: 2000, MaxHP: 2000, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            new ComboNode("F1", Damage: 400, RatioAD: 0, RatioAP: 0, DamageType.True),
            new ComboNode("Tick", Damage: 0, RatioAD: 0, RatioAP: 0, DamageType.True,
                ExecuteType: ExecuteType.CurrentHp, ExecutePercent: 0.25),
            new ComboNode("F2", Damage: 300, RatioAD: 0, RatioAP: 0, DamageType.True),
        };

        var result = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, defender);

        Assert.NotNull(result);
        Assert.Equal(800.0000, result!.Value, 4);
    }

    [Fact]
    public void Vector7_KrakenShaped_MatchesM20Section10()
    {
        // M20 §10 vector 7: flat600, BaseWithMissingHpBonus B=150 p=0.75, flat200, uniform k=0.55.
        // M is not stated explicitly in §10's vector text; M=2000 (the only value consistent with
        // the stated H*=576.7414, and matching §8's own M=2000 convention) is used here -
        // independently confirmed by hand: solving the fold with M=2000 reproduces 576.7414
        // to 4 decimals exactly (see Agent Report derivation trace).
        // Armor tuned so EffectiveResistMultiplier(Armor,0,0,0,0) == 0.55 exactly:
        // 100/(100+R) = 0.55 => R = 900/11.
        var defender = new DefenderStat(CurrentHP: 2000, MaxHP: 2000, Armor: 900.0 / 11.0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            new ComboNode("F1", Damage: 600, RatioAD: 0, RatioAP: 0, DamageType.Physical),
            new ComboNode("Kraken", Damage: 150, RatioAD: 0, RatioAP: 0, DamageType.Physical,
                ExecuteType: ExecuteType.BaseWithMissingHpBonus, ExecutePercent: 0.75),
            new ComboNode("F2", Damage: 200, RatioAD: 0, RatioAP: 0, DamageType.Physical),
        };

        var result = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, defender);

        Assert.NotNull(result);
        Assert.Equal(576.7414, result!.Value, 4);
    }

    [Fact]
    public void OracleAgreement_FoldAndBisection_AgreeOnNonThresholdZeroShieldCombos()
    {
        // §10's required "oracle agreement" test: for combos with no Threshold node and
        // zero shield, the O(n) fold and the general-case bisection must agree (within
        // bisection's own convergence tolerance, well under 0.01 HP per its 60 iterations).
        var combos = new[]
        {
            new[] { Q(), W(), R() },
            new[] { Q(), W(), R(), A() },
            new[] { Q(), W(), A(), R() },
        };

        foreach (var nodes in combos)
        {
            var foldResult = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, GarenShapedDefender);
            var bisectionResult = DamageEngine.SolveKillThresholdHp(nodes, NeutralAttacker, GarenShapedDefender);

            Assert.NotNull(foldResult);
            Assert.Equal(bisectionResult, foldResult!.Value, 2);
        }
    }

    [Fact]
    public void OrderingProperty_MovingFlatNodeBeforeMissingHpNode_NeverDecreasesKillThreshold()
    {
        // §10's required ordering-property test, using the §6.1 helper directly: move A
        // (flat, index 3) in QWRA to before R (index 2) and confirm H* never decreases.
        var nodes = new[] { Q(), W(), R(), A() };

        var delta = DamageEngine.OrderingDeltaKillThresholdHp(
            nodes, fromIndex: 3, toIndex: 2, NeutralAttacker, GarenShapedDefender, DamageEngine.CritTrack.Blend);

        Assert.NotNull(delta);
        Assert.True(delta!.Value >= 0);
        // Matches vectors 2/3 directly: QWAR - QWRA = 915.3765 - 896.4210 = 18.9555.
        Assert.Equal(915.3765 - 896.4210, delta.Value, 3);
    }

    [Fact]
    public void Degenerate_EmptyCombo_KillThresholdIsZero()
    {
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);

        var result = DamageEngine.SolveKillThresholdHpFold(Array.Empty<ComboNode>(), NeutralAttacker, defender);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.Value, 4);
    }

    [Fact]
    public void Degenerate_CurrentHpOnlyCombo_BIsZero_KillThresholdIsZero()
    {
        // %-only damage can never kill from full precision (H asymptotes to 0, never
        // reaches it) - b stays 0 through the whole fold, so H* = 0/a = 0.
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            new ComboNode("Tick1", Damage: 0, RatioAD: 0, RatioAP: 0, DamageType.True,
                ExecuteType: ExecuteType.CurrentHp, ExecutePercent: 0.5),
            new ComboNode("Tick2", Damage: 0, RatioAD: 0, RatioAP: 0, DamageType.True,
                ExecuteType: ExecuteType.CurrentHp, ExecutePercent: 0.3),
        };

        var result = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, defender);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.Value, 4);
    }

    [Fact]
    public void Degenerate_ZeroA_DoesNotDivideByZero_ReturnsZero()
    {
        // A single CurrentHp node with s=1 (k=1, True damage) drives a to exactly 0
        // ((1 - k*s) = 0) — SolveKillThresholdHpFold must guard this, not return NaN/Infinity.
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            new ComboNode("AllHp", Damage: 0, RatioAD: 0, RatioAP: 0, DamageType.True,
                ExecuteType: ExecuteType.CurrentHp, ExecutePercent: 1.0),
        };

        var result = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, defender);

        Assert.NotNull(result);
        Assert.False(double.IsNaN(result!.Value));
        Assert.False(double.IsInfinity(result.Value));
        Assert.Equal(0.0, result.Value, 4);
    }

    [Fact]
    public void Fold_ReturnsNull_WhenThresholdNodePresent_FallsBackToBisectionViaCalculate()
    {
        // §7: a Threshold node breaks the affine assumption. Fold must decline (null),
        // and Calculate() (wired to prefer the fold) must still produce the correct
        // bisection-derived killThresholdHP transparently.
        var defender = new DefenderStat(CurrentHP: 45, MaxHP: 100, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[]
        {
            new ComboNode("R", Damage: 10, RatioAD: 0, RatioAP: 0, DamageType.True,
                ExecuteType: ExecuteType.Threshold, ExecuteThreshold: 50),
        };

        var foldResult = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, defender);
        Assert.Null(foldResult);

        var calcResult = new DamageEngine().Calculate(new DamageCalcInput(nodes, NeutralAttacker, defender));
        Assert.True(calcResult.IsLethal); // bisection fallback still finds the correct answer
    }

    [Fact]
    public void Fold_ReturnsNull_WhenShieldNonzero()
    {
        // §7: shield partially absorbing breaks the affine assumption; this implementation's
        // documented choice is to decline the fold whenever ANY shield is present (see
        // Fold's doc comment for why depletion-timing can itself be H0-dependent).
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 50);
        var nodes = new[] { new ComboNode("N", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.True) };

        var result = DamageEngine.SolveKillThresholdHpFold(nodes, NeutralAttacker, defender);

        Assert.Null(result);
    }
}
