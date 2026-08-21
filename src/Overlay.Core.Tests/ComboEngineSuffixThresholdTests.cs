using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
// Disambiguates against the implicitly-global-used System.Threading.ExecutionContext, same as
// ComboEngineTests.cs — M03's spec names its own execution-context type "ExecutionContext"
// literally (docs/modules/M03_COMBO_ENGINE.md Interfaces).
using ExecutionContext = Overlay.Core.Combo.ExecutionContext;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M20 §6 item 3 / §6.3 (suffix kill-threshold) being wired all the way
/// into <see cref="ComboResult.FinisherNodeLabel"/>/<see cref="ComboResult.SuffixThresholdHP"/>
/// by <see cref="ComboEngine.Execute"/> — the internal math itself (<c>DamageEngine.
/// SolveSuffixThresholdHp</c>) is already covered by <c>DamageEngineFoldTests.cs</c>; this file
/// only proves the COMBO-level wiring: finding the right "finisher" node and threading its
/// suffix threshold through to the engine's public result.
///
/// New sibling file (not added to ComboEngineTests.cs/ComboRunnerTests.cs) per this round's
/// task instructions, to avoid touching files another concurrent round might be editing.
///
/// Reuses M20 §8's own worked example (Q flat 300 phys, W flat 200 mag, R MissingHp B=400
/// r=0.286 mag, A(auto) flat 250 phys, M=2000, armor 100 -> k=0.5, MR 60 -> k=0.625) so the
/// expected numbers (896.42 whole-combo H*, 621.42 suffix-at-R H*) are the SAME already-
/// hand-verified constants asserted in DamageEngineFoldTests.cs's §10 vectors 2 and 4, rather
/// than a fresh derivation.
/// </summary>
public class ComboEngineSuffixThresholdTests
{
    public ComboEngineSuffixThresholdTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
    }

    private static ComboEngine NewEngine() => new(new DamageEngine(), new RuneEngine());

    private static Combo.ComboNode PlainNode(string id, double damage, ComboDamageType type) => new(
        Id: id,
        NodeType: ComboNodeType.Skill,
        Name: id,
        Cooldown: 0,
        Mana: 0,
        Damage: damage,
        DamageType: type,
        RatioAD: 0,
        RatioBonusAD: 0,
        RatioAP: 0,
        CastTime: 0.5,
        Delay: 0.1,
        TravelTime: 0);

    [Fact]
    public void Execute_TrailingMissingHpFinisher_ComputesSuffixThreshold()
    {
        var engine = NewEngine();
        var attacker = new AttackerStat(Ad: 0, BonusAD: 0, Ap: 0, Level: 1, CriticalChance: 0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 2000, MaxHP: 2000, Armor: 100, Mr: 60, Shield: 0);

        var q = PlainNode("Q", damage: 300, ComboDamageType.Physical);
        var w = PlainNode("W", damage: 200, ComboDamageType.Magic);
        var r = PlainNode("R", damage: 400, ComboDamageType.Magic) with
        {
            ExecuteType = ComboExecuteType.MissingHp,
            ExecutePercent = 0.286,
        };
        var aa = PlainNode("AA", damage: 250, ComboDamageType.Physical) with { NodeType = ComboNodeType.Aa };

        var graph = engine.BuildGraph(new[] { q, w, r, aa });
        var context = new ExecutionContext("TestChamp", attacker, defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        // Whole-combo H* for this exact QWRA shape is M20 §8's own verified 896.42 — sanity
        // cross-check that this test's node setup really does reproduce the spec's worked example.
        Assert.Equal(896.42, result.KillThresholdHP, 2);

        Assert.Equal("R", result.FinisherNodeLabel);
        Assert.NotNull(result.SuffixThresholdHP);
        Assert.Equal(621.42, result.SuffixThresholdHP!.Value, 2);
    }

    [Fact]
    public void Execute_NoHpScalingNode_LeavesSuffixThresholdNull()
    {
        var engine = NewEngine();
        var attacker = new AttackerStat(Ad: 0, BonusAD: 0, Ap: 0, Level: 1, CriticalChance: 0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);

        var q = PlainNode("Q", damage: 100, ComboDamageType.Physical);
        var w = PlainNode("W", damage: 100, ComboDamageType.Physical);
        var graph = engine.BuildGraph(new[] { q, w });
        var context = new ExecutionContext("TestChamp", attacker, defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Null(result.FinisherNodeLabel);
        Assert.Null(result.SuffixThresholdHP);
    }
}
