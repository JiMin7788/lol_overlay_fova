using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #15 — Aphelios (golden-unlock round 2026-07-26): the HASHED-binSpell weapon golden.
/// The five per-weapon Q slots resolve top-level hashed BIN objects ({9501e989}…), the riskiest
/// lookup path in the curation set; the Q calcs are ByCharLevelBreakpoints (level-scaled, no
/// rank), so these rows also lock the championLevel evaluation path (M25 §12③).
///
/// SETUP (measured 2026-07-26, clean rune page, no items): level 6, total AD 85, AP 0, bonus AS
/// 36.295% (0.904 att/s), no armor pen, dummy 1000 HP / 50 armor / 50 MR (×2/3). Bonus AD = 20,
/// SOLVED from the R reading (129 raw = 125 + 0.2×bonus → 20; Aphelios' passive weapon-mastery
/// AD counts as bonus) and INDEPENDENTLY CONFIRMED by the Infernum R reading
/// (123 = 129 + 50 + 0.25×20 → 184 raw ×2/3 = 122.7).
///
/// Reconciled BEFORE encoding (raw → ×2/3):
///   AA 57 (85 ×2/3) · Calibrum Q 74 (engine 73.1) · Calibrum mark follow-up 12 = measured
///   69 − off-hand AA 57 (engine BonusDamagePerMark 18 raw = 12 dealt EXACT) · Gravitum Q 58
///   (engine 58.4) · Infernum Q 35 (engine 35.6; its off-hand follow-up AA 57 = plain AA line)
///   · Crescendum turret 49/shot with Severum off-hand (engine TurretDamage 48.7; the measured
///   54 with INFERNUM off-hand = ×1.1 InfernumDamageMultiplier — off-hand passive, reconciled
///   but not modeled) · Infernum AA 63 = 85×1.1×2/3 (the ×1.1 splash multiplier, unmodeled
///   weapon passive, reconciled) · R 86 (EXACT) · R-Infernum 123 (engine 122.7). The R
///   follow-up attack lines (+57 / +62) are the plain (×1.1 for Infernum) AA the ult triggers.
///   Severum Q RESOLVED (final corrected reading: 7 hits × 15 dealt): the live per-hit is the
///   standalone {9480e64f} calc EXACTLY (0.27×85 → 15.3, log-truncated 15) and the count is
///   StartingAttacks 6 + 1 AS-granted extra — re-curated to the Illaoi-R composite (guaranteed
///   6-hit floor + perAttackCalc knob for AS extras), replacing the tooltip TotalDamage calc
///   whose 0.19-base multiplier and fractional count were both off live.
///
/// This round also found and fixed the QCrescendum RATIO-AS-DAMAGE bug: the old curated calc
/// 'MiniDamageMin' was the 5%-per-chakram passive ratio (≈4 raw — a positivity-guard blind
/// spot), re-curated to the summon pattern perAttackCalc=TurretDamage × UserAttackCount.
/// </summary>
public class GoldenApheliosTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "Aphelios";
    private const double TotalAd = 85.0;
    /// <summary>Solved from R and cross-confirmed by R-Infernum (see class remarks).</summary>
    private const double BonusAd = 20.0;
    private const double AttackSpeed = 0.904;
    /// <summary>Panel bonus AS 36.295% → base = 0.904 / 1.36295.</summary>
    private const double BaseAs = 0.6633;

    public GoldenApheliosTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenAphelios_" + Guid.NewGuid().ToString("N"));
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

    private static ChampionData Aphelios()
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
            BaseStats = new ChampionBaseStats { Ad = TotalAd - BonusAd, AttackSpeed = BaseAs },
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
        AttackDamage = TotalAd, AbilityPower = 0, AttackSpeed = AttackSpeed,
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

    private void InitRepo() => ChampionRepository.Initialize(new[] { Aphelios(), Dummy() });

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

    // ── measured rows (post-mitigation vs 1000 HP / 50/50 dummy) ────────────────────────

    [Fact]
    public void Golden_Aphelios_AA()
    {
        InitRepo();
        AssertWithinOne(57, RunNodes(AaNode()).TotalDamage, "AA (85 ×2/3)");
    }

    [Theory]
    [InlineData("QCalibrum", 74)]
    [InlineData("QGravitum", 58)]
    [InlineData("QInfernum", 35)]
    public void Golden_Aphelios_WeaponQ_HashedBinSpell_LevelScaled(string slot, double measured)
    {
        InitRepo();
        AssertWithinOne(measured, RunNodes(SkillNode(slot)).TotalDamage,
            $"{slot} (hashed-binSpell object, ByCharLevelBreakpoints at level 6, ×2/3)");
    }

    [Fact]
    public void Golden_Aphelios_CalibrumMark_FollowUpBonus()
    {
        // Measured 69 = off-hand AA 57 + mark consumption 12. The mark is an AA rider outside
        // the slot model, so this pins the calc path directly.
        InitRepo();
        var champ = ChampionRepository.Get(ChampId)!;
        double? raw = SkillDamage.ComputeCalcDamage(champ, "{9501e989}", "BonusDamagePerMark",
            Stats(), level: 6, rankSlot: "Q");
        Assert.NotNull(raw);
        AssertWithinOne(12, raw!.Value * (2.0 / 3.0), "Calibrum mark (0.15×20 bonus AD + base, ×2/3)");
    }

    [Fact]
    public void Golden_Aphelios_CrescendumTurret_PerShot()
    {
        // 49/shot measured with the Severum off-hand (the 54 reading with Infernum off-hand is
        // the ×1.1 weapon passive — reconciled, deliberately unmodeled). Summon pattern: the
        // shot-count knob at 1 pins one turret shot.
        InitRepo();
        var oneShot = SkillNode("QCrescendum") with { UserAttackCount = 1 };
        AssertWithinOne(49, RunNodes(oneShot).TotalDamage,
            "Crescendum turret per shot (TurretDamage bylevel + AD sub-part, ×2/3)");
    }

    [Fact]
    public void Golden_Aphelios_SeverumQ_SixHitFloorPlusAsExtra()
    {
        // Final corrected live reading: 7 hits × 15 dealt at 36.3% bonus AS. Per-hit is the
        // standalone {9480e64f} calc (0.27×85 = 22.95 raw → 15.3 dealt, log-truncated to 15);
        // the re-curated slot = guaranteed 6-hit floor + AS extras via the attack-count knob.
        InitRepo();
        var champ = ChampionRepository.Get(ChampId)!;
        double? perHitRaw = SkillDamage.ComputeCalcDamage(champ, "{c872c72d}", "{9480e64f}",
            Stats(), level: 6, rankSlot: "Q");
        Assert.NotNull(perHitRaw);
        AssertWithinOne(15, perHitRaw!.Value * (2.0 / 3.0), "Severum per-hit (0.27×85, ×2/3)");
        var withAsExtra = SkillNode("QSeverum") with { UserAttackCount = 1 };
        AssertWithinOne(7 * perHitRaw.Value * (2.0 / 3.0), RunNodes(withAsExtra).TotalDamage,
            "Severum Q slot = 6-hit floor + 1 AS-granted extra (the measured 7-hit channel)");
    }

    [Fact]
    public void Golden_Aphelios_R_MoonlightVigil()
    {
        InitRepo();
        AssertWithinOne(86, RunNodes(SkillNode("R")).TotalDamage,
            "R (125 + 0.2×20 bonus AD, ×2/3 — measured EXACT; also the bonus-AD solve source)");
    }

    [Fact]
    public void Golden_Aphelios_RInfernum_TwoHitVariant()
    {
        InitRepo();
        AssertWithinOne(123, RunNodes(SkillNode("RInfernum")).TotalDamage,
            "R with Infernum (base 129 + bonus 50 + 0.25×20 raw, ×2/3)");
    }
}
