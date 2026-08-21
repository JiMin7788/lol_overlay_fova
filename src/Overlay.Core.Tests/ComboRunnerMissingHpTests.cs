using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Items;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable end-to-end proof that wiring a <see cref="TargetHealthTracker"/> into
/// <see cref="ComboRunner"/> makes a REAL, live-resolved target's defender <c>CurrentHP</c>
/// honestly (lower-bound) reflect damage <see cref="ComboRunner"/> itself already dealt them
/// since their last respawn — triggering the SAME combo against the SAME target twice, with no
/// respawn event in between, must show the second trigger's missing-HP-based damage HIGHER than
/// the first's (the target is "more missing" the second time, because the first trigger's own
/// damage was recorded against it).
///
/// Kept in its own file (mirrors <c>ComboRunnerTargetSnapshotTests.cs</c>'s isolation rationale)
/// rather than editing the large, frequently-touched shared <c>ComboRunnerTests.cs</c>.
/// </summary>
public class ComboRunnerMissingHpTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ComboRunnerMissingHpTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ItemRepository.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "ComboRunnerMissingHpTests_" + Guid.NewGuid().ToString("N"));
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
        StatsPerLevel = new ChampionStatsPerLevel(), // level-1 test: per-level growth is 0
    };

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

    /// <summary>A single node combining a flat RatioAD hit with an ExecuteType.MissingHp bonus
    /// (True damage, to keep the math unmitigated and easy to hand-verify): its damage is
    /// <c>RatioAD * totalAd + ExecutePercent * (target's live missing HP)</c>. Ahri carries no
    /// curated skill data, so ComboRunner.ApplyBinDamage passes this node through unchanged
    /// (verified by the existing ComboRunnerTests flat-damage cases using the same setup).</summary>
    private static string SaveMissingHpCombo(ComboEditor editor, double ratioAd, double executePercent)
    {
        var draft = editor.CreateCombo("Ahri", "C");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "Q", NodeType: ComboNodeType.Skill, Name: "Q", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.True, RatioAD: ratioAd, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0,
            ExecuteType: ComboExecuteType.MissingHp, ExecutePercent: executePercent));
        editor.SaveCombo(draft.Id);
        return draft.Id;
    }

    private static (ComboEngine Engine, ComboEditor Editor) BuildEngineAndEditor(ConfigManager config)
    {
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        return (engine, new ComboEditor(engine, config));
    }

    [Fact]
    public void SecondTrigger_NoRespawnBetween_ShowsHigherMissingHpDamage_ThanFirst()
    {
        // Zed: MaxHP 1000, 0 armor/MR (True damage keeps the math unmitigated regardless).
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 1000, armor: 0, mr: 0, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var (engine, editor) = BuildEngineAndEditor(config);
        const double totalAd = 100.0;
        // ExecutePercent 1.0: the MissingHp bonus adds exactly 1x the target's currently-tracked
        // missing HP as extra True damage on top of the flat RatioAD(1.0)*totalAd(100) = 100 base.
        string comboId = SaveMissingHpCombo(editor, ratioAd: 1.0, executePercent: 1.0);

        var tracker = new TargetHealthTracker();
        var snap = BuildSnapshot(totalAd);
        using var runner = new ComboRunner(engine, config, () => snap, targetHealthTracker: tracker);
        runner.Start();

        double? first = null;
        double? second = null;
        int received = 0;
        using var gate1 = new ManualResetEventSlim(false);
        using var gate2 = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt =>
        {
            if (evt.Payload is not ComboHudResult hud) return;
            received++;
            if (received == 1) { first = hud.Result.TotalDamage; gate1.Set(); }
            else if (received == 2) { second = hud.Result.TotalDamage; gate2.Set(); }
        });

        // Trigger 1: target is untouched (accumulated damage 0) -> missing HP = 0 -> TotalDamage
        // is just the flat 100 base (0 MissingHp bonus).
        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");
        Assert.True(gate1.Wait(TimeSpan.FromSeconds(2)), "first UI.COMBO_RESULT was not delivered");
        Assert.NotNull(first);
        Assert.Equal(100.0, first!.Value, 2); // 1.0*100 base + 1.0*(1000-1000) missing-HP bonus = 100

        // Trigger 2: the SAME target, NO respawn event in between. The first trigger's own 100
        // damage was recorded against Zed, so the tracker now reports 100 accumulated -> the
        // defender's CurrentHP this time is 1000-100=900 -> missing HP = 100 -> a real MissingHp
        // bonus this time (100 base + 1.0*100 bonus = 200), strictly greater than the first.
        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");
        Assert.True(gate2.Wait(TimeSpan.FromSeconds(2)), "second UI.COMBO_RESULT was not delivered");
        Assert.NotNull(second);
        Assert.Equal(200.0, second!.Value, 2);

        Assert.True(second > first, "second trigger's missing-HP-based damage must exceed the first's");
    }

    [Fact]
    public void NoTrackerInjected_BehavesExactlyAsBefore_TargetAlwaysFullHp()
    {
        // Regression guard: a ComboRunner built with no TargetHealthTracker (every pre-existing
        // call site) must keep computing CurrentHP == MaxHP on every trigger, unaffected by this
        // change — i.e. the MissingHp bonus is always 0 regardless of how many times it fires.
        ChampionRepository.Initialize(new[]
        {
            Champ("Ahri", hp: 590, armor: 21, mr: 30, ad: 53),
            Champ("Zed", hp: 1000, armor: 0, mr: 0, ad: 63),
        });

        using var config = new ConfigManager(_configPath);
        var (engine, editor) = BuildEngineAndEditor(config);
        const double totalAd = 100.0;
        string comboId = SaveMissingHpCombo(editor, ratioAd: 1.0, executePercent: 1.0);

        var snap = BuildSnapshot(totalAd);
        using var runner = new ComboRunner(engine, config, () => snap); // no targetHealthTracker
        runner.Start();

        double? last = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt =>
        {
            if (evt.Payload is ComboHudResult hud) { last = hud.Result.TotalDamage; gate.Set(); }
        });

        for (int i = 0; i < 2; i++)
        {
            gate.Reset();
            EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");
            Assert.True(gate.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(100.0, last!.Value, 2); // always full HP -> MissingHp bonus always 0
        }
    }
}
