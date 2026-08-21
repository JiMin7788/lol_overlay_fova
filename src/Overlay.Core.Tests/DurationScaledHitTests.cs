using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
// ComboNode exists in both Overlay.Core.Combo (M03/M04 node) and Overlay.Core.Damage
// (M05 node). This file only ever means the M03/M04 node — alias to resolve CS0104,
// same pattern as ComboRunnerTests/PercentHpDamageTests.
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for the duration-scaled hit feature (combo editor "적중시간" control, M04/M05
/// changelog): an escapable persistent zone/DoT hit (<see cref="SkillHit.IsDurationScaled"/>, e.g.
/// Malzahar W "Null Zone" — curated in data/skill_damage/Malzahar.json) deals PARTIAL damage
/// proportional to how many seconds the USER manually says the target was actually exposed, instead
/// of this project's prior convention of omitting such a hit entirely (P2, never assume full
/// duration):
///  1. Unset (null) <see cref="ComboNode.UserHitDurationSeconds"/> -> 0 damage, the same honest
///     default as full omission.
///  2. A mid-range value -> damage = perSecondHpPercent x defender MaxHP x seconds, mitigated by the
///     hit's own damage type exactly like any other curated hit.
///  3. A value EXCEEDING the ability's real max duration -> clamped to the max-duration total, never
///     more (proves <see cref="SkillHit.MaxDurationSeconds"/> is a hard ceiling, not just a UI hint).
///
/// Uses the REAL curated Malzahar.json file (loaded live from disk by SkillDamageDb, same as every
/// other champion's damage), not a synthetic fixture — this hit type resolves purely from its own
/// literal fields (see SkillHit.PerSecondHpPercent's doc comment), so the caster ChampionData needs
/// no BIN-loaded skill data at all (unlike the %HP-DataValue tests in PercentHpDamageTests).
/// </summary>
public class DurationScaledHitTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public DurationScaledHitTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "DurationScaledHitTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── helpers (same shape as ComboRunnerTests/PercentHpDamageTests) ──────────────

    private static ChampionData Dummy(string id, double hp = 0, double armor = 0, double mr = 0) => new()
    {
        Id = id,
        Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(), // level-invariant test: per-level growth is 0
    };

    private static GameSnapshot Snapshot(string activeChampion, string enemyChampion)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = 6,
            PlayerCount = 2,
            Stats = new ActivePlayerStats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = activeChampion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;

        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = enemyChampion;
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 6;
        return snap;
    }

    /// <summary>Saves a single-node "W" combo through the real M04 editor with the given
    /// <see cref="ComboNode.UserHitDurationSeconds"/> (null = unset).</summary>
    private static string SaveOneNodeWCombo(ComboEditor editor, string championId, double? userSeconds)
    {
        var draft = editor.CreateCombo(championId, "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "W",
            NodeType: ComboNodeType.Skill,
            Name: "W",
            Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Magic,
            RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0,
            UserHitDurationSeconds: userSeconds));
        editor.SaveCombo(draft.Id);
        return draft.Id;
    }

    /// <summary>Saves a single-node combo for an arbitrary skill slot with the given exposure seconds
    /// (for §27 Nasus R, whose DoT lives on R rather than W).</summary>
    private static string SaveOneNodeSkillCombo(ComboEditor editor, string championId, string slot, double? userSeconds)
    {
        var draft = editor.CreateCombo(championId, "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: slot, NodeType: ComboNodeType.Skill, Name: slot,
            Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Magic,
            RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0,
            UserHitDurationSeconds: userSeconds));
        editor.SaveCombo(draft.Id);
        return draft.Id;
    }

    /// <summary>Nasus with REAL BIN skill data (needed so R's AOEDamagePercent DataValue resolves live,
    /// unlike the literal-field Malzahar caster). Mirrors GoldenMaokaiTests' BIN loader.</summary>
    private static ChampionData NasusWithBin()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "nasus.bin.json"));
        var bin = ChampionBinParser.ParseChampion("Nasus", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, b) in bin)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Nasus" + slot,
                DataValues = b.DataValues, SpellCalculations = b.SpellCalculations,
                EffectAmounts = b.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Nasus", Name = "Nasus", Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private ComboResult RunCombo(string championId, string enemyChampion, GameSnapshot snap, string comboId, ComboEngine engine, ConfigManager config)
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

    // ── 1. loader parses perSecondHpPercent/maxDurationSeconds, and IsDurationScaled ────

    [Fact]
    public void Loader_ParsesDurationScaledFields_MalzaharW()
    {
        var w = Assert.Single(SkillDamageDb.GetHits("Malzahar", "W")!);
        Assert.Equal(0.06, w.PerSecondHpPercent!.Value);
        Assert.Equal(5.0, w.MaxDurationSeconds!.Value);
        Assert.True(w.IsDurationScaled);
        Assert.Equal(HitDamageType.Magic, w.Type);

        // The lookup helper the combo editor's UI uses to decide whether to offer "적중시간".
        var found = SkillDamageDb.GetDurationScaledHit("Malzahar", "W");
        Assert.NotNull(found);
        Assert.Equal(0.06, found!.PerSecondHpPercent!.Value);

        // A non-duration-scaled slot (Q) must not be picked up.
        Assert.Null(SkillDamageDb.GetDurationScaledHit("Malzahar", "Q"));
    }

    // ── 1b. loader parses the FLAT per-second DoT variant (perSecondCalc), Heimerdinger Q ─

    [Fact]
    public void Loader_ParsesFlatPerSecondDoT_HeimerdingerQ()
    {
        // Heimerdinger Q (H-28G turret) is curated (loop 51) as a flat per-second DoT: its
        // per-second rate is a BIN calc name (perSecondCalc="Damage"), NOT a %HP fraction, so
        // PerSecondHpPercent stays null while IsDurationScaled must still be true (the flat sibling
        // of the Malzahar-W %HP path). This is pure loader/shape verification (no BIN eval needed).
        var q = Assert.Single(SkillDamageDb.GetHits("Heimerdinger", "Q")!);
        Assert.Equal("Damage", q.PerSecondCalc);
        Assert.Null(q.PerSecondHpPercent);
        Assert.Equal(6.0, q.MaxDurationSeconds!.Value);
        Assert.True(q.IsDurationScaled, "a perSecondCalc + maxDurationSeconds hit is duration-scaled");
        Assert.Equal(HitDamageType.Magic, q.Type);

        // The combo editor's "적중시간" gate must offer this slot too.
        var found = SkillDamageDb.GetDurationScaledHit("Heimerdinger", "Q");
        Assert.NotNull(found);
        Assert.Equal("Damage", found!.PerSecondCalc);
    }

    // ── 1c. loader parses the per-attack SUMMON hit (M22 Phase 3), Annie R (Tibbers) ────

    [Fact]
    public void Loader_ParsesPerAttackSummonHit_AnnieR()
    {
        // Annie R curates two hits: the always-applied InitialBurstDamage (a normal hit) and Tibbers'
        // auto-attack as a per-attack summon hit (perAttackCalc, scaled by ComboNode.UserAttackCount).
        SkillDamageDb.ResetForTests();

        var hits = SkillDamageDb.GetHits("Annie", "R")!;
        Assert.Contains(hits, h => h.Calc == "InitialBurstDamage" && string.IsNullOrEmpty(h.PerAttackCalc));
        Assert.Contains(hits, h => h.PerAttackCalc == "TibbersAADamage");

        // The editor's "몇 대" gate helper picks up the per-attack hit (and not a plain slot).
        var perAttack = SkillDamageDb.GetPerAttackHit("Annie", "R");
        Assert.NotNull(perAttack);
        Assert.Equal("TibbersAADamage", perAttack!.PerAttackCalc);
        Assert.Null(SkillDamageDb.GetPerAttackHit("Annie", "Q"));

        SkillDamageDb.ResetForTests();
    }

    [Fact]
    public void Loader_ParsesStackScaledHit_NasusQ()
    {
        // (M25 §11.G) Nasus Q is curated stack-scaled: TotalDamage carries a BuffCounter per-stack term
        // fed by ComboNode.UserStackCount. The editor's "몇 스택" gate helper (GetStackScaledHit) picks it
        // up so the node offers a stack-count input; a non-stack slot (E) does not.
        SkillDamageDb.ResetForTests();

        var q = Assert.Single(SkillDamageDb.GetHits("Nasus", "Q")!);
        Assert.Equal("TotalDamage", q.Calc);
        Assert.True(q.StackScaled);

        var stackHit = SkillDamageDb.GetStackScaledHit("Nasus", "Q");
        Assert.NotNull(stackHit);
        Assert.Equal("TotalDamage", stackHit!.Calc);
        Assert.Null(SkillDamageDb.GetStackScaledHit("Nasus", "E"));

        SkillDamageDb.ResetForTests();
    }

    // ── 1e. (§27) loader parses the BIN-sourced %maxHP DoT variant, Nasus R ─────────────

    [Fact]
    public void Loader_ParsesBinSourcedHpPercentDoT_NasusR()
    {
        SkillDamageDb.ResetForTests();

        var r = Assert.Single(SkillDamageDb.GetHits("Nasus", "R")!);
        Assert.Equal("AOEDamagePercent", r.PerSecondHpPercentDataValue); // BIN DataValue, rank-scaled
        Assert.Equal(15.0, r.MaxDurationSeconds!.Value);
        Assert.Null(r.PerSecondHpPercent);        // BIN-sourced rate, NOT the literal sibling
        Assert.True(r.IsDurationScaled, "a perSecondHpPercentDataValue + maxDurationSeconds hit is duration-scaled");
        Assert.Equal(HitDamageType.Magic, r.Type);

        // The combo editor's "적중시간" gate must offer this slot.
        Assert.NotNull(SkillDamageDb.GetDurationScaledHit("Nasus", "R"));

        SkillDamageDb.ResetForTests();
    }

    // ── 3b. (§27) Nasus R BIN-sourced %maxHP DoT scales with exposure × max HP ──────────

    [Fact]
    public void NasusR_BinSourcedHpPercentDoT_ScalesWithExposureAndMaxHp()
    {
        // Nasus R's %maxHP/sec DoT resolves LIVE from the BIN DataValue (AOEDamagePercent, r1-3 =
        // 3/4/5% per second), then scales by exposure seconds × target max HP — the same engine formula
        // the Malzahar-W literal path uses, but rank-scaled from the BIN. 2s exposure vs a 2000-HP,
        // MR:0 target ⇒ 3-5% × 2000 × 2 = 120-200 magic (unmitigated), proving the DataValue resolves.
        double maxHp = 2000, seconds = 2.0;
        ChampionRepository.Initialize(new[] { NasusWithBin(), Dummy("Target", hp: maxHp, mr: 0) });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeSkillCombo(editor, "Nasus", "R", seconds);

        var result = RunCombo("Nasus", "Target", Snapshot("Nasus", "Target"), comboId, engine, config);

        Assert.InRange(result.TotalDamage, 110.0, 210.0);      // 3-5%/s × 2000 × 2s, unmitigated
        Assert.True(result.TotalDamage > 0, "the BIN-sourced %HP DoT must resolve to a non-zero number");
    }

    // ── 1d. loader parses the All-Out stance variant (M22 Phase 4), K'Sante Q ──────────

    [Fact]
    public void Loader_ParsesAllOutStanceVariant_KsanteQ()
    {
        // K'Sante Q declares an All-Out variant: while a preceding R node has toggled the All-Out
        // stance, ComboRunner resolves 'RDamage' on KSanteQ3 instead of the base 'BaseDamage'.
        SkillDamageDb.ResetForTests();

        var q = Assert.Single(SkillDamageDb.GetHits("Ksante", "Q")!);
        Assert.Equal("BaseDamage", q.Calc);               // base (non-All-Out) resolution
        Assert.Equal("RDamage", q.AllOutCalc);            // All-Out override calc
        Assert.Equal("KSanteQ3", q.AllOutBinSpell);       // resolved against the All-Out spell object

        SkillDamageDb.ResetForTests();
    }

    // ── 2. unset seconds -> 0 damage (same honest default as full omission) ────────────

    [Fact]
    public void MalzaharW_UnsetHitDuration_DealsZeroDamage()
    {
        ChampionRepository.Initialize(new[]
        {
            Dummy("Malzahar"),
            Dummy("Target", hp: 2000, mr: 50),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeWCombo(editor, "Malzahar", userSeconds: null);

        var result = RunCombo("Malzahar", "Target", Snapshot("Malzahar", "Target"), comboId, engine, config);

        Assert.Equal(0.0, result.TotalDamage, precision: 2);
    }

    // ── 2b. (M24 P2) unset knob -> RangeMax widens to full duration, floor/Total stay 0 ──

    [Fact]
    public void MalzaharW_UnsetHitDuration_RangeMaxWidensToFullDuration_FloorAndTotalStayZero()
    {
        double maxHp = 2000, mr = 50;
        ChampionRepository.Initialize(new[] { Dummy("Malzahar"), Dummy("Target", hp: maxHp, mr: mr) });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeWCombo(editor, "Malzahar", userSeconds: null);

        var result = RunCombo("Malzahar", "Target", Snapshot("Malzahar", "Target"), comboId, engine, config);

        // Resolved floor is UNCHANGED (zero regression): unset -> 0 exposure -> 0 damage == RangeMin.
        Assert.Equal(0.0, result.TotalDamage, precision: 2);
        Assert.Equal(0.0, result.RangeMin, precision: 2);
        // Ceiling widens to the FULL 5s-duration mitigated total: 0.06*2000*5 * 100/150 = 400.
        double fullDurationMitigated = Math.Round(0.06 * maxHp * 5.0 * (100.0 / (100.0 + mr)), 2);
        Assert.Equal(fullDurationMitigated, result.RangeMax, precision: 2);
        Assert.True(result.RangeMax > result.RangeMin, "an unset duration knob must widen the range ceiling");
    }

    // ── 2c. (M24 P2) a SET knob collapses its axis -> no widening (RangeMin == RangeMax) ──

    [Fact]
    public void MalzaharW_SetHitDuration_CollapsesRange_RangeEqualsTotal()
    {
        double maxHp = 2000, mr = 50, seconds = 2.5;
        ChampionRepository.Initialize(new[] { Dummy("Malzahar"), Dummy("Target", hp: maxHp, mr: mr) });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeWCombo(editor, "Malzahar", userSeconds: seconds);

        var result = RunCombo("Malzahar", "Target", Snapshot("Malzahar", "Target"), comboId, engine, config);

        // A set knob fixes exposure: no crit here either, so the whole range collapses to the total.
        Assert.Equal(result.TotalDamage, result.RangeMin, precision: 2);
        Assert.Equal(result.TotalDamage, result.RangeMax, precision: 2);
    }

    // ── 3. mid-range seconds -> proportional damage, mitigated by MR ───────────────────

    [Fact]
    public void MalzaharW_MidRangeHitDuration_DealsProportionalMitigatedDamage()
    {
        double maxHp = 2000, mr = 50, seconds = 2.5;
        ChampionRepository.Initialize(new[]
        {
            Dummy("Malzahar"),
            Dummy("Target", hp: maxHp, mr: mr),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        string comboId = SaveOneNodeWCombo(editor, "Malzahar", userSeconds: seconds);

        var result = RunCombo("Malzahar", "Target", Snapshot("Malzahar", "Target"), comboId, engine, config);

        // raw = 0.06 * 2000 * 2.5 = 300 magic; mitigated by 50 MR: 300 * 100/150 = 200.
        double raw = 0.06 * maxHp * seconds;
        double expected = Math.Round(raw * (100.0 / (100.0 + mr)), 2);
        Assert.Equal(expected, result.TotalDamage, precision: 2);
        Assert.True(result.TotalDamage < raw, "MR must reduce a magic duration-scaled hit");
    }

    // ── 4. seconds exceeding MaxDurationSeconds -> clamped to the full-duration total ──

    [Fact]
    public void MalzaharW_HitDurationExceedingMax_IsClampedToFullDurationTotal()
    {
        double maxHp = 2000, mr = 50;
        ChampionRepository.Initialize(new[]
        {
            Dummy("Malzahar"),
            Dummy("Target", hp: maxHp, mr: mr),
        });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        // 8s user input > Null Zone's real 5s max duration.
        string comboId = SaveOneNodeWCombo(editor, "Malzahar", userSeconds: 8.0);

        var result = RunCombo("Malzahar", "Target", Snapshot("Malzahar", "Target"), comboId, engine, config);

        // Clamped to 5s (the curated maxDurationSeconds), NOT 8s: raw = 0.06*2000*5 = 600,
        // mitigated: 600 * 100/150 = 400. An unclamped 8s would have been 960 raw / 640 mitigated.
        double clampedRaw = 0.06 * maxHp * 5.0;
        double expected = Math.Round(clampedRaw * (100.0 / (100.0 + mr)), 2);
        Assert.Equal(expected, result.TotalDamage, precision: 2);

        double unclampedRaw = 0.06 * maxHp * 8.0;
        double unclampedMitigated = Math.Round(unclampedRaw * (100.0 / (100.0 + mr)), 2);
        Assert.True(result.TotalDamage < unclampedMitigated,
            "a seconds value beyond the ability's real max duration must not deal more than the full-duration total");
    }
}
