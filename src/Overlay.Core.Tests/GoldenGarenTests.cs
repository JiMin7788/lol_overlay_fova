using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode exists in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #3 round 4 — Garen R (Demacian Justice), the M20 missing-HP sequencing model's flagship
/// (docs/reports/golden/GOLDEN_03_GAREN.md §6, the 2026-08-11 patch-16.15 VANILLA re-measurement).
/// R is curated as two TRUE hits (<c>skill_damage/Garen.json</c>): hit1 <c>ExecuteDamage[rank]</c> ×
/// the target's LIVE missing HP at cast time + hit2 flat <c>BaseDamage[rank]</c> (see _noteR2 for
/// why the missing-HP hit resolves first).
///
/// WHY THIS ROUND EXISTS: patch 16.15 nerfed <c>GarenR:BaseDamage</c> 150/250/350 → 125/200/275
/// (weekly drift audit, CLAUDE_CODE_TODO §70), which invalidated round 2's live numbers — both of
/// them were recorded with Axiom Arcanist equipped (×1.12) on top of the OLD 150 base, so neither
/// 168 nor 297 is reproducible now. The recorder re-measured on 16.15 with a VANILLA rune page
/// (user-confirmed 2026-08-11), which is strictly cleaner: no amp to back out, and the two points
/// pin BOTH terms independently rather than through a rune multiplier.
///
/// Measured (R rank 1, no runes, no items, TRUE damage so target resistances are irrelevant):
///   1000/1000 (0 missing)   → 125   ⇒ BaseDamage[1] = 125 read directly
///    480/1000 (520 missing) → 255   ⇒ ExecuteDamage[1] = (255-125)/520 = 0.2500 exactly
/// Both back-solve to Δ=0.0 against <c>BaseDamage[rank] + ExecuteDamage[rank] × missing</c>, and
/// the pair independently reproduces the cdragon 16.15 arrays the drift audit fetched — a live
/// cross-check of the BIN refresh, not just of the engine.
///
/// Four checks:
///  1. Full-HP row (measurement): 1000/1000 → 125 exactly. Reads the flat term directly, and
///     doubles as the standing regression for round 2's ordering fix — with the flat hit resolving
///     first this row would read 125 + 0.25×125 = 156.25 off its own damage (Garen.json _noteR2).
///  2. Missing-HP row (measurement): 480/1000 → 255 exactly, via a <see cref="TargetHealthTracker"/>
///     seeded with 520 confirmed damage — the honest lower-bound mechanism the live path uses.
///  3. Sequencing identity (generalized, rune-independent, NO Axiom): a Q→E→R combo where R's own
///     missing-HP hit must read the RUNNING TRACK's HP after Q+E already fired in the SAME combo,
///     not the pre-combo full HP — verified against the SAME combo's own live-computed Q+E total
///     (no hardcoded "prefix" assumption), proving the dynamic re-evaluation genuinely happens
///     end-to-end through the real curated Q/E/R data (M20 §3/§4).
///  4. Axiom amp lock — <b>MODEL, NOT A MEASUREMENT</b>. Axiom Arcanist (rune 8224) is wired in
///     <c>rune_effects.json</c> + <see cref="ComboEngine"/>'s <c>ApplyAxiomArcanistUltimateMultiplier</c>
///     (×1.12 single-target / ×1.08 AoE by the curated "AoeUltimate" R-slot tag — Garen's R carries
///     no such tag, so it resolves 1.12×). Its two live rows died with the 16.15 nerf and were NOT
///     re-measured (recorder chose to close Garen on the vanilla rows first), so this asserts the
///     amp against a formula derived from the engine's OWN live prefix instead of a stale constant.
///     It exists to guard one specific regression: the amp must scale the ExecutePercent term too,
///     not just the flat one (see ComboEngine's own fix note) — a bug this shape catches and the
///     vanilla rows cannot. Promote it back to a measured row if Axiom is ever re-recorded.
///
/// Cowork-authored round (GOLDEN_03_GAREN.md §6), Claude Code build+test — see CLAUDE_CODE_TODO §72.
/// </summary>
public class GoldenGarenTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const int AxiomRuneId = 8224;

    public GoldenGarenTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenGarenTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────

    private static ChampionData LoadGarenFromBin()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "garen.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Garen", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Garen" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Garen", Name = "Garen", Skills = skills,
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
            HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2, Stats = stats,
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Garen";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 11;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 11;
        return snap;
    }

    private ComboResult RunSlots(GameSnapshot snap, TargetHealthTracker? tracker, params string[] slots)
    {
        using var config = new ConfigManager(_configPath);
        // SAME runeEngine instance shared between ComboEngine (Execute reads Axiom via
        // context.UserRuneConfig, armed off this) and ComboRunner (LoadRuneSelectionAndArmManualFlags
        // requires a non-null _runeEngine to auto-detect equipped auto-trigger runes at all — a null
        // runeEngine short-circuits to an EMPTY UserRuneConfig, silently skipping Axiom) — the exact
        // AppComposition wiring, matching ComboRunnerTests' rune-testing convention.
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Garen", "c");
        foreach (var slot in slots)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
                DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
                CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap, runeEngine: runeEngine,
            targetHealthTracker: tracker);
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

    private static double Sum(ComboResult result, string nodeIdPrefix)
        => result.NodeBreakdown.Where(n => n.NodeId.StartsWith(nodeIdPrefix, StringComparison.Ordinal)).Sum(n => n.Damage);

    // ── 1. MEASURED (2026-08-11, patch 16.15, vanilla): full HP 1000/1000 -> 125 exactly ──

    [Fact]
    public void Golden_Garen_R_FullHp_Rank1_Vanilla_Is125()
    {
        // Resistances are deliberately nonzero AND different from the round-2 fixture: R is TRUE
        // damage, so if either ever moves this number the curation's damage type is wrong.
        ChampionRepository.Initialize(new[] { LoadGarenFromBin(), Dummy("Target", hp: 1000, armor: 60, mr: 40) });
        var stats = new ActivePlayerStats { AttackDamage = 0, AbilityR = 1 };

        var result = RunSlots(Snapshot(stats), tracker: null, "R");

        // flat hit = BaseDamage[rank1] = 125 (patch 16.15); missing-HP hit = 0.25 x 0 missing = 0.
        // TRUE damage -> unmitigated, so this is exact, not just ±1. Doubles as the standing
        // ordering regression: with the flat hit first this reads 156.25 (Garen.json _noteR2).
        Assert.Equal(125.0, result.TotalDamage, 2);
    }

    // ── 2. MEASURED (2026-08-11, 16.15, vanilla): 480/1000 = 520 missing -> 255 exactly ──

    [Fact]
    public void Golden_Garen_R_Missing520_Rank1_Vanilla_Is255()
    {
        ChampionRepository.Initialize(new[] { LoadGarenFromBin(), Dummy("Target", hp: 1000, armor: 60, mr: 40) });
        var stats = new ActivePlayerStats { AttackDamage = 0, AbilityR = 1 };

        // The live path never reads an enemy's real current HP — it reconstructs it from the
        // tracker's confirmed damage-since-respawn (BuildDefenderFor: CurrentHP = MaxHP - accumulated),
        // so seeding 520 against a 1000 max reproduces the recorded 480/1000 target exactly. This
        // also exercises the same estimator the live R number depends on, not a bypass of it.
        using var tracker = new TargetHealthTracker();
        tracker.RecordDamageDealt("Target", 520);

        var result = RunSlots(Snapshot(stats), tracker, "R");

        // 125 flat + 0.25 x 520 missing = 255. The two measured rows together pin BOTH DataValues:
        // row 1 gives BaseDamage[1] = 125, this one gives ExecuteDamage[1] = (255-125)/520 = 0.25.
        Assert.Equal(255.0, result.TotalDamage, 2);
    }

    // ── 3. Sequencing identity (generalized, rune-independent): R's hit1 reads the SAME combo's ──
    // ── own live Q+E total, not the pre-combo full HP. No Axiom — a clean, exact identity. ───────

    [Fact]
    public void Golden_Garen_R_SequencingIdentity_Hit2ReadsLiveRunningTrackHp_NoAxiom()
    {
        ChampionRepository.Initialize(new[] { LoadGarenFromBin(), Dummy("Target", hp: 890, armor: 29, mr: 30) });
        var stats = new ActivePlayerStats { AttackDamage = 65, AbilityQ = 5, AbilityE = 5, AbilityR = 1 };

        var result = RunSlots(Snapshot(stats), tracker: null, "Q", "E", "R");

        double qTotal = Sum(result, "Q_0");
        double eTotal = Sum(result, "E_0");
        // (GOLDEN #3 round 2 ordering fix) the missing-HP hit is now curated FIRST (h0), the flat
        // hit SECOND (h1) — see Garen.json's _noteR2.
        double r_missingHit = Sum(result, "R_0#cast0h0c0");
        double r_flatHit = Sum(result, "R_0#cast0h1c0");

        // Sanity: Q/E resolve from the real curated BIN calcs, not zero/placeholder values.
        Assert.True(qTotal > 0 && eTotal > 0, "Q and E must both resolve real curated damage");

        // THE identity (M20 §3/§4): R's own missing-HP hit exactly equals ExecuteDamage[1] (0.25) ×
        // the missing HP produced by THIS SAME combo's Q+E (the engine's own live running-track
        // value — not a hardcoded prefix), proving the dynamic re-evaluation is real, not coincidence.
        double expectedMissingHit = 0.25 * (qTotal + eTotal);
        Assert.Equal(expectedMissingHit, r_missingHit, 1);

        // The flat hit is a bare flat DataValue -> never affected by prior combo damage, and (with
        // the ordering fix) no longer affected by R's OWN missing-HP hit either. 125 = the patch
        // 16.15 BaseDamage[rank1], the same constant test 1 reads directly off a full-HP target.
        Assert.Equal(125.0, r_flatHit, 2);
    }

    // ── 4. Axiom amp lock — MODEL, NOT A MEASUREMENT (see the class doc). Round 2's two live ────
    // ── Axiom rows (168 / 297) were recorded on the pre-16.15 base=150 and are unreproducible; ───
    // ── the recorder re-measured VANILLA only, so this row is derived, not observed. It stays ────
    // ── because it is the ONLY coverage for "the amp scales the ExecutePercent term too". ────────

    [Fact]
    public void Garen_R_AxiomAmp_ScalesBothFlatAndExecuteTerms_ModelNotMeasured()
    {
        ChampionRepository.Initialize(new[] { LoadGarenFromBin(), Dummy("Target", hp: 890, armor: 29, mr: 30) });
        var stats = new ActivePlayerStats
        {
            AttackDamage = 65, AbilityQ = 5, AbilityE = 5, AbilityR = 1,
            EquippedRuneIds = new[] { AxiomRuneId },
        };

        var result = RunSlots(Snapshot(stats), tracker: null, "Q", "E", "R");

        double prefixMissing = Sum(result, "Q_0") + Sum(result, "E_0");
        double rTotal = Sum(result, "R_0");

        // Expectation is built from the engine's OWN live prefix rather than a hardcoded constant,
        // so a future BaseDamage/Q/E patch shift cannot silently rot this row the way it rotted the
        // stale 297. The regression it exists to catch: an amp applied to the flat term ONLY would
        // give 1.12*125 + 0.25*prefix, which diverges from this by 0.12*0.25*prefix (~14 here).
        double expected = 1.12 * (125.0 + 0.25 * prefixMissing);
        Assert.Equal(expected, rTotal, 1);
    }
}
