using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using Overlay.Core.Summoners;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (Combo vs Damage ComboNode)

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN — Ignite (SummonerDot): level-scaled TRUE damage sourced from data/summoner_effects.json.
/// Values are FROZEN FROM THE USER'S IN-GAME MEASUREMENT (M26 §3 — neither Data Dragon nor
/// CommunityDragon exposes a numeric value; both give tooltip placeholders). Measured 2026-07-15:
/// L1=70, L2=90, L3=110, L4=130, L5=150, L6=175, L8=225, L11=300, L13=350, L16=425, L18=475, L20=525.
/// TWO-SEGMENT piecewise linear: L1-5 = 50 + 20×level (+20/level), L6+ = 25 + 25×level (+25/level);
/// no cap at 18 so Arena keeps the level-6+ slope.
///
/// The test pins that (a) the engine reproduces the measured value from the player's live level, and
/// (b) it lands as TRUE damage: a target with 200/200 resists must take the SAME number as a naked one.
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs.
/// </summary>
public class GoldenIgniteTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public GoldenIgniteTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        SummonerEffectDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenIgniteTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ } }

    private static ChampionData Dummy(string id, double hp, double armor, double mr) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static GameSnapshot Snapshot(int level)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = level, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 60, AbilityPower = 0 },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Caster";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = level;
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = level;
        return snap;
    }

    private ComboResult RunIgnite(GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Caster", "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "Ignite_0", NodeType: ComboNodeType.Summoner, Name: "Ignite", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.True, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    [Theory]
    [InlineData(1, 70)]    // seg1: 50 + 20×1
    [InlineData(2, 90)]    // seg1: 50 + 20×2
    [InlineData(3, 110)]   // seg1: 50 + 20×3
    [InlineData(5, 150)]   // seg1: 50 + 20×5 (segment boundary)
    [InlineData(6, 175)]   // seg2: 25 + 25×6 (segment boundary)
    [InlineData(8, 225)]   // seg2: 25 + 25×8
    [InlineData(11, 300)]  // seg2: 25 + 25×11
    [InlineData(13, 350)]  // seg2: 25 + 25×13
    [InlineData(16, 425)]  // seg2: 25 + 25×16
    [InlineData(18, 475)]  // seg2: 25 + 25×18
    [InlineData(20, 525)]  // seg2 extended past L18 (Arena)
    public void Golden_Ignite_LevelScaledTrueDamage(int level, double expected)
    {
        // 200/200 resist target proves TRUE damage takes no mitigation — the number is level-only.
        ChampionRepository.Initialize(new[] { Dummy("Caster", 600, 30, 32), Dummy("Target", 2000, 200, 200) });
        var result = RunIgnite(Snapshot(level));
        Assert.Single(result.NodeBreakdown);
        Assert.Equal(expected, result.TotalDamage, 2);
    }
}
