using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
// Disambiguates against the implicitly-global System.Threading.ExecutionContext — same
// reason ComboEngineTests aliases it (M04/M03 name their own type "ExecutionContext").
using ExecutionContext = Overlay.Core.Combo.ExecutionContext;
// ComboNode also exists in Overlay.Core.Damage (M05's own node shape) — this file only
// ever means M03/M04's node, so alias it to resolve CS0104.
using ComboNode = Overlay.Core.Combo.ComboNode;
// DamageType exists in both ChampionDb (M11 skill type) and Damage (M05); the palette
// test only references M11's SkillData.DamageType.
using DamageType = Overlay.Core.ChampionDb.DamageType;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M04 Combo Editor (docs/modules/M04_COMBO_EDITOR.md):
///  - create + add 5+ nodes, reorder, update, remove.
///  - export -> import round-trip equality (serialize a graph, import the JSON back,
///    assert identical nodes/edges).
///  - import schema-validation failure paths (bad JSON / missing required field /
///    empty Nodes) throw the typed ComboImportException, not a raw crash.
///  - bindHotkey records the mapping via M14 and invokes NO input API.
///  - UI.COMBO_SAVED is published on save (subscribed on the bus and asserted).
///  - save -> load round-trips a 5+ node combo identically.
///  - preview reuses M03/M05 (real ComboEngine + DamageEngine).
///
/// ConfigManager persists to disk and EventBus/ChampionRepository are static/process-wide,
/// so each test uses its own temp config dir and resets the static state first — same
/// isolation pattern as ConfigManagerTests/ComboEngineTests. AssemblyInfo.cs already
/// disables cross-class parallelization.
/// </summary>
public class ComboEditorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ComboEditorTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "M04_ComboEditorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private ComboEditor NewEditor(out ConfigManager config)
    {
        config = new ConfigManager(_configPath);
        return new ComboEditor(new ComboEngine(new DamageEngine(), new RuneEngine()), config);
    }

    private static ComboNode Node(string id, double damage = 10, ComboNodeType type = ComboNodeType.Skill) => new(
        Id: id,
        NodeType: type,
        Name: id,
        Cooldown: 0,
        Mana: 0,
        Damage: damage,
        DamageType: ComboDamageType.Physical,
        RatioAD: 0,
        RatioBonusAD: 0,
        RatioAP: 0,
        CastTime: 0.5,
        Delay: 0.1,
        TravelTime: 0);

    private static void AddFive(ComboEditor editor, string comboId)
    {
        editor.AddNode(comboId, Node("Q"));
        editor.AddNode(comboId, Node("W"));
        editor.AddNode(comboId, Node("E"));
        editor.AddNode(comboId, Node("R"));
        editor.AddNode(comboId, Node("AA", type: ComboNodeType.Aa));
    }

    // ---------------------------------------------------------------- create + edit

    [Fact]
    public void CreateAndAddFiveNodes_ProducesFiveNodeGraph()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "MyCombo");
        Assert.Empty(draft.Graph.Nodes);

        AddFive(editor, draft.Id);

        var graph = editor.GetCombo(draft.Id).Graph;
        Assert.Equal(5, graph.Nodes.Count);
        Assert.Equal(4, graph.Edges.Count); // linear chain from ComboEngine.BuildGraph
        config.Dispose();
    }

    [Fact]
    public void AddNode_RejectsDuplicateId()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");
        editor.AddNode(draft.Id, Node("Q"));

        Assert.Throws<ArgumentException>(() => editor.AddNode(draft.Id, Node("Q")));
        config.Dispose();
    }

    [Fact]
    public void ReorderNodes_ReordersSequence()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");
        AddFive(editor, draft.Id);

        editor.ReorderNodes(draft.Id, new[] { "AA", "R", "E", "W", "Q" });

        var ids = editor.GetCombo(draft.Id).Graph.Nodes.Select(n => n.Id).ToArray();
        Assert.Equal(new[] { "AA", "R", "E", "W", "Q" }, ids);
        config.Dispose();
    }

    [Fact]
    public void ReorderNodes_RejectsMismatchedIdSet()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");
        AddFive(editor, draft.Id);

        Assert.Throws<ArgumentException>(() => editor.ReorderNodes(draft.Id, new[] { "Q", "W", "E" }));
        config.Dispose();
    }

    [Fact]
    public void UpdateNode_AppliesPatch()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");
        editor.AddNode(draft.Id, Node("Q", damage: 10));

        editor.UpdateNode(draft.Id, "Q", n => n with { Damage = 250 });

        var q = editor.GetCombo(draft.Id).Graph.Nodes.Single(n => n.Id == "Q");
        Assert.Equal(250, q.Damage);
        config.Dispose();
    }

    [Fact]
    public void RemoveNode_RemovesById()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");
        AddFive(editor, draft.Id);

        editor.RemoveNode(draft.Id, "W");

        var ids = editor.GetCombo(draft.Id).Graph.Nodes.Select(n => n.Id).ToArray();
        Assert.Equal(new[] { "Q", "E", "R", "AA" }, ids);
        config.Dispose();
    }

    [Fact]
    public void RemoveNode_UnknownId_Throws()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");
        editor.AddNode(draft.Id, Node("Q"));

        Assert.Throws<KeyNotFoundException>(() => editor.RemoveNode(draft.Id, "Z"));
        config.Dispose();
    }

    [Fact]
    public void UnknownComboId_Throws()
    {
        var editor = NewEditor(out var config);
        Assert.Throws<KeyNotFoundException>(() => editor.AddNode("does-not-exist", Node("Q")));
        config.Dispose();
    }

    // ---------------------------------------------------------------- export/import round-trip

    [Fact]
    public void ExportImport_RoundTrips_Identically()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");
        AddFive(editor, draft.Id);
        var original = editor.GetCombo(draft.Id).Graph;

        var json = editor.ExportCombo(draft.Id);
        var restored = editor.ImportCombo(json);

        Assert.Equal(original.Nodes, restored.Nodes);
        Assert.Equal(original.Edges, restored.Edges);
        config.Dispose();
    }

    [Fact]
    public void ExportImport_RoundTrips_UserConditionMet()
    {
        // (M28 §1/§2 "최대 데미지" checkbox) The one knob field with no prior round-trip coverage —
        // every other UserXxx knob (UserHitDurationSeconds/UserDistanceFraction/UserAttackCount/
        // UserStackCount) is already exercised via AddFive's node shapes below; this locks
        // UserConditionMet the same way (both true and the default-null/unset state).
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("KSante", "C");
        editor.AddNode(draft.Id, Node("R_0") with { UserConditionMet = true });
        editor.AddNode(draft.Id, Node("Q_0") with { UserConditionMet = null });
        var original = editor.GetCombo(draft.Id).Graph;

        var json = editor.ExportCombo(draft.Id);
        var restored = editor.ImportCombo(json);

        Assert.Equal(original.Nodes, restored.Nodes);
        Assert.True(restored.Nodes.Single(n => n.Id == "R_0").UserConditionMet);
        Assert.Null(restored.Nodes.Single(n => n.Id == "Q_0").UserConditionMet);
        config.Dispose();
    }

    // ---------------------------------------------------------------- import validation failures

    [Fact]
    public void ImportCombo_MalformedJson_ThrowsComboImportException()
    {
        var editor = NewEditor(out var config);
        Assert.Throws<ComboImportException>(() => editor.ImportCombo("{ not valid json "));
        config.Dispose();
    }

    [Fact]
    public void ImportCombo_MissingRequiredField_ThrowsComboImportException()
    {
        var editor = NewEditor(out var config);
        // A node with every field present EXCEPT the required numeric "Damage".
        // System.Text.Json would silently default the missing field; validation must reject.
        const string json = """
            { "Nodes": [ { "Id": "Q", "Name": "Q", "NodeType": "Skill", "DamageType": "Physical",
              "Cooldown": 0, "Mana": 0, "RatioAD": 0, "RatioBonusAD": 0, "RatioAP": 0,
              "CastTime": 0, "Delay": 0, "TravelTime": 0 } ], "Edges": [] }
            """;
        var ex = Assert.Throws<ComboImportException>(() => editor.ImportCombo(json));
        Assert.Contains("Damage", ex.Message);
        config.Dispose();
    }

    [Fact]
    public void ImportCombo_EmptyNodes_ThrowsComboImportException()
    {
        var editor = NewEditor(out var config);
        Assert.Throws<ComboImportException>(() => editor.ImportCombo("""{ "Nodes": [], "Edges": [] }"""));
        config.Dispose();
    }

    [Fact]
    public void ImportCombo_InvalidEnumValue_ThrowsComboImportException()
    {
        var editor = NewEditor(out var config);
        const string json = """
            { "Nodes": [ { "Id": "Q", "Name": "Q", "NodeType": "NotAType", "DamageType": "Physical",
              "Cooldown": 0, "Mana": 0, "Damage": 10, "RatioAD": 0, "RatioBonusAD": 0, "RatioAP": 0,
              "CastTime": 0, "Delay": 0, "TravelTime": 0 } ], "Edges": [] }
            """;
        Assert.Throws<ComboImportException>(() => editor.ImportCombo(json));
        config.Dispose();
    }

    // ---------------------------------------------------------------- hotkey binding (mapping only)

    [Fact]
    public void BindHotkey_RecordsMappingInConfig_NoInputApi()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");

        editor.BindHotkey(draft.Id, "Alt+1");

        // Persisted as {hotkey}::{championId} -> comboId under hotkeys.comboSlots (loop 44: keyed
        // per champion so a different champion's combo can share the same raw hotkey without
        // silently unbinding this one — see ComboEditor.ComposeSlotKey).
        Assert.Equal(draft.Id, config.Get("hotkeys.comboSlots." + ComboEditor.ComposeSlotKey("Alt+1", "Ahri")));
        config.Dispose();
    }

    [Fact]
    public void BindHotkey_TwoDifferentChords_BothMapToSameCombo()
    {
        // M13 "Pending User-Reported Changes" (loop 38): a combo may bind 2 independent chords.
        // hotkeys.comboSlots is keyed by {hotkey}::{championId} (not by comboId), so it is already
        // N:1 — binding a second, different chord to the same comboId needs no schema change, just
        // a second BindHotkey call (which the M04/UI layer now offers via a second capture control).
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");

        editor.BindHotkey(draft.Id, "Alt+1");
        editor.BindHotkey(draft.Id, "Alt+2");

        Assert.Equal(draft.Id, config.Get("hotkeys.comboSlots." + ComboEditor.ComposeSlotKey("Alt+1", "Ahri")));
        Assert.Equal(draft.Id, config.Get("hotkeys.comboSlots." + ComboEditor.ComposeSlotKey("Alt+2", "Ahri")));
        config.Dispose();
    }

    [Fact]
    public void BindHotkey_SameRawHotkey_DifferentChampions_BothPersist()
    {
        // loop 44 bug 1: assigning hotkey "A" to a Garen combo used to silently overwrite/unbind an
        // Ahri combo already on "A", because the old schema was a flat hotkey->comboId map with no
        // champion dimension. Both must now coexist under the composite key.
        var editor = NewEditor(out var config);
        var ahriDraft = editor.CreateCombo("Ahri", "Ahri combo");
        var garenDraft = editor.CreateCombo("Garen", "Garen combo");

        editor.BindHotkey(ahriDraft.Id, "A");
        editor.BindHotkey(garenDraft.Id, "A");

        Assert.Equal(ahriDraft.Id, config.Get("hotkeys.comboSlots." + ComboEditor.ComposeSlotKey("A", "Ahri")));
        Assert.Equal(garenDraft.Id, config.Get("hotkeys.comboSlots." + ComboEditor.ComposeSlotKey("A", "Garen")));
        config.Dispose();
    }

    [Fact]
    public void LoadPalette_IncludesExtraMultiFormSlots_Jayce()
    {
        // M22 Phase 2: curated extra slots (Jayce cannon form QCannon/WCannon in Jayce.json) must be
        // offered as selectable palette nodes so the user can build cross-form combos. A dummy Jayce
        // in the repo (no BIN skills) is enough — the extra slots come from the deployed Jayce.json,
        // read via SkillDamageDb.GetCuratedSlotKeys.
        SkillDamageDb.ResetForTests();
        ChampionRepository.Initialize(new[] { new ChampionData { Id = "Jayce", Name = "Jayce" } });

        var palette = ComboEditor.LoadPalette("Jayce");

        var cannonQ = palette.AvailableNodes.FirstOrDefault(n => n.Id == "QCannon");
        Assert.NotNull(cannonQ);
        Assert.Equal("Q (Cannon)", cannonQ!.Name);
        Assert.Equal(ComboDamageType.Physical, cannonQ.DamageType);
        Assert.Contains(palette.AvailableNodes, n => n.Id == "WCannon");

        SkillDamageDb.ResetForTests();
    }

    [Fact]
    public void SplitSlotKey_RoundTripsComposeSlotKey()
    {
        var (hotkey, championId) = ComboEditor.SplitSlotKey(ComboEditor.ComposeSlotKey("Alt+1", "Ahri"));
        Assert.Equal("Alt+1", hotkey);
        Assert.Equal("Ahri", championId);
    }

    [Fact]
    public void SplitSlotKey_LegacyFlatKey_DegradesToEmptyChampionId()
    {
        // Pre-loop-44 saved config may still have flat "hotkeys.comboSlots.Alt+1" entries with no
        // "::championId" segment at all — must not throw, just fail to scope (see ComboEditor's
        // SplitSlotKey doc-comment: such entries are orphaned, not crash-inducing).
        var (hotkey, championId) = ComboEditor.SplitSlotKey("Alt+1");
        Assert.Equal("Alt+1", hotkey);
        Assert.Equal(string.Empty, championId);
    }

    [Fact]
    public void MigrateLegacyHotkeyBindings_RewritesFlatKeyToComposite_AndClearsFlat()
    {
        // A pre-upgrade flat binding "hotkeys.comboSlots.Alt+1 -> comboId" (no champion segment)
        // must be recovered into "{Alt+1}::{champion}" using the bound combo's own saved champion,
        // and the orphaned flat key cleared.
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "c");
        AddFive(editor, draft.Id);
        editor.SaveCombo(draft.Id); // persists combos.saved.{id} with ChampionId="Ahri"

        // Simulate a legacy flat binding written by a pre-loop-44 build.
        config.Set("hotkeys.comboSlots.Alt+1", draft.Id);

        int acted = editor.MigrateLegacyHotkeyBindings();

        Assert.Equal(1, acted);
        Assert.Equal(draft.Id, config.Get("hotkeys.comboSlots." + ComboEditor.ComposeSlotKey("Alt+1", "Ahri")));
        Assert.Null(config.Get("hotkeys.comboSlots.Alt+1"));

        // Idempotent: a second pass finds no flat keys left to act on.
        Assert.Equal(0, editor.MigrateLegacyHotkeyBindings());
        config.Dispose();
    }

    [Fact]
    public void MigrateLegacyHotkeyBindings_DropsDanglingFlatKey_AndLeavesCompositeUntouched()
    {
        var editor = NewEditor(out var config);
        var ahri = editor.CreateCombo("Ahri", "c");
        AddFive(editor, ahri.Id);
        editor.SaveCombo(ahri.Id);

        // A valid composite binding (already-migrated) must be preserved as-is.
        editor.BindHotkey(ahri.Id, "A"); // writes A::Ahri
        // A dangling flat key whose combo id was never saved -> should just be dropped.
        config.Set("hotkeys.comboSlots.Ctrl+9", "no-such-combo-id");

        int acted = editor.MigrateLegacyHotkeyBindings();

        Assert.Equal(1, acted); // only the dangling flat key
        Assert.Null(config.Get("hotkeys.comboSlots.Ctrl+9"));
        Assert.Equal(ahri.Id, config.Get("hotkeys.comboSlots." + ComboEditor.ComposeSlotKey("A", "Ahri")));
        config.Dispose();
    }

    // ---------------------------------------------------------------- save + UI.COMBO_SAVED

    [Fact]
    public void SaveCombo_PublishesUiComboSaved()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");
        AddFive(editor, draft.Id);

        ComboSavedPayload? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_SAVED", evt =>
        {
            received = evt.Payload as ComboSavedPayload;
            gate.Set();
        });

        editor.SaveCombo(draft.Id);

        // UI.* is dispatched async on the bus's worker thread — wait for delivery.
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_SAVED was not delivered");
        Assert.NotNull(received);
        Assert.Equal(draft.Id, received!.ComboId);
        Assert.Equal("Ahri", received.ChampionId);
        config.Dispose();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFiveNodeCombo_AcrossRestart()
    {
        // Simulates a real app restart: editor1 saves via ConfigManager A, A is disposed
        // (flushing to disk), then a SECOND ConfigManager B is constructed on the SAME file
        // path and editor2 reconstructs the combo purely from B's reloaded store. This proves
        // combos.saved.* now survives the M14 typed-schema round-trip (the combos section was
        // added to ConfigSchema), not just an in-memory shared instance.
        ComboGraph savedGraph;
        string comboId;
        using (var configA = new ConfigManager(_configPath))
        {
            var editor1 = new ComboEditor(new ComboEngine(new DamageEngine(), new RuneEngine()), configA);
            var draft = editor1.CreateCombo("Ahri", "C");
            AddFive(editor1, draft.Id);
            savedGraph = editor1.GetCombo(draft.Id).Graph;
            comboId = draft.Id;
            editor1.SaveCombo(draft.Id);
            // Dispose flushes any pending debounced write synchronously.
        }

        using var configB = new ConfigManager(_configPath);
        var editor2 = new ComboEditor(new ComboEngine(new DamageEngine(), new RuneEngine()), configB);
        var loaded = editor2.LoadCombo(comboId);

        Assert.Equal(savedGraph.Nodes, loaded.Graph.Nodes);
        Assert.Equal(savedGraph.Edges, loaded.Graph.Edges);
    }

    // ---------------------------------------------------------------- preview (reuses M03/M05)

    [Fact]
    public void PreviewCombo_ReusesEngine_ReturnsDamage()
    {
        var editor = NewEditor(out var config);
        var draft = editor.CreateCombo("Ahri", "C");
        editor.AddNode(draft.Id, Node("Q", damage: 100));

        var attacker = new AttackerStat(Ad: 60, BonusAD: 40, Ap: 30, Level: 11, CriticalChance: 0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 500, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var context = new ExecutionContext("Ahri", attacker, defender, new UserRuneConfig(Array.Empty<string>()));

        var result = editor.PreviewCombo(draft.Id, context);

        Assert.Equal(100.0, result.TotalDamage, 2); // flat 100, armor 0 => mitigated 100
        Assert.Single(result.NodeBreakdown);
        config.Dispose();
    }

    // ---------------------------------------------------------------- NodePalette (M11 load)

    [Fact]
    public void LoadPalette_LoadsSkillsPlusDefaultAutoAttack()
    {
        ChampionRepository.Initialize(new[]
        {
            new ChampionData
            {
                Id = "Ahri",
                Name = "Ahri",
                Skills = new Dictionary<string, SkillData>
                {
                    ["Q"] = new SkillData { Key = "Q", Name = "Orb of Deception", Cooldown = new[] { 7.0 }, Mana = new[] { 55.0 }, DamageType = DamageType.MAGIC },
                    ["W"] = new SkillData { Key = "W", Name = "Fox-Fire", Cooldown = new[] { 9.0 }, Mana = new[] { 40.0 }, DamageType = DamageType.MAGIC },
                },
            },
        });

        var palette = ComboEditor.LoadPalette("Ahri");

        Assert.Equal("Ahri", palette.ChampionId);
        Assert.Contains(palette.AvailableNodes, n => n.Id == "Q" && n.Name == "Orb of Deception" && n.DamageType == ComboDamageType.Magic);
        Assert.Contains(palette.AvailableNodes, n => n.Id == "AA" && n.NodeType == ComboNodeType.Aa);
        // Q cooldown/mana carried from M11 rank-1 values.
        var q = palette.AvailableNodes.Single(n => n.Id == "Q");
        Assert.Equal(7.0, q.Cooldown);
        Assert.Equal(55.0, q.Mana);
    }

    [Fact]
    public void LoadPalette_UnknownChampion_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => ComboEditor.LoadPalette("Nobody"));
    }
}
