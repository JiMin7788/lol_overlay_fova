using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Items;
using Overlay.Core.Runes;
// Disambiguates against the implicitly-global-used System.Threading.ExecutionContext:
// M03's spec names its own execution-context type "ExecutionContext" literally
// (docs/modules/M03_COMBO_ENGINE.md Interfaces), so the production type keeps that
// name; this test file just needs an alias to resolve the otherwise-ambiguous
// unqualified reference (CS0104).
using ExecutionContext = Overlay.Core.Combo.ExecutionContext;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M03 Combo Engine (docs/modules/M03_COMBO_ENGINE.md):
///  - buildGraph validation (missing/duplicate id rejection).
///  - each of the 3 Condition types independently (met => included, unmet => skipped
///    with mana/damage unaffected by the skipped node).
///  - mana accumulation + manaSufficient (both true and false cases; damage calc still
///    proceeds when insufficient, per the spec's explicit no-abort instruction).
///  - the ratioAD/ratioBonusAD -> M05 Damage.ComboNode translation-layer arithmetic,
///    proven against a hand-computed expectation.
///  - a full execute() integration test wiring a real RuneEngine + real DamageEngine
///    (not mocks), with one API-trackable and one manual rune.
///  - serialize/deserialize round-trip (deep equality to the original graph).
///
/// RuneEngine subscribes to the static EventBus, and RuneRepository/ChampionRepository
/// are both static/process-wide, so every test resets all three first — same isolation
/// pattern as RuneEngineTests. AssemblyInfo.cs already disables cross-class parallelization.
/// </summary>
public class ComboEngineTests
{
    private const string TrackableRuneId = "9999";  // ApiTrackable: true (Conqueror stand-in)
    private const string ManualRuneId = "8126";      // ApiTrackable: false (Cheap Shot stand-in)

    private static readonly AttackerStat Attacker =
        new(Ad: 60, BonusAD: 40, Ap: 30, Level: 11, CriticalChance: 0, LifeSteal: 0);

    private static readonly DefenderStat Defender =
        new(CurrentHP: 500, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0); // Armor 0 => raw==mitigated, isolates arithmetic

