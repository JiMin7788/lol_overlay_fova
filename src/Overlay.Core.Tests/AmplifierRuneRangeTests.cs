using System.Threading;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// M24 P4 (slice 1): an equipped CONDITIONAL amplifier rune (Coup de Grace / Cut Down / …) lifts only
/// the uncertainty range's CEILING — RangeMax ×= (1+amp) — while RangeMin and TotalDamage stay
/// unamplified (the conservative floor, condition-unmet). No amplifier equipped ⇒ range unchanged.
/// Uses a Dummy Malzahar with a SET 적중시간 knob (so there's damage and no P2 widening), isolating
/// the amp effect. Numbers via the %HP path (no BIN eval needed).
/// </summary>
public class AmplifierRuneRangeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public AmplifierRuneRangeTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "AmplifierRuneRangeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ChampionRepository.ResetForTests();
        SkillDamageDb.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ChampionData Dummy(string id, double hp = 0, double mr = 0) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static GameSnapshot Snapshot(int[]? equippedRunes, double casterCurrentHp = 0, double casterMaxHp = 0)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = 6,
            PlayerCount = 2,
            Stats = new ActivePlayerStats
            {
                EquippedRuneIds = equippedRunes ?? Array.Empty<int>(),
                CurrentHealth = casterCurrentHp,
                MaxHealth = casterMaxHp,
            },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Malzahar";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 6;
        return snap;
    }

    private string RunMalzaharW(int[]? equippedRunes, double maxHp = 2000, double mr = 50, double seconds = 2.5,
        double casterCurrentHp = 0, double casterMaxHp = 0, int[]? assumedEnemyDefensiveRunes = null)
    {
        ChampionRepository.Initialize(new[] { Dummy("Malzahar"), Dummy("Target", hp: maxHp, mr: mr) });
        using var config = new ConfigManager(_configPath);
        if (assumedEnemyDefensiveRunes is { Length: > 0 })
            config.Set("combo.assumedEnemyDefensiveRunes", string.Join(",", assumedEnemyDefensiveRunes));
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Malzahar", "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "W", NodeType: ComboNodeType.Skill, Name: "W",
            Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Magic,
            RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0,
            UserHitDurationSeconds: seconds));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snapshot(equippedRunes, casterCurrentHp, casterMaxHp));
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        _lastResult = received!;
        return draft.Id;
    }

    private ComboResult _lastResult = null!;

    [Fact]
    public void NoAmplifierRune_RangeEqualsTotal()
    {
        RunMalzaharW(equippedRunes: null);
        Assert.True(_lastResult.TotalDamage > 0);
        Assert.Equal(_lastResult.TotalDamage, _lastResult.RangeMin, precision: 2);
        Assert.Equal(_lastResult.TotalDamage, _lastResult.RangeMax, precision: 2);
    }

    [Fact]
    public void CoupDeGraceEquipped_LiftsCeilingBy8Percent_FloorUnchanged()
    {
        RunMalzaharW(equippedRunes: new[] { 8014 }); // Coup de Grace, +8% (best case)
        Assert.True(_lastResult.TotalDamage > 0);
        // Floor and resolved total are unamplified (conservative: condition unmet).
        Assert.Equal(_lastResult.TotalDamage, _lastResult.RangeMin, precision: 2);
        // Ceiling lifted by 8% (best case: target below 40% HP).
        Assert.Equal(_lastResult.TotalDamage * 1.08, _lastResult.RangeMax, precision: 2);
    }

    [Fact]
    public void TwoAmplifierRunes_StackMultiplicativelyOnCeiling()
    {
        RunMalzaharW(equippedRunes: new[] { 8014, 8369 }); // Coup de Grace 8% × First Strike 7%
        Assert.Equal(_lastResult.TotalDamage, _lastResult.RangeMin, precision: 2);
        Assert.Equal(_lastResult.TotalDamage * 1.08 * 1.07, _lastResult.RangeMax, precision: 2);
    }

    // ── M24 P4: Last Stand auto-resolves its amp from live caster HP ──────────────────

    [Theory]
    [InlineData(1.00, 0.00)] // full HP -> off
    [InlineData(0.60, 0.00)] // exactly 60% -> off (boundary)
    [InlineData(0.45, 0.08)] // midpoint of 60->30 -> 0.05 + 0.5*0.06 = 0.08
    [InlineData(0.30, 0.11)] // 30% -> max
    [InlineData(0.10, 0.11)] // below 30% -> capped
    public void LastStandAmp_ScalesWithCasterHp(double hpPct, double expectedAmp)
    {
        Assert.Equal(expectedAmp, ComboEngine.LastStandAmp(hpPct * 1000, 1000), precision: 4);
    }

    [Fact]
    public void LastStandEquipped_LowCaster_LiftsCeilingByLiveAmp_HealthyCaster_NoLift()
    {
        // Caster at 45% HP -> Last Stand amp 0.08 lifts the ceiling.
        RunMalzaharW(equippedRunes: new[] { 8299 }, casterCurrentHp: 450, casterMaxHp: 1000);
        Assert.Equal(_lastResult.TotalDamage, _lastResult.RangeMin, precision: 2);
        Assert.Equal(_lastResult.TotalDamage * 1.08, _lastResult.RangeMax, precision: 2);

        // Healthy caster (80%) -> Last Stand off -> no ceiling lift (range collapses to total).
        RunMalzaharW(equippedRunes: new[] { 8299 }, casterCurrentHp: 800, casterMaxHp: 1000);
        Assert.Equal(_lastResult.TotalDamage, _lastResult.RangeMax, precision: 2);
    }

    // ── M24 P5: assumed enemy DEFENSIVE runes lower the floor (RangeMin) ───────────────

    [Fact]
    public void ApplyDefensiveRunes_SumsArmorMrShield()
    {
        var d = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 30, Mr: 20, Shield: 0);
        var buffed = ComboEngine.ApplyDefensiveRunes(d, new[] { 8429, 8465 }); // Conditioning + Guardian
        Assert.Equal(30 + 8, buffed.Armor, precision: 2);
        Assert.Equal(20 + 8, buffed.Mr, precision: 2);
        Assert.Equal(0 + 100, buffed.Shield, precision: 2);
    }

    [Fact]
    public void AssumedEnemyAftershock_LowersFloor_ResolvedAndCeilingUnchanged()
    {
        // No assumed runes: floor == total (no crit, set duration).
        RunMalzaharW(equippedRunes: null, seconds: 2.5);
        double baseTotal = _lastResult.TotalDamage;
        Assert.Equal(baseTotal, _lastResult.RangeMin, precision: 2);

        // Assume the enemy has Aftershock (+45 MR while CC'd): the magic W is mitigated more, so the
        // FLOOR drops below the (unchanged) resolved total. Resolved/ceiling stay favorable (no rune).
        RunMalzaharW(equippedRunes: null, seconds: 2.5, assumedEnemyDefensiveRunes: new[] { 8439 });
        Assert.Equal(baseTotal, _lastResult.TotalDamage, precision: 2); // resolved unchanged (zero regression)
        Assert.Equal(baseTotal, _lastResult.RangeMax, precision: 2);     // ceiling unchanged
        Assert.True(_lastResult.RangeMin < baseTotal, "an assumed enemy resist rune must lower the floor");
    }
}
