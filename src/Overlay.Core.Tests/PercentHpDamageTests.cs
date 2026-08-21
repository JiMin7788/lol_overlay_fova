using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// Proves T7: percent-of-HP skill/passive damage, with the fraction resolved LIVE per rank from
/// each champion's BIN DataValue (NOT a baked literal — honoring the "patch-dependent values are
/// always a dynamic BIN lookup" Hard Rule). The curated <see cref="SkillHit"/> names the DataValue
/// (<c>hpPercentDataValue</c>); <see cref="ComboRunner"/> resolves it via
/// <see cref="SkillDamage.ResolveHpPercent"/> and turns a %-max-HP hit into a flat number
/// (fraction × the target's MaxHP) the M05 engine still mitigates by the hit's own type:
///  1. Vayne W (%-max-HP TRUE) = fraction × MaxHP, INDEPENDENT of armor/MR (true bypasses resist).
///  2. Vayne W is per-RANK: a different ability rank resolves a different fraction (dynamic, not
///     a constant) — hand-verified against the MaxHealthRatio array.
///  3. Brand P (%-max-HP MAGIC) is mitigated by MR; its whole-percent DataValue (2.0) normalizes
///     to a 0.02 fraction.
///  4. Backward-compat: a hit WITHOUT the new fields resolves the SAME number as before.
///  5. The loader parses hpPercentDataValue/hpBasis and defaults them when absent.
///  6-8. T7 extension: Skarner P (Essence Sting) proves <see cref="SkillHit.HpPercentCalc"/> —
///     for a %HP fraction that lives ONLY inside a BIN GameCalculation (ByCharLevelInterpolation
///     x mMultiplier), not any DataValues array entry, resolved via
///     <see cref="SkillDamage.ResolveHpPercentCalc"/> instead of <see cref="SkillDamage.ResolveHpPercent"/>,
///     then shaped by TryBuildHitShape exactly like a DataValue-sourced %HP hit (fraction x
///     defenderMaxHp, mitigated by the hit's own damage type). Tests 1-3b above are themselves the
///     backward-compat regression check for the pre-existing hpPercentDataValue path (Vayne
///     W / Brand P / Sejuani P): they are unmodified by the hpPercentCalc addition and still pass
///     with the same hand-computed numbers, proving the new field didn't disturb the old path.
/// All numbers are hand-verified from the cached BIN DataValues/GameCalculations.
/// </summary>
public class PercentHpDamageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public PercentHpDamageTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "PercentHpDamageTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────

    private static string FixturePath(string champion)
        => Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{champion}.bin.json");

    /// <summary>Loads a champion straight from its cached BIN with ZERO base stats (so the passive
    /// "P" slot + its DataValues/EffectAmounts are present), matching the PassiveBonusEffectTests
    /// style. A %HP hit reads a DataValue off this real skill data.</summary>
    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(FixturePath(championId.ToLowerInvariant()));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);

        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot,
                Name = championId + slot,
                DataValues = bin.DataValues,
                SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };

        return new ChampionData
        {
            Id = championId,
            Name = championId,
            Skills = skills,
            BaseStats = new ChampionBaseStats(),
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy(string id, double hp = 0, double armor = 0, double mr = 0) => new()
    {
        Id = id,
        Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ComboNode SkillNode(string slot) => new(
        Id: $"{slot}_0",
        NodeType: ComboNodeType.Skill,
        Name: slot,
        Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Magic,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    private static GameSnapshot Snapshot(string activeChampion, ActivePlayerStats stats,
        string enemyChampion, int enemyLevel, int level = 6)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = level,
            PlayerCount = 2,
            Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = activeChampion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = level;

        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = enemyChampion;
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = enemyLevel;
        return snap;
    }

    private ComboResult RunCombo(string championId, string[] slots, GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo(championId, "c");
        foreach (var s in slots) editor.AddNode(draft.Id, SkillNode(s));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
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

    // Vayne W BIN DataValue MaxHealthRatio, indexed directly by ability rank (index 0 is the
    // rank-0 slot, same convention as every other DataValue lookup in the interpreter).
    private static readonly double[] VayneMaxHealthRatio =
        { 0.05, 0.06, 0.07, 0.08, 0.09, 0.10, 0.11 };

    private static ActivePlayerStats StatsWithWRank(int wRank) =>
        new() { AbilityW = wRank, AbilityQ = 5, AbilityE = 5 };

    // ── 1. Vayne W %-max-HP TRUE = fraction×MaxHP, independent of resist ───────────────

    [Fact]
    public void VayneW_PercentMaxHpTrue_EqualsFractionTimesMaxHp_AndIgnoresResist()
    {
        int wRank = 4; // MaxHealthRatio[4] = 0.09
        double maxHp = 2000;
        double expected = VayneMaxHealthRatio[wRank] * maxHp; // 0.09 * 2000 = 180

        // Enemy with a KNOWN MaxHP and DELIBERATELY high armor+MR — true damage must ignore both.
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Vayne"),
            Dummy("Tank", hp: maxHp, armor: 300, mr: 300),
        });

        // Sanity: the fraction really is resolved live from the BIN DataValue at this rank.
        var vayne = LoadChampionFromBin("Vayne");
        Assert.Equal(0.09, SkillDamage.ResolveHpPercent(vayne, "W", "MaxHealthRatio", StatsWithWRank(wRank))!.Value, precision: 4);

        var snap = Snapshot("Vayne", StatsWithWRank(wRank), enemyChampion: "Tank", enemyLevel: 1);
        var result = RunCombo("Vayne", new[] { "W" }, snap);

        Assert.Equal(expected, result.TotalDamage, precision: 2); // 180 despite 300/300 resist
        Assert.Single(result.NodeBreakdown);
    }

    // ── 2. Vayne W is per-RANK dynamic (different rank -> different fraction) ───────────

    [Fact]
    public void VayneW_FractionVariesByRank_ProvingDynamicBinLookup()
    {
        double maxHp = 2000;
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Vayne"),
            Dummy("Squishy", hp: maxHp), // 0 resist so total == raw true damage
        });

        // Rank 4 -> MaxHealthRatio[4] = 0.09 -> 180.
        var high = RunCombo("Vayne", new[] { "W" },
            Snapshot("Vayne", StatsWithWRank(4), "Squishy", enemyLevel: 1));
        Assert.Equal(VayneMaxHealthRatio[4] * maxHp, high.TotalDamage, precision: 2); // 180

        // Rank 2 -> MaxHealthRatio[2] = 0.07 -> 140. A LITERAL would give the same number at both
        // ranks; a live per-rank lookup gives a different one. Reset the bus so the first run's
        // now-disposed UI.COMBO_RESULT gate does not fire on the second publish.
        EventBus.EventBus.ResetForTests();
        SkillDamageDb.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Vayne"), Dummy("Squishy", hp: maxHp) });
        var low = RunCombo("Vayne", new[] { "W" },
            Snapshot("Vayne", StatsWithWRank(2), "Squishy", enemyLevel: 1));
        Assert.Equal(VayneMaxHealthRatio[2] * maxHp, low.TotalDamage, precision: 2); // 140

        Assert.True(high.TotalDamage > low.TotalDamage + 0.01,
            "a higher W rank must resolve a higher %HP fraction (dynamic, not a baked constant)");
    }

    // ── 3. Brand P %-max-HP MAGIC is mitigated by MR (whole-percent DataValue -> /100) ──

    [Fact]
    public void BrandP_PercentMaxHpMagic_NormalizesWholePercent_AndIsMitigatedByMr()
    {
        double maxHp = 2000;
        double mr = 50;
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Brand"),
            Dummy("Target", hp: maxHp, mr: mr),
        });

        // BIN PercentHealthDamage = 2.0 (whole percent) -> normalized to a 0.02 fraction.
        var brand = LoadChampionFromBin("Brand");
        double frac = SkillDamage.ResolveHpPercent(brand, "P", "PercentHealthDamage",
            new ActivePlayerStats(), level: 6)!.Value;
        Assert.Equal(0.02, frac, precision: 4);

        var snap = Snapshot("Brand", new ActivePlayerStats(), enemyChampion: "Target", enemyLevel: 6);
        var result = RunCombo("Brand", new[] { "P" }, snap);

        double raw = frac * maxHp;                                      // 0.02 * 2000 = 40 magic
        double expected = Math.Round(raw * (100.0 / (100.0 + mr)), 2);  // 40 * 100/150 = 26.67
        Assert.Equal(expected, result.TotalDamage, precision: 2);
        Assert.True(result.TotalDamage < raw, "MR must reduce a magic %HP hit");
    }

    // ── 3b. Sejuani P %-max-HP MAGIC (T3.x-ext): flat DataValue, no interpolation ───────

    [Fact]
    public void SejuaniP_PercentMaxHpMagic_IsFlatFraction_AndIsMitigatedByMr()
    {
        double maxHp = 2000;
        double mr = 50;
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Sejuani"),
            Dummy("Target", hp: maxHp, mr: mr),
        });

        // BIN PercentHPDamageBase = 0.1 (already a fraction, < 1) -> used as-is, no /100 needed,
        // and no ByCharLevel/stat term at all, so it is the same at every level/rank.
        var sejuani = LoadChampionFromBin("Sejuani");
        double frac = SkillDamage.ResolveHpPercent(sejuani, "P", "PercentHPDamageBase",
            new ActivePlayerStats(), level: 6)!.Value;
        Assert.Equal(0.1, frac, precision: 4);

        var snap = Snapshot("Sejuani", new ActivePlayerStats(), enemyChampion: "Target", enemyLevel: 6);
        var result = RunCombo("Sejuani", new[] { "P" }, snap);

        double raw = frac * maxHp;                                      // 0.1 * 2000 = 200 magic
        double expected = Math.Round(raw * (100.0 / (100.0 + mr)), 2);  // 200 * 100/150 = 133.33
        Assert.Equal(expected, result.TotalDamage, precision: 2);
        Assert.True(result.TotalDamage < raw, "MR must reduce a magic %HP hit");
    }

    // ── 4. backward-compat: a non-%HP hit resolves the SAME number as before ───────────

    [Fact]
    public void NonPercentHpHit_IsUnchanged_ResolvesFromCalcAsBefore()
    {
        // Vayne E (Condemn) is an ordinary flat/ratio hit (no %HP fields). It must resolve from its
        // BIN calc exactly as before the schema change; enemy has 0 resist so it is unmitigated.
        var e = Assert.Single(SkillDamageDb.GetHits("Vayne", "E")!);
        Assert.Null(e.HpPercentDataValue);
        Assert.Equal(0.0, e.HpPercent);
        Assert.Equal("TotalDamage", e.Calc);

        var vayne = LoadChampionFromBin("Vayne");
        ChampionRepository.Initialize(new[] { vayne, Dummy("Target", hp: 1000) });

        var stats = new ActivePlayerStats { AbilityE = 5, AttackDamage = 100, AbilityPower = 100 };
        double expected = SkillDamage.ComputeCalcDamage(vayne, "E", e.Calc, stats, level: 6)!.Value;
        Assert.True(expected > 0, "Vayne E calc must resolve to a real number");

        var snap = Snapshot("Vayne", stats, enemyChampion: "Target", enemyLevel: 1);
        var result = RunCombo("Vayne", new[] { "E" }, snap);

        Assert.Equal(expected, result.TotalDamage, precision: 2);
        Assert.Single(result.NodeBreakdown);
    }

    // ── 5. loader parses hpPercentDataValue/hpBasis and defaults them when absent ───────

    [Fact]
    public void Loader_ParsesHpPercentDataValueAndBasis_AndDefaultsWhenAbsent()
    {
        // Present: Vayne W names its BIN DataValue and carries NO baked literal.
        var w = Assert.Single(SkillDamageDb.GetHits("Vayne", "W")!);
        Assert.Equal("MaxHealthRatio", w.HpPercentDataValue);
        Assert.Equal(0.0, w.HpPercent);          // no baked constant
        Assert.Equal(HpBasis.Max, w.HpBasis);
        Assert.Equal(HitDamageType.True, w.Type);

        // Absent: Vayne Q has no %HP fields -> defaults (null / 0 / Max), and stays a calc hit.
        var q = Assert.Single(SkillDamageDb.GetHits("Vayne", "Q")!);
        Assert.Null(q.HpPercentDataValue);
        Assert.Equal(0.0, q.HpPercent);
        Assert.Equal(HpBasis.Max, q.HpBasis);
        Assert.False(string.IsNullOrEmpty(q.Calc), "a non-%HP hit still carries its calc");

        // Brand P: magic %HP sourced from PercentHealthDamage, no baked literal.
        var p = Assert.Single(SkillDamageDb.GetHits("Brand", "P")!);
        Assert.Equal("PercentHealthDamage", p.HpPercentDataValue);
        Assert.Equal(0.0, p.HpPercent);
        Assert.Equal(HpBasis.Max, p.HpBasis);
        Assert.Equal(HitDamageType.Magic, p.Type);
    }

    // ── 6. Skarner P %-max-HP MAGIC sourced via hpPercentCalc (NOT hpPercentDataValue) ──
    //
    // SkarnerPassive.mSpellCalculations "PercentHealthDamage" = ByCharLevelInterpolation(5.0->9.0)
    // x mMultiplier(0.01) -- a %maxHP fraction that lives ONLY inside a GameCalculation formula
    // tree. SkarnerPassive's mSpell.DataValues has NO entry named "PercentHealthDamage" (only
    // StacksToTriggerPassive/Duration/TickFrequency), so the pre-existing hpPercentDataValue path
    // cannot resolve it -- proven below by asserting ResolveHpPercent returns null for it.

    [Fact]
    public void SkarnerP_HasNoMatchingDataValue_ProvingHpPercentCalcIsRequired()
    {
        var skarner = LoadChampionFromBin("Skarner");
        Assert.Null(SkillDamage.ResolveHpPercent(skarner, "P", "PercentHealthDamage", new ActivePlayerStats(), level: 6));
        Assert.Equal(0.0617647059, SkillDamage.ResolveHpPercentCalc(skarner, "P", "PercentHealthDamage", new ActivePlayerStats(), level: 6)!.Value, precision: 6);
    }

    [Fact]
    public void SkarnerP_PercentMaxHpMagic_ViaHpPercentCalc_EqualsFractionTimesMaxHp_AndIsMitigatedByMr()
    {
        double maxHp = 2000;
        double mr = 50;
        // Champion level 1: ByCharLevelInterpolation t = (1-1)/17 = 0 -> start value 5.0 -> x0.01 = 0.05.
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Skarner"),
            Dummy("Target", hp: maxHp, mr: mr),
        });

        var skarner = LoadChampionFromBin("Skarner");
        double frac = SkillDamage.ResolveHpPercentCalc(skarner, "P", "PercentHealthDamage",
            new ActivePlayerStats(), level: 1)!.Value;
        Assert.Equal(0.05, frac, precision: 4);

        var snap = Snapshot("Skarner", new ActivePlayerStats(), enemyChampion: "Target", enemyLevel: 1, level: 1);
        var result = RunCombo("Skarner", new[] { "P" }, snap);

        double raw = frac * maxHp;                                      // 0.05 * 2000 = 100 magic
        double expected = Math.Round(raw * (100.0 / (100.0 + mr)), 2);  // 100 * 100/150 = 66.67
        Assert.Equal(expected, result.TotalDamage, precision: 2);
        Assert.True(result.TotalDamage < raw, "MR must reduce a magic %HP hit");
    }

    // ── 7. Skarner P is per-CHAMPION-LEVEL dynamic (a GameCalculation, not a baked constant) ──

    [Fact]
    public void SkarnerP_FractionVariesByChampionLevel_ProvingDynamicCalcEvaluation()
    {
        double maxHp = 2000;
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Skarner"),
            Dummy("Squishy", hp: maxHp), // 0 resist so total == raw magic damage
        });

        // Level 1 -> t=(1-1)/17=0 -> 5.0*0.01 = 0.05 -> 100.
        var low = RunCombo("Skarner", new[] { "P" },
            Snapshot("Skarner", new ActivePlayerStats(), "Squishy", enemyLevel: 1, level: 1));
        Assert.Equal(0.05 * maxHp, low.TotalDamage, precision: 2); // 100

        // Level 18 -> t=(18-1)/17=1 (clamped) -> 9.0*0.01 = 0.09 -> 180. A LITERAL/DataValue-array
        // fraction would give the same number at both levels since Skarner P has no such array
        // entry at all; a live GameCalculation evaluation gives a different one per champion level.
        EventBus.EventBus.ResetForTests();
        SkillDamageDb.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Skarner"), Dummy("Squishy", hp: maxHp) });
        var high = RunCombo("Skarner", new[] { "P" },
            Snapshot("Skarner", new ActivePlayerStats(), "Squishy", enemyLevel: 1, level: 18));
        Assert.Equal(0.09 * maxHp, high.TotalDamage, precision: 2); // 180

        Assert.True(high.TotalDamage > low.TotalDamage + 0.01,
            "a higher champion level must resolve a higher %HP fraction (dynamic, not a baked constant)");
    }

    // ── 8. loader parses hpPercentCalc and defaults it when absent ─────────────────────

    [Fact]
    public void Loader_ParsesHpPercentCalc_AndDefaultsWhenAbsent()
    {
        // Present: Skarner P names its BIN GameCalculation and carries neither a DataValue name
        // nor a baked literal.
        var p = Assert.Single(SkillDamageDb.GetHits("Skarner", "P")!);
        Assert.Equal("PercentHealthDamage", p.HpPercentCalc);
        Assert.Null(p.HpPercentDataValue);
        Assert.Equal(0.0, p.HpPercent);
        Assert.Equal(HpBasis.Max, p.HpBasis);
        Assert.Equal(HitDamageType.Magic, p.Type);

        // Absent: Vayne W (hpPercentDataValue path) and Vayne Q (plain calc) both carry no
        // hpPercentCalc -> defaults to null, unaffected by the new field.
        var w = Assert.Single(SkillDamageDb.GetHits("Vayne", "W")!);
        Assert.Null(w.HpPercentCalc);
        var q = Assert.Single(SkillDamageDb.GetHits("Vayne", "Q")!);
        Assert.Null(q.HpPercentCalc);
    }

    // ── 9. T-new2 %HP pass (Fiora/Gwen/Aurora/Lillia): further hand-verified live BIN numbers ──
    // All four are DIRECT P hits (SkillDamageDb.GetHits, same shape as Skarner/Brand/Sejuani above),
    // not bonusEffects — a bonusEffects onHit/onAbility trigger only fires when attached to an
    // AA/ability node, not when "P" is run as its own combo node, so the direct-hit shape is what
    // ComboRunner actually resolves for a standalone P SkillNode (see each champion's _noteP).

    [Fact]
    public void FioraP_PassiveDamageTotal_ViaHpPercentCalc_EqualsFractionTimesMaxHp()
    {
        double maxHp = 2000;
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Fiora"),
            Dummy("Target", hp: maxHp),
        });

        // PassiveDamageTotal = NamedDataValue('PassiveDamageBase'=0.03, flat) +
        // StatByNamedDataValue(mStat=2, mStatFormula=2 -> BONUS AD, 'PassiveDamageADRatio'=0.0004).
        // At bonus AD 100 (AttackDamage=100, base AD 0 in this harness): 0.03 + 0.0004*100 = 0.07.
        var fiora = LoadChampionFromBin("Fiora");
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        double frac = SkillDamage.ResolveHpPercentCalc(fiora, "P", "PassiveDamageTotal", stats, level: 6)!.Value;
        Assert.Equal(0.07, frac, precision: 4);

        var snap = Snapshot("Fiora", stats, enemyChampion: "Target", enemyLevel: 6);
        var result = RunCombo("Fiora", new[] { "P" }, snap);

        Assert.Equal(0.07 * maxHp, result.TotalDamage, precision: 2); // 140.0, no resist on target
    }

    [Fact]
    public void GwenP_PercentHealth1000Cuts_ViaHpPercentCalc_EqualsFractionTimesMaxHp()
    {
        double maxHp = 2000;
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Gwen"),
            Dummy("Target", hp: maxHp),
        });

        // '{540bce84}' = NumberCalculationPart(1.0) + StatByCoefficient(0.006, no mStat -> AP)
        // (percent-POINTS) x mMultiplier(0.01) via 'PercentHealth1000Cuts' (GameCalculationModified)
        // = fraction. At AP=100: (1.0 + 0.006*100) * 0.01 = 1.6 * 0.01 = 0.016.
        var gwen = LoadChampionFromBin("Gwen");
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double frac = SkillDamage.ResolveHpPercentCalc(gwen, "P", "PercentHealth1000Cuts", stats, level: 6)!.Value;
        Assert.Equal(0.016, frac, precision: 4);

        var snap = Snapshot("Gwen", stats, enemyChampion: "Target", enemyLevel: 6);
        var result = RunCombo("Gwen", new[] { "P" }, snap);

        Assert.Equal(0.016 * maxHp, result.TotalDamage, precision: 2); // 32.0, no resist on target
    }

    [Fact]
    public void AuroraP_ProcDamage_ViaHpPercentCalc_EqualsFractionTimesMaxHp()
    {
        double maxHp = 2000;
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Aurora"),
            Dummy("Target", hp: maxHp),
        });

        // ProcDamage = NamedDataValue('BaseHPDamage'=0.01, flat) + StatByCoefficient(0.00027, no
        // mStat -> AP), mDisplayAsPercent -> already a fraction. At AP=100: 0.01 + 0.00027*100
        //   = 0.037.
        var aurora = LoadChampionFromBin("Aurora");
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double frac = SkillDamage.ResolveHpPercentCalc(aurora, "P", "ProcDamage", stats, level: 6)!.Value;
        Assert.Equal(0.037, frac, precision: 4);

        var snap = Snapshot("Aurora", stats, enemyChampion: "Target", enemyLevel: 6);
        var result = RunCombo("Aurora", new[] { "P" }, snap);

        Assert.Equal(0.037 * maxHp, result.TotalDamage, precision: 2); // 74.0, no resist on target
    }

    [Fact]
    public void LilliaP_DotPercentTooltip_ViaHpPercentCalc_EqualsFractionTimesMaxHp()
    {
        double maxHp = 2000;
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Lillia"),
            Dummy("Target", hp: maxHp),
        });

        // DotPercentTotal = NamedDataValue('DotDamagePercent'=5.0, flat) + StatByCoefficient(0.0125,
        // no mStat -> AP) (percent-POINTS) x mMultiplier(0.01) via 'DotPercentTooltip'
        // (GameCalculationModified) = fraction. At AP=100: (5.0 + 0.0125*100) * 0.01
        //   = 6.25 * 0.01 = 0.0625.
        var lillia = LoadChampionFromBin("Lillia");
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double frac = SkillDamage.ResolveHpPercentCalc(lillia, "P", "DotPercentTooltip", stats, level: 6)!.Value;
        Assert.Equal(0.0625, frac, precision: 4);

        var snap = Snapshot("Lillia", stats, enemyChampion: "Target", enemyLevel: 6);
        var result = RunCombo("Lillia", new[] { "P" }, snap);

        Assert.Equal(0.0625 * maxHp, result.TotalDamage, precision: 2); // 125.0, no resist on target
    }
}
