using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #14 — Gnar (golden-unlock round 2026-07-26): the SAME-OBJECT multiform golden. Unlike
/// Jayce (separate Cannon spell objects), Riot stores Gnar's Mini+Mega calcs on one spell object
/// per slot, so the Mega slots route binSpell to the CANONICAL slot key ("Q") — these rows lock
/// the canonical-binSpell own-rank rule (loop 421) and the same-object multiform path end to end.
///
/// SETUP (measured 2026-07-26, clean rune page — no damage runes, adaptive shard as +5.4 AD, NO
/// items): level 6, ALL RANKS 1, dummy 1000 HP / 50 armor / 50 MR (×2/3 both types).
/// Mini form: total AD 78 (base 72.6 + 5.4 shard). Mega form: total AD 93 (AA-pinned: 62 × 1.5).
/// Own max health (the 6% mStat-12 term in both E forms) was not recorded on the stat panel —
/// SOLVED from the E readings themselves (Mini 904, Mega 1179), so the E rows pin the
/// flat + 6%-own-HP SHAPE and the mStat-12 resolution path, not an independent HP source.
///
/// Reconciled BEFORE encoding (raw → ×2/3):
///   Mini AA 52 (=78×2/3 exact) · Mini Q outgoing 68 (5 + 1.25×78 → 102.5); the return hit was
///   not landed, so the full-node pin derives return = outgoing × MiniSubsequentMult(0.5) from
///   the BIN · Mini W 3rd-hit proc 39 (rank-1 flat 0 + 6%×1000 → 60; the §41 sweep's zero-flat
///   finding confirmed live) · Mini E 69~70 (50 + 6%×904) · Mega AA 62 (AD 93) · Mega Q 116~117
///   (45 + 1.4×93 → 175.2) · Mega W 92 (45 + 1.0×93 → 138, EXACT) · Mega E 100~101 (80 + 6%×1179)
///   · Mega R 135 no-wall (200 + 1.0×0 AP + 0.5×5.4 bonus AD → 202.7) and 203 wall
///   (× RWallHitDamageMultiplier 1.5 → 304.1 → 202.7 dealt — confirms the live wall multiplier;
///   the wall variant stays UNCURATED, conservative floor, so it is reconciled but unencoded).
///
/// MegaDamageAoE ADJUDICATED (closes the loop-417 audit item): the orphaned flat-375 DataValue
/// does NOT appear in single-target Mega E damage (100.5 dealt ≈ 80 + 6% own HP; 375 would have
/// added ~250 dealt) — it is an AoE/secondary cap or vestigial, NOT a missed damage term.
/// Gnar.json stays unchanged; _noteE_sweep2 records the resolution.
/// </summary>
public class GoldenGnarTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "Gnar";
    /// <summary>One adaptive shard taken as AD (Gnar defaults adaptive to AD); no items.</summary>
    private const double ShardAd = 5.4;
    private const double MiniAd = 78.0;
    private const double MegaAd = 93.0;
    /// <summary>Own max health per form, solved from the measured E rows (see class remarks).</summary>
    private const double MiniHp = 904.0;
    private const double MegaHp = 1179.0;

    public GoldenGnarTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenGnar_" + Guid.NewGuid().ToString("N"));
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

    private static ChampionData Gnar(double totalAd)
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
        // Pin base AD so bonus (total − base) = exactly the adaptive shard (R's 0.5× bonus-AD term).
        return new ChampionData
        {
            Id = ChampId, Name = ChampId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = totalAd - ShardAd },
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = 1000, Armor = 50, Mr = 50 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats(double ad, double ownHp) => new()
    {
        AttackDamage = ad, AbilityPower = 0, MaxHealth = ownHp,
        AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static GameSnapshot Snap(double ad, double ownHp)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2,
            Stats = Stats(ad, ownHp),
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

    private ComboResult RunMini(params ComboNode[] nodes) => Run(MiniAd, MiniHp, nodes);

    private ComboResult RunMega(params ComboNode[] nodes) => Run(MegaAd, MegaHp, nodes);

    private ComboResult Run(double ad, double ownHp, ComboNode[] nodes)
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { Gnar(ad), Dummy() });
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, "c");
        foreach (var n in nodes) editor.AddNode(draft.Id, n);
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snap(ad, ownHp));
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

    // ── Mini form (AD 78, own HP 904) ───────────────────────────────────────────────────

    [Fact]
    public void Golden_Gnar_Mini_AA()
    {
        AssertWithinOne(52, RunMini(AaNode()).TotalDamage, "Mini AA (78 ×2/3)");
    }

    [Fact]
    public void Golden_Gnar_Mini_Q_BoomerangBothHits()
    {
        // Measured outgoing hit 68; the return hit was not landed in the session, so the node
        // total pins outgoing + outgoing × MiniSubsequentMult(0.5) — the multiplier itself is
        // the BIN value the {4b371c72} curated hit encodes.
        AssertWithinOne(68 * 1.5, RunMini(SkillNode("Q")).TotalDamage,
            "Mini Q out+return ((5 + 1.25×78) × 1.5, ×2/3)");
    }

    [Fact]
    public void Golden_Gnar_Mini_W_ThirdHitProc_ZeroFlatPlusPercentHp()
    {
        // Rank-1 MiniBaseDamage is 0 — the whole proc is 6% of the TARGET's max HP (the §41
        // sweep's motivating case, now measured live: 39 ≈ 6%×1000 ×2/3 = 40).
        AssertWithinOne(39, RunMini(SkillNode("W")).TotalDamage,
            "Mini W 3rd-hit proc (0 flat + 6%×1000 target HP, ×2/3)");
    }

    [Fact]
    public void Golden_Gnar_Mini_E_FlatPlusOwnHp()
    {
        AssertWithinOne(69.5, RunMini(SkillNode("E")).TotalDamage,
            "Mini E (50 + 6%×904 own HP via mStat-12, ×2/3)");
    }

    // ── Mega form (AD 93, own HP 1179) — binSpell routes to the CANONICAL slot object ────

    [Fact]
    public void Golden_Gnar_Mega_AA()
    {
        AssertWithinOne(62, RunMega(AaNode()).TotalDamage, "Mega AA (93 ×2/3)");
    }

    [Fact]
    public void Golden_Gnar_Mega_Q_SameObjectBinSpell()
    {
        AssertWithinOne(116.5, RunMega(SkillNode("QMega")).TotalDamage,
            "Mega Q (45 + 1.4×93, ×2/3) — MegaTotalDamage on the same GnarQ object");
    }

    [Fact]
    public void Golden_Gnar_Mega_W_FullAdRatio()
    {
        AssertWithinOne(92, RunMega(SkillNode("WMega")).TotalDamage,
            "Mega W (45 + 1.0×93, ×2/3 — measured EXACT)");
    }

    [Fact]
    public void Golden_Gnar_Mega_E_NoOrphanedAoeValue()
    {
        // THE MegaDamageAoE adjudication row: 100.5 dealt fits 80 + 6%×1179 own HP; the orphaned
        // flat 375 (≈250 dealt) is demonstrably absent from single-target damage.
        AssertWithinOne(100.5, RunMega(SkillNode("EMega")).TotalDamage,
            "Mega E (80 + 6%×1179 own HP, ×2/3) — no 375 orphan in the single-target number");
    }

    [Fact]
    public void Golden_Gnar_Mega_R_NoWall_BonusAdTerm()
    {
        // 135 no-wall = (200 + 1.0×0 AP + 0.5×5.4 bonus AD) ×2/3. The measured 203 wall hit
        // (×1.5 RWallHitDamageMultiplier) reconciles but stays unencoded — the wall variant is
        // deliberately uncurated (conservative floor).
        AssertWithinOne(135, RunMega(SkillNode("R")).TotalDamage,
            "Mega R no-wall (200 + 0.5×5.4 bonus AD, ×2/3)");
    }
}
