using Overlay.Core.Damage;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M20 §6.2 (kill-feasible maximum for a variance-heavy node) —
/// <see cref="DamageEngine.SolveKillFeasibleMaxDamage"/>. The combo card's headline is whatever
/// <c>Simulate</c> produced for the AUTHORED node order; for an HP-basis node that understates the
/// realizable burst by a large factor (Garen R rank 1 spans 125→375 on a 1000-HP target purely by
/// placement), so the engine also reports the best placement at which the target is STILL ALIVE.
///
/// <para>The O(n²) search is cross-checked against an independent closed-form oracle
/// (<see cref="ClosedFormMissingHp"/>) in the same fold↔bisection spirit
/// <c>DamageEngineFoldTests</c> already uses. The oracle lives in this file rather than in the
/// engine on purpose: unlike <c>Fold</c> (a genuine production fast path) it would have no
/// production caller, and M20 §7's piecewise-affine breakers mean it cannot be used whenever the
/// combo carries a <c>Threshold</c> or <c>HpBelowGateFraction</c> node anyway
/// (CLAUDE_CODE_TODO §75-5) — so it exists purely as a second derivation to disagree with.</para>
///
/// <para>All vectors use TRUE damage and zero crit chance unless a test says otherwise, so the
/// mitigation multiplier is 1.0 and the arithmetic in each test's comments is checkable by hand.</para>
/// </summary>
public class DamageEnginePlacementTests
{
    private static readonly AttackerStat NeutralAttacker =
        new(Ad: 0, BonusAD: 0, Ap: 0, Level: 1, CriticalChance: 0, LifeSteal: 0);

    private static ComboNode Flat(string id, double damage) =>
        new(id, Damage: damage, RatioAD: 0, RatioAP: 0, DamageType.True);

    /// <summary>Garen R's curated shape: TWO hits in ONE cast — the missing-HP hit resolves FIRST
    /// (so it never reads its own sibling's flat damage as freshly-missing health, <c>Garen.json</c>
    /// <c>_noteR2</c>), then the flat base.</summary>
    private static ComboNode[] GarenR(double executePercent, double flatBase) =>
    [
        new("R#h0", Damage: 0, RatioAD: 0, RatioAP: 0, DamageType.True,
            ExecuteType: ExecuteType.MissingHp, ExecutePercent: executePercent),
        new("R#h1", Damage: flatBase, RatioAD: 0, RatioAP: 0, DamageType.True),
    ];

    // ── the closed-form oracle (M20 §6.2) ────────────────────────────────────────────────
    //
    // R(j) = B + r·((M − H0) + P_j), strictly increasing in P_j
    // alive: P_j < H0 + Shield        (TrackState depletes shield before HP)
    // j*    = max { j : P_j < H0 + Shield }
    //
    // Pure arithmetic over post-mitigation per-group damage — never calls the engine, so an
    // agreement assertion is a genuine second opinion. Valid only for the MissingHp shape with all
    // other groups flat and no Threshold/gate node (M20 §7).

    private static (int JStar, double Total) ClosedFormMissingHp(
        double h0, double maxHp, double shield,
        double[] otherGroupDamage, double varianceRatio, double varianceFlat)
    {
        int n = otherGroupDamage.Length;
        var p = new double[n + 1];
        for (int j = 1; j <= n; j++) p[j] = p[j - 1] + otherGroupDamage[j - 1];

        double pool = h0 + shield;
        int jStar = 0;
        for (int j = 0; j <= n; j++) if (p[j] < pool) jStar = j;

        // Missing HP when the variance hit lands: the shield soaks the first `shield` points, so
        // only the excess has actually come off HP.
        double missing = (maxHp - h0) + Math.Max(0, p[jStar] - shield);

        double total = p[jStar] + varianceRatio * missing;   // the missing-HP hit
        if (total < pool) total += varianceFlat;             // its flat sibling, if the target lived
        for (int j = jStar; j < n && total < pool; j++) total += otherGroupDamage[j]; // the suffix

        return (jStar, total);
    }

    // ── §75 test contract ────────────────────────────────────────────────────────────────

