using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN — Electrocute (rune 8112), measured in the Ahri session (2026-07-14, GOLDEN_01_AHRI.md).
/// Deliberately EXCLUDED from the Ahri skill golden (a multi-hit ability = ONE trigger toward the
/// 3-attack proc, so no single cast procs it) and measured SEPARATELY = 93 (adaptive → magic vs the
/// Vayne target, MR 32).
///
/// Formula (rune_effects.json id 8112, DDragon 16.13.1): 70–240 (by level, linear) + 0.1·bonusAD +
/// 0.05·AP, adaptive. Ahri L6, AP 74, bonus AD 0 → 70 + 10·5 + 0.05·74 = 123.7, adaptive→MAGIC.
///
/// TWO locks:
///  1. Formula (pure, always exact): RuneEffectDb.Evaluate = 123.7 MAGIC.
///  2. End-to-end (§17 "93±1 재현"): the rune's contribution through the real combo path (auto-
///     trigger + MR mitigation) = the MEASURED 93. Isolated as the WITH-rune − WITHOUT-rune delta on
///     an identical Q→W→E combo, so only Electrocute varies. 123.7 × 100/132 = 93.7 ≈ 93.
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs; ledger row 8112
/// promotes to VERIFIED only after green. See CLAUDE_CODE_TODO §16/§17.
/// </summary>
public class GoldenElectrocuteTests : IDisposable
{
    private const string ElectrocuteId = "8112";
    private readonly string _dir;
    private readonly string _configPath;

    public GoldenElectrocuteTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenElectrocuteTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── 1. Formula lock (pure RuneEffectDb.Evaluate) ─────────────────────────────────

    [Fact]
    public void Golden_Electrocute_Formula_Ahri_L6_Ap74_Is_123_7_Magic()
    {
        var formula = RuneEffectDb.Get(ElectrocuteId);
        Assert.NotNull(formula);
        var (bonus, type) = RuneEffectDb.Evaluate(
            formula!, new RuneCasterStats(Level: 6, BonusAd: 0, Ap: 74, MaxHealth: 0, IsMelee: false));
        Assert.Equal(123.7, bonus, precision: 1);   // 70 + 10×5 (level) + 0.05×74 (AP)
        Assert.Equal(RuneDamageType.MAGIC, type);    // apTerm 3.7 > adTerm 0 → magic
    }

    // ── 2. End-to-end: rune-path contribution through MR mitigation = measured 93 ─────

    [Fact]
    public void Golden_Electrocute_EndToEnd_ProcContribution_Is_93()
    {
        ChampionRepository.Initialize(new[] { LoadAhriFromBin(), Dummy("Target", hp: 3000, armor: 30, mr: 32) });

        // AttackDamage 0 → bonus AD 0 (Electrocute's 0.1·bonusAD term = 0); AP 74. Abilities are
        // AP-based magic, so AD 0 does not change the skill damage — it only pins bonus AD = 0.
        ActivePlayerStats Stats(int[] runes) => new()
        {
            AttackDamage = 0, AbilityPower = 74, AbilityQ = 3, AbilityW = 1, AbilityE = 1, AbilityR = 1,
            EquippedRuneIds = runes,
        };

        // Q(out+return) + W(3 flames) + E = ≥3 separate abilities within the combo → Electrocute procs.
        double without = RunSlots(Snapshot(Stats(Array.Empty<int>())), "Q", "W", "E").TotalDamage;
        double with = RunSlots(Snapshot(Stats(new[] { 8112 })), "Q", "W", "E").TotalDamage;

        // The skills are identical across both runs, so the delta is exactly Electrocute's one proc,
        // mitigated by the target's MR 32: 123.7 × 100/132 = 93.7 ≈ 93 (measured).
        AssertWithinOne(93, with - without, "Electrocute proc contribution (MR-mitigated)");
    }

    // ── fixtures (mirror GoldenGarenTests / GoldenAhriTests) ─────────────────────────

    private static void AssertWithinOne(double expected, double actual, string what)
        => Assert.True(Math.Abs(actual - expected) <= 1.0,
            $"{what}: expected {expected} ±1, got {actual:0.##} (Δ={actual - expected:0.##})");

    private static ChampionData LoadAhriFromBin()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "ahri.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Ahri", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Ahri" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Ahri", Name = "Ahri", Skills = skills,
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
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Ahri";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 6;
        return snap;
    }

    private ComboResult RunSlots(GameSnapshot snap, params string[] slots)
    {
        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();                       // shared: ComboEngine + ComboRunner
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Ahri", "c");
        foreach (var slot in slots)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
                DamageType: ComboDamageType.Magic, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
                CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }
}
