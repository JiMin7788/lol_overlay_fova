using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ExecutionContext = Overlay.Core.Combo.ExecutionContext;

namespace Overlay.Core.Tests;

/// <summary>
/// COMBO-level wiring proof for M20 §6.2 (CLAUDE_CODE_TODO §75): the kill-feasible maximum reaching
/// <see cref="ComboResult.BurstCeiling"/>/<see cref="ComboResult.BurstCeilingDamage"/>/
/// <see cref="ComboResult.BurstCeilingSequence"/>. The search math itself is covered by
/// <c>DamageEnginePlacementTests.cs</c>; this file only proves that <see cref="ComboEngine.Execute"/>
/// groups nodes by authored cast, runs the search, and reports BOTH numbers — the authored-order
/// <see cref="ComboResult.TotalDamage"/> is never overwritten by the ceiling.
///
/// Sibling file to <c>ComboEngineSuffixThresholdTests.cs</c> and reuses M20 §8's same worked example
/// (Q flat 300 phys, W flat 200 mag, R MissingHp B=400 r=0.286 mag, A flat 250 phys, M=2000,
/// armor 100 → k=0.5, MR 60 → k=0.625) so the two §6 readings sit on identical inputs.
/// </summary>
public class ComboEngineBurstCeilingTests
{
    public ComboEngineBurstCeilingTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
    }

    private static ComboEngine NewEngine() => new(new DamageEngine(), new RuneEngine());

    private static Combo.ComboNode PlainNode(string id, double damage, ComboDamageType type) => new(
        Id: id, NodeType: ComboNodeType.Skill, Name: id, Cooldown: 0, Mana: 0,
        Damage: damage, DamageType: type, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0.5, Delay: 0.1, TravelTime: 0);

    private static ExecutionContext Context(DefenderStat defender) => new(
        "TestChamp",
        new AttackerStat(Ad: 0, BonusAD: 0, Ap: 0, Level: 1, CriticalChance: 0, LifeSteal: 0),
        defender,
        new UserRuneConfig(Array.Empty<string>()));

    /// <summary>
    /// QWRA against a full-HP 2000 target the combo does not kill. Post-mitigation the flats are
    /// Q 150, W 125, A 125 and R is 250 + 0.17875·missing.
    /// Authored (Q W R A): R reads a 275 prefix → 299.16, total 699.16.
    /// Optimal (Q W A R): R reads a 400 prefix → 321.50, total 721.50.
    /// Both are reported; the headline total stays the authored one.
    /// </summary>
    [Fact]
    public void Execute_MissingHpFinisher_ReportsCeilingWithoutTouchingAuthoredTotal()
    {
        var engine = NewEngine();
        var defender = new DefenderStat(CurrentHP: 2000, MaxHP: 2000, Armor: 100, Mr: 60, Shield: 0);

        var q = PlainNode("Q", damage: 300, ComboDamageType.Physical);
        var w = PlainNode("W", damage: 200, ComboDamageType.Magic);
        var r = PlainNode("R", damage: 400, ComboDamageType.Magic) with
        {
            ExecuteType = ComboExecuteType.MissingHp,
            ExecutePercent = 0.286,
        };
        var aa = PlainNode("AA", damage: 250, ComboDamageType.Physical) with { NodeType = ComboNodeType.Aa };

        var result = engine.Execute(engine.BuildGraph(new[] { q, w, r, aa }), Context(defender));

        Assert.Equal(699.16, result.TotalDamage, 2);
        Assert.Equal(BurstCeilingStatus.Optimized, result.BurstCeiling);
        Assert.Equal(721.50, result.BurstCeilingDamage, 2);
        Assert.Equal("R", result.BurstCeilingNodeLabel);
        Assert.Equal("Q W AA R", result.BurstCeilingSequence);
        Assert.True(result.BurstCeilingDamage > result.TotalDamage);
    }

    /// <summary>Same combo already authored at its optimum: the ceiling equals the headline and is
    /// labelled as confirmation, not as a suggestion.</summary>
    [Fact]
    public void Execute_AlreadyOptimalOrder_IsReportedAsSuch()
    {
        var engine = NewEngine();
        var defender = new DefenderStat(CurrentHP: 2000, MaxHP: 2000, Armor: 100, Mr: 60, Shield: 0);

        var q = PlainNode("Q", damage: 300, ComboDamageType.Physical);
        var aa = PlainNode("AA", damage: 250, ComboDamageType.Physical) with { NodeType = ComboNodeType.Aa };
        var r = PlainNode("R", damage: 400, ComboDamageType.Magic) with
        {
            ExecuteType = ComboExecuteType.MissingHp,
            ExecutePercent = 0.286,
        };

        var result = engine.Execute(engine.BuildGraph(new[] { q, aa, r }), Context(defender));

        Assert.Equal(BurstCeilingStatus.AlreadyOptimal, result.BurstCeiling);
        Assert.Equal(result.TotalDamage, result.BurstCeilingDamage, 2);
        Assert.Equal("Q AA R", result.BurstCeilingSequence);
    }

    /// <summary>M20 §6.2's degenerate case, end to end: the target dies to Q+AA alone, so R's
    /// ceiling is not a requirement and the combo says so rather than showing a number as one.</summary>
    [Fact]
    public void Execute_ComboKillsWithoutTheVarianceNode_ReportsVarianceUnnecessary()
    {
        var engine = NewEngine();
        var defender = new DefenderStat(CurrentHP: 300, MaxHP: 2000, Armor: 100, Mr: 60, Shield: 0);

        var q = PlainNode("Q", damage: 300, ComboDamageType.Physical);   // 150 post-mitigation
        var aa = PlainNode("AA", damage: 400, ComboDamageType.Physical) with { NodeType = ComboNodeType.Aa }; // 200
        var r = PlainNode("R", damage: 400, ComboDamageType.Magic) with
        {
            ExecuteType = ComboExecuteType.MissingHp,
            ExecutePercent = 0.286,
        };

        var result = engine.Execute(engine.BuildGraph(new[] { q, aa, r }), Context(defender));

        Assert.Equal(BurstCeilingStatus.VarianceUnnecessary, result.BurstCeiling);
        Assert.Equal("R", result.BurstCeilingNodeLabel);
    }

    /// <summary>An all-flat combo has one honest number and no ceiling row — the additive fields
    /// stay at their defaults so the HUD draws nothing extra.</summary>
    [Fact]
    public void Execute_NoVarianceNode_LeavesCeilingFieldsUnset()
    {
        var engine = NewEngine();
        var defender = new DefenderStat(CurrentHP: 2000, MaxHP: 2000, Armor: 0, Mr: 0, Shield: 0);

        var q = PlainNode("Q", damage: 100, ComboDamageType.Physical);
        var w = PlainNode("W", damage: 100, ComboDamageType.Physical);

        var result = engine.Execute(engine.BuildGraph(new[] { q, w }), Context(defender));

        Assert.Equal(BurstCeilingStatus.None, result.BurstCeiling);
        Assert.Equal(0, result.BurstCeilingDamage);
        Assert.Null(result.BurstCeilingSequence);
    }
}