    /// <summary>
    /// §75 test 1 — Garen QERA. R rank 1 (25% missing HP + 125 flat) on a full-HP 1000 target that
    /// the combo does NOT kill. Authored QERA puts R third: prefix 200 → 0.25·200 = 50, +125 → 175,
    /// total 100+100+175+100 = 475. Optimal pushes R last: prefix 300 → 75, +125 → 200, total 500.
    /// j* = max{j : P_j &lt; 1000} = 3 (P₃ = 300), i.e. the last placement — matching §6.2's
    /// "latest placement at which the target is still alive".
    /// </summary>
    [Fact]
    public void GarenQERA_AuthoredOrderUnderstatesCeiling_AndOptimumIsJStar()
    {
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var r = GarenR(executePercent: 0.25, flatBase: 125);
        var nodes = new[] { Flat("Q", 100), Flat("E", 100), r[0], r[1], Flat("A", 100) };
        var groups = new[] { 0, 1, 2, 2, 3 }; // Q | E | R(two hits, one cast) | A

        var result = DamageEngine.SolveKillFeasibleMaxDamage(nodes, groups, NeutralAttacker, defender);

        Assert.Equal(DamageEngine.PlacementStatus.Optimized, result.Status);
        Assert.Equal(475, result.AuthoredTotalDamage, 4);
        Assert.Equal(500, result.MaxTotalDamage, 4);
        Assert.True(result.AuthoredTotalDamage < result.MaxTotalDamage);

        Assert.Equal(2, result.AuthoredGroupIndex);
        Assert.Equal(3, result.OptimalGroupIndex);

        var (jStar, total) = ClosedFormMissingHp(
            h0: 1000, maxHp: 1000, shield: 0,
            otherGroupDamage: [100, 100, 100], varianceRatio: 0.25, varianceFlat: 125);
        Assert.Equal(jStar, result.OptimalGroupIndex);
        Assert.Equal(total, result.MaxTotalDamage, 4);
    }

    /// <summary>
    /// The cast group is the movable unit, not the node — and it is load-bearing. Moving ONLY Garen
    /// R's missing-HP hit to the end would leave its own flat sibling (125) sitting in the prefix,
    /// so the hit would read that 125 as freshly-missing health: exactly the 125→156.25 inflation
    /// <c>Garen.json</c>'s <c>_noteR2</c> hit ordering exists to prevent. Same nodes, same
    /// placement index, only the grouping differs — and the ungrouped answer is measurably wrong.
    /// </summary>
    [Fact]
    public void CastGroupMovesAsAUnit_SplittingItWouldLetTheMissingHpHitReadItsOwnSibling()
    {
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var r = GarenR(executePercent: 0.25, flatBase: 125);
        var nodes = new[] { Flat("Q", 100), Flat("E", 100), r[0], r[1], Flat("A", 100) };

        var grouped = DamageEngine.SolveKillFeasibleMaxDamage(
            nodes, [0, 1, 2, 2, 3], NeutralAttacker, defender);
        // Same sequence, but every node its own group: the search is now free to move the
        // missing-HP hit alone.
        var split = DamageEngine.SolveKillFeasibleMaxDamage(
            nodes, null, NeutralAttacker, defender);

        // Grouped: R last reads a 300 prefix → 0.25·300 + 125 = 200, total 500.
        Assert.Equal(500, grouped.MaxTotalDamage, 4);
        // Split: the missing-HP hit alone goes last and reads 100+100+125+100 = 425 → 106.25,
        // total 100+100+125+100+106.25 = 531.25. Higher, and physically impossible — the two hits
        // land together.
        Assert.Equal(531.25, split.MaxTotalDamage, 4);
        Assert.True(split.MaxTotalDamage > grouped.MaxTotalDamage);
    }

    /// <summary>
    /// §75 test 2 — a <see cref="ExecuteType.CurrentHp"/> node (Dr. Mundo Q shape) optimizes in the
    /// OPPOSITE direction: its damage falls as the target drops, so the earliest placement is the
    /// maximum (M20 §6.2's shape table). Nothing in the engine is told this — it falls out of
    /// re-running the real pipeline per placement.
    /// 20% current HP on a 1000-HP target: authored last reads 1000−400 = 600 → 120 (total 520);
    /// placed first it reads the full 1000 → 200 (total 600).
    /// </summary>
    [Fact]
    public void CurrentHpNode_OptimizesEarliest_OppositeOfMissingHp()
    {
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var q = new ComboNode("Q", Damage: 0, RatioAD: 0, RatioAP: 0, DamageType.True,
            ExecuteType: ExecuteType.CurrentHp, ExecutePercent: 0.20);
        var nodes = new[] { Flat("A1", 200), Flat("A2", 200), q };

        var result = DamageEngine.SolveKillFeasibleMaxDamage(nodes, null, NeutralAttacker, defender);

        Assert.Equal(DamageEngine.PlacementStatus.Optimized, result.Status);
        Assert.Equal(520, result.AuthoredTotalDamage, 4);
        Assert.Equal(600, result.MaxTotalDamage, 4);
        Assert.Equal(2, result.AuthoredGroupIndex);
        Assert.Equal(0, result.OptimalGroupIndex); // earliest, not latest
    }

