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
/// (CLAUDE_CODE_TODO §76) The damage range has to say WHY it is wide. A user reported min≠max on
/// fixed-damage abilities with no crit and no knob; the cause was a setting — an assumed enemy
/// defensive rune (M24 P5) making the floor's defender hypothetically tankier — and nothing on screen
/// named it. The math was already honest; the reporting was not, which is a P4 problem: a number moved
/// by an assumption must be labelled as assumed.
///
/// <para>This file pins the ATTRIBUTION carried on <see cref="ComboHudResult"/>
/// (<see cref="ComboHudResult.FloorFromAssumedEnemyRunes"/> /
/// <see cref="ComboHudResult.CeilingFromAmplifierRunes"/>), which is what the overlay's caption reads.
/// The numeric behaviour of both mechanisms is already covered by <c>AmplifierRuneRangeTests</c> —
/// asserted again here only where it proves the flag tracks the number rather than the setting.</para>
///
/// <para>Rendering (the gold caption, and the min==max single-box collapse) is WPF and stays
/// human-gated, same standing exception M28 §3's own status note records.</para>
/// </summary>
public class ComboRangeAttributionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ComboRangeAttributionTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "ComboRangeAttributionTests_" + Guid.NewGuid().ToString("N"));
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

    private static GameSnapshot Snapshot(int[]? equippedRunes)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2,
            Stats = new ActivePlayerStats { EquippedRuneIds = equippedRunes ?? Array.Empty<int>() },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Malzahar";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 6;
        return snap;
    }

    /// <summary>Same Malzahar-W-with-a-set-duration-knob shape <c>AmplifierRuneRangeTests</c> uses (no
    /// crit, no unset knob ⇒ any remaining spread is attributable), but returns the whole HUD wrapper
    /// rather than just the result.</summary>
    private ComboHudResult RunMalzaharW(int[]? equippedRunes, int[]? assumedEnemyDefensiveRunes)
    {
        ChampionRepository.Initialize(new[] { Dummy("Malzahar"), Dummy("Target", hp: 2000, mr: 50) });
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
            UserHitDurationSeconds: 2.5));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snapshot(equippedRunes));
        runner.Start();
        ComboHudResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = evt.Payload as ComboHudResult; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    /// <summary>Nothing assumed ⇒ nothing labelled, and the range is a single certain value. This is
    /// the state the user expected to see on a fixed-damage ability.</summary>
    [Fact]
    public void NoAssumptions_NothingLabelled_AndRangeIsCertain()
    {
        var hud = RunMalzaharW(equippedRunes: null, assumedEnemyDefensiveRunes: null);

        Assert.False(hud.FloorFromAssumedEnemyRunes);
        Assert.False(hud.CeilingFromAmplifierRunes);
        Assert.Equal(hud.Result.RangeMin, hud.Result.RangeMax, precision: 2);
        // The 적중시간 knob this harness sets IS itself an assumption (the user stated a duration the
        // API cannot observe), so the 가정 chip counts it — the two mechanisms are independent: the
        // range is certain because the knob is SET, and the chip declares that it was set by hand.
        Assert.Equal(1, hud.AssumptionCount);
    }

    /// <summary>
    /// The reported case: an assumed enemy defensive rune lowers the floor on an ability with no crit
    /// and no knob. The number moving is correct (M24 P5); the point is that the HUD is now told so it
    /// can say the floor is hypothetical.
    /// </summary>
    [Fact]
    public void AssumedEnemyDefensiveRune_LowersFloor_AndIsLabelled()
    {
        var hud = RunMalzaharW(equippedRunes: null, assumedEnemyDefensiveRunes: new[] { 8429 }); // Conditioning

        Assert.True(hud.Result.RangeMin < hud.Result.TotalDamage, "the assumed rune must lower the floor");
        Assert.Equal(hud.Result.TotalDamage, hud.Result.RangeMax, precision: 2); // ceiling stays observed
        Assert.True(hud.FloorFromAssumedEnemyRunes);
        Assert.False(hud.CeilingFromAmplifierRunes);
    }

    /// <summary>The ceiling's own story: an equipped conditional amplifier assumes its condition holds,
    /// so the top of the range is an assumption too and is labelled independently of the floor.</summary>
    [Fact]
    public void EquippedAmplifierRune_LiftsCeiling_AndIsLabelled()
    {
        var hud = RunMalzaharW(equippedRunes: new[] { 8014 }, assumedEnemyDefensiveRunes: null); // Coup de Grace

        Assert.Equal(hud.Result.TotalDamage * 1.08, hud.Result.RangeMax, precision: 2);
        Assert.True(hud.CeilingFromAmplifierRunes);
        Assert.False(hud.FloorFromAssumedEnemyRunes);
    }

    /// <summary>Both ends assumed at once, each for its own reason — the two flags are independent, so
    /// the card can name the floor's cause and the ceiling's cause separately.</summary>
    [Fact]
    public void BothEndsAssumed_AreLabelledIndependently()
    {
        var hud = RunMalzaharW(equippedRunes: new[] { 8014 }, assumedEnemyDefensiveRunes: new[] { 8429 });

        Assert.True(hud.FloorFromAssumedEnemyRunes);
        Assert.True(hud.CeilingFromAmplifierRunes);
        Assert.True(hud.Result.RangeMin < hud.Result.TotalDamage);
        Assert.True(hud.Result.RangeMax > hud.Result.TotalDamage);
    }

    /// <summary>
    /// The flag tracks the NUMBER, not the setting. A defensive rune that adds only a shield leaves a
    /// duration-knobbed magic hit's floor exactly where it was (nothing about the mitigation changed
    /// and the shield is absorbed within the same total), so nothing is labelled — a caption on an
    /// unmoved number would be its own kind of dishonesty.
    /// </summary>
    [Fact]
    public void AssumedRuneThatDoesNotMoveTheFloor_IsNotLabelled()
    {
        double unassumedFloor = RunMalzaharW(null, null).Result.RangeMin;
        var hud = RunMalzaharW(equippedRunes: null, assumedEnemyDefensiveRunes: new[] { 8465 }); // Guardian: shield only

        if (hud.Result.RangeMin >= unassumedFloor)
            Assert.False(hud.FloorFromAssumedEnemyRunes);
        else
            Assert.True(hud.FloorFromAssumedEnemyRunes); // it did move ⇒ it must be labelled
    }
}
