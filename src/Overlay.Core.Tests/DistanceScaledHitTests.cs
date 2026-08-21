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
/// M24 P3: the DistanceScaled shape (<see cref="SkillHit.MinCalc"/> + <see cref="ComboNode.UserDistanceFraction"/>)
/// on Hecarim E (Devastating Charge), whose real BIN defines MaxDamage = MinDamage×2.0.
///  1. Curation: the two curated calcs resolve live from the real BIN with max == 2×min.
///  2. Integration (ComboRunner): an UNSET 거리 knob resolves to the MAX end (value identity with the
///     prior full-charge curation) while the uncertainty range widens DOWN to the min-charge floor
///     (RangeMin == RangeMax/2 < RangeMax == TotalDamage).
///  3. A SET knob (0 = min charge) collapses the range to the min value (no widening).
/// Uses the real cached Hecarim BIN via ChampionRepository.InitializeFromCache (same harness as
/// SkillDataCurationTests), so the numbers are live BIN evaluations, nothing hardcoded.
/// </summary>
public class DistanceScaledHitTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public DistanceScaledHitTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "DistanceScaledHitTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static void InitRepositoryFromCache()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var ddragonRoot = Path.Combine(dataDir, "ddragon");
        var summary = Directory.GetFiles(ddragonRoot, "champion.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).Last();
        ChampionRepository.InitializeFromCache(
            Path.GetDirectoryName(summary)!, Path.Combine(dataDir, "communitydragon"));
    }

    private static ActivePlayerStats SampleStats() => new()
    {
        AttackDamage = 200,
        AbilityPower = 0,
        AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
    };

    private static GameSnapshot Snapshot(string activeChampion, string enemyChampion)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = 18,
            PlayerCount = 2,
            Stats = SampleStats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = activeChampion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 18;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = enemyChampion;
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 18;
        return snap;
    }

    private static string SaveOneNodeCombo(ComboEditor editor, string championId, string slot, double? userDistanceFraction)
    {
        var draft = editor.CreateCombo(championId, "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: slot, NodeType: ComboNodeType.Skill, Name: slot,
            Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Magic,
            RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0,
            UserDistanceFraction: userDistanceFraction));
        editor.SaveCombo(draft.Id);
        return draft.Id;
    }

    private ComboResult RunCombo(GameSnapshot snap, string comboId, ComboEngine engine, ConfigManager config)
    {
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", comboId, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    // ── 1. curation: the two calcs resolve live, max == 2×min ──────────────────────────

    [Fact]
    public void HecarimE_MaxDamageIsTwiceMinDamage_LiveFromRealBin()
    {
        InitRepositoryFromCache();
        var hecarim = ChampionRepository.Get("Hecarim");
        Assert.NotNull(hecarim);

        double min = SkillDamage.ComputeCalcDamage(hecarim!, "E", "MinDamage", SampleStats(), level: 18) ?? 0;
        double max = SkillDamage.ComputeCalcDamage(hecarim!, "E", "MaxDamage", SampleStats(), level: 18) ?? 0;

        Assert.True(min > 0, "MinDamage must resolve to a positive number from the real BIN");
        Assert.True(max > 0, "MaxDamage must resolve to a positive number from the real BIN");
        Assert.Equal(2.0 * min, max, precision: 2); // BIN: MaxDamage = GameCalculationModified(MinDamage ×2.0)
    }

    // ── 2. unset knob -> resolved=MAX (value identity), range widens down to the min floor ─

    [Fact]
    public void HecarimE_UnsetDistance_ResolvesToMax_RangeSpansMinToMax()
    {
        InitRepositoryFromCache();
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeCombo(editor, "Hecarim", "E", userDistanceFraction: null);

        var result = RunCombo(Snapshot("Hecarim", "Garen"), comboId, engine, config);

        Assert.True(result.TotalDamage > 0);
        // Resolved default = full charge (value identity with the prior curation): Total == RangeMax.
        Assert.Equal(result.TotalDamage, result.RangeMax, precision: 2);
        // Range widened DOWN to the min-charge floor; min charge is exactly half the full charge.
        Assert.True(result.RangeMin < result.RangeMax, "an unset distance knob must widen the range floor");
        Assert.Equal(result.RangeMax / 2.0, result.RangeMin, precision: 2);
    }

    // ── 3. set knob (0 = min charge) collapses the range to the min value ──────────────

    [Fact]
    public void HecarimE_DistanceZero_CollapsesRangeToMinCharge()
    {
        InitRepositoryFromCache();
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeCombo(editor, "Hecarim", "E", userDistanceFraction: 0.0);

        var resultMin = RunCombo(Snapshot("Hecarim", "Garen"), comboId, engine, config);

        // f=0 -> min charge; a set knob collapses its axis -> RangeMin == RangeMax == TotalDamage.
        Assert.Equal(resultMin.TotalDamage, resultMin.RangeMin, precision: 2);
        Assert.Equal(resultMin.TotalDamage, resultMin.RangeMax, precision: 2);

        // And that collapsed min value is half of the unset (full-charge) total — cross-check vs test 2.
        var editor2 = new ComboEditor(engine, config);
        string maxComboId = SaveOneNodeCombo(editor2, "Hecarim", "E", userDistanceFraction: null);
        var resultMax = RunCombo(Snapshot("Hecarim", "Garen"), maxComboId, engine, config);
        Assert.Equal(resultMax.TotalDamage / 2.0, resultMin.TotalDamage, precision: 2);
    }

    // ── 4. resolved-anchor at the MIN end (Fizz R, Nidalee Q): unset -> Total==RangeMin, ──
    //       range widens UP to the max end (the opposite of Hecarim's resolved-at-max).

    [Theory]
    [InlineData("Fizz", "R")]
    [InlineData("Nidalee", "Q")]
    [InlineData("Jinx", "R")] // Super Mega Death Rocket: DamageFloor(near) resolved, DamageMax(far) ceiling
    public void MinAnchoredDistanceScaled_UnsetKnob_ResolvesToMin_RangeWidensUp(string champion, string slot)
    {
        InitRepositoryFromCache();
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeCombo(editor, champion, slot, userDistanceFraction: null);

        var result = RunCombo(Snapshot(champion, "Garen"), comboId, engine, config);

        Assert.True(result.TotalDamage > 0);
        // Resolved default = the conservative near-throw end (value identity): Total == RangeMin.
        Assert.Equal(result.TotalDamage, result.RangeMin, precision: 2);
        // Range widens UP to the far-throw ceiling — strictly above the resolved floor.
        Assert.True(result.RangeMax > result.RangeMin,
            $"{champion} {slot}: an unset distance knob must widen the range ceiling above the min anchor");
    }

    // ── 5. MAX-anchored charge (Varus Q ChargeHold): resolved = full charge, range widens DOWN ──

    [Fact]
    public void VarusQ_ChargeHold_UnsetKnob_ResolvesToFullCharge_RangeWidensDownToUncharged()
    {
        InitRepositoryFromCache();
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeCombo(editor, "Varus", "Q", userDistanceFraction: null);

        var result = RunCombo(Snapshot("Varus", "Garen"), comboId, engine, config);

        Assert.True(result.TotalDamage > 0);
        // Resolved default = full charge (value identity with the prior TotalDamageMax curation).
        Assert.Equal(result.TotalDamage, result.RangeMax, precision: 2);
        // Range widens DOWN to the uncharged floor (TotalDamage < TotalDamageMax).
        Assert.True(result.RangeMin < result.RangeMax, "the uncharged floor must be below the full-charge ceiling");
    }
}