    /// <summary>
    /// §75 test 3 — the degenerate case M20 §6.2 insists on reporting rather than hiding: the combo
    /// already kills WITHOUT the variance node (600+600 against 1000 HP), so its ceiling is not a
    /// requirement. The status says so; no fabricated ceiling is presented as one.
    /// </summary>
    [Fact]
    public void CombosThatKillWithoutTheVarianceNode_ReportVarianceUnnecessary()
    {
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var r = GarenR(executePercent: 0.25, flatBase: 125);
        var nodes = new[] { Flat("Q", 600), Flat("E", 600), r[0], r[1] };

        var result = DamageEngine.SolveKillFeasibleMaxDamage(
            nodes, [0, 1, 2, 2], NeutralAttacker, defender);

        Assert.Equal(DamageEngine.PlacementStatus.VarianceUnnecessary, result.Status);
    }

    /// <summary>
    /// §75 test 4 — the O(n²) search agrees with the closed form across a sweep of combos with no
    /// Threshold and no gate node (the only regime in which §6.2's affine derivation holds — M20 §7).
    /// Both the chosen placement <c>j*</c> and the resulting total must match; mitigation is folded
    /// into the oracle's per-group numbers by using TRUE damage on both sides.
    /// </summary>
    [Theory]
    [InlineData(1000, 1000, 0)]   // full HP, survives
    [InlineData(1000, 1000, 300)] // shielded, survives
    [InlineData(400, 1000, 0)]    // already damaged, R lands lethal
    [InlineData(300, 1000, 500)]  // shield is most of the effective pool
    [InlineData(150, 1000, 0)]    // dies partway — j* is NOT the last placement
    public void OracleAgreement_SearchMatchesClosedForm(double currentHp, double maxHp, double shield)
    {
        var defender = new DefenderStat(currentHp, maxHp, Armor: 0, Mr: 0, Shield: shield);
        var r = GarenR(executePercent: 0.25, flatBase: 125);
        // Authored order deliberately puts R first, so the search has to move it to find the max.
        var nodes = new[] { r[0], r[1], Flat("A", 120), Flat("B", 120), Flat("C", 120) };

        var result = DamageEngine.SolveKillFeasibleMaxDamage(
            nodes, [0, 0, 1, 2, 3], NeutralAttacker, defender);

        var (jStar, total) = ClosedFormMissingHp(
            currentHp, maxHp, shield,
            otherGroupDamage: [120, 120, 120], varianceRatio: 0.25, varianceFlat: 125);

        Assert.Equal(jStar, result.OptimalGroupIndex);
        Assert.Equal(total, result.MaxTotalDamage, 4);
    }

    /// <summary>
    /// §75 test 5 — the alive constraint is <c>P_j &lt; H0 + Shield</c>, not <c>P_j &lt; H0</c>:
    /// <see cref="DamageEngine"/>'s TrackState depletes the shield before HP, so a shielded target
    /// stays reorderable-into for longer. Same 300-HP target, same three 250-damage groups: with a
    /// 500 shield every placement is alive (j* = 3), without it the target is already dead by the
    /// second (j* = 1). A constraint written against H0 alone would return 1 in both rows.
    /// </summary>
    [Fact]
    public void AliveConstraintIncludesShield()
    {
        var r = GarenR(executePercent: 0.25, flatBase: 0);
        var nodes = new[] { r[0], r[1], Flat("A", 250), Flat("B", 250), Flat("C", 250) };
        int[] groups = [0, 0, 1, 2, 3];

        var shielded = DamageEngine.SolveKillFeasibleMaxDamage(
            nodes, groups, NeutralAttacker,
            new DefenderStat(CurrentHP: 300, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 500));
        var bare = DamageEngine.SolveKillFeasibleMaxDamage(
            nodes, groups, NeutralAttacker,
            new DefenderStat(CurrentHP: 300, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0));

        Assert.Equal(3, shielded.OptimalGroupIndex); // 750 < 300+500 → still alive at the last slot
        Assert.Equal(1, bare.OptimalGroupIndex);     // 500 ≥ 300 → dead from the second slot on

        // And both agree with the closed form's own j*.
        Assert.Equal(
            ClosedFormMissingHp(300, 1000, 500, [250, 250, 250], 0.25, 0).JStar,
            shielded.OptimalGroupIndex);
        Assert.Equal(
            ClosedFormMissingHp(300, 1000, 0, [250, 250, 250], 0.25, 0).JStar,
            bare.OptimalGroupIndex);
    }

