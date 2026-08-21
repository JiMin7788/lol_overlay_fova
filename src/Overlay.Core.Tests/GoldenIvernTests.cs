using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #16 — Ivern (golden-unlock round 2026-07-26, the FINAL golden-lock champion): the
/// SUMMON per-attack golden. Locks the M22 Phase-3 pattern on its motivating champion (Daisy's
/// TotalDaisyAD × UserAttackCount) plus the brush-conditional W onHit rider and the Triggerseed
/// AoE — completing measured coverage of every engine hit-shape path in the coverage plan.
///
/// SETUP (measured 2026-07-26, fully clean: no items, no shards — total AD 62 IS Ivern's level-6
/// base, AP 0): level 6, all ranks 1, dummy 1000 HP / 50 armor / 50 MR (×2/3).
///
/// Reconciled BEFORE encoding (raw → ×2/3):
///   AA 41 (62 ×2/3) · Q 53 (Rootcaller rank-1 base 80, AP term zero) · W brush rider 13 on the
///   in-brush AA (rider base ≈20 raw at rank 1; the AA line stays 41) · E 46 (Triggerseed
///   rank-1 base ≈70) · Daisy per-attack 46 (TotalDaisyAD ≈70 raw — the summon knob path) ·
///   Daisy 3rd-hit shockwave 59 (TotalShockwaveDamage ≈88.5 raw, reference: reconciled via a
///   direct calc-path pin; the shockwave stays uncurated in the R slot, conservative floor).
/// </summary>
public class GoldenIvernTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "Ivern";
    private const double TotalAd = 62.0;

    public GoldenIvernTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenIvern_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static void AssertWithinOne(double expected, double actual, string what)
        => Assert.True(Math.Abs(expected - actual) <= 1.0,
            $"{what}: expected {expected:0.##} ±1, engine computed {actual:0.##}");

    private static ChampionData Ivern()
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
        return new ChampionData
        {
            Id = ChampId, Name = ChampId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = TotalAd },
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

    private void InitRepo() => ChampionRepository.Initialize(new[] { Ivern(), Dummy() });

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

    private static ComboNode AaNode(string id) => new(
        Id: id, NodeType: ComboNodeType.Aa, Name: "AA", Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    // ── measured rows (post-mitigation vs 1000 HP / 50/50 dummy) ────────────────────────

    [Fact]
    public void Golden_Ivern_Q_Rootcaller()
    {
        InitRepo();
        AssertWithinOne(53, RunNodes(SkillNode("Q")).TotalDamage, "Q (rank-1 base 80, ×2/3)");
    }

    [Fact]
    public void Golden_Ivern_WRider_BrushEmpoweredAa()
    {
        InitRepo();
        double aaAlone = RunNodes(AaNode("AA_0")).TotalDamage;
        AssertWithinOne(41, aaAlone, "plain AA (62 ×2/3)");
        double rider = RunNodes(SkillNode("W"), AaNode("AA_1")).TotalDamage - aaAlone;
        AssertWithinOne(13, rider, "W brush rider on the in-brush AA (rank-1 base, ×2/3)");
    }

    [Fact]
    public void Golden_Ivern_E_TriggerseedExplosion()
    {
        InitRepo();
        AssertWithinOne(46, RunNodes(SkillNode("E")).TotalDamage, "E (rank-1 base, ×2/3)");
    }

    [Fact]
    public void Golden_Ivern_R_DaisyPerAttack_SummonKnob()
    {
        // THE summon-path row: Daisy's per-attack damage through the UserAttackCount knob.
        InitRepo();
        var oneHit = SkillNode("R") with { UserAttackCount = 1 };
        AssertWithinOne(46, RunNodes(oneHit).TotalDamage,
            "Daisy per-attack (TotalDaisyAD, ×2/3) — the M22 Phase-3 summon pattern measured");
        // Knob linearity in engine space (3 × 46.67 = 140; the measured "46" is log-truncated
        // from 46.67, so 3× the display value would drift by 2 — compare engine-to-engine).
        double single = RunNodes(SkillNode("R") with { UserAttackCount = 1 }).TotalDamage;
        var threeHits = SkillNode("R") with { UserAttackCount = 3 };
        AssertWithinOne(3 * single, RunNodes(threeHits).TotalDamage, "three Daisy attacks = 3× the knob");
    }

    [Fact]
    public void Golden_Ivern_DaisyShockwave_ReferencePin()
    {
        // The 3rd-hit shockwave line (59 measured) — reconciled via the direct calc path; the
        // shockwave stays UNCURATED in the R slot (conservative floor), this pins the BIN value.
        InitRepo();
        var champ = ChampionRepository.Get(ChampId)!;
        double? raw = SkillDamage.ComputeCalcDamage(champ, "R", "TotalShockwaveDamage", Stats(), level: 6);
        Assert.NotNull(raw);
        AssertWithinOne(59, raw!.Value * (2.0 / 3.0), "Daisy 3rd-hit shockwave (rank-1 base, ×2/3)");
    }
}
