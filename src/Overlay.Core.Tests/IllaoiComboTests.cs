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
/// Full-kit combo coverage for Illaoi (release_checklist.md open item) against the REAL curated
/// <c>data/skill_damage/Illaoi.json</c> + live <c>illaoi.bin.json</c> BIN numbers, run through the
/// actual ComboEngine/ComboEditor/ComboRunner via the EventBus (same harness as
/// <see cref="GoldenKsanteTests"/>/<see cref="AbilitySlotOnHitGatingTests"/>).
///
/// R's second hit is the file's headline mechanic under test: <c>"perAttackCalc":
/// "TentacleDamageTotal", "binSpell": "Q"</c> — Illaoi's real DPS driver (the ring of Tentacles her R
/// spawns, each slamming the same TentacleDamageTotal calc Q itself uses). It scales by
/// <see cref="ComboNode.UserAttackCount"/> ("몇 번의 텐타클 슬램" in the combo editor): unset/0 ->
/// contributes 0 (the honest floor — how many slams land is board-state dependent, P2), and the
/// contribution is exactly <c>UserAttackCount x one tentacle slam's raw value</c>. This file proves
/// that scaling at 3 different counts.
///
/// FIXTURE CHOICES:
///  - AbilityPower = 0 (Illaoi is a pure-AD bruiser) — Q/R's AP-ratio terms then collapse to 0
///    exactly, keeping the arithmetic to the AD/flat terms actually curated as primary.
///  - BaseStats are all 0 (LoadChampionFromBin, same as AbilitySlotOnHitGatingTests), so "bonus"
///    AD/health == the live stat verbatim.
///  - Attacker level 18 so Q/R's <c>ByCharLevelInterpolationCalculationPart</c> reaches its END value
///    exactly (t = (18-1)/17 = 1.0), a clean reproducible endpoint.
///  - A real 2000-HP, 0-armor/0-MR target IS placed on the board (unlike the other 3 new files),
///    because Illaoi W is a genuine %maxHP hit (hpBasis=Max) that needs a concrete target maxHP to
///    assert against; 0 resists keep the mitigation multiplier at exactly k=1 so the %HP arithmetic
///    isn't obscured by a second, unrelated ratio.
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs; see
/// CLAUDE_CODE_TODO.md's build+test entry for the exact `dotnet test --filter`.
/// </summary>
public class IllaoiComboTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "Illaoi";
    private const double TargetHp = 2000;

    public IllaoiComboTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "IllaoiComboTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────

    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{championId.ToLowerInvariant()}.bin.json"));
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

    private static ChampionData Dummy(string id, double hp, double armor, double mr) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(), // 0 growth -> maxHp/resists stay pinned at the above
    };

    private static ComboNode SkillNode(string slot, int? userAttackCount = null) => new(
        Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot,
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0,
        UserAttackCount: userAttackCount);

    private static ComboNode AaNode() => new(
        Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA",
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    private static GameSnapshot Snapshot(ActivePlayerStats stats, int level)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = level, PlayerCount = 2, Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = ChampId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = level;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 18;
        return snap;
    }

    private void InitRepo()
    {
        var illaoi = LoadChampionFromBin(ChampId);
        ChampionRepository.Initialize(new[] { illaoi, Dummy("Target", TargetHp, 0, 0) });
    }

    private ComboResult RunCombo(string[] nodeSpecs, GameSnapshot snap, int? rAttackCount = null)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo(ChampId, "c");
        foreach (var s in nodeSpecs)
            editor.AddNode(draft.Id, s == "AA" ? AaNode() : SkillNode(s, s == "R" ? rAttackCount : null));
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
    private const int Level = 18; // ByCharLevelInterpolation t = (18-1)/17 = 1.0 for Q's base term
    private static ActivePlayerStats Stats() => new() { AttackDamage = Ad, AbilityPower = Ap, AbilityQ = 5, AbilityW = 5, AbilityR = 3 };

    // ── 1. base AA alone: Illaoi's P has no direct-damage calc at all (confirmed no-damage per
    // Illaoi.json's own _noteP — SpawnCD is a cooldown timer, not a hit), so AA alone is pure AD. ───

    [Fact]
    public void Illaoi_AaAlone_IsPureAd_NoPassiveOnHit()
    {
        InitRepo();
        var result = RunCombo(new[] { "AA" }, Snapshot(Stats(), Level));

        Assert.Equal(100.0, result.TotalDamage, precision: 2); // AD 100, target armor 0 -> unmitigated
        Assert.Single(result.NodeBreakdown);
    }

    // ── 2. one skill alone per damage-dealing slot (Q, W, R's own ground-slam hit) ───────────────

    [Fact]
    public void Illaoi_QAlone_TentacleDamageTotal()
    {
        // TentacleDamageTotal = [ByCharLevelInterpolation(9 -> 180) + StatByNamedDataValue(mStat=2,
        // TotalADRatio[rank5]=1.1, no mStatFormula -> TOTAL AD) + StatByNamedDataValue(APRatio[rank5]
        // =0.4, no mStat -> AP)] x mMultiplier(1 + TentacleDamageAmp[rank5]).
        // At level 18, t=1.0 -> the interpolation term = 180 (its END value) exactly.
        // TentacleDamageAmp[5] = 0.30 (illaoi.bin.json Q DataValues: [0,.10,.15,.20,.25,.30,.35],
        // index = rank) -> multiplier = 1.30.
        // Inner sum = 180 + 1.1*100 (AD) + 0.4*0 (AP) = 180 + 110 = 290.
        // TentacleDamageTotal = 290 * 1.30 = 377.
        InitRepo();
        var result = RunCombo(new[] { "Q" }, Snapshot(Stats(), Level));

        Assert.Equal(377.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Illaoi_WAlone_PercentMaxHpHit()
    {
        // HealthPercentTotal = 0.01 x [NamedDataValue(HealthPercentDamage, rank5)=5.0 +
        // StatByNamedDataValue(mStat=2, HealthDamageADRatio[rank]=0.035, no mStatFormula -> TOTAL AD)].
        // Fraction = 0.01 * (5.0 + 0.035*100) = 0.01 * (5.0 + 3.5) = 0.01*8.5 = 0.085 (8.5% max HP).
        // hpBasis=Max -> damage = fraction x target maxHP (2000) = 0.085*2000 = 170, then mitigated as
        // a normal Physical hit (target armor 0 -> k=1, unchanged).
        InitRepo();
        var result = RunCombo(new[] { "W" }, Snapshot(Stats(), Level));

        Assert.Equal(170.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Illaoi_RAlone_WithNoTentacleSlams_IsOnlyTheGroundSlam()
    {
        // DamageCalc (R's own hit1, always fires) = NamedDataValue(BaseDamage, rank3) +
        // StatByCoefficient(mStat=2, mStatFormula=2 -> bonus AD (== total AD, base 0), coefficient 0.5).
        // BaseDamage[3] = 350 (illaoi.bin.json R DataValues: [50,150,250,350,450,550,650], index=rank).
        // DamageCalc = 350 + 0.5*100 = 400. UserAttackCount unset -> hit2 (the tentacle-slam summon
        // hit) contributes exactly 0 (the honest floor, see class remarks), not fabricated/omitted.
        InitRepo();
        var result = RunCombo(new[] { "R" }, Snapshot(Stats(), Level), rAttackCount: null);

        Assert.Equal(400.0, result.TotalDamage, precision: 2);
    }

    // ── R's tentacle-slam count actually scales the total (the mechanic under test) ─────────────

    [Theory]
    [InlineData(0, 400.0)]  // hit1 only (400) + 0 slams
    [InlineData(1, 777.0)]  // 400 + 1 x 377 (one tentacle slam, same TentacleDamageTotal as Q)
    [InlineData(3, 1531.0)] // 400 + 3 x 377
    public void Illaoi_R_TentacleSlamCount_ScalesTheSecondHitLinearly(int attackCount, double expectedTotal)
    {
        // hit2 = UserAttackCount x TentacleDamageTotal(rank5,AD100,AP0,level18) = UserAttackCount x 377
        // (identical calc to the Q-alone test above, resolved via binSpell="Q" against the SAME Q
        // ability rank). Proven at 3 different counts to demonstrate real linear scaling, not a
        // coincidental match at one value.
        InitRepo();
        var result = RunCombo(new[] { "R" }, Snapshot(Stats(), Level), rAttackCount: attackCount == 0 ? null : attackCount);

        Assert.Equal(expectedTotal, result.TotalDamage, precision: 2);
    }

    // ── 3. full combo: Q + W + R (with 2 tentacle slams) ────────────────────────────────────────

    [Fact]
    public void Illaoi_FullCombo_QThenWThenR_WithTwoTentacleSlams_SumsToExpectedTotal()
    {
        // Q (377) + W (170) + R (400 ground slam + 2*377 tentacle slams = 1154) = 377 + 170 + 1154
        // = 1701.
        InitRepo();
        var result = RunCombo(new[] { "Q", "W", "R" }, Snapshot(Stats(), Level), rAttackCount: 2);

        Assert.Equal(1701.0, result.TotalDamage, precision: 2);
    }
}
