using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #12 — Jayce, the first MULTIFORM golden (golden-unlock round 2026-07-26): freezes the
/// user's live Practice Tool session and locks the binSpell extra-slot path (Cannon stance)
/// end to end, both stances of one champion in one sheet.
///
/// SETUP (measured 2026-07-26): Jayce level 6, ONE Long Sword — total AD 85.79 (stat panel
/// shows 87 by rounding; the measured AA pins the real value), bonus AD = 10, AP 0. Ranks
/// Q1/W1/E1/R1. Target dummy: 1000 max HP, 50 armor, 50 MR (mitigation ×2/3 both types).
///
/// Every recorded number reconciled against the BIN to &lt;0.5% BEFORE encoding:
///   AA 57 (= total AD ×2/3) · Hammer Q 49 (60 + 1.35×10 → 73.5) · Hammer E 60
///   (10 flat + 8%×1000 → 90, magic) · Cannon Q 62 (80 + 1.3×10 → 93) · Cannon W 40/hit
///   (0.70×85.79 → 60.05, ×3 hits) · R transform proc 42 (60@L6 + 0.3×10 → 63, magic)
///   · gate-empowered Q 87 (×1.403 ≈ the BIN's 1.4 modifier — deliberately NOT curated, floor).
///   The measured full-duration Hammer W 93 (BIN 4s total 140 ×2/3 = 93.3) CONFIRMS the
///   uncurated-W adjudication's underlying value without curating it.
/// </summary>
public class GoldenJayceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "Jayce";
    /// <summary>True total AD from the measured AA (85.5 raw ≈ 85.79 computed; panel rounds to 87).</summary>
    private const double TotalAd = 85.79;
    /// <summary>One Long Sword.</summary>
    private const double BaseAdL6 = TotalAd - 10.0;

    public GoldenJayceTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenJayce_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static void AssertWithinOne(double expected, double actual, string what)
        => Assert.True(Math.Abs(expected - actual) <= 1.0,
            $"{what}: expected {expected} ±1, engine computed {actual:0.##}");

    private static ChampionData Jayce()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon",
            $"{ChampId.ToLowerInvariant()}.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion(ChampId, json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = ChampId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        // Pin base AD so bonus (total − base) = exactly the one Long Sword (10) at level 6.
        return new ChampionData
        {
            Id = ChampId, Name = ChampId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = BaseAdL6 },
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = 1000, Armor = 50, Mr = 50 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = TotalAd, AbilityPower = 0,
        AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static GameSnapshot Snap()
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2, Stats = Stats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = ChampId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 6;
        return snap;
    }

    private void InitRepo() => ChampionRepository.Initialize(new[] { Jayce(), Dummy() });

    private ComboResult RunNodes(params ComboNode[] nodes)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, "c");
        foreach (var n in nodes) editor.AddNode(draft.Id, n);
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, Snap);
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

    private static ComboNode SkillNode(string slot) => new(
        Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    private static ComboNode AaNode() => new(
        Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA", Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    // ── measured rows (post-mitigation vs 50/50 dummy) ─────────────────────────────

    [Fact]
    public void Golden_Jayce_HammerQ()
    {
        InitRepo();
        AssertWithinOne(49, RunNodes(SkillNode("Q")).TotalDamage, "Hammer Q (60 + 1.35×10, ×2/3)");
    }

    [Fact]
    public void Golden_Jayce_HammerE_FlatPlusPercentMaxHp()
    {
        InitRepo();
        AssertWithinOne(60, RunNodes(SkillNode("E")).TotalDamage, "Hammer E (10 + 8%×1000 magic, ×2/3)");
    }

    [Fact]
    public void Golden_Jayce_CannonQ_ViaBinSpellExtraSlot()
    {
        InitRepo();
        AssertWithinOne(62, RunNodes(SkillNode("QCannon")).TotalDamage, "Cannon Q (80 + 1.3×10, ×2/3)");
    }

    [Fact]
    public void Golden_Jayce_CannonW_ThreeEmpoweredHits()
    {
        InitRepo();
        AssertWithinOne(120, RunNodes(SkillNode("WCannon")).TotalDamage, "Cannon W (0.70×85.79 ×3, ×2/3)");
    }
}