    public ComboEngineTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = TrackableRuneId, Name = "Conqueror", Tree = "Precision", EffectFormula = "stack-based", ApiTrackable = true },
            new RuneData { Id = ManualRuneId, Name = "Cheap Shot", Tree = "Domination", EffectFormula = null, ApiTrackable = false },
            new RuneData { Id = "8143", Name = "Sudden Impact", Tree = "Domination", EffectFormula = null, ApiTrackable = false }, // loop 174 (Flash/dash gate test)
        });
        ChampionRepository.ResetForTests();
        ItemEffectDb.ResetForTests(); // the Attached Item Effects tests below read the real bundled item_effects.json
    }

    private static ComboEngine NewEngine(out RuneEngine runeEngine)
    {
        runeEngine = new RuneEngine();
        return new ComboEngine(new DamageEngine(), runeEngine);
    }

    private static Combo.ComboNode PlainNode(string id, double damage = 10, double ratioAD = 0, double ratioBonusAD = 0,
        double ratioAP = 0, double mana = 0, Combo.Condition? condition = null) => new(
        Id: id,
        NodeType: ComboNodeType.Skill,
        Name: id,
        Cooldown: 0,
        Mana: mana,
        Damage: damage,
        DamageType: ComboDamageType.Physical,
        RatioAD: ratioAD,
        RatioBonusAD: ratioBonusAD,
        RatioAP: ratioAP,
        CastTime: 0.5,
        Delay: 0.1,
        TravelTime: 0,
        Condition: condition);

    // ---------------------------------------------------------------- buildGraph

    [Fact]
    public void BuildGraph_RejectsMissingId()
    {
        var engine = NewEngine(out _);
        var node = PlainNode("") ; // empty id

        Assert.Throws<ArgumentException>(() => engine.BuildGraph(new[] { node }));
    }

    [Fact]
    public void BuildGraph_RejectsDuplicateId()
    {
        var engine = NewEngine(out _);
        var nodes = new[] { PlainNode("Q"), PlainNode("Q") };

        Assert.Throws<ArgumentException>(() => engine.BuildGraph(nodes));
    }

    [Fact]
    public void BuildGraph_ValidNodes_ProducesLinearImplicitEdges()
    {
        var engine = NewEngine(out _);
        var nodes = new[] { PlainNode("Q"), PlainNode("W"), PlainNode("E") };

        var graph = engine.BuildGraph(nodes);

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);
        Assert.Equal(new ComboEdge("Q", "W"), graph.Edges[0]);
        Assert.Equal(new ComboEdge("W", "E"), graph.Edges[1]);
    }

    // ---------------------------------------------------------------- M24 P1: RangeMin/Max

    [Fact]
    public void RangeMinMax_EqualCritEndpoints_AndContainTotal_NoCrit()
    {
        // With CriticalChance 0 the crit axis is degenerate, and no condition/knob axis is modeled
        // yet (P1), so the unified range collapses to the single total: RangeMin == RangeMax ==
        // CritMin == CritMax == TotalDamage. Proves P1 is pure-additive (no number changed).
        var engine = NewEngine(out var runeEngine);
        var graph = engine.BuildGraph(new[] { PlainNode("Q", damage: 100), PlainNode("W", damage: 40) });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Equal(result.TotalDamageMin, result.CritMin, 6); // alias
        Assert.Equal(result.TotalDamageMax, result.CritMax, 6);
        Assert.Equal(result.CritMin, result.RangeMin, 6);       // no extra axis yet -> range == crit
        Assert.Equal(result.CritMax, result.RangeMax, 6);
        Assert.Equal(result.TotalDamage, result.RangeMin, 6);   // degenerate -> collapses to total
        Assert.Equal(result.TotalDamage, result.RangeMax, 6);
        runeEngine.Dispose();
    }

    // ---------------------------------------------------------------- Condition types

    [Fact]
    public void StackGte_Met_NodeIncluded_Unmet_NodeSkipped()
    {
        var engine = NewEngine(out var runeEngine);
        var metNode = PlainNode("Met", damage: 50, condition: new Combo.Condition(ConditionType.StackGte, 3)) with { Stack = 3 };
        var unmetNode = PlainNode("Unmet", damage: 999, condition: new Combo.Condition(ConditionType.StackGte, 3)) with { Stack = 2 };
        var graph = engine.BuildGraph(new[] { metNode, unmetNode });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Single(result.NodeBreakdown);
        Assert.Equal("Met", result.NodeBreakdown[0].NodeId);
        Assert.Equal(50.0, result.TotalDamage, 2); // unmet node's 999 never contributes
        runeEngine.Dispose();
    }

    [Fact]
    public void StackGte_UsesSpecialPropertyFormula_WhenKeyPresent()
    {
        // formula "stack * 2": with node.Stack=2, resolved value = 4, condition value=3 -> met.
        ChampionRepository.Initialize(new[]
        {
            new ChampionData
            {
                Id = "TestChamp",
                SpecialProperties = new Dictionary<string, ChampionSpecialProperty>
                {
                    ["PassiveStack"] = new ChampionSpecialProperty
                    {
                        ChampionId = "TestChamp",
                        Key = "PassiveStack",
                        ValueFormula = "stack * 2",
                    },
                },
            },
        });

        var engine = NewEngine(out var runeEngine);
        var node = PlainNode("Passive", damage: 42, condition: new Combo.Condition(ConditionType.StackGte, 3))
            with { Stack = 2, SpecialPropertyKey = "PassiveStack" };
        var graph = engine.BuildGraph(new[] { node });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Single(result.NodeBreakdown); // resolved 2*2=4 >= 3 -> met
        runeEngine.Dispose();
    }

    [Fact]
    public void HpBelow_Met_NodeIncluded_Unmet_NodeSkipped()
    {
        // Defender.CurrentHP=500, MaxHP=1000 => 50% HP.
        var engine = NewEngine(out var runeEngine);
        var metNode = PlainNode("Met", damage: 10, condition: new Combo.Condition(ConditionType.HpBelow, 0.6)); // 50% <= 60% -> met
        var unmetNode = PlainNode("Unmet", damage: 999, condition: new Combo.Condition(ConditionType.HpBelow, 0.3)); // 50% <= 30% -> false
        var graph = engine.BuildGraph(new[] { metNode, unmetNode });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Single(result.NodeBreakdown);
        Assert.Equal("Met", result.NodeBreakdown[0].NodeId);
        runeEngine.Dispose();
    }

    [Fact]
    public void ManaGte_GatesOnRunningAccumulator_MetAfterPriorNodeSpendsEnough()
    {
        var engine = NewEngine(out var runeEngine);
        // First node costs 60 mana (no condition). Second node requires accumulated >= 50 (met, since 60>=50).
        // Third node requires accumulated >= 200 (unmet, since only 60 spent so far).
        var n1 = PlainNode("N1", damage: 10, mana: 60);
        var n2 = PlainNode("N2", damage: 20, mana: 0, condition: new Combo.Condition(ConditionType.ManaGte, 50));
        var n3 = PlainNode("N3", damage: 999, mana: 0, condition: new Combo.Condition(ConditionType.ManaGte, 200));
        var graph = engine.BuildGraph(new[] { n1, n2, n3 });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Equal(2, result.NodeBreakdown.Count); // N1, N2 executed; N3 skipped
        Assert.Equal(60.0, result.TotalMana, 2);      // N3 contributes 0 (skipped)
        Assert.False(result.ManaSufficient);          // N3's ManaGte condition failed
        runeEngine.Dispose();
    }

    [Fact]
    public void ManaSufficient_StaysTrue_WhenNoManaGteConditionFails()
    {
        var engine = NewEngine(out var runeEngine);
        var n1 = PlainNode("N1", damage: 10, mana: 60);
        var graph = engine.BuildGraph(new[] { n1 });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.True(result.ManaSufficient);
        Assert.Equal(60.0, result.TotalMana, 2);
        runeEngine.Dispose();
    }

    // ---------------------------------------------------------------- AD/BonusAD ratio translation

    [Fact]
    public void RatioAdBonusAd_Translation_MatchesHandComputedTwoRatioFormula()
    {
        // Architecture semantics: damage = flat + ratioAD*AD + ratioBonusAD*BonusAD + ratioAP*AP.
        // Attacker: Ad=60, BonusAD=40, Ap=30. Node: flat=20, ratioAD=1.0, ratioBonusAD=0.5, ratioAP=0.
        // Hand-computed (as if M05 natively supported two AD ratios):
        //   raw = 20 + 1.0*60 + 0.5*40 + 0*30 = 20 + 60 + 20 = 100
        // Armor/Mr = 0 => mitigated == raw == 100.
        var engine = NewEngine(out var runeEngine);
        var node = PlainNode("Q", damage: 20, ratioAD: 1.0, ratioBonusAD: 0.5, ratioAP: 0);
        var graph = engine.BuildGraph(new[] { node });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Equal(100.0, result.TotalDamage, 2);
        Assert.Equal(100.0, result.NodeBreakdown[0].Damage, 2);
        runeEngine.Dispose();
    }

    // ---------------------------------------------------------------- Full integration (real RuneEngine + DamageEngine)

    [Fact]
    public void Execute_FullIntegration_WithRealRuneEngineAndDamageEngine_AndMixedRuneTypes()
    {
        var engine = NewEngine(out var runeEngine);
        // Q: flat 20 + 1.0 ratioAD (=60) + 0 ratioBonusAD + 0.5 ratioAP (=15) = 95, Physical, armor 0.
        var q = PlainNode("Q", damage: 20, ratioAD: 1.0, ratioBonusAD: 0, ratioAP: 0.5, mana: 40);
        var graph = engine.BuildGraph(new[] { q });
        var runeConfig = new UserRuneConfig(new[] { TrackableRuneId, ManualRuneId });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, runeConfig);

        var result = engine.Execute(graph, context);

        // 20 + 1.0*60 + 0*40 + 0.5*30 = 20+60+15 = 95; armor 0 => mitigated 95.
        Assert.Equal(95.0, result.TotalDamage, 2);
        Assert.Equal(40.0, result.TotalMana, 2);
        Assert.True(result.ManaSufficient);
        Assert.False(result.IsLethal); // 95 dmg vs 500 current HP
        Assert.Single(result.NodeBreakdown);
        Assert.Equal(0.1, result.NodeBreakdown[0].Delay, 2);
        Assert.Equal(0.6, result.TotalCastTime, 2); // castTime 0.5 + delay 0.1

        runeEngine.Dispose();
    }

    /// <summary>
    /// Proves the additive-hit rune wiring (ComboEngine.Execute): an active manual rune with a real
    /// RuneEffectDb formula becomes ONE extra damage node for the whole combo, on top of the combo's
    /// own node damage — mirroring the exact pattern ComboRunner.AppendBonusHits already uses for
    /// champion on-hit/on-ability passives, just applied once per combo per active rune rather than
    /// once per node (see ComboEngine.Execute's rune-bonus-node doc comment).
    ///
    /// Hand-verified: ManualRuneId ("8126") is the real Cheap Shot id — rune_effects.json gives it
    /// baseAtLevel1=10, baseAtLevel18=49.12, TRUE damage, no AD/AP/maxHealth ratio. Attacker.Level=11
    /// (fixture): 10 + (49.12-10)/17*(11-1) = 33.011764705882353. Node Q is a flat 20 Physical hit
    /// with Armor=0 (mitigated == raw == 20). TRUE damage is never mitigated, so:
    ///   total = 20 (node) + 33.011764705882353 (rune, rounded to 33.01) = 53.01...  ~ 53.01 (2dp).
    /// </summary>
    [Fact]
    public void Execute_ActiveManualRune_WithRealFormula_AddsOneExtraHit_ForTheWholeCombo()
    {
        var engine = NewEngine(out var runeEngine);
        runeEngine.SetManualFlag(ManualRuneId, true); // "8126" == real Cheap Shot id
        var q = PlainNode("Q", damage: 20, ratioAD: 0, ratioBonusAD: 0, ratioAP: 0);
        var graph = engine.BuildGraph(new[] { q });
        var runeConfig = new UserRuneConfig(new[] { ManualRuneId });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, runeConfig);

        var result = engine.Execute(graph, context);

        Assert.Equal(53.01, result.TotalDamage, 2);
        // NodeBreakdown includes the appended rune hit alongside the executed combo node.
        Assert.Equal(2, result.NodeBreakdown.Count);
        Assert.Contains(result.NodeBreakdown, e => e.NodeId == "rune#8126");
        runeEngine.Dispose();
    }

    /// <summary>Regression: an active manual rune with NO covered RuneEffectDb formula (e.g. First
    /// Strike, "8369" — see rune_effects.json's own "_note" for why) contributes no extra hit, and
    /// a manual rune whose checkbox was never toggled on contributes no extra hit either — both
    /// leave TotalDamage exactly the combo's own node damage, unchanged from before this feature.</summary>
    [Fact]
    public void Execute_ManualRune_WithNoFormulaOrInactiveFlag_AddsNoExtraHit()
    {
        var engine = NewEngine(out var runeEngine);
        // ManualRuneId ("8126") never toggled active -> no bonus, even though it has a formula.
        var q = PlainNode("Q", damage: 20, ratioAD: 0, ratioBonusAD: 0, ratioAP: 0);
        var graph = engine.BuildGraph(new[] { q });
        var runeConfig = new UserRuneConfig(new[] { ManualRuneId });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, runeConfig);

        var result = engine.Execute(graph, context);

        Assert.Equal(20.0, result.TotalDamage, 2);
        Assert.Single(result.NodeBreakdown);
        runeEngine.Dispose();
    }

    /// <summary>(loop 174) Sudden Impact (8143) is a DASH-triggered rune: its real condition ("after a
    /// dash/blink") can't be observed, so the user signals it by placing a Flash (점멸) summoner node in
    /// the combo. With a Flash node present the bonus applies (rune#8143); with no Flash node it stays
    /// gated even though the rune is armed. Damage value is pinned by RuneEngineTests; here we only
    /// assert the dash-gate's presence/absence.</summary>
    [Fact]
    public void Execute_SuddenImpact_AppliesOnlyWhenComboHasFlashDashNode()
    {
        var engine = NewEngine(out var runeEngine);
        runeEngine.SetManualFlag("8143", true); // Sudden Impact armed
        var q = PlainNode("Q", damage: 20);
        var flash = new Combo.ComboNode(
            Id: "Flash", NodeType: ComboNodeType.Summoner, Name: "Flash",
            Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
            RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);
        var runeConfig = new UserRuneConfig(new[] { "8143" });

        var withFlash = engine.Execute(
            engine.BuildGraph(new[] { q, flash }),
            new ExecutionContext("TestChamp", Attacker, Defender, runeConfig));
        Assert.Contains(withFlash.NodeBreakdown, e => e.NodeId == "rune#8143");

        var noFlash = engine.Execute(
            engine.BuildGraph(new[] { q }),
            new ExecutionContext("TestChamp", Attacker, Defender, runeConfig));
        Assert.DoesNotContain(noFlash.NodeBreakdown, e => e.NodeId == "rune#8143");

        runeEngine.Dispose();
    }

    // ---------------------------------------------------------------- serialize/deserialize round-trip

    [Fact]
    public void SerializeDeserialize_RoundTrips_ToDeeplyEqualGraph()
    {
        var engine = NewEngine(out var runeEngine);
        var nodes = new[]
        {
            PlainNode("Q", damage: 10, ratioAD: 0.5, condition: new Combo.Condition(ConditionType.StackGte, 2)) with { Stack = 3, MaxStack = 5, ExecutePercent = 0.1, SpecialPropertyKey = "Foo" },
            PlainNode("W", damage: 5, mana: 30) with { ExecuteType = ComboExecuteType.Threshold, ExecuteThreshold = 100 },
        };
        var graph = engine.BuildGraph(nodes);

        var json = engine.Serialize(graph);
        var restored = engine.Deserialize(json);

        // NOT Assert.Equal(graph, restored): ComboGraph is a record, so it auto-implements
        // IEquatable<ComboGraph>, and xUnit's Assert.Equal prefers that over structural
        // enumerable comparison — record.Equals compares the IReadOnlyList<T> members via
        // the default equality comparer, which is reference equality for a plain
        // interface type. BuildGraph produces Nodes as an array (ToArray()) while
        // System.Text.Json deserializes an IReadOnlyList<T>-typed property into a List<T>,
        // so record equality spuriously fails even when the contents are identical.
        // Comparing the two IReadOnlyList<T> members directly instead makes each
        // Assert.Equal call operate on a type that does NOT implement IEquatable<T>
        // itself, so xUnit falls back to real element-by-element structural comparison.
        Assert.Equal(graph.Nodes, restored.Nodes);
        Assert.Equal(graph.Edges, restored.Edges);
        runeEngine.Dispose();
    }

    // ---------------------------------------------------------------- AttachedItemId (item drag-attach)

    /// <summary>
    /// M04 "Pending User-Reported Changes" item 3 (item half only, per the loop-38-continuation-14 HOLD
    /// note deferring the rune half): a node's <see cref="Combo.ComboNode.AttachedItemId"/> round-trips
    /// through Serialize/Deserialize, mirroring <see cref="SerializeDeserialize_RoundTrips_ToDeeplyEqualGraph"/>.
    /// This is a data-model/persistence proof only — the drag-and-hold WPF gesture itself lives in
    /// Overlay.Client and is not unit-testable from this headless project.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_RoundTrips_AttachedItemId()
    {
        var engine = NewEngine(out var runeEngine);
        var nodes = new[]
        {
            PlainNode("AA", damage: 10) with { AttachedItemId = "3153" }, // Blade of the Ruined King, arbitrary
            PlainNode("Q", damage: 5), // no attached item -> stays null, proves backward-compat
        };
        var graph = engine.BuildGraph(nodes);

        var json = engine.Serialize(graph);
        var restored = engine.Deserialize(json);

        Assert.Equal("3153", restored.Nodes[0].AttachedItemId);
        Assert.Null(restored.Nodes[1].AttachedItemId);
        runeEngine.Dispose();
    }

    /// <summary>
    /// A saved combo written BEFORE <see cref="Combo.ComboNode.AttachedItemId"/> existed (its JSON has
    /// no "attachedItemId" property at all) must still deserialize without error, with the field
    /// defaulting to null — the exact backward-compatibility guarantee the LEAD DECISION note requires
    /// ("existing saved combos remain valid").
    /// </summary>
    [Fact]
    public void Deserialize_PreExistingComboJson_WithoutAttachedItemId_DefaultsToNull()
    {
        var engine = NewEngine(out var runeEngine);
        // Property casing matches ComboEngine.JsonOptions (no PropertyNamingPolicy configured, so
        // System.Text.Json serializes/deserializes using the record's exact PascalCase member names —
        // this is a hand-written stand-in for a real "before this field existed" saved-combo file, not
        // a Serialize() round-trip, so it deliberately omits "AttachedItemId" entirely.
        const string oldJson = """
        {
          "Nodes": [
            {
              "Id": "AA_0",
              "NodeType": "Aa",
              "Name": "AA",
              "Cooldown": 0,
              "Mana": 0,
              "Damage": 0,
              "DamageType": "Physical",
              "RatioAD": 1,
              "RatioBonusAD": 0,
              "RatioAP": 0,
              "CastTime": 0,
              "Delay": 0,
              "TravelTime": 0
            }
          ],
          "Edges": []
        }
        """;

        var restored = engine.Deserialize(oldJson);

        Assert.Null(Assert.Single(restored.Nodes).AttachedItemId);
        runeEngine.Dispose();
    }

    // ---------------------------------------------------------------- UserHitDurationSeconds (적중시간)

    /// <summary>
    /// M04 duration-scaled hit feature (combo editor "적중시간" control): a node's
    /// <see cref="Combo.ComboNode.UserHitDurationSeconds"/> round-trips through
    /// Serialize/Deserialize, mirroring <see cref="SerializeDeserialize_RoundTrips_AttachedItemId"/>
    /// exactly — same optional, nullable, backward-compatible field pattern.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_RoundTrips_UserHitDurationSeconds()
    {
        var engine = NewEngine(out var runeEngine);
        var nodes = new[]
        {
            PlainNode("W", damage: 0) with { UserHitDurationSeconds = 2.5 }, // Malzahar W, arbitrary
            PlainNode("Q", damage: 5), // unset -> stays null, proves backward-compat
        };
        var graph = engine.BuildGraph(nodes);

        var json = engine.Serialize(graph);
        var restored = engine.Deserialize(json);

        Assert.Equal(2.5, restored.Nodes[0].UserHitDurationSeconds!.Value);
        Assert.Null(restored.Nodes[1].UserHitDurationSeconds);
        runeEngine.Dispose();
    }

    // ---------------------------------------------------------------- Auto-Trigger Rune Damage Engine
    // Real ids (auto-trackable, distinct from the 8-manual-id / synthetic-"9999" ids used above).
    // Attacker fixture: Ad=60, BonusAD=40, Ap=30, Level=11. Defender fixture (class-level): CurrentHP=500,
    // MaxHP=1000, Armor=0, Mr=0 (raw==mitigated for Physical/Magic; True always unmitigated).
    private const string AxiomArcanistId = "8224";
    private const string SummonAeryId = "8214";
    private const string RealElectrocuteId = "8112";
    private const string HailOfBladesId = "9923";
    private const string DarkHarvestId = "8128";
    private const string RealConquerorId = "8010";

    /// <summary>Axiom Arcanist trigger-satisfied: a node whose Id starts with "R_" (the champion's
    /// own Ultimate, per ComboSettingsView.xaml.cs's node-id convention) has ALL its damage-bearing
    /// fields scaled by 1.12x. Hand-verified: flat 100 Physical, Armor=0 -> 100*1.12=112.</summary>
    [Fact]
    public void AxiomArcanist_UltimateNodePresent_ScalesItsDamageBy1_12x()
    {
        var engine = NewEngine(out var runeEngine);
        var r = PlainNode("R_0", damage: 100);
        var graph = engine.BuildGraph(new[] { r });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { AxiomArcanistId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(112.0, result.TotalDamage, 2);
        runeEngine.Dispose();
    }

    /// <summary>Trigger-NOT-satisfied: no node's Id starts with "R_" -> no scaling, total damage
    /// stays exactly the node's own raw value.</summary>
    [Fact]
    public void AxiomArcanist_NoUltimateNodeInCombo_LeavesDamageUnscaled()
    {
        var engine = NewEngine(out var runeEngine);
        var q = PlainNode("Q_0", damage: 100);
        var graph = engine.BuildGraph(new[] { q });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { AxiomArcanistId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(100.0, result.TotalDamage, 2);
        runeEngine.Dispose();
    }

    /// <summary>Summon Aery trigger-satisfied: >= 1 damaging node -> ONE flat adaptive bonus hit.
    /// Hand-verified: level-linear(10,50,level=11) = 10 + 40/17*10 = 33.5294117647; +0.1*BonusAD(40)=4,
    /// +0.05*Ap(30)=1.5 -> 39.0294117647 (adTerm 4 > apTerm 1.5 -> Physical, Armor=0 -> unmitigated).
    /// Total = 20 (node) + 39.0294117647 = 59.0294117647 ~ 59.03.</summary>
    [Fact]
    public void SummonAery_AtLeastOneDamagingNode_AddsOneFlatAdaptiveBonusHit()
    {
        var engine = NewEngine(out var runeEngine);
        var q = PlainNode("Q_0", damage: 20);
        var graph = engine.BuildGraph(new[] { q });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { SummonAeryId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(59.03, result.TotalDamage, 2);
        Assert.Contains(result.NodeBreakdown, e => e.NodeId == "autorune#8214");
        runeEngine.Dispose();
    }

    /// <summary>Trigger-NOT-satisfied: the combo's only node deals 0 damage (no flat, no ratios) ->
    /// no damaging node at all -> no Aery bonus, total stays 0.</summary>
    [Fact]
    public void SummonAery_NoDamagingNodeInCombo_AddsNoBonus()
    {
        var engine = NewEngine(out var runeEngine);
        var q = PlainNode("Q_0", damage: 0, ratioAD: 0, ratioBonusAD: 0, ratioAP: 0);
        var graph = engine.BuildGraph(new[] { q });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { SummonAeryId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(0.0, result.TotalDamage, 2);
        Assert.DoesNotContain(result.NodeBreakdown, e => e.NodeId == "autorune#8214");
        runeEngine.Dispose();
    }

    /// <summary>Electrocute trigger-satisfied: exactly 3 damaging nodes -> ONE flat adaptive bonus hit.
    /// Hand-verified: level-linear(70,240,11) = 70 + 170/17*10 = 170; +4+1.5 = 175.5 (Physical, Armor=0).
    /// Total = 3*10 (nodes) + 175.5 = 205.5.</summary>
    [Fact]
    public void Electrocute_ThreeDamagingNodes_AddsOneFlatAdaptiveBonusHit()
    {
        var engine = NewEngine(out var runeEngine);
        var nodes = new[] { PlainNode("Q_0", damage: 10), PlainNode("W_0", damage: 10), PlainNode("E_0", damage: 10) };
        var graph = engine.BuildGraph(nodes);
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { RealElectrocuteId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(205.5, result.TotalDamage, 2);
        Assert.Contains(result.NodeBreakdown, e => e.NodeId == "autorune#8112");
        runeEngine.Dispose();
    }

    /// <summary>Trigger-NOT-satisfied: only 2 damaging nodes (real Electrocute needs 3 within 3s) ->
    /// no bonus, total stays exactly the 2 nodes' own damage.</summary>
    [Fact]
    public void Electrocute_OnlyTwoDamagingNodes_AddsNoBonus()
    {
        var engine = NewEngine(out var runeEngine);
        var nodes = new[] { PlainNode("Q_0", damage: 10), PlainNode("W_0", damage: 10) };
        var graph = engine.BuildGraph(nodes);
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { RealElectrocuteId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(20.0, result.TotalDamage, 2);
        Assert.DoesNotContain(result.NodeBreakdown, e => e.NodeId == "autorune#8112");
        runeEngine.Dispose();
    }

    /// <summary>Hail of Blades trigger-satisfied: 3 executed Aa nodes -> 3 separate TRUE-damage bonus
    /// hits, one per AA. Hand-verified: level-linear(4,20,11)=4+16/17*10=13.5294117647; +0.08*40=3.2,
    /// +0.06*30=1.8 -> 18.4117647059 per hit (True, unmitigated regardless of armor). Total = 3*10
    /// (AA base, Physical, Armor=0) + 3*18.4117647059 = 85.2352941176 ~ 85.24.</summary>
    [Fact]
    public void HailOfBlades_ThreeAaNodes_AddsThreeTrueDamageBonusHits()
    {
        var engine = NewEngine(out var runeEngine);
        var nodes = new[]
        {
            PlainNode("AA_0", damage: 10) with { NodeType = ComboNodeType.Aa },
            PlainNode("AA_1", damage: 10) with { NodeType = ComboNodeType.Aa },
            PlainNode("AA_2", damage: 10) with { NodeType = ComboNodeType.Aa },
        };
        var graph = engine.BuildGraph(nodes);
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { HailOfBladesId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(85.24, result.TotalDamage, 2);
        Assert.Contains(result.NodeBreakdown, e => e.NodeId == "autorune#9923_1");
        Assert.Contains(result.NodeBreakdown, e => e.NodeId == "autorune#9923_2");
        Assert.Contains(result.NodeBreakdown, e => e.NodeId == "autorune#9923_3");
        runeEngine.Dispose();
    }

    /// <summary>Trigger-NOT-satisfied: no Aa node in the combo (ability-only combo) -> no bonus.</summary>
    [Fact]
    public void HailOfBlades_NoAaNodeInCombo_AddsNoBonus()
    {
        var engine = NewEngine(out var runeEngine);
        var q = PlainNode("Q_0", damage: 10);
        var graph = engine.BuildGraph(new[] { q });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { HailOfBladesId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(10.0, result.TotalDamage, 2);
        Assert.DoesNotContain(result.NodeBreakdown, e => e.NodeId.StartsWith("autorune#9923"));
        runeEngine.Dispose();
    }

    /// <summary>Dark Harvest trigger-satisfied: target genuinely crosses below 50% HP mid-combo.
    /// Defender overridden to CurrentHP=600/MaxHP=1000 (60%); a 150-damage node brings it to 450 (45%
    /// -&lt;= 50%) -&gt; triggers. Hand-verified: bonus = 30 (flat, not level-scaled) + 0.1*40 + 0.05*30 =
    /// 35.5 (Physical, Armor=0). Total = 150 (node) + 35.5 = 185.5.</summary>
    [Fact]
    public void DarkHarvest_TargetCrossesBelow50PercentHp_AddsFlatAdaptiveBonus()
    {
        var engine = NewEngine(out var runeEngine);
        var defender = Defender with { CurrentHP = 600 };
        var q = PlainNode("Q_0", damage: 150);
        var graph = engine.BuildGraph(new[] { q });
        var context = new ExecutionContext("TestChamp", Attacker, defender, new UserRuneConfig(new[] { DarkHarvestId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(185.5, result.TotalDamage, 2);
        Assert.Contains(result.NodeBreakdown, e => e.NodeId == "autorune#8128");
        runeEngine.Dispose();
    }

    /// <summary>Trigger-NOT-satisfied: target never crosses below 50% HP (600 -&gt; 550, still 55%) ->
    /// no bonus at all — no default-always-apply fallback.</summary>
    [Fact]
    public void DarkHarvest_TargetNeverCrossesBelow50PercentHp_AddsNoBonus()
    {
        var engine = NewEngine(out var runeEngine);
        var defender = Defender with { CurrentHP = 600 };
        var q = PlainNode("Q_0", damage: 50);
        var graph = engine.BuildGraph(new[] { q });
        var context = new ExecutionContext("TestChamp", Attacker, defender, new UserRuneConfig(new[] { DarkHarvestId }));

        var result = engine.Execute(graph, context);

        Assert.Equal(50.0, result.TotalDamage, 2);
        Assert.DoesNotContain(result.NodeBreakdown, e => e.NodeId == "autorune#8128");
        runeEngine.Dispose();
    }

    /// <summary>
    /// Conqueror, full-stack case: 6 melee damaging hits (melee gains 2 stacks/hit, capped 12, so hit
    /// #6 is the first to see the full 10-stacks-accumulated-so-far value used below) -> per-node
    /// ramping bonus on EVERY node AND the full-stack heal (hit count 6 &gt;= melee threshold 6).
    /// Hand-verified (6 nodes, each flat 10 + ratioAD 0.5 -> folded 10+0.5*60=40 Physical, Armor=0;
    /// caster BonusAD=40 &gt; Ap=30 -> adaptive force resolves to AD): forcePerStack =
    /// level-linear(1.8,4,11) = 1.8+2.2/17*10 = 3.0941176471. Stacks-accumulated-BEFORE each hit
    /// (0-indexed, melee 2/hit, capped 12): [0,2,4,6,8,10] -> adaptiveForce*0.5 (node's own RatioAD) =
    /// [0, 3.0941176471, 6.1882352941, 9.2823529412, 12.3764705882, 15.4705882353], sum=46.4117647059.
    /// Total = 6*40 (base) + 46.4117647059 = 286.4117647059 ~ 286.41. Heal = round(0.08*286.41,2) =
    /// 22.91 (melee 8% of TotalDamage, using the ALREADY-ROUNDED TotalDamage per ComboEngine.Execute's
    /// RuneHeal computation).
    /// </summary>
    [Fact]
    public void Conqueror_SixMeleeHits_AppliesPerNodeRampingBonus_AndFullStackHeal()
    {
        var engine = NewEngine(out var runeEngine);
        var nodes = Enumerable.Range(0, 6)
            .Select(i => PlainNode($"Q_{i}", damage: 10, ratioAD: 0.5))
            .ToArray();
        var graph = engine.BuildGraph(nodes);
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { RealConquerorId }),
            CasterMaxHealth: 2000, CasterIsMelee: true);

        var result = engine.Execute(graph, context);

        Assert.Equal(286.41, result.TotalDamage, 2);
        Assert.Equal(22.91, result.RuneHeal, 2);
        runeEngine.Dispose();
    }

    /// <summary>
    /// Conqueror, NOT-full-stack case: only 3 melee damaging hits (need 6 to reach the 12-stack cap) ->
    /// the per-node ramping bonus STILL partially applies (stacks accumulate progressively, hit-by-
    /// hit, independent of reaching the cap), but the full-stack HEAL correctly does NOT trigger
    /// (0, not a default/guessed value) since the hit-count threshold was never reached.
    /// Hand-verified: stacks-before-each-hit [0,2,4] -> bonuses [0, 3.0941176471, 6.1882352941],
    /// sum=9.2823529412. Total = 3*40 + 9.2823529412 = 129.2823529412 ~ 129.28. Heal = 0.
    /// </summary>
    [Fact]
    public void Conqueror_ThreeMeleeHits_RampingBonusAppliesButNoFullStackHeal()
    {
        var engine = NewEngine(out var runeEngine);
        var nodes = Enumerable.Range(0, 3)
            .Select(i => PlainNode($"Q_{i}", damage: 10, ratioAD: 0.5))
            .ToArray();
        var graph = engine.BuildGraph(nodes);
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(new[] { RealConquerorId }),
            CasterMaxHealth: 2000, CasterIsMelee: true);

        var result = engine.Execute(graph, context);

        Assert.Equal(129.28, result.TotalDamage, 2);
        Assert.Equal(0.0, result.RuneHeal, 2);
        runeEngine.Dispose();
    }

    // ---------------------------------------------------------------- Attached Rune Effects (Part 3, loop 50)
    // ComboNode.AttachedRuneId — the theorycraft/force-apply sibling of AttachedItemId. Unlike every
    // other rune path (all gated on the rune being genuinely equipped AND its real trigger met), an
    // attached rune is applied to its node UNCONDITIONALLY. UserRuneConfig is EMPTY in these tests
    // (the rune is NOT equipped/selected), and the normal trigger condition is deliberately NOT met,
    // proving the force-apply is independent of both. Reuses the real bundled rune_effects.json.

    /// <summary>Force-apply, additive rune: Electrocute (8112) attached to a SINGLE damaging node,
    /// with an EMPTY UserRuneConfig (not equipped) and only 1 damaging node (its real 3-hit condition
    /// is NOT met). It still contributes its full bonus hit. Hand-verified identically to
    /// <see cref="Electrocute_ThreeDamagingNodes_AddsOneFlatAdaptiveBonusHit"/>: bonus = 175.5. Total =
    /// 10 (node) + 175.5 = 185.5. Bonus node id is "{nodeId}#rune8112" (not the auto path's
    /// "autorune#8112"), so the two sources are distinguishable in the breakdown.</summary>
    [Fact]
    public void AttachedRune_Electrocute_ForceAppliesEvenWhenNotEquippedAndConditionUnmet()
    {
        var engine = NewEngine(out var runeEngine);
        var node = PlainNode("Q_0", damage: 10) with { AttachedRuneId = RealElectrocuteId };
        var graph = engine.BuildGraph(new[] { node });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Equal(185.5, result.TotalDamage, 2);
        Assert.Contains(result.NodeBreakdown, e => e.NodeId == "Q_0#rune8112");
        Assert.DoesNotContain(result.NodeBreakdown, e => e.NodeId == "autorune#8112");
        runeEngine.Dispose();
    }

    /// <summary>Force-apply, multiplier rune: Axiom Arcanist (8224) attached to a NON-ultimate node
    /// (Q_0, not "R_"-prefixed), with an EMPTY UserRuneConfig. The auto path (which requires both the
    /// rune selected AND an "R_" node) would do nothing here — but force-apply scales the attached
    /// node itself: 100 * 1.12 = 112 (TestChamp has no "AoeUltimate" R tag -> single-target 1.12).</summary>
    [Fact]
    public void AttachedRune_Axiom_ForceScalesAttachedNode_EvenIfNotUltimate()
    {
        var engine = NewEngine(out var runeEngine);
        var node = PlainNode("Q_0", damage: 100) with { AttachedRuneId = AxiomArcanistId };
        var graph = engine.BuildGraph(new[] { node });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Equal(112.0, result.TotalDamage, 2);
        runeEngine.Dispose();
    }

    /// <summary>A node's <see cref="Combo.ComboNode.AttachedRuneId"/> round-trips through
    /// Serialize/Deserialize, mirroring <see cref="SerializeDeserialize_RoundTrips_AttachedItemId"/> —
    /// same optional, nullable, backward-compatible field pattern.</summary>
    [Fact]
    public void SerializeDeserialize_RoundTrips_AttachedRuneId()
    {
        var engine = NewEngine(out var runeEngine);
        var nodes = new[]
        {
            PlainNode("Q", damage: 10) with { AttachedRuneId = "8112" }, // Electrocute, arbitrary
            PlainNode("W", damage: 5), // no attached rune -> stays null, proves backward-compat
        };
        var graph = engine.BuildGraph(nodes);

        var json = engine.Serialize(graph);
        var restored = engine.Deserialize(json);

        Assert.Equal("8112", restored.Nodes[0].AttachedRuneId);
        Assert.Null(restored.Nodes[1].AttachedRuneId);
        runeEngine.Dispose();
    }

    // ---------------------------------------------------------------- Attached Item Effects (loop 48)
    // ComboNode.AttachedItemId, previously decorative-only (loop 38 LEAD DECISION) -- now a real
    // engine input for items ItemEffectDb classifies as ItemTrigger.ManualActiveBurst. Deathfire
    // Grasp (3128, item_effects.json): targetMaxHpPercent=0.15, amplifyDamagePercent=0.15,
    // amplifyDurationSeconds=4. Reads the REAL bundled data/item_effects.json (see constructor's
    // ItemEffectDb.ResetForTests()), same convention as ItemEffectTests.cs, not a test-only fixture.
    private const string DeathfireGraspId = "3128";

    /// <summary>Trigger-satisfied, full mechanic in one test (burst + amplify + window boundary):
    /// a Deathfire-Grasp-attached node (Q_0, damage 10, t=0) is followed by a node inside the 4s
    /// window (W_0, damage 100, lands at t=1 -> amplified to 115) and a node outside it (E_0,
    /// damage 100, lands at t=4.5 -> unamplified, stays 100). Defender MaxHP=1000, Armor=0/MR=0
    /// (raw==mitigated). Hand-verified: Q_0 (10, unmodified -- the burst is a separate trailing
    /// node, never folded into the attached node itself) + W_0 (100*1.15=115) + E_0 (100,
    /// unmodified) + burst (0.15*1000=150) = 375.</summary>
    [Fact]
    public void AttachedDeathfireGrasp_AddsInstantBurst_AndAmplifiesOnlyNodesWithinWindow()
    {
        var engine = NewEngine(out var runeEngine);
        var itemNode = PlainNode("Q_0", damage: 10) with { AttachedItemId = DeathfireGraspId, CastTime = 0, Delay = 0 };
        var insideWindow = PlainNode("W_0", damage: 100) with { CastTime = 0, Delay = 1 };
        var outsideWindow = PlainNode("E_0", damage: 100) with { CastTime = 0, Delay = 3.5 };
        var graph = engine.BuildGraph(new[] { itemNode, insideWindow, outsideWindow });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Equal(375.0, result.TotalDamage, 2);
        Assert.Contains(result.NodeBreakdown, e => e.NodeId == "Q_0#item3128");
        Assert.Equal(4, result.NodeBreakdown.Count); // 3 executed nodes + 1 trailing burst node
        runeEngine.Dispose();
    }

    /// <summary>Trigger-NOT-satisfied: an item id ItemEffectDb does not cover (Zhonya's Hourglass,
    /// 3157 -- a real Data Dragon id with no item_effects.json entry) attached to a node produces NO
    /// burst and NO amplify window -- damage stays exactly the node's own raw value, proving the
    /// pre-loop-48 decorative-only behavior is unchanged for every item other than the covered
    /// ManualActiveBurst set.</summary>
    [Fact]
    public void AttachedUncoveredItem_RemainsDecorativeOnly_NoDamageChange()
    {
        var engine = NewEngine(out var runeEngine);
        var itemNode = PlainNode("Q_0", damage: 50) with { AttachedItemId = "3157" };
        var graph = engine.BuildGraph(new[] { itemNode });
        var context = new ExecutionContext("TestChamp", Attacker, Defender, new UserRuneConfig(Array.Empty<string>()));

        var result = engine.Execute(graph, context);

        Assert.Equal(50.0, result.TotalDamage, 2);
        Assert.Single(result.NodeBreakdown);
        runeEngine.Dispose();
    }
}
