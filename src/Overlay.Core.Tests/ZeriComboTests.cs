using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode exists in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// Full-kit combo coverage for Zeri (release_checklist.md open item) against the REAL curated
/// <c>data/skill_damage/Zeri.json</c> + live <c>zeri.bin.json</c> BIN numbers, run through the
/// actual ComboEngine/ComboEditor/ComboRunner via the EventBus (same harness as
/// <see cref="GoldenKsanteTests"/>/<see cref="AbilitySlotOnHitGatingTests"/>).
///
/// Zeri.json deliberately has NO "P" block (Living Battery is a genuine two-state conditional plus
/// an execute mechanic this schema can't express — see the file's own _noteP) — that is a documented
/// gap, not a bug, so this file covers only Q/W/E/R plus a full rotation, per the task brief.
///
/// Q/W/E/R are each a single plain <c>calc</c> hit (no bonusEffects, no conditional/percent/per-attack
/// shape) — the simplest of the 4 new champions' kits. Q ("ActiveDamageThatCanCrit", a basic-attack
/// replacement) is curated WITHOUT a <c>canCrit</c> flag on its hit, so the combo engine applies no
/// separate crit multiplier to it (SkillHit.CanCrit defaults false) — the raw calc value IS the
/// "no crit assumed" floor number itself (the §26 convention referenced in the file's own _note),
/// not a value this test needs to further discount.
///
/// FIXTURE CHOICES:
///  - AbilityPower = 0 (an AD-marksman baseline) — every Q/W/E/R formula below carries an AP term
///    that would otherwise add a 4th number to track; zeroing it collapses those terms to 0 exactly.
///  - BaseStats are all 0 (LoadChampionFromBin, same as AbilitySlotOnHitGatingTests), so "bonus"/
///    "total" AD are identical to the live stat.
///  - E's crit-scaling multiplier: BonusDamageTotal is wrapped in <c>1 + CritScalingMod(mStat=8) x
///    (CritDamage(mStat=9) − 1)</c>. mStat 8/9 (crit chance/damage) both resolve to the documented
///    §26 "model as no crit" floor of 0.0 (BuildStatResolver), so this multiplier is 0.0 x anything =
///    0 term inside the sum, collapsing the whole bracket to exactly 1 regardless of any other stat
///    — confirmed directly from zeri.bin.json + SkillDamage.cs, not assumed.
///  - No enemy is placed on the board (<c>PlayerCount = 1</c>), so every hit lands unmitigated
///    (FallbackDefender Armor 0 / MR 0, k = 1).
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs; see
/// CLAUDE_CODE_TODO.md's build+test entry for the exact `dotnet test --filter`.
/// </summary>
public class ZeriComboTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ZeriComboTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "ZeriComboTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── fixtures (mirrors AbilitySlotOnHitGatingTests' LoadChampionFromBin) ──────────────

    private static string FixturePath(string champion)
        => Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{champion}.bin.json");

    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(FixturePath(championId.ToLowerInvariant()));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);

        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
        {
            skills[slot] = new SkillData
            {
                Key = slot,
                Name = championId + slot,
                DataValues = bin.DataValues,
                SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        }

        return new ChampionData
        {
            Id = championId,
            Name = championId,
            Skills = skills,
            BaseStats = new ChampionBaseStats(),
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ComboNode SkillNode(string slot) => new(
        Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot,
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    private static ComboNode AaNode() => new(
        Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA",
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    private static GameSnapshot Snapshot(string activeChampion, ActivePlayerStats stats, int level)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = level,
            PlayerCount = 1,
            Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = activeChampion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = level;
        return snap;
    }

    private ComboResult RunCombo(string championId, string[] slots, GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo(championId, "c");
        foreach (var s in slots)
            editor.AddNode(draft.Id, s == "AA" ? AaNode() : SkillNode(s));
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

    private const double Ad = 100;
    private const double Ap = 0;
    private const int Level = 11; // arbitrary champion level; none of Q/W/E/R's terms are ByCharLevel*
    private static ActivePlayerStats Stats() => new() { AttackDamage = Ad, AbilityPower = Ap, AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3 };

    // ── 1. base AA alone (the floor): Zeri has no P, so a bare auto is pure AD ──────────────────

    [Fact]
    public void Zeri_AaAlone_IsPureAd_NoPassive()
    {
        var zeri = LoadChampionFromBin("Zeri");
        ChampionRepository.Initialize(new[] { zeri });

        var result = RunCombo("Zeri", new[] { "AA" }, Snapshot("Zeri", Stats(), Level));

        Assert.Equal(100.0, result.TotalDamage, precision: 2);
        Assert.Single(result.NodeBreakdown);
    }

    // ── 2. one skill alone per damage-dealing slot (Q/W/E/R) ────────────────────────────────────

    [Fact]
    public void Zeri_QAlone_ActiveDamageThatCanCrit_NoCritMultiplierApplied()
    {
        // ActiveDamageThatCanCrit = NamedDataValue(BaseDamage, rank5) + StatByNamedDataValue(mStat=2,
        // ActiveADRatio[rank5], no mStatFormula -> TOTAL AD). BaseDamage[5] = 38 (zeri.bin.json Q
        // DataValues: [18,22,26,30,34,38,42], index=rank). ActiveADRatio[5] = 1.10.
        // Q = 38 + 1.10*100 = 38 + 110 = 148. No canCrit flag on the curated hit -> no extra crit
        // scalar from the engine; this raw value is the whole answer.
        var zeri = LoadChampionFromBin("Zeri");
        ChampionRepository.Initialize(new[] { zeri });

        var result = RunCombo("Zeri", new[] { "Q" }, Snapshot("Zeri", Stats(), Level));

        Assert.Equal(148.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Zeri_WAlone_TotalDamage()
    {
        // TotalDamage = NamedDataValue(Damage, rank5) + StatByNamedDataValue(mStat=2, ADRatio=1.2, no
        // mStatFormula -> TOTAL AD) + StatByNamedDataValue(APRatio=0.5, no mStat -> AP).
        // Damage[5] = 190 (zeri.bin.json W DataValues: [-10,30,70,110,150,190,230], index=rank).
        // W = 190 + 1.2*100 + 0.5*0 = 190 + 120 = 310. Magic (per Zeri.json, a documented kit quirk —
        // AD-scaling but Magic damage type).
        var zeri = LoadChampionFromBin("Zeri");
        ChampionRepository.Initialize(new[] { zeri });

        var result = RunCombo("Zeri", new[] { "W" }, Snapshot("Zeri", Stats(), Level));

        Assert.Equal(310.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Zeri_EAlone_BonusDamageTotal_CritMultiplierCollapsesToOne()
    {
        // BonusDamageTotal = [NamedDataValue(BonusDamageBase, rank5) + StatByNamedDataValue
        // (BonusAPRatio=0.2, no mStat -> AP)] x mMultiplier(1 + CritScalingMod(mStat=8) x
        // (CritDamage(mStat=9) − 1)). BonusDamageBase[5] = 30 (zeri.bin.json E DataValues:
        // [20,22,24,26,28,30,32], index=rank). mStat 8 (crit chance) resolves to the §26 no-crit floor
        // 0.0, so the multiplier's inner product is 0 regardless of mStat 9 -> bracket = 1 + 0 = 1.
        // E = (30 + 0.2*0) * 1 = 30.
        var zeri = LoadChampionFromBin("Zeri");
        ChampionRepository.Initialize(new[] { zeri });

        var result = RunCombo("Zeri", new[] { "E" }, Snapshot("Zeri", Stats(), Level));

        Assert.Equal(30.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Zeri_RAlone_TotalActiveDamage()
    {
        // TotalActiveDamage = NamedDataValue(ActiveDamage, rank3) + StatByCoefficient(1.1, no mStat ->
        // AP) + StatByCoefficient(mStat=2, mStatFormula=2 -> bonus AD (== total, base 0), coefficient
        // 0.6). ActiveDamage[3] = 350 (zeri.bin.json R DataValues: [50,150,250,350,450,550,650],
        // index=rank). R = 350 + 1.1*0 + 0.6*100 = 350 + 60 = 410.
        var zeri = LoadChampionFromBin("Zeri");
        ChampionRepository.Initialize(new[] { zeri });

        var result = RunCombo("Zeri", new[] { "R" }, Snapshot("Zeri", Stats(), Level));

        Assert.Equal(410.0, result.TotalDamage, precision: 2);

        var tags = SkillDamageDb.GetSlotTags("Zeri", "R");
        Assert.Contains("BurstCeiling", tags);
        Assert.Contains("AoeUltimate", tags);
    }

    // ── 3. full rotation: Q + W + E + R ──────────────────────────────────────────────────────────

    [Fact]
    public void Zeri_FullRotation_QWER_SumsToExpectedTotal()
    {
        // 148 (Q) + 310 (W) + 30 (E) + 410 (R) = 898.
        var zeri = LoadChampionFromBin("Zeri");
        ChampionRepository.Initialize(new[] { zeri });

        var result = RunCombo("Zeri", new[] { "Q", "W", "E", "R" }, Snapshot("Zeri", Stats(), Level));

        Assert.Equal(898.0, result.TotalDamage, precision: 2);
    }
}
