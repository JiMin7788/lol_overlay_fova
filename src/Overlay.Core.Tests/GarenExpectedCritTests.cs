using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode exists in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// M27 Ability Expected-Crit — Garen E (Judgment) integration endpoints
/// (docs/modules/M27_ABILITY_EXPECTED_CRIT.md §6/§9). Confirms the curated <c>canCrit</c>/
/// <c>critDamageScalar</c> flags (skill_damage/Garen.json) thread end-to-end — SkillDamageDb ->
/// ComboRunner -> ComboEngine.BuildDamageNode -> DamageEngine.CritFactor — producing the three
/// measured endpoint multipliers:
///  - 0% crit chance -> ×1.00 (== the Min floor; the feature adds no exposure here).
///  - 100% crit, no IE -> ×1.30 (Blend and Max coincide: CriticalChance=1 makes Blend's term equal
///    Max's guaranteed-crit term, and both fall back to the same 1.75 constant with no IE reported).
///  - 100% crit + IE (CritDamage=1.975 reported, standing in for a real IE build) -> Max ×1.39,
///    confirming the scalar amplifies the SAME IE-raised bonus the AA crit path already uses (§3.3).
/// Each endpoint is checked as a MULTIPLIER against a CriticalChance=0 baseline run with identical
/// AD/level, so the assertions are independent of Garen E's exact curated per-strike number.
/// </summary>
public class GarenExpectedCritTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public GarenExpectedCritTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "GarenExpectedCritTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static ChampionData LoadGarenFromBin()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "garen.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Garen", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Garen" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Garen", Name = "Garen", Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy(string id, double hp = 5000, double armor = 0, double mr = 0) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats(double critChance, double critDamage) => new()
    {
        AttackDamage = 100, AbilityE = 5, CritChance = critChance, CritDamage = critDamage,
    };

    private static GameSnapshot Snapshot(ActivePlayerStats stats)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2, Stats = stats,
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Garen";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 11;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 11;
        return snap;
    }

    private ComboResult RunGarenE(ActivePlayerStats stats)
    {
        ChampionRepository.Initialize(new[] { LoadGarenFromBin(), Dummy("Target") });
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Garen", "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "E_0", NodeType: ComboNodeType.Skill, Name: "E", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snapshot(stats));
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    [Fact]
    public void GarenE_ZeroCritChance_EqualsFloorTimesOne()
    {
        var noCrit = RunGarenE(Stats(critChance: 0, critDamage: 0));

        // No crit assumed anywhere (CriticalChance=0 -> CritFactor is 1.0 on every track) — Blend,
        // Min, and Max must all collapse to the same base total (the ×1.00 floor endpoint).
        Assert.Equal(noCrit.TotalDamage, noCrit.TotalDamageMin, 2);
        Assert.Equal(noCrit.TotalDamage, noCrit.TotalDamageMax, 2);
        Assert.True(noCrit.TotalDamage > 0, "Garen E must resolve a real per-strike number");
    }

    [Fact]
    public void GarenE_HundredPercentCrit_NoIE_MultiplierIs130()
    {
        var baseline = RunGarenE(Stats(critChance: 0, critDamage: 0));
        var crit = RunGarenE(Stats(critChance: 1.0, critDamage: 0)); // no IE -> CritDamage unreported

        // Max = 1 + 0.40*(1.75-1) = 1.30. Blend coincides here since CriticalChance=1.0 and no IE
        // means Blend's constant-1.75 term and Max's fallback-1.75 term use the same multiplier.
        Assert.Equal(baseline.TotalDamage * 1.30, crit.TotalDamageMax, 1);
        Assert.Equal(baseline.TotalDamage * 1.30, crit.TotalDamage, 1);
    }

    [Fact]
    public void GarenE_HundredPercentCrit_WithIe_MaxMultiplierIs139()
    {
        var baseline = RunGarenE(Stats(critChance: 0, critDamage: 0));
        // CritDamage=1.975 stands in for an Infinity-Edge-class crit-damage build (the real value
        // the Live Client API would report) — the M27 spec's §6 endpoint solves for this Mreal:
        // Max = 1 + 0.40*(1.975-1) = 1.39.
        var crit = RunGarenE(Stats(critChance: 1.0, critDamage: 1.975));

        Assert.Equal(baseline.TotalDamage * 1.39, crit.TotalDamageMax, 1);
        // Blend stays on the pre-existing 1.75 constant (M27 §7, unaffected by IE) — still ×1.30,
        // proving the IE amplification is scoped to the Max/ceiling track only.
        Assert.Equal(baseline.TotalDamage * 1.30, crit.TotalDamage, 1);
    }
}
