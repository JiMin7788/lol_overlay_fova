using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #13 — Hwei (golden-unlock round 2026-07-26): freezes the user's live Practice
/// Tool sessions for the muse sub-cast binSpell path (QQ), the E muse cast, and the WE
/// empowered-AA onHit rider — the three paths this round's silent-zero fixes touched.
///
/// SETUP (user-confirmed): Hwei level 6, total AD 67, AP 9 (one adaptive shard), target dummy
/// 1000 max HP / 50 armor / 50 MR (mitigation ×2/3), AND the Cut Down rune active (+8% damage
/// vs enemies above 60% max health — the dummy starts full, so every reading carries it).
/// The engine does not model the rune, so every expectation below is the RAW MEASUREMENT ÷ 1.08
/// — this is also why the readings initially looked like a 40/40 dummy: ×2/3×1.08 = 0.72 is
/// within 0.8% of ×100/140, indistinguishable until the user disclosed the rune.
///
/// Reconciled in engine space (measured ÷ 1.08 vs engine, all within ±0.75):
///   AA 48 → 44.44 (engine 44.67) · QQ 62 → 57.41 at Q RANK 1 (see the rank note below) ·
///   QW 45 → 41.67 at Q RANK 1 (engine 41.80; session-2 full-HP re-measure — the session-1
///   reading of 57 mixed in QW's missing-health amplification on a non-full dummy and stays
///   unencoded; the amp itself is deliberately uncurated, conservative full-HP baseline per
///   Hwei.json _noteQsub) · E 54 → 50.00 (engine 50.57) · W rider 15/hit → 13.89 (engine 14.23).
/// (loop 480) THE QQ ROW MOVED FROM RANK 2 TO RANK 1, AND ITS MEASURED 62 DID NOT MOVE. Devastating
/// Fire also deals 3/4/5/6/7% of the target's maximum health — both wikis say so and the BIN carried
/// the DataValue all along — and it was never curated. Missing that hit, 62 reconciled only by
/// assuming Q sat at rank 2: flat-only rank 2 is 80 + 0.8×9 = 87.2, and rank 1 WITH the health ratio
/// is 50 + 7.2 + 3% of 1000 = 87.2. The same number to the decimal, which is why the wrong reading
/// looked airtight and why this file used to claim the row proved rank inheritance. Every other row
/// of that session sits at rank 1 (E 54.6 vs 54, QW 45.1 vs 45, WE 15.4 vs 15, AA 48.2 vs 48), so an
/// all-rank-1 session with the health ratio present is the reading that needs no special pleading.
/// Rank inheritance is now pinned directly by HweiMuseSlotsTests instead of as a side effect here.
///
/// (loop 480) TWO ROWS CHANGED THE NODE THEY CAST, AND NOT ONE MEASURED NUMBER. Hwei's E and W
/// are muse SELECTORS that deal nothing; what the user cast in 2026-07-26 was an E muse and the
/// WE boon, and the curation of the day carried those under the E and W slot keys via binSpell.
/// Those keys are now the sub-casts they always were, so these rows say EQ and WE. Every
/// expectation below is byte-identical to the measurement, which is the point: the reading was
/// always of EQ and WE, and only the label was wrong.
///
///   R: RESOLVED by the clean-room session (see Golden_Hwei_R_ZoneTotal_TwoApPoints) — the
///   0.95AP-total model confirmed exactly at AP 18 and AP 83 with a no-damage-rune page; the
///   earlier vanilla-176 residual traced to the old page's small adaptive source, not the engine.
///   QE measured 72 = EXACTLY 5 ticks × BaseDPS[rank2] 20 × 2/3 × 1.08 → the zone DoT's
///   BaseDPS is per-0.5s-TICK, and the curated QE (initial hit only, DoT disclosed-omitted)
///   underrepresents the slot — candidate for a perSecondCalc upgrade, tracked in the sheet.
/// </summary>
public class GoldenHweiTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "Hwei";
    private const double TotalAd = 67.0;
    private const double Ap = 9.0;
    /// <summary>Cut Down (+8% vs targets above 60% max HP) was active on every reading; the
    /// engine models champion+item damage only, so expectations divide it out.</summary>
    private const double CutDown = 1.08;

    public GoldenHweiTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenHwei_" + Guid.NewGuid().ToString("N"));
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

    private static ChampionData Hwei()
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

    /// <summary>Session 1 (AA/QQ/E/rider) measured at Q rank 2; sessions 2–3 (QW re-measure, R)
    /// at all-rank-1, user-confirmed. Each golden row states the rank set it was measured under.</summary>
    private static ActivePlayerStats Stats(int qRank, double ap) => new()
    {
        AttackDamage = TotalAd, AbilityPower = ap,
        AbilityQ = qRank, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static GameSnapshot Snap(int qRank, double ap)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2, Stats = Stats(qRank, ap),
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

    private void InitRepo() => ChampionRepository.Initialize(new[] { Hwei(), Dummy() });

    private ComboResult RunNodes(params ComboNode[] nodes) => RunNodes(2, Ap, nodes);

    private ComboResult RunNodes(int qRank, ComboNode[] nodes) => RunNodes(qRank, Ap, nodes);

    private ComboResult RunNodes(int qRank, double ap, ComboNode[] nodes)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, "c");
        foreach (var n in nodes) editor.AddNode(draft.Id, n);
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snap(qRank, ap));
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

    // ── measured rows (vs 1000 HP / 50 armor / 50 MR dummy; expectations = measured ÷ 1.08
    //    Cut Down, see class remarks) ─────────────────────────────────────────────────────

    [Fact]
    public void Golden_Hwei_QQ_MuseSubCast_AtQRank1()
    {
        InitRepo();
        AssertWithinOne(62 / CutDown, RunNodes(1, new[] { SkillNode("QQ") }).TotalDamage,
            "QQ (BaseDamage[rank1] 50 + 0.8×9 AP + 3% of the dummy's 1000 max HP, ×2/3)");
    }

    [Fact]
    public void Golden_Hwei_QW_FullHpBaseline_AtQRank1()
    {
        InitRepo();
        AssertWithinOne(45 / CutDown, RunNodes(1, new[] { SkillNode("QW") }).TotalDamage,
            "QW full-HP baseline (BaseDamage[rank1] 60 + 0.3×9 AP, ×2/3) — session-2 re-measure; "
            + "the missing-health amplification is deliberately uncurated");
    }

    [Fact]
    public void Golden_Hwei_EQ_MuseSubCast()
    {
        InitRepo();
        AssertWithinOne(54 / CutDown, RunNodes(SkillNode("EQ")).TotalDamage,
            "EQ (70 + 0.65×9 AP via binSpell=HweiEQ, ×2/3) — same reading, cast under its own key");
    }

    /// <summary>(loop 480) The other two E muses resolve to the same number, which is what let one
    /// measurement stand for all three — but only while they are three real slots. If a future patch
    /// splits their damage apart, this is the row that goes red rather than two casts silently
    /// inheriting a reading that was never theirs.</summary>
    [Theory]
    [InlineData("EW")]
    [InlineData("EE")]
    public void Golden_Hwei_OtherEMuses_MatchTheMeasuredOne(string slot)
    {
        InitRepo();
        AssertWithinOne(54 / CutDown, RunNodes(SkillNode(slot)).TotalDamage,
            $"{slot} shares EQ's BaseDamage and ratio");
    }

    /// <summary>R measured in a CLEAN-ROOM session: the user rebuilt the rune page with no
    /// damage runes (two adaptive shards only — no Cut Down, no Scorch), so these two rows pin
    /// the engine DIRECTLY with no divide-out factor, at two AP points (165 at AP 18; 206 at
    /// AP 83 after a Needlessly Large Rod). Confirms TotalMaxDamage = 230 + 0.95×AP exactly
    /// (0.2% / 0.05%) and kills the doubled-AP hypothesis (would have read ~259 at AP 83). The
    /// earlier vanilla-176 residual (+2.5%) is thereby attributed to the OLD rune page's small
    /// adaptive source (Absolute Focus fits: 9+6.6 AP → 176.2), not the engine.</summary>
    [Theory]
    [InlineData(18, 165)]
    [InlineData(83, 206)]
    public void Golden_Hwei_R_ZoneTotal_TwoApPoints(double ap, double measured)
    {
        InitRepo();
        AssertWithinOne(measured, RunNodes(1, ap, new[] { SkillNode("R") }).TotalDamage,
            $"R full-zone total (230 + 0.95×{ap} AP, ×2/3) — clean-room page, direct pin");
    }

    [Fact]
    public void Golden_Hwei_WERider_EmpoweredAaAfterCast()
    {
        InitRepo();
        double aaAlone = RunNodes(AaNode("AA_0")).TotalDamage;
        AssertWithinOne(48 / CutDown, aaAlone, "plain AA (67 AD ×2/3)");
        double rider = RunNodes(SkillNode("WE"), AaNode("AA_1")).TotalDamage - aaAlone;
        AssertWithinOne(15 / CutDown, rider,
            "WE onHit rider per empowered AA (20 + 0.15×9 AP via binSpell=HweiWE, ×2/3)");

        // (loop 480) …and the two boons that are not WE no longer carry it. Under the old W-slot
        // curation this was the same rider, so picking Fleeting Current added damage.
        AssertWithinOne(aaAlone, RunNodes(SkillNode("WQ"), AaNode("AA_2")).TotalDamage,
            "WQ is a movement boon — it must not empower the attack after it");
    }
}
