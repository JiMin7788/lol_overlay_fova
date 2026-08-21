using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof of the defender-side "virtual model" (loop 38 continuation 19): capturing the
/// currently-resolved LIVE target's stats into <see cref="TargetSnapshotStore"/> via
/// <see cref="ComboRunner.CaptureTargetSnapshot"/>, and the per-combo <c>useSnapshot</c> toggle that
/// makes <see cref="ComboRunner"/> substitute that frozen snapshot for the live-resolved defender
/// ONLY when explicitly turned on (CLAUDE.md Policy P2: default OFF, live resolution unchanged
/// otherwise).
///
/// Kept in its own file (mirrors <c>ComboRunnerTests.cs</c>'s helper shapes rather than editing that
/// shared file) since another agent was concurrently working ComboRunner's rune-loading method this
/// round — see the Agent Report for the isolation rationale.
/// </summary>
public class ComboRunnerTargetSnapshotTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ComboRunnerTargetSnapshotTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ItemRepository.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "ComboRunnerTargetSnapshotTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static ChampionData Champ(string id, double hp, double armor, double mr, double ad) => new()
    {
        Id = id,
        Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr, Ad = ad },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    /// <summary>Active player "Me"/Ahri (ORDER) plus one living enemy "Foe"/Zed (CHAOS) — same shape
    /// as <c>ComboRunnerTests.BuildSnapshot</c>. <paramref name="hasEnemy"/> false drops the enemy
    /// row entirely (no living target resolvable) for the "capture with no real target" case.</summary>
    private static GameSnapshot BuildSnapshot(double attackDamage, bool hasEnemy = true)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = 1,
            PlayerCount = hasEnemy ? 2 : 1,
            Stats = new ActivePlayerStats { AttackDamage = attackDamage, AbilityPower = 0 },
        };

        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = "Ahri";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 1;
        snap.Players[0].IsDead = false;

        if (hasEnemy)
        {
            snap.Players[1].SummonerName = "Foe";
            snap.Players[1].ChampionName = "Zed";
            snap.Players[1].Team = "CHAOS";
            snap.Players[1].Level = 1;
            snap.Players[1].IsDead = false;
        }

        return snap;
    }

    private static string SaveOneNodeCombo(ComboEditor editor, double ratioAd)
    {
        var draft = editor.CreateCombo("Ahri", "C");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "Q", NodeType: ComboNodeType.Skill, Name: "Q", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: ratioAd, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);
        return draft.Id;
    }

    private static (ComboEngine Engine, ComboEditor Editor) BuildEngineAndEditor(ConfigManager config)
    {
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        return (engine, new ComboEditor(engine, config));
    }

    // (a) Capturing a snapshot from a resolved live target persists the right values.

    [Fact]
    public void CaptureTargetSnapshot_RealTargetResolved_PersistsExpectedArmorMrMaxHp()
    {
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var (engine, editor) = BuildEngineAndEditor(config);
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);

        var snap = BuildSnapshot(100.0);
        using var runner = new ComboRunner(engine, config, () => snap);

        bool ok = runner.CaptureTargetSnapshot(comboId);

        Assert.True(ok);
        var saved = TargetSnapshotStore.Load(config, comboId);
        Assert.NotNull(saved);
        Assert.Equal("Zed", saved!.ChampionName);
        Assert.Equal(654, saved.MaxHp, 2); // Zed level-1 HP, no per-level growth in this fixture
        Assert.Equal(32, saved.Armor, 2);
        Assert.Equal(29, saved.Mr, 2);
    }

    // (d) Capturing with no real target resolved does nothing (no fake snapshot saved).

    [Fact]
    public void CaptureTargetSnapshot_NoLivingEnemy_ReturnsFalse_SavesNothing()
    {
        ChampionRepository.Initialize(new[] { Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53) });

        using var config = new ConfigManager(_configPath);
        var (engine, editor) = BuildEngineAndEditor(config);
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);

        var snap = BuildSnapshot(100.0, hasEnemy: false); // no living enemy at all
        using var runner = new ComboRunner(engine, config, () => snap);

        bool ok = runner.CaptureTargetSnapshot(comboId);

        Assert.False(ok);
        Assert.Null(TargetSnapshotStore.Load(config, comboId));
    }

    [Fact]
    public void CaptureTargetSnapshot_TargetChampionUnresolvable_FallsBackDefender_ReturnsFalse_SavesNothing()
    {
        // Enemy row resolves fine (a real name) but that champion is outside BOTH ChampionRepository
        // and the Data Dragon summary — BuildDefenderFor falls back to the zeroed FallbackDefender.
        // Capturing must treat this exactly like "no real target": never persist FallbackDefender's
        // fake 0/0/1000 as if it were a real captured target.
        ChampionRepository.Initialize(new[] { Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53) });

        using var config = new ConfigManager(_configPath);
        var (engine, editor) = BuildEngineAndEditor(config);
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);

        var snap = BuildSnapshot(100.0);
        snap.Players[1].ChampionName = "TotallyNotARealChampionXYZ";
        using var runner = new ComboRunner(engine, config, () => snap);

        bool ok = runner.CaptureTargetSnapshot(comboId);

        Assert.False(ok);
        Assert.Null(TargetSnapshotStore.Load(config, comboId));
    }

    // (b) With the toggle ON and a snapshot present, BuildContext's defender matches the snapshot
    //     instead of live resolution.

    [Fact]
    public void ComboTrigger_ToggleOn_WithSnapshot_UsesSnapshotDefender_NotLiveTarget()
    {
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63), // live target — must NOT be used
        });

        using var config = new ConfigManager(_configPath);
        var (engine, editor) = BuildEngineAndEditor(config);
        const double totalAd = 100.0;
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);

        // A captured "virtual" target with a very different (much higher) armor than live Zed's 32,
        // so the resulting damage number unambiguously proves which one was used.
        var snapshot = new TargetSnapshot("VirtualTarget", Armor: 200, Mr: 100, MaxHp: 3000, CapturedAtUtcMs: 123);
        TargetSnapshotStore.Save(config, comboId, snapshot);
        TargetSnapshotStore.SetUseSnapshot(config, comboId, true);

        double expected = Math.Round(totalAd * (100.0 / (100.0 + 200.0)), 2); // snapshot's armor 200, not Zed's 32

        var snap = BuildSnapshot(totalAd);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboHudResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = evt.Payload as ComboHudResult; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.Equal(expected, received!.Result.TotalDamage, 2);
        Assert.Equal("VirtualTarget", received.TargetChampion);
        Assert.True(received.UsingSnapshotTarget);
        Assert.False(received.DefenderIsFallback); // an explicit virtual target, not a failed resolution
    }

    // (c) With the toggle OFF, behavior is unchanged (regression-safe).

    [Fact]
    public void ComboTrigger_ToggleOff_WithSnapshotPresent_StillUsesLiveTarget_Unchanged()
    {
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 654, armor: 32, mr: 29, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var (engine, editor) = BuildEngineAndEditor(config);
        const double totalAd = 100.0;
        string comboId = SaveOneNodeCombo(editor, ratioAd: 1.0);

        // A snapshot exists but the toggle is left at its default (never set) — must be ignored.
        var snapshot = new TargetSnapshot("VirtualTarget", Armor: 200, Mr: 100, MaxHp: 3000, CapturedAtUtcMs: 123);
        TargetSnapshotStore.Save(config, comboId, snapshot);
        // TargetSnapshotStore.SetUseSnapshot intentionally NOT called — default OFF.

        double expected = Math.Round(totalAd * (100.0 / (100.0 + 32.0)), 2); // live Zed armor 32, unchanged

        var snap = BuildSnapshot(totalAd);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboHudResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = evt.Payload as ComboHudResult; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.Equal(expected, received!.Result.TotalDamage, 2);
        Assert.Equal("Zed", received.TargetChampion);
        Assert.False(received.UsingSnapshotTarget);
    }
}
