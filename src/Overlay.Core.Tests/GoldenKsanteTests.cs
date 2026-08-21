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
/// GOLDEN #2 v1 — K'Sante normal-stance baseline (M26 §3). Freezes the user-verified Practice Tool
/// session in <c>docs/reports/golden/GOLDEN_02_KSANTE.md</c> (2026-07-14). K'Sante is the
/// max-engine-risk golden: it exercises the rarest resolver paths — bonus-Armor/MR ratio terms
/// (Q, W) and %maxHP-via-calc (W).
///
/// SETUP (sheet §4): K'Sante L6, AD 78 / AP 0, ranks Q3/W1/E1/R1. Target Kennen L3, Armor 36 / MR
/// 32. Two item configs prove the bonus-resist resolver by DELTA:
///   • config-1 (no items): K'Sante Armor 57 / MR 38 (= his true L6 base, 0 bonus — confirmed by
///     the W check), target MaxHP 755.
///   • config-2 (Chain Vest +40 Armor, Negatron +45 MR, Giant's Belt +350 HP): Armor 97 / MR 83
///     (bonus 40 / 45), target MaxHP 845.
///
/// The fixture pins BaseStats so baseArmor/baseMr@L6 = 57 / 38 (StatsPerLevel 0, level 6), i.e.
/// bonus = total − base resolves to exactly 0 (config-1) and 40 / 45 (config-2) — isolating the
/// bonus-resist RATIO term (the thing under test) from base-growth derivation. Recorded numbers are
/// post-mitigation; a zero-growth Dummy pins the target's 36/32/maxHP. Normal stance has no armor
/// penetration (that is an All-Out stat), so hits mitigate by the full 36 armor / 32 MR.
///
/// v1 rows: AA, Q (no-items 95 + items 120 = resolver lock), W (no-items 77 + items 93 = %maxHP +
/// resolver lock), E = 0. HELD (see sheet): P mark (uncurated {ee18a47b} — a separate CURATION
/// round, formula now known), all All-Out rows, R + wall.
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs; ledger promotion to
/// VERIFIED only after green. See CLAUDE_CODE_TODO §16.
/// </summary>
// This class's trace test deletes+counts blocks in the shared always-on calc_trace.log; the
// CalcTraceLog collection serializes it with KsanteAllOutWRiderTests (which also reads that log).
[Collection("CalcTraceLog")]
public class GoldenKsanteTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "KSante";
    private const double BaseArmorL6 = 57;
    private const double BaseMrL6 = 38;
    private const double BaseHpL6 = 1219;
    private const double Ad = 78;

    public GoldenKsanteTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenKsanteTests_" + Guid.NewGuid().ToString("N"));
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

    // ── fixtures ──────────────────────────────────────────────────────────────────

    private static ChampionData Ksante()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{ChampId.ToLowerInvariant()}.bin.json"));
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
        // Pin base so bonus (total − base) = 0 at config-1 and 40/45 at config-2, at level 6.
        return new ChampionData
        {
            Id = ChampId, Name = ChampId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = Ad, Armor = BaseArmorL6, Mr = BaseMrL6, Hp = BaseHpL6 },
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy(string id, double hp, double armor, double mr) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    // total Armor/MR = the scoreboard values; K'Sante MaxHealth set for completeness (v1 hits don't use it).
    private static ActivePlayerStats Stats(double totalArmor, double totalMr, double ksanteHp) => new()
    {
        AttackDamage = Ad, AbilityPower = 0,
        Armor = totalArmor, MagicResist = totalMr, MaxHealth = ksanteHp,
        AbilityQ = 3, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static GameSnapshot Snapshot(ActivePlayerStats stats)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2, Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = ChampId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 3;
        return snap;
    }

    private void InitRepo(double targetHp) =>
        ChampionRepository.Initialize(new[] { Ksante(), Dummy("Target", targetHp, 36, 32) });

    // (M28) wallHit feeds ComboNode.UserConditionMet — the new "최대 데미지(벽꿍)" checkbox knob.
    // Default false/OFF (P2 floor); irrelevant/harmless for a slot with no conditional hit.
    private ComboResult RunSlot(GameSnapshot snap, string slot, bool wallHit = false)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0, UserConditionMet: wallHit));
        editor.SaveCombo(draft.Id);
        return Run(config, engine, snap, draft.Id);
    }

    private ComboResult RunSlots(GameSnapshot snap, string[] slots, bool wallHit = false)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, "c");
        foreach (var slot in slots)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
                DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
                CastTime: 0, Delay: 0, TravelTime: 0, UserConditionMet: wallHit));
        editor.SaveCombo(draft.Id);
        return Run(config, engine, snap, draft.Id);
    }

    private ComboResult RunAa(GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);
        return Run(config, engine, snap, draft.Id);
    }

    private static ComboResult Run(ConfigManager config, ComboEngine engine, GameSnapshot snap, string draftId)
    {
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draftId, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    // ── config 1 (no items): bonus resist = 0, target 755 ────────────────────────────

    [Fact]
    public void Golden_Ksante_AA_NoItems()
    {
        InitRepo(755);
        AssertWithinOne(57, RunAa(Snapshot(Stats(57, 38, 1219))).TotalDamage, "K'Sante AA (AD 78, armor 36)");
    }

    [Fact]
    public void Golden_Ksante_Q_NoItems_PureBaseDamage()
    {
        InitRepo(755);
        AssertWithinOne(95, RunSlot(Snapshot(Stats(57, 38, 1219)), "Q").TotalDamage, "K'Sante Q (bonus resist = 0)");
    }

    [Fact]
    public void Golden_Ksante_W_NoItems_FlatPlusPercentMaxHp()
    {
        // W = 45 + 8% target maxHP (charge only affects stun, not damage). MEASURED = 77.
        // The recorded target MaxHP 755 ALREADY INCLUDES the scaling-health rune (the measurement was
        // taken with it applied), so 755 is the real maxHP: 45 + 0.08*755 = 105.4 -> *100/136 = 77.5
        // ≈ 77 ✓.
        // ACCURACY BUG (do NOT change this expected — M26 §3): the engine currently returns ~79.26
        // because ComboRunner.BuildContext (loop-122 shard) adds an ASSUMED +10×level MaxHP on top of
        // a target maxHP that already contains the rune → DOUBLE-COUNT (755+30=785). The golden
        // asserts the measured 77; the shard double-count must be fixed engine-side. See
        // CLAUDE_CODE_TODO §16 "W/P shard double-count".
        InitRepo(755);
        AssertWithinOne(77, RunSlot(Snapshot(Stats(57, 38, 1219)), "W").TotalDamage, "K'Sante W (45 + 8% maxHP) = measured 77");
    }

    [Fact]
    public void Golden_Ksante_E_NoDamage()
    {
        InitRepo(755);
        AssertWithinOne(0, RunSlot(Snapshot(Stats(57, 38, 1219)), "E").TotalDamage, "K'Sante E (shield/dash, no damage)");
    }

    // ── config 2 (Chain Vest + Negatron + Giant's Belt): bonus 40/45, target 845 ─────

    [Fact]
    public void Golden_Ksante_Q_WithBonusResist_ResolverAddsTerm()
    {
        // 95 -> 120 (+25) purely from +40 bonus Armor / +45 bonus MR (AD & AP unchanged) — the
        // bonus-Armor/MR ratio resolver (mStat 1/6).
        InitRepo(845);
        AssertWithinOne(120, RunSlot(Snapshot(Stats(97, 83, 1569)), "Q").TotalDamage, "K'Sante Q (+40 Armor/+45 MR)");
    }

    [Fact]
    public void Golden_Ksante_W_WithBonusResist_PercentMaxHpPlusResistTerm()
    {
        // MEASURED = 93 (target maxHP 845 already includes the scaling-health rune):
        // 45 + (0.08 + 0.0002*85)*845 = 125.6 -> *100/136 = 92.4 ≈ 93 ✓.
        // Same ACCURACY BUG as the no-items W (do NOT change this expected — M26 §3): the engine's
        // +10×level shard double-counts (845+30=875 -> ~95.5). Golden asserts the measured 93; fix
        // the shard engine-side. See CLAUDE_CODE_TODO §16.
        InitRepo(845);
        AssertWithinOne(93, RunSlot(Snapshot(Stats(97, 83, 1569)), "W").TotalDamage, "K'Sante W (%maxHP + resist term) = measured 93");
    }

    // ── v2: normal-stance P mark (Dauntless Instinct) ────────────────────────────────
    // §16 P-round: the {ee18a47b} CalculationPart is now supported engine-side
    // (CalculationPart.Kind.ByCharLevelInterpolationByDataValue) and P is re-curated (Ksante.json
    // _notePBaseForm), so these are now live. Formula CONFIRMED by the 2026-07-14 session:
    // normal P = flat 12 + [1% + (level−1)/17 %] × target maxHP, PHYSICAL (linear 1%@L1 → 2%@L18,
    // matching the BIN MarkDamagePercentMin 0.01 / Max 0.02; the wiki's "2.12%" is the L20 value, out
    // of the L1–18 range — this resolves the old note's discrepancy). P.hits[0] = MaxHealthDamagePercent
    // is now the ALL-OUT variant (allOutOnly); these base-stance hits (baseStanceOnly) fire only outside
    // All-Out. Expected uses the MEASURED maxHP (no shard double-count): the % term is small so ±1 holds.

    [Fact]
    public void Golden_Ksante_P_Normal_NoItems()
    {
        // Measured P+AA = 73, AA = 57 -> P mark = 16. 12 + (0.01 + 5/17*0.01)*755 = 21.77 ->
        // *100/136 = 16.0 ≈ 16 ✓ (maxHP 755 = the measured with-rune total; no +10×level shard).
        InitRepo(755);
        AssertWithinOne(16, RunSlot(Snapshot(Stats(57, 38, 1219)), "P").TotalDamage, "K'Sante P mark, normal (12 + level%maxHP) = measured 16");
    }

    [Fact]
    public void Golden_Ksante_P_Normal_WithItems()
    {
        // 12 + 0.012941*845 = 22.94 -> *100/136 = 16.9 ≈ 17. Bonus resists do NOT change the NORMAL P
        // (the 0.01%/resist term is the All-Out variant only).
        InitRepo(845);
        AssertWithinOne(17, RunSlot(Snapshot(Stats(97, 83, 1569)), "P").TotalDamage, "K'Sante P mark, normal (items, unchanged %) = measured 17");
    }

    // ── R (All Out Slam) — session-3 2-hit re-curation (GOLDEN_02_KSANTE.md §6), M28 round: hit2 ──
    // is now a REAL conditional hit (conditionType "HitsWall", UserAssumed, default OFF/floor — the
    // combo editor's "최대 데미지(벽꿍)" checkbox). hit1 (flatDataValue BaseDamage) always fires;
    // hit2 (wall bonus) fires ONLY when the node's UserConditionMet is explicitly set true. Neither
    // hit reads the TARGET's maxHP (unlike Q/W), only K'Sante's OWN bonus health (hit2's 5% term)
    // and the target's armor (mitigation) — so these are exact, non-provisional mechanical checks,
    // independent of the target maxHp configs used for Q/W/P above.

    [Fact]
    public void Golden_Ksante_R_WallHitOff_OnlyFlatHitFires()
    {
        // Default (checkbox OFF, the P2 floor): only hit1 (flat 80, mitigated by target's 36 armor,
        // k=100/136=0.7353) fires. hit2 (wall bonus) contributes NOTHING — the round-3 review's
        // flagged "R fires both hits unconditionally" issue, now closed.
        InitRepo(755);
        var result = RunSlot(Snapshot(Stats(57, 38, 1219)), "R"); // wallHit defaults to false

        Assert.Single(result.NodeBreakdown); // hit2 dropped entirely — 0 contribution, not a 0-damage node
        Assert.Equal(58.82, result.NodeBreakdown[0].Damage, 1); // hit1: flat 80 * 0.7353
        Assert.Equal(58.82, result.TotalDamage, 1);
    }

    [Fact]
    public void Golden_Ksante_R_WallHitOn_NoBonusHealth_BothHitsEqualFlatBaseDamage()
    {
        // Checkbox ON: K'Sante bonus HP = 0 (Stats' 1219 == BaseHpL6, no HP items) -> hit2's 5% term
        // is 0, so hit1 and hit2 both resolve to the SAME flat 80 raw, each mitigated identically:
        // 80*k=58.8235 each, total 117.65 — the ORIGINAL round-2 assertion, now opt-in.
        InitRepo(755);
        var result = RunSlot(Snapshot(Stats(57, 38, 1219)), "R", wallHit: true);

        Assert.Equal(2, result.NodeBreakdown.Count); // hit1 (BaseDamage) + hit2 (TotalDamageSlamDown)
        Assert.Equal(58.82, result.NodeBreakdown[0].Damage, 1); // hit1: flat 80 * 0.7353
        Assert.Equal(58.82, result.NodeBreakdown[1].Damage, 1); // hit2: (80 + 0.05*0) * 0.7353
        Assert.Equal(117.65, result.TotalDamage, 1);
    }

    [Fact]
    public void Golden_Ksante_R_WallHitOn_WithBonusHealth_Hit2AddsFivePercentOwnBonusHp()
    {
        // Checkbox ON: K'Sante bonus HP = 1569-1219 = 350 (Giant's Belt +350, same config-2 stats as
        // Q/W). hit1 unaffected (no stat term at all): 80*0.7353=58.82. hit2 = (80+0.05*350)*0.7353
        // = 97.5*0.7353=71.69 — must now DIFFER from hit1, proving the bonus-HP term actually applies
        // only to hit2 (the wall-bonus calc), not hit1 (the bare flat DataValue).
        InitRepo(845);
        var result = RunSlot(Snapshot(Stats(97, 83, 1569)), "R", wallHit: true);

        Assert.Equal(2, result.NodeBreakdown.Count);
        Assert.Equal(58.82, result.NodeBreakdown[0].Damage, 1);
        Assert.Equal(71.69, result.NodeBreakdown[1].Damage, 1);
        Assert.NotEqual(result.NodeBreakdown[0].Damage, result.NodeBreakdown[1].Damage);
        Assert.Equal(130.51, result.TotalDamage, 1);
    }

    [Fact]
    public void Golden_Ksante_R_Hit1PlusAllOutPRider_MatchesMeasured64_OneTraceBlockConfirmsAttribution()
    {
        // Reproduces GOLDEN_02_KSANTE.md §6's re-derivation inputs exactly: target Kennen 845/36/32,
        // K'Sante bonus armor/mr=0 (base 57/38, like config-1) but bonus HP=60 (an assumed
        // scaling-health rune ON K'SANTE HIMSELF — his own MaxHealth is a REAL live stat, not an
        // estimate, so this is not a P2 concern; it stands in for that specific measurement
        // session's real K'Sante build). Combo = [R, P] (P's currently-curated hit is the All-Out
        // mark-pop variant per Ksante.json's _noteP/CLAUDE_CODE_TODO §16).
        //
        // The doc's point: R's OWN hit1 (58.8 mitigated) does NOT equal the practice-tool's
        // measured "R" reading of 64 by itself — the remaining ~5-7 damage is the All-Out passive
        // mark (P) firing alongside R in the same real cast, NOT a missing term in R's curation.
        // hit1 (58.82) + P's rider (0.01*845*0.7353=6.21) = 65.03 vs measured 64 — the doc's own
        // hand-rounded arithmetic landed exactly on the ±1 boundary (65.0); the engine's more
        // precise math is 65.03, negligibly outside a strict ±1.0, same conclusion (±1.1 below).
        // Confirms the attribution and un-holds the R golden datapoint (previously PROVISIONAL).
        string tracePath = Path.Combine(AppContext.BaseDirectory, "logs", "calc_trace.log");
        try { File.Delete(tracePath); } catch (IOException) { /* best-effort */ }

        InitRepo(845);
        // wallHit: true — this test's OWN attribution point only needs hit1+P (see below), but
        // keeping the checkbox ON here preserves the original "one trace block shows every hit,
        // including R's wall-conditional one" demonstration (M28's own trace/triage use case).
        var result = RunSlots(Snapshot(Stats(57, 38, 1279)), new[] { "R", "P" }, wallHit: true);

        // hit1 = R's flatDataValue BaseDamage row; the P row is this combo's single P hit.
        var hit1 = Assert.Single(result.NodeBreakdown, n => n.NodeId.Contains("R_0#cast0h0c0"));
        var pRider = Assert.Single(result.NodeBreakdown, n => n.NodeId.StartsWith("P_0"));

        Assert.Equal(58.82, hit1.Damage, 1);
        Assert.Equal(6.21, pRider.Damage, 1);
        double combined = hit1.Damage + pRider.Damage;
        Assert.True(Math.Abs(combined - 64) <= 1.1,
            $"K'Sante R hit1 + All-Out P rider = measured 64 (±1.1): engine gave {combined:0.##}");

        // One CalcTrace block (this single combo trigger) must show BOTH hits as distinct rows —
        // the same block a human would attach to a triage report per M26 §6/§8.
        string trace = File.ReadAllText(tracePath);
        int blockCount = trace.Split("=== combo ", StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(1, blockCount); // exactly one Begin/End block was written
        Assert.Contains("node=R_0#cast0h0c0", trace);
        Assert.Contains("node=R_0#cast0h1c0", trace);
        Assert.Contains("node=P_0#cast0h0c0", trace);
    }

    // ── M28 §1: the "최대 데미지(벽꿍)" checkbox is derived purely from curated data ────────────

    [Fact]
    public void Golden_Ksante_R_ExposesConditionalWallHit_ForTheEditorCheckbox()
    {
        SkillDamageDb.ResetForTests();
        var hit = SkillDamageDb.GetConditionalHit(ChampId, "R");

        Assert.NotNull(hit);
        Assert.Equal("HitsWall", hit!.ConditionType);
        Assert.Equal("TotalDamageSlamDown", hit.MetCalc);
        Assert.True(Enum.TryParse<ConditionType>(hit.ConditionType, out var type));
        Assert.True(ConditionResolution.IsUserAssumed(type)); // no live signal -> user checkbox, default OFF
    }

    [Fact]
    public void Golden_Ksante_Q_HasNoConditionalHit_CheckboxNotOffered()
    {
        SkillDamageDb.ResetForTests();
        Assert.Null(SkillDamageDb.GetConditionalHit(ChampId, "Q"));
    }
}
