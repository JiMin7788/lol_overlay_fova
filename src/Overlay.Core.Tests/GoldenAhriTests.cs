using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
// ComboNode exists in both Overlay.Core.Combo (M03/M04 node) and Overlay.Core.Damage (M05 node).
// This file only ever means the M03/M04 node — alias to resolve CS0104, same pattern as
// ComboRunnerTests/PercentHpDamageTests/DurationScaledHitTests.
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #1 — Ahri exact baseline (M26 §3). Freezes the user-verified Practice Tool session
/// recorded in <c>docs/reports/golden/GOLDEN_01_AHRI.md</c> (session 2026-07-14) as exact-value
/// asserts, so no future engine/data change can silently regress the flat + stat-ratio + true
/// baseline. A red assert here is an automatic PR FAIL (M26 §9).
///
/// SETUP (verbatim from the sheet §1, panel values): Ahri L6, AP 74 / AD 65,
/// ranks Q3/W1/E1/R1. Target: Armor 30 / MR 32 / MaxHP 732.
///
/// The recorded numbers are POST-MITIGATION (the target had 30 armor / 32 MR), so the golden runs
/// the real per-hit-type mitigation path against a defender PINNED to exactly 30/32/732 (a
/// zero-growth Dummy — isolates calc + mitigation from champion base-resistance derivation, which
/// ChampionRepositoryTests covers separately). MR multiplier 100/132 = 0.7576, armor 100/130 =
/// 0.7692.
///
/// Rows encoded here: Q (magic out + true return), W (3 flames), E, R, AA, and a full-combo
/// composition check (Ahri has no %HP/stacking/HP-state terms, so the combo is exactly additive =
/// 541; the test locks the ComboRunner against double-count/drop — NOT an independent measurement).
///
/// NOTE (Cowork handoff): authored in Cowork, which has no dotnet SDK — UNVERIFIED until built and
/// run locally. See CLAUDE_CODE_TODO.md "build+test" for the gate; the ledger promotion to VERIFIED
/// happens only AFTER Claude Code confirms this class is green.
/// </summary>
public class GoldenAhriTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    // Target, pinned to the measured resistances (zero per-level growth => level-independent).
    private const double TargetArmor = 30;
    private const double TargetMr = 32;
    private const double TargetHp = 732;

    // Recorded HP-delta values (sheet §2), tolerance ±1 per hit (M26 §7).
    private const double QOut = 92;      // Q magic out (mitigated by MR)
    private const double QReturn = 122;  // Q true return (unmitigated)
    private const double QFull = QOut + QReturn; // 214
    private const double W3Flames = 94;  // Single + 2×Multi, magic
    private const double E = 108;        // magic
    private const double R = 76;         // one dash / one orb, magic
    private const double AA = 49;        // basic attack, physical (crit off); AD 65 → 65×100/130 = 50.0

    public GoldenAhriTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenAhriTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static void AssertWithinOne(double expected, double actual, string what)
        => Assert.True(Math.Abs(actual - expected) <= 1.0,
            $"{what}: expected {expected} ±1, engine gave {actual:0.##} (Δ={actual - expected:0.##})");

    // ── fixtures (mirrors ComboDamageModelTests) ─────────────────────────────────────

    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{championId.ToLowerInvariant()}.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = championId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = championId, Name = championId, Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy(string id, double hp, double armor, double mr) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static GameSnapshot Snapshot(ActivePlayerStats stats)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2, Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = "Ahri";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 3;
        return snap;
    }

    private ComboResult RunSlots(GameSnapshot snap, params string[] slots)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Ahri", "c");
        foreach (var s in slots)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{s}_0", NodeType: ComboNodeType.Skill, Name: s, Cooldown: 0, Mana: 0, Damage: 0,
                DamageType: ComboDamageType.Magic, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
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

    private ComboResult RunMixed(GameSnapshot snap, params (ComboNodeType type, string name)[] nodes)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Ahri", "c");
        for (int i = 0; i < nodes.Length; i++)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{nodes[i].name}_{i}", NodeType: nodes[i].type, Name: nodes[i].name, Cooldown: 0, Mana: 0, Damage: 0,
                DamageType: nodes[i].type == ComboNodeType.Aa ? ComboDamageType.Physical : ComboDamageType.Magic,
                RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0));
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

    private ActivePlayerStats AhriStats() => new()
    {
        AbilityPower = 74, AttackDamage = 65, // panel values (sheet §1)
        AbilityQ = 3, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private ComboResult RunAa(GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Ahri", "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
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

    private void InitRepo() =>
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Ahri"), Dummy("Target", TargetHp, TargetArmor, TargetMr) });

    // ── golden rows ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Golden_Ahri_Q_MagicOutPlusTrueReturn()
    {
        InitRepo();
        var result = RunSlots(Snapshot(AhriStats()), "Q");
        Assert.Equal(2, result.NodeBreakdown.Count); // out + return
        AssertWithinOne(QFull, result.TotalDamage, "Ahri Q (out 92 magic + return 122 true = 214)");
    }

    [Fact]
    public void Golden_Ahri_W_ThreeFlames()
    {
        InitRepo();
        var result = RunSlots(Snapshot(AhriStats()), "W");
        Assert.Equal(3, result.NodeBreakdown.Count); // single + 2 reduced
        AssertWithinOne(W3Flames, result.TotalDamage, "Ahri W (3 flames, magic)");
    }

    [Fact]
    public void Golden_Ahri_E()
    {
        InitRepo();
        var result = RunSlots(Snapshot(AhriStats()), "E");
        AssertWithinOne(E, result.TotalDamage, "Ahri E (magic)");
    }

    [Fact]
    public void Golden_Ahri_R_OneOrb()
    {
        InitRepo();
        var result = RunSlots(Snapshot(AhriStats()), "R");
        AssertWithinOne(R, result.TotalDamage, "Ahri R (one orb, magic)");
    }

    [Fact]
    public void Golden_Ahri_AA()
    {
        InitRepo();
        var result = RunAa(Snapshot(AhriStats()));
        AssertWithinOne(AA, result.TotalDamage, "Ahri AA (physical, crit off)");
    }

    [Fact]
    public void Golden_Ahri_FullCombo_ComposesAsSumOfHits()
    {
        // COMPOSITION check (not an independent measurement): Ahri's kit has NO %HP / stacking /
        // HP-state-dependent terms, so the full combo is exactly additive. This locks the
        // ComboRunner against a double-count or dropped-hit regression for this loadout. The
        // measured reference sum = 92 + 122 + 94 + 108 + 76 + 49 = 541 (Electrocute excluded by
        // design — no rune bonuses attached).
        InitRepo();
        var s = AhriStats();
        double q = RunSlots(Snapshot(s), "Q").TotalDamage;
        double w = RunSlots(Snapshot(s), "W").TotalDamage;
        double e = RunSlots(Snapshot(s), "E").TotalDamage;
        double r = RunSlots(Snapshot(s), "R").TotalDamage;
        double aa = RunAa(Snapshot(s)).TotalDamage;

        var combo = RunMixed(Snapshot(s),
            (ComboNodeType.Skill, "Q"), (ComboNodeType.Skill, "W"), (ComboNodeType.Skill, "E"),
            (ComboNodeType.Aa, "AA"), (ComboNodeType.Skill, "R"));

        Assert.Equal(q + w + e + r + aa, combo.TotalDamage, precision: 2); // exact composition
        Assert.True(Math.Abs(combo.TotalDamage - 541) <= 6,
            $"combo should land on the measured reference 541 (was {combo.TotalDamage:0.##})");
    }

    // ── BIN-DERIVED rows (measurement-free) ────────────────────────────────────────────
    // Computed purely from ahri.bin.json + ddragon-published cooldowns — NO in-game measurement.
    // Rank→index proven independent of the game: BIN cooldownTime arrays match ddragon's real
    // per-rank cooldowns EXACTLY once read as rank r = array index r (index 0 = rank-0 placeholder):
    //   W BIN [_,9,8,7,6,5,4] == ddragon [9,8,7,6,5] ; R BIN [_,140,120,100,..] == ddragon [140,120,100].
    // BIN damage formulas (rank r = index r), cross-checked to reproduce the measured L6/AP74 golden
    // above EXACTLY (Q 85+0.5·74=122 → out 122·100/132=92.4, return 122 true; W 69.6·1.8·0.7576=94.9;
    // E 142.9·0.7576=108.3; R 100.9·0.7576=76.4):
    //   Q TotalDamage = base[r] + 0.5·AP   base=[_,35,60,85,110,135]   (hit1 magic out, hit2 true return)
    //   W SingleFire  = base[r] + 0.4·AP ; MultiFire = Single×0.4 ; 3 flames = Single×1.8   base=[_,40,60,80,100,120]
    //   E TotalDamage = base[r] + 0.85·AP   base=[_,80,120,160,200,240]
    //   R RCalculated = base[r] + 0.35·AP   base=[_,75,125,175] (per bolt)
    // This block pins the RAW base+ratio magnitudes at MAX rank / AP 200 against a 0-resist dummy, so
    // the arithmetic is locked with zero mitigation rounding (mitigation typing is covered by the
    // L6/MR32 rows above: Q return stays TRUE/unmitigated there). Cowork-authored — UNVERIFIED until built.

    private ActivePlayerStats AhriStatsMax() => new()
    {
        AbilityPower = 200, AttackDamage = 65,
        AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
    };

    private void InitRepoRaw() =>
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Ahri"), Dummy("Target", 5000, 0, 0) });

    [Fact]
    public void Golden_Ahri_Q_BinDerived_MaxRank_AP200_Raw()
    {
        InitRepoRaw();
        var result = RunSlots(Snapshot(AhriStatsMax()), "Q");
        Assert.Equal(2, result.NodeBreakdown.Count);           // out + return
        // per hit = 135 + 0.5·200 = 235 ; magic out + true return, 0 resist ⇒ 470
        AssertWithinOne(470, result.TotalDamage, "Ahri Q BIN r5/AP200 (235 magic + 235 true)");
    }

    [Fact]
    public void Golden_Ahri_W_BinDerived_MaxRank_AP200_Raw()
    {
        InitRepoRaw();
        var result = RunSlots(Snapshot(AhriStatsMax()), "W");
        Assert.Equal(3, result.NodeBreakdown.Count);           // single + 2 reduced
        // single = 120 + 0.4·200 = 200 ; multi = 200×0.4 = 80 ; 200 + 2×80 = 360
        AssertWithinOne(360, result.TotalDamage, "Ahri W BIN r5/AP200 (200 + 2×80)");
    }

    [Fact]
    public void Golden_Ahri_E_BinDerived_MaxRank_AP200_Raw()
    {
        InitRepoRaw();
        var result = RunSlots(Snapshot(AhriStatsMax()), "E");
        // 240 + 0.85·200 = 410
        AssertWithinOne(410, result.TotalDamage, "Ahri E BIN r5/AP200 (410)");
    }

    [Fact]
    public void Golden_Ahri_R_BinDerived_MaxRank_AP200_Raw()
    {
        InitRepoRaw();
        var result = RunSlots(Snapshot(AhriStatsMax()), "R");
        // 175 + 0.35·200 = 245 (one bolt)
        AssertWithinOne(245, result.TotalDamage, "Ahri R BIN r3/AP200 (245 per bolt)");
    }
}