    /// <summary>
    /// CLAUDE_CODE_TODO §75-4: two independently movable variance nodes are NOT silently
    /// approximated. §6.2's closed form is derived for one moved node, so the search is skipped,
    /// the authored total stands, and the status says why.
    /// </summary>
    [Fact]
    public void TwoVarianceNodes_NoOptimizationAttempted_AuthoredTotalStands()
    {
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var r1 = new ComboNode("R1", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.True,
            ExecuteType: ExecuteType.MissingHp, ExecutePercent: 0.25);
        var r2 = new ComboNode("R2", Damage: 100, RatioAD: 0, RatioAP: 0, DamageType.True,
            ExecuteType: ExecuteType.MissingHp, ExecutePercent: 0.25);
        var nodes = new[] { Flat("Q", 100), r1, Flat("A", 100), r2 };

        var result = DamageEngine.SolveKillFeasibleMaxDamage(nodes, null, NeutralAttacker, defender);

        Assert.Equal(DamageEngine.PlacementStatus.MultipleVarianceGroups, result.Status);
        Assert.Equal(result.AuthoredTotalDamage, result.MaxTotalDamage, 4);
    }

    /// <summary>A combo with nothing HP-dependent has one honest answer and no ceiling to add.</summary>
    [Fact]
    public void NoVarianceNode_ReportsNothingToOptimize()
    {
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var nodes = new[] { Flat("Q", 100), Flat("W", 100) };

        var result = DamageEngine.SolveKillFeasibleMaxDamage(nodes, null, NeutralAttacker, defender);

        Assert.Equal(DamageEngine.PlacementStatus.NoVarianceNode, result.Status);
        Assert.Equal(200, result.AuthoredTotalDamage, 4);
        Assert.Equal(-1, result.AuthoredGroupIndex);
    }

    /// <summary>
    /// A gate node (§27's <see cref="ComboNode.HpBelowGateFraction"/>, Ekko W shape) is a
    /// CONSTRAINT, never the thing being moved (CLAUDE_CODE_TODO §75-5) — the search still runs,
    /// picks the missing-HP node as the movable group, and the gate simply re-evaluates against
    /// whatever running HP each candidate placement produces. The closed-form oracle is skipped
    /// here (piecewise-affine, M20 §7); the search result is used on its own.
    /// </summary>
    [Fact]
    public void GateNodeIsAConstraintNotTheMovableGroup()
    {
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var gated = new ComboNode("W", Damage: 150, RatioAD: 0, RatioAP: 0, DamageType.True,
            HpBelowGateFraction: 0.60);
        var r = GarenR(executePercent: 0.25, flatBase: 125);
        var nodes = new[] { Flat("Q", 300), r[0], r[1], gated };

        var result = DamageEngine.SolveKillFeasibleMaxDamage(
            nodes, [0, 1, 1, 2], NeutralAttacker, defender);

        Assert.Equal(DamageEngine.PlacementStatus.Optimized, result.Status);
        Assert.Equal(1, result.AuthoredGroupIndex); // the missing-HP cast, not the gated node

        // Authored (Q, R, W): R reads a 300 prefix → 75+125 = 200; HP is then 500, i.e. below the
        // 60% gate, so W fires → 300+200+150 = 650.
        Assert.Equal(650, result.AuthoredTotalDamage, 4);
        // The alternatives are both WORSE, and the gate is why. Placing R first (j=0) costs it its
        // whole missing-HP term (125, total 575). Placing it last (j=2) leaves the target at 700
        // when W lands — above the gate, so W contributes nothing at all (total 500). So the
        // authored order already IS the optimum here: pushing a missing-HP node later is not
        // unconditionally better once a gate depends on the HP it consumes.
        Assert.Equal(1, result.OptimalGroupIndex);
        Assert.Equal(650, result.MaxTotalDamage, 4);
    }
}
