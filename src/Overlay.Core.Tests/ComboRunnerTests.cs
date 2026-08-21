using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Items;
using Overlay.Core.Overlay;
using Overlay.Core.Runes;
// ComboNode exists in both Overlay.Core.Combo (M03/M04 node) and Overlay.Core.Damage
// (M05 node). This file only ever means the M03/M04 node — alias to resolve CS0104,
// same pattern as ComboEditorTests.
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof that ComboRunner closes the hotkey -> combo -> HUD loop
/// (FINAL_INSPECTION FINDING 1): a COMBO.TRIGGER carrying a saved comboId, against a
/// synthetic live snapshot, resolves the combo, builds an ExecutionContext, runs the
/// M03 engine, and publishes UI.COMBO_RESULT carrying a ComboResult — end to end, with
/// no live game. Also proves graceful handling of an unknown combo and an absent/empty
/// snapshot (no publish, no throw).
///
/// EventBus/ChampionRepository are static/process-wide and ConfigManager persists to
/// disk, so each test resets the static state and uses its own temp config dir — the
/// same isolation pattern as ComboEditorTests. UI.* events are dispatched async on the
/// bus worker thread, so delivery is synchronized with a ManualResetEventSlim.
/// </summary>
public class ComboRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ComboRunnerTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ItemRepository.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "ComboRunnerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── M02/M04/M13 toggle-off test double (no WPF window needed) ────────────────

    private sealed class FakeOverlayView : IOverlayView
    {
        public List<HUDPayload> Shown { get; } = new();
        public List<string> Hidden { get; } = new();
        public void ShowHud(HUDPayload payload) => Shown.Add(payload);
        public void HideHud(string hudId) => Hidden.Add(hudId);
        public void OpenDashboard() { }
        public void CloseDashboard() { }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static ChampionData Champ(string id, double hp, double armor, double mr, double ad) => new()
    {
        Id = id,
        Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr, Ad = ad },
        StatsPerLevel = new ChampionStatsPerLevel(), // level-1 test: per-level growth is 0
    };

    /// <summary>Synthetic snapshot: active player "Me"/Ahri (ORDER) with total AD set, and
    /// one living enemy "Foe"/Zed (CHAOS) at level 1.</summary>
    private static GameSnapshot BuildSnapshot(double attackDamage)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = 1,
            PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = attackDamage, AbilityPower = 0 },
        };

        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = "Ahri";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 1;
        snap.Players[0].IsDead = false;

        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Zed";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 1;
        snap.Players[1].IsDead = false;

        return snap;
    }

    /// <summary>Saves a single-node combo (flat physical damage via RatioAD) through the real
    /// M04 editor so it lands under combos.saved.{id}, exactly where ComboRunner reads it.</summary>
    private static string SaveOneNodeCombo(ComboEditor editor, double ratioAd)
    {
        var draft = editor.CreateCombo("Ahri", "C");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "Q",
            NodeType: ComboNodeType.Skill,
            Name: "Q",
            Cooldown: 0,
            Mana: 0,
            Damage: 0,
            DamageType: ComboDamageType.Physical,
            RatioAD: ratioAd,
            RatioBonusAD: 0,
            RatioAP: 0,
            CastTime: 0,
            Delay: 0,
            TravelTime: 0));
        editor.SaveCombo(draft.Id);
        return draft.Id;
    }

    /// <summary>A zero-damage combo node of the given id/type — enough to drive command-label
    /// derivation without needing real damage data.</summary>
    private static ComboNode Node(string id, ComboNodeType type, string name) => new(
        Id: id, NodeType: type, Name: name, Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    // ── tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComboTrigger_ComputesAndPublishesComboResult_EndToEnd()
    {
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        // Node damage = RatioAD(1.0) * total AD(100) = 100, physical, mitigated by Zed's
        // level-1 armor(32): 100 * 100/(100+32) = 75.76 (DamageEngine rounds to 2 dp).
        const double totalAd = 100.0;
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);
        double expected = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2);

        var snap = BuildSnapshot(totalAd);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt =>
        {
            received = (evt.Payload as ComboHudResult)?.Result;
            gate.Set();
        });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.Equal(expected, received!.TotalDamage, 2);
        Assert.Single(received.NodeBreakdown);
    }

    [Fact]
    public void ComboTrigger_MatchesActiveByRiotId_WhenSummonerNameEmpty_AppliesEnemyArmor()
    {
        // Regression: the modern Live Client API leaves allPlayers[].summonerName empty and
        // identifies players by riotId ("Name#TAG"). The old Ordinal summonerName match then
        // failed → active=null → FallbackDefender (0 armor) → NO mitigation on any skill (the
        // "방어력 미반영" bug). With riotId matching the enemy's armor must be applied.
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);
        double expected = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2); // Zed armor 32 applied

        // API-shaped snapshot: identity carried ONLY by riotId, summonerName empty on every row.
        var snap = BuildSnapshot(totalAd);
        snap.ActivePlayerSummonerName = string.Empty;
        snap.ActivePlayerRiotId = "Me#KR1";
        snap.Players[0].SummonerName = string.Empty;
        snap.Players[0].RiotId = "Me#KR1";
        snap.Players[1].SummonerName = string.Empty;
        snap.Players[1].RiotId = "Foe#KR1";

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.Equal(expected, received!.TotalDamage, 2); // mitigated, not the naked 100
    }

    [Fact]
    public void ComboTrigger_AutoAttackNode_DealsTotalAd_MitigatedByArmor()
    {
        // Regression: the palette's auto-attack template carries 0 damage; ComboRunner must
        // fill it with total AD as physical (the "기본공격 = 0" bug).
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        var draft = editor.CreateCombo("Ahri", "AA");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA", Cooldown: 0, Mana: 0,
            Damage: 0, DamageType: ComboDamageType.Physical,
            RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);

        double expected = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2); // total AD, Zed armor 32

        var snap = BuildSnapshot(totalAd);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.Equal(expected, received!.TotalDamage, 2); // not 0
    }

    [Fact]
    public void ComboTrigger_PublishesComboHudResult_WithCommandLabelAndTarget()
    {
        // T4: the card needs the target champion (for its portrait) and the command string
        // (the built ability sequence) alongside the damage result. A Q / AA / W combo against
        // the synthetic Zed enemy must publish a ComboHudResult carrying "Q-A-W" + "Zed".
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo("Ahri", "QAW");
        editor.AddNode(draft.Id, Node("Q_0", ComboNodeType.Skill, "Q"));
        editor.AddNode(draft.Id, Node("AA_0", ComboNodeType.Aa, "AA"));
        editor.AddNode(draft.Id, Node("W_0", ComboNodeType.Skill, "W"));
        editor.SaveCombo(draft.Id);

        var snap = BuildSnapshot(100);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboHudResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = evt.Payload as ComboHudResult; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.Equal("Q-A-W", received!.CommandLabel);
        Assert.Equal("Zed", received.TargetChampion);
        Assert.NotNull(received.Result);
    }

    [Fact]
    public void ComboTrigger_Sequence_CarriesPerNodeKnobState_ForOverlayVisualLanguage()
    {
        // (M28 §3) The published ComboHudResult.Sequence tags each node with its above-floor knob so the
        // overlay can decorate the chip with no champion branching: a UserAssumed condition assumed MET
        // -> MaxDamage (▲), a charge slider -> Slider(fraction). Order + skip rules mirror CommandLabel.
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo("Ahri", "knobs");
        editor.AddNode(draft.Id, Node("Q_0", ComboNodeType.Skill, "Q") with { UserConditionMet = true });
        editor.AddNode(draft.Id, Node("AA_0", ComboNodeType.Aa, "AA"));
        editor.AddNode(draft.Id, Node("W_0", ComboNodeType.Skill, "W") with { UserDistanceFraction = 0.7 });
        editor.SaveCombo(draft.Id);

        var snap = BuildSnapshot(100);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboHudResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = evt.Payload as ComboHudResult; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);

        var seq = received!.Sequence;
        Assert.NotNull(seq);
        Assert.Equal(new[] { "Q", "A", "W" }, seq!.Select(t => t.Label).ToArray());
        Assert.Equal(ComboKnobShape.MaxDamage, seq[0].Knob);   // Q: condition assumed MET
        Assert.True(seq[0].IsAbility);
        Assert.Equal(ComboKnobShape.None, seq[1].Knob);        // AA: no knob, not an ability chip
        Assert.False(seq[1].IsAbility);
        Assert.Equal(ComboKnobShape.Slider, seq[2].Knob);      // W: charge slider
        Assert.Equal(0.7, seq[2].SliderFraction, 3);
        Assert.Equal(2, received.AssumptionCount);
    }

    [Fact]
    public void ComboTrigger_Sequence_NoKnobs_AllNone_ZeroAssumptions()
    {
        // (M28 §4 regression guard) With no knob set, every token is None and AssumptionCount is 0, so
        // the overlay renders the plain chips byte-identically to before M28 §3 (no badges/chip/hollow).
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo("Ahri", "plain");
        editor.AddNode(draft.Id, Node("Q_0", ComboNodeType.Skill, "Q"));
        editor.AddNode(draft.Id, Node("W_0", ComboNodeType.Skill, "W"));
        editor.SaveCombo(draft.Id);

        var snap = BuildSnapshot(100);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboHudResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = evt.Payload as ComboHudResult; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.All(received!.Sequence!, t => Assert.Equal(ComboKnobShape.None, t.Knob));
        Assert.Equal(0, received.AssumptionCount);
    }

    // ── M02/M04/M13 toggle-off wiring ─────────────────────────────────────────────

    [Fact]
    public void ComboTrigger_SameComboAgain_WhileCardShowing_TogglesOff_ClearsCoordinator()
    {
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);

        var snap = BuildSnapshot(100);
        using var runner = new ComboRunner(engine, config, () => snap);

        var view = new FakeOverlayView();
        using var coordinator = new OverlayCoordinator(view, config);
        coordinator.Start();
        runner.AttachOverlayCoordinator(coordinator);
        runner.Start();

        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", _ => gate.Set());

        // 1st press: computes + shows the card (async UI.* delivery — wait for it).
        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.True(coordinator.IsActive("UI.COMBO_RESULT"));

        // 2nd press of the SAME combo's hotkey while its card is still showing: toggle-off.
        // The clear is a direct (synchronous) coordinator call from within COMBO.TRIGGER's own
        // sync dispatch — no further UI.* publish, so no wait needed.
        gate.Reset();
        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.False(coordinator.IsActive("UI.COMBO_RESULT"));
        Assert.Contains("UI.COMBO_RESULT", view.Hidden);
        Assert.False(gate.Wait(TimeSpan.FromMilliseconds(300)), "toggle-off must not re-trigger a new UI.COMBO_RESULT");
    }

    [Fact]
    public void ComboTrigger_DifferentCombo_WhileCardShowing_ReplacesCard_DoesNotToggleOff()
    {
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboA = SaveOneNodeCombo(editor, ratioAd: 1.0);
        string comboB = SaveOneNodeCombo(editor, ratioAd: 2.0);

        var snap = BuildSnapshot(100);
        using var runner = new ComboRunner(engine, config, () => snap);

        var view = new FakeOverlayView();
        using var coordinator = new OverlayCoordinator(view, config);
        coordinator.Start();
        runner.AttachOverlayCoordinator(coordinator);
        runner.Start();

        int publishCount = 0;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", _ => { Interlocked.Increment(ref publishCount); gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboA, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(coordinator.IsActive("UI.COMBO_RESULT"));

        gate.Reset();
        EventBus.EventBus.Publish("COMBO.TRIGGER", comboB, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "a different combo must still trigger normally, not toggle off");

        Assert.Equal(2, publishCount);
        Assert.True(coordinator.IsActive("UI.COMBO_RESULT")); // replaced in place, still showing
    }

    [Fact]
    public void ComboTrigger_UnknownComboId_NoPublish_NoThrow()
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());

        var snap = BuildSnapshot(100);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", _ => gate.Set());

        EventBus.EventBus.Publish("COMBO.TRIGGER", "no-such-combo", "M13");

        Assert.False(gate.Wait(TimeSpan.FromMilliseconds(500)), "no result should be published for an unknown combo");
    }

    [Fact]
    public void ComboTrigger_NullSnapshot_NoPublish_NoThrow()
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);

        using var runner = new ComboRunner(engine, config, () => null);
        runner.Start();

        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", _ => gate.Set());

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.False(gate.Wait(TimeSpan.FromMilliseconds(500)), "no result should be published without an active game");
    }

    [Fact]
    public void ComboTrigger_SnapshotWithoutData_NoPublish_NoThrow()
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);

        var empty = new GameSnapshot { HasData = false };
        using var runner = new ComboRunner(engine, config, () => empty);
        runner.Start();

        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", _ => gate.Set());

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.False(gate.Wait(TimeSpan.FromMilliseconds(500)), "no result should be published when the snapshot has no data");
    }

    // ── T6: target selection (override → same-position → first-living) ─────────────

    /// <summary>Builds a scenario: active player (Ahri/ORDER) at <paramref name="activePosition"/>
    /// plus the given CHAOS enemies in order. Returns the snapshot and the active row.</summary>
    private static (GameSnapshot Snap, ScoreboardEntry Active) Scenario(
        string activePosition, params (string Champ, string Position, bool Dead)[] enemies)
    {
        var snap = new GameSnapshot { HasData = true, PlayerCount = 1 + enemies.Length };
        snap.Players[0].ChampionName = "Ahri";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Position = activePosition;
        snap.Players[0].IsDead = false;
        for (int i = 0; i < enemies.Length; i++)
        {
            var e = snap.Players[i + 1];
            e.ChampionName = enemies[i].Champ;
            e.Team = "CHAOS";
            e.Position = enemies[i].Position;
            e.IsDead = enemies[i].Dead;
        }
        return (snap, snap.Players[0]);
    }

    [Fact]
    public void ResolveTarget_ManualOverride_PicksNamedEnemy_EvenWhenSamePositionExists()
    {
        // Active is MIDDLE; Zed is the same-position enemy, but the user manually chose Jinx.
        var (snap, active) = Scenario("MIDDLE",
            ("Zed", "MIDDLE", false),
            ("Jinx", "BOTTOM", false));

        var target = ComboRunner.ResolveTarget(snap, active, TargetMode.Manual, "Jinx");

        Assert.Equal("Jinx", target?.ChampionName); // override wins over the same-position Zed
    }

    [Fact]
    public void ResolveTarget_Auto_PrefersSamePositionEnemy_OverFirstLiving()
    {
        // Positions known: Jinx is listed first, but Zed shares the active player's MIDDLE lane.
        var (snap, active) = Scenario("MIDDLE",
            ("Jinx", "BOTTOM", false),
            ("Zed", "MIDDLE", false));

        var target = ComboRunner.ResolveTarget(snap, active, TargetMode.Auto, "");

        Assert.Equal("Zed", target?.ChampionName); // same-position beats first-living
    }

    [Fact]
    public void ResolveTarget_Auto_FallsBackToFirstLiving_WhenPositionsEmpty()
    {
        // Practice tool / ARAM: the API leaves position "" → same-position rule can't apply.
        var (snap, active) = Scenario("",
            ("Jinx", "", false),
            ("Zed", "", false));

        var target = ComboRunner.ResolveTarget(snap, active, TargetMode.Auto, "");

        Assert.Equal("Jinx", target?.ChampionName); // first living enemy
    }

    [Fact]
    public void ResolveTarget_ManualOverride_DeadNamedEnemy_StaysPinned_NoSurvivorCondition()
    {
        // (loop 119) HARD pin, NO survivor condition: the manually chosen Zed is DEAD but STAYS the
        // target — we do NOT retarget Jinx and do NOT clear. Zed's scoreboard row keeps live level/items
        // while dead, so the defender stats compute from that last-known state (user request: no "적
        // 사망 초기화", keep calculating from saved stats). The pin only clears if Zed leaves the game.
        var (snap, active) = Scenario("MIDDLE",
            ("Zed", "MIDDLE", true),   // named (pinned) target, dead — still the target
            ("Jinx", "BOTTOM", false));

        var target = ComboRunner.ResolveTarget(snap, active, TargetMode.Manual, "Zed");

        Assert.Equal("Zed", target?.ChampionName); // stays pinned to the dead Zed, NOT Jinx
    }

    // ── M02 loop 38 continuation 12: combo-overlay click-to-target (NextLivingEnemy) ──────────

    /// <summary>Wires the active player's identity so <c>ComboRunner.ResolveActive</c> (which
    /// <see cref="ComboRunner.NextLivingEnemy"/> calls internally, unlike <c>ResolveTarget</c>
    /// which takes the resolved active row directly) can find <see cref="Scenario"/>'s Players[0]
    /// row. <c>Scenario</c> itself never sets SummonerName/ActivePlayerSummonerName because every
    /// OTHER existing test passes <c>active</c> straight into <c>ResolveTarget</c>, bypassing
    /// <c>ResolveActive</c> entirely.</summary>
    private static GameSnapshot WithActiveIdentity(GameSnapshot snap)
    {
        snap.Players[0].SummonerName = "Ahri";
        snap.ActivePlayerSummonerName = "Ahri";
        return snap;
    }

    [Fact]
    public void NextLivingEnemy_CyclesToTheNextLivingEnemy_Wrapping()
    {
        var (snap, _) = Scenario("MIDDLE",
            ("Zed", "MIDDLE", false),
            ("Jinx", "BOTTOM", false),
            ("Ahri", "TOP", false));
        WithActiveIdentity(snap);

        Assert.Equal("Jinx", ComboRunner.NextLivingEnemy(snap, "Zed"));
        Assert.Equal("Ahri", ComboRunner.NextLivingEnemy(snap, "Jinx"));
        Assert.Equal("Zed", ComboRunner.NextLivingEnemy(snap, "Ahri")); // wraps back to the first
    }

    [Fact]
    public void NextLivingEnemy_SkipsDeadEnemies()
    {
        var (snap, _) = Scenario("MIDDLE",
            ("Zed", "MIDDLE", false),
            ("Jinx", "BOTTOM", true), // dead: never offered as a click-to-target destination
            ("Ahri", "TOP", false));
        WithActiveIdentity(snap);

        Assert.Equal("Ahri", ComboRunner.NextLivingEnemy(snap, "Zed"));
    }

    [Fact]
    public void NextLivingEnemy_UnknownCurrentTarget_StartsFromFirstLivingEnemy()
    {
        var (snap, _) = Scenario("MIDDLE",
            ("Zed", "MIDDLE", false),
            ("Jinx", "BOTTOM", false));
        WithActiveIdentity(snap);

        // "Teemo" isn't in the living-enemy list at all (stale/unknown name) -> start from index 0.
        Assert.Equal("Zed", ComboRunner.NextLivingEnemy(snap, "Teemo"));
    }

    [Fact]
    public void NextLivingEnemy_NoLivingEnemies_ReturnsNull()
    {
        var (snap, _) = Scenario("MIDDLE", ("Zed", "MIDDLE", true)); // only enemy is dead
        WithActiveIdentity(snap);

        Assert.Null(ComboRunner.NextLivingEnemy(snap, "Zed"));
    }

    [Fact]
    public void NextLivingEnemy_NoActivePlayerRow_ReturnsNull()
    {
        // Active identity deliberately NOT wired (mirrors "Me" not found on the scoreboard) ->
        // ResolveActive returns null -> NextLivingEnemy must degrade to null, not throw.
        var (snap, _) = Scenario("MIDDLE", ("Zed", "MIDDLE", false));

        Assert.Null(ComboRunner.NextLivingEnemy(snap, "Zed"));
    }

    [Fact]
    public void Targeting_ModeAndManualTarget_PersistAcrossConfigReload()
    {
        // E2-style cross-restart check: a Manual choice must survive the typed ConfigSchema
        // round-trip (unschema'd keys are dropped), so it needs its schema home in TargetingConfig.
        using (var a = new ConfigManager(_configPath))
        {
            a.Set("targeting.mode", "Manual");
            a.Set("targeting.manualTarget", "Zed");
        } // dispose flushes the debounced write

        using var b = new ConfigManager(_configPath);
        Assert.Equal("Manual", b.Get("targeting.mode"));
        Assert.Equal("Zed", b.Get("targeting.manualTarget"));
    }

    // ── Rune selection wiring (activates the previously-dormant M06 rune damage math) ──────

    /// <summary>Cheap Shot (8126): hand-verified level-scaled TRUE-damage flat rune with no
    /// AD/AP ratio — RuneEngineTests already proves the exact formula math; these tests only
    /// prove ComboRunner plumbs a REAL persisted selection + manual-flag state into it. At
    /// level 1 the linear interpolation (baseAtLevel1=10, baseAtLevel18=49.12) is exactly 10.</summary>
    private const string CheapShotRuneId = "8126";

    private static string SaveOneNodeComboForChampion(ComboEditor editor, string championId, double ratioAd)
    {
        var draft = editor.CreateCombo(championId, "C");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "Q",
            NodeType: ComboNodeType.Skill,
            Name: "Q",
            Cooldown: 0,
            Mana: 0,
            Damage: 0,
            DamageType: ComboDamageType.Physical,
            RatioAD: ratioAd,
            RatioBonusAD: 0,
            RatioAP: 0,
            CastTime: 0,
            Delay: 0,
            TravelTime: 0));
        editor.SaveCombo(draft.Id);
        return draft.Id;
    }

    [Fact]
    public void ComboTrigger_UsesPersistedRuneSelection_AndArmedManualFlag_AddsRuneDamage()
    {
        ChampionRepository.Initialize(new[]
        {
            Champ("Aatrox", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = CheapShotRuneId, Name = "Cheap Shot", Tree = "Domination", EffectFormula = null, ApiTrackable = false },
        });

        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        var snap = BuildSnapshot(totalAd);
        snap.Players[0].ChampionName = "Aatrox"; // combo champion = active player's champion

        string comboId = SaveOneNodeComboForChampion(editor, "Aatrox", ratioAd: 1.0);

        // Persist via the SAME store the M04 rune panel writes to: Cheap Shot selected AND its
        // manual-activation checkbox explicitly checked ON.
        RuneSelectionStore.Save(config, new RuneSelection(
            "Aatrox", new[] { CheapShotRuneId }, new Dictionary<string, bool> { [CheapShotRuneId] = true }));

        // The SAME runeEngine instance is shared between the ComboEngine (Execute reads its
        // manual flags) and the ComboRunner (arms them) — exactly the AppComposition wiring.
        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);

        double skillDamage = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2); // Zed armor 32, physical
        double expected = Math.Round(skillDamage + 10.0, 2); // + Cheap Shot's level-1 TRUE flat (unmitigated)

        Assert.Equal(expected, received!.TotalDamage, 2);
        Assert.Equal(2, received.NodeBreakdown.Count);
        Assert.Contains(received.NodeBreakdown, b => b.NodeId == $"rune#{CheapShotRuneId}");
    }

    [Fact]
    public void ComboTrigger_RuneSelected_ButManualFlagNeverChecked_StaysInert_NoBonusDamage()
    {
        // CLAUDE.md Policy P2 / RuneEngine's own Policy Compliance Checklist item 1, proved through
        // the REAL execution path (not just RuneEngine in isolation, which RuneEngineTests already
        // covers): a rune the user selected but never checked ON must contribute nothing.
        ChampionRepository.Initialize(new[]
        {
            Champ("Aatrox", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = CheapShotRuneId, Name = "Cheap Shot", Tree = "Domination", EffectFormula = null, ApiTrackable = false },
        });

        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        var snap = BuildSnapshot(totalAd);
        snap.Players[0].ChampionName = "Aatrox";

        string comboId = SaveOneNodeComboForChampion(editor, "Aatrox", ratioAd: 1.0);

        // Selected but ManualFlags is empty — the default/untouched checkbox state, exactly what a
        // freshly-saved selection has before the user checks anything.
        RuneSelectionStore.Save(config, new RuneSelection(
            "Aatrox", new[] { CheapShotRuneId }, new Dictionary<string, bool>()));

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);

        double expected = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2); // skill node ONLY
        Assert.Equal(expected, received!.TotalDamage, 2);
        Assert.Single(received.NodeBreakdown); // no rune bonus node — never auto-activated
    }

    // ── Live rune auto-load (loop 38 continuation 19): GameSnapshot.Stats.EquippedRuneIds, ────
    // ── when present, is now the PREFERRED source for WHICH of the 8 manual runes are selected;
    // ── RuneSelectionStore.SelectedRuneIds is fallback-only. The per-rune manual ACTIVATION flag
    // ── always still comes from RuneSelectionStore.ManualFlags either way. ─────────────────────

    // Scorch (8237): an ABILITY-DAMAGE-triggered manual rune — its bonus is auto-applied from live
    // equip ONLY when the combo actually casts an ability (ComboEngine.AbilityDamageTriggeredManualRuneIds
    // + the Skill-node gate). The one-node combo here IS a Skill node, so it applies. Magic damage.
    private const string ScorchRuneId = "8237";

    [Fact]
    public void ComboTrigger_LiveEquippedAbilityTriggeredRune_AutoSelectsAndApplies_WithAbilityInCombo()
    {
        // Scorch is NOT in the persisted SelectedRuneIds (empty) — proving the live EquippedRuneIds path
        // drove the SELECTION. It is an ability-damage rune and the combo casts an ability (Skill node),
        // so the condition is genuinely met and its bonus applies. (loop 170 — replaces the old
        // Cheap-Shot version of this test, which relied on the now-removed "equip alone auto-applies any
        // manual rune" behavior; Cheap Shot's CC trigger is unobservable and no longer auto-armed.)
        ChampionRepository.Initialize(new[]
        {
            Champ("Aatrox", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = ScorchRuneId, Name = "Scorch", Tree = "Sorcery", EffectFormula = null, ApiTrackable = false },
        });

        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        var snap = BuildSnapshot(totalAd);
        snap.Players[0].ChampionName = "Aatrox";
        // Live Client reports Scorch (8237) equipped this game, plus an untracked rune id
        // (not one of the 8) that must be ignored.
        snap.Stats.EquippedRuneIds = new[] { 8237, 9999 };

        string comboId = SaveOneNodeComboForChampion(editor, "Aatrox", ratioAd: 1.0);

        // Persisted SelectedRuneIds intentionally empty — selection must come from the live path.
        RuneSelectionStore.Save(config, new RuneSelection(
            "Aatrox", Array.Empty<string>(), new Dictionary<string, bool>()));

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);

        // Structural assertion (avoids re-deriving Scorch's exact magic-mitigated value here — its
        // formula math is already pinned in RuneEngineTests): the rune node is present and it added
        // damage on top of the plain skill hit.
        double skillDamage = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2);
        Assert.Contains(received!.NodeBreakdown, b => b.NodeId == $"rune#{ScorchRuneId}");
        Assert.True(received.TotalDamage > skillDamage, $"expected rune bonus on top of {skillDamage}, got {received.TotalDamage:0.##}");
    }

    [Fact]
    public void ComboTrigger_LiveEquippedNonDetectableRune_NotAutoAppliedFromEquip_Loop170()
    {
        // loop 170 (user report: rune effects applied to combos that DON'T meet the rune's condition).
        // Cheap Shot's real trigger — target already crowd-controlled — is NOT observable from the combo
        // or any public API, so a live-equipped Cheap Shot is NO LONGER auto-applied from mere equip
        // (that blanket "equipped -> always one extra hit" was the bug). It now requires an explicit user
        // confirmation (persisted manual flag). With none set here, the combo is exactly the plain skill
        // hit and NO rune#8126 node appears. (Supersedes the old loop-48 "equip alone applies" test.)
        ChampionRepository.Initialize(new[]
        {
            Champ("Aatrox", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = CheapShotRuneId, Name = "Cheap Shot", Tree = "Domination", EffectFormula = null, ApiTrackable = false },
        });

        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        var snap = BuildSnapshot(totalAd);
        snap.Players[0].ChampionName = "Aatrox";
        snap.Stats.EquippedRuneIds = new[] { 8126 }; // Cheap Shot equipped live, but its CC condition is unobservable

        string comboId = SaveOneNodeComboForChampion(editor, "Aatrox", ratioAd: 1.0);

        // No explicit user confirmation (no persisted manual flag) → must NOT auto-apply.

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);

        double skillDamage = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2); // plain skill only, no rune
        Assert.Equal(skillDamage, received!.TotalDamage, 2);
        Assert.DoesNotContain(received.NodeBreakdown, b => b.NodeId == $"rune#{CheapShotRuneId}");
    }

    [Fact]
    public void ComboTrigger_EmptyEquippedRuneIds_FallsBackToPersistedManualPicker_Unchanged()
    {
        // Regression: with no live rune data (EquippedRuneIds empty, e.g. no active game / editor
        // preview), behavior must be byte-for-byte the pre-existing manual-picker-only path.
        ChampionRepository.Initialize(new[]
        {
            Champ("Aatrox", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = CheapShotRuneId, Name = "Cheap Shot", Tree = "Domination", EffectFormula = null, ApiTrackable = false },
        });

        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        var snap = BuildSnapshot(totalAd);
        snap.Players[0].ChampionName = "Aatrox";
        snap.Stats.EquippedRuneIds = Array.Empty<int>(); // no live rune data this tick

        string comboId = SaveOneNodeComboForChampion(editor, "Aatrox", ratioAd: 1.0);

        RuneSelectionStore.Save(config, new RuneSelection(
            "Aatrox", new[] { CheapShotRuneId }, new Dictionary<string, bool> { [CheapShotRuneId] = true }));

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);

        double skillDamage = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2);
        double expected = Math.Round(skillDamage + 10.0, 2);
        Assert.Equal(expected, received!.TotalDamage, 2);
        Assert.Contains(received.NodeBreakdown, b => b.NodeId == $"rune#{CheapShotRuneId}");
    }

    [Fact]
    public void ComboTrigger_LiveEquippedAutoTriggerRune_ReachesEngine_AddsBonus_Loop50()
    {
        // Part 1/2 (loop 50): an equipped API-"trackable" AUTO-TRIGGER rune (Summon Aery, 8214) must
        // now reach ComboEngine's auto-trigger pipeline. Before this change, LoadRuneSelectionAndArm-
        // ManualFlags intersected EquippedRuneIds with ONLY the 8 manual runes, so an equipped
        // auto-trigger rune never entered SelectedRuneIds and ComboEngine's Contains(...) gate never
        // fired — the whole loop-46 auto-rune engine was dormant in live games. Aery fires on >= 1
        // damaging node; its bonus node id is "autorune#8214".
        ChampionRepository.Initialize(new[]
        {
            Champ("Aatrox", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = CheapShotRuneId, Name = "Cheap Shot", Tree = "Domination", EffectFormula = null, ApiTrackable = false },
        });

        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        var snap = BuildSnapshot(100.0);
        snap.Players[0].ChampionName = "Aatrox";
        snap.Stats.EquippedRuneIds = new[] { 8214 }; // Summon Aery equipped live this game

        string comboId = SaveOneNodeComboForChampion(editor, "Aatrox", ratioAd: 1.0);
        // runes.autoApply defaults TRUE (schema default) — no explicit Set needed.

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.Contains(received!.NodeBreakdown, b => b.NodeId == "autorune#8214");
    }

    [Fact]
    public void ComboTrigger_AutoApplyOff_EquippedAutoTriggerRuneSuppressed_Loop50()
    {
        // Part 1/2 toggle (loop 50): with runes.autoApply = false, an equipped auto-trigger rune must
        // NOT be auto-applied — the combo's damage is exactly the plain skill damage, and no
        // "autorune#..." bonus node appears. (Per-node AttachedRuneId force-overrides are a separate
        // path not exercised here.)
        ChampionRepository.Initialize(new[]
        {
            Champ("Aatrox", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = CheapShotRuneId, Name = "Cheap Shot", Tree = "Domination", EffectFormula = null, ApiTrackable = false },
        });

        using var config = new ConfigManager(_configPath);
        config.Set("runes.autoApply", false);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        var snap = BuildSnapshot(totalAd);
        snap.Players[0].ChampionName = "Aatrox";
        snap.Stats.EquippedRuneIds = new[] { 8214 }; // Summon Aery equipped, but auto-apply is OFF

        string comboId = SaveOneNodeComboForChampion(editor, "Aatrox", ratioAd: 1.0);

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        double expected = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2); // plain skill damage vs Zed's 32 armor
        Assert.Equal(expected, received!.TotalDamage, 2);
        Assert.DoesNotContain(received.NodeBreakdown, b => b.NodeId.StartsWith("autorune#"));
    }

    [Fact]
    public void ComboTrigger_NoSavedRuneSelection_BehavesAsBeforeRuneUI_NoBonusDamage()
    {
        // Pre-existing dormant behavior for a champion the user never opened the rune panel for:
        // an empty UserRuneConfig, exactly like before this feature existed.
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);
        double expected = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2);

        var snap = BuildSnapshot(totalAd);
        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.Equal(expected, received!.TotalDamage, 2);
        Assert.Single(received.NodeBreakdown);
    }

    // ── Item build wiring (loop 38 continuation 18: finalizes the "hypothetical build" item ──
    // ── picker into damage calc, additively, mirroring how runes already worked) ────────────

    private const string SyntheticAdItemId = "9999test";

    [Fact]
    public void ComboTrigger_UsesPersistedItemBuild_AddsItemAdOnTopOfLiveStats()
    {
        ChampionRepository.Initialize(new[]
        {
            Champ("Aatrox", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });
        ItemRepository.Initialize(new[]
        {
            new ItemData { Id = SyntheticAdItemId, Name = "Synthetic AD Item", Stats = new ItemStats { Ad = 40 } },
        });

        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        var snap = BuildSnapshot(totalAd);
        snap.Players[0].ChampionName = "Aatrox"; // combo champion = active player's champion

        string comboId = SaveOneNodeComboForChampion(editor, "Aatrox", ratioAd: 1.0);

        // Persist via the SAME store the M04 item picker writes to: a hypothetical +40 AD item.
        ItemBuildStore.Save(config, "Aatrox", new[] { SyntheticAdItemId });

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);

        // Live AD (100) + hypothetical item AD (40) = 140, mitigated by Zed's real armor (32).
        double expected = Math.Round(140.0 * (100.0 / (100.0 + 32.0)), 2);
        Assert.Equal(expected, received!.TotalDamage, 2);
    }

    [Fact]
    public void ComboTrigger_ItemBuildHasUnknownItemId_SkippedGracefully_NoCrashNoBonus()
    {
        // ItemRepository.Get returning null for a saved-but-unresolvable id must be skipped, not
        // crash the trigger and not silently contribute a fabricated stat.
        ChampionRepository.Initialize(new[]
        {
            Champ("Aatrox", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });
        // ItemRepository intentionally left uninitialized/empty — the saved id resolves nowhere.

        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);

        const double totalAd = 100.0;
        var snap = BuildSnapshot(totalAd);
        snap.Players[0].ChampionName = "Aatrox";

        string comboId = SaveOneNodeComboForChampion(editor, "Aatrox", ratioAd: 1.0);
        ItemBuildStore.Save(config, "Aatrox", new[] { "not-a-real-item-id" });

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);

        double expected = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2); // no item bonus
        Assert.Equal(expected, received!.TotalDamage, 2);
    }
}
