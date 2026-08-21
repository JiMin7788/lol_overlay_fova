using System.Threading;
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
/// M24 P9 wiring: <see cref="ExecuteEffectsDb.SelectForAttacker"/> maps an attacker's champion +
/// built items to the execute rules that arm for them (ability/passive by champion, item by id,
/// buff never), so the combo flow can compute a kill-line. Reads the real bundled
/// execute_effects.json.
/// </summary>
public class ExecuteWiringTests
{
    public ExecuteWiringTests() => ExecuteEffectsDb.ResetForTests();

    [Fact]
    public void SelectForAttacker_ByChampion_ArmsThatChampionsAbilityExecute()
    {
        var rules = ExecuteEffectsDb.SelectForAttacker("Urgot", Array.Empty<int>());
        Assert.Contains(rules, r => r.Id == "urgot_r");
        Assert.DoesNotContain(rules, r => r.Id == "collector"); // no item built
    }

    [Fact]
    public void SelectForAttacker_ByBuiltItem_ArmsThatItemExecute()
    {
        var rules = ExecuteEffectsDb.SelectForAttacker("Ahri", new[] { 6676 }); // The Collector
        Assert.Contains(rules, r => r.Id == "collector");
    }

    [Fact]
    public void SelectForAttacker_ChampionAndItem_ArmsBoth()
    {
        var rules = ExecuteEffectsDb.SelectForAttacker("Urgot", new[] { 6676 });
        Assert.Contains(rules, r => r.Id == "urgot_r");
        Assert.Contains(rules, r => r.Id == "collector");
    }

    [Fact]
    public void SelectForAttacker_ChampionMatchIsCaseInsensitive()
    {
        Assert.Contains(ExecuteEffectsDb.SelectForAttacker("uRgOt", Array.Empty<int>()), r => r.Id == "urgot_r");
    }

    [Fact]
    public void SelectForAttacker_NonExecuteChampion_NoItem_ReturnsEmpty()
    {
        Assert.Empty(ExecuteEffectsDb.SelectForAttacker("Ahri", Array.Empty<int>()));
    }

    [Fact]
    public void SelectForAttacker_NeverArmsBuffExecute()
    {
        // The Elder Dragon buff execute has no attacker signal, so no champion/item selection arms it.
        Assert.DoesNotContain(ExecuteEffectsDb.SelectForAttacker("Elder", new[] { 6676 }), r => r.Id == "elder");
    }
}

/// <summary>
/// M24 P9 wiring (end-to-end): a combo through <see cref="ComboRunner"/> surfaces the execute
/// kill-line on <see cref="ComboResult.ExecuteThresholdHP"/>/<see cref="ComboResult.ExecuteRuleLabel"/>
/// — from the attacker's champion (Urgot R = 25% max HP) and from a built item (The Collector = 5%).
/// A stack-gated execute stays off (Stacks unreadable ⇒ 0), and a non-execute champion shows no line.
/// </summary>
public class ExecuteKillLineIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ExecuteKillLineIntegrationTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ExecuteEffectsDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "ExecKillLineTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ChampionRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ChampionData Dummy(string id, double hp = 0) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static GameSnapshot Snapshot(string attacker, int[]? builtItems = null)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 18, PlayerCount = 2,
            Stats = new ActivePlayerStats { AbilityR = 3, EquippedRuneIds = Array.Empty<int>() },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = attacker;
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 18;
        foreach (var it in builtItems ?? Array.Empty<int>()) snap.Players[0].TryAddItem(it);
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 18;
        return snap;
    }

    private ComboResult Run(string attacker, double targetMaxHp, int[]? builtItems = null)
    {
        ChampionRepository.Initialize(new[] { Dummy(attacker), Dummy("Target", hp: targetMaxHp) });
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(attacker, "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "R", NodeType: ComboNodeType.Skill, Name: "R",
            Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
            RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snapshot(attacker, builtItems));
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!;
    }

    [Fact]
    public void UrgotCombo_SurfacesRExecuteKillLine_At25PercentMaxHp()
    {
        var r = Run("Urgot", targetMaxHp: 2000);
        Assert.Equal(0.25 * 2000, r.ExecuteThresholdHP, precision: 2); // 25% of 2000 = 500
        Assert.Equal("우르곳 R", r.ExecuteRuleLabel);
    }

    [Fact]
    public void CollectorItem_OnNonExecuteChampion_SurfacesItemKillLineAt5Percent()
    {
        var r = Run("Ahri", targetMaxHp: 2000, builtItems: new[] { 6676 }); // The Collector
        Assert.Equal(0.05 * 2000, r.ExecuteThresholdHP, precision: 2); // 5% of 2000 = 100
        Assert.Equal("징수의 총", r.ExecuteRuleLabel);
    }

    [Fact]
    public void NonExecuteChampion_NoItem_ShowsNoKillLine()
    {
        var r = Run("Ahri", targetMaxHp: 2000);
        Assert.Equal(0, r.ExecuteThresholdHP);
        Assert.Null(r.ExecuteRuleLabel);
    }

    [Fact]
    public void StackGatedExecute_StaysOff_WhenStacksUnreadable()
    {
        // Syndra R gates on 100 Splinters (unreadable live -> Stacks=0), so no kill-line surfaces
        // despite Syndra being an execute champion. Conservative/honest default.
        var r = Run("Syndra", targetMaxHp: 2000);
        Assert.Equal(0, r.ExecuteThresholdHP);
        Assert.Null(r.ExecuteRuleLabel);
    }
}
