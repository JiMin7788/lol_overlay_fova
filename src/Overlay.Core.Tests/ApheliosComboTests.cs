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
/// Full-kit combo coverage for Aphelios (release_checklist.md open item) against the REAL curated
/// <c>data/skill_damage/Aphelios.json</c> + live <c>aphelios.bin.json</c> BIN numbers, run through
/// the actual ComboEngine/ComboEditor/ComboRunner via the EventBus (same harness as
/// <see cref="GoldenKsanteTests"/>/<see cref="AbilitySlotOnHitGatingTests"/>).
///
/// Aphelios has no unified "Q": the canonical Q slot is an empty ClientTooltipWrapper (see
/// Aphelios.json's own _noteQ), so the curation exposes 5 separate weapon-Q top-level keys
/// (QCalibrum/QSeverum/QGravitum/QInfernum/QCrescendum), each pointing at a different real BIN spell
/// object via <c>binSpell</c> (a hashed top-level key exposed by
/// <see cref="ChampionBinParser"/>'s ParseExtraSpells, M22 Phase 5). This file targets each weapon-Q
/// key directly, plus R (MaxDamage). The curated JSON still carries <c>"auto": true</c> (a known
/// pre-existing flag per Aphelios.json's own _note — not touched here, just worked around by
/// curating exact slot names).
///
/// FIXTURE CHOICES (documented so the hand-computed numbers below are reproducible):
///  - AbilityPower = 0 throughout (Aphelios is a pure-AD marksman) — several of these formulas carry
///    a THIRD term whose BIN part has no explicit mStat id (defaults to 0 = Ability Power per
///    FormulaInterpreter), which would otherwise complicate the arithmetic for no in-game reason;
///    zeroing AP collapses that term to 0 exactly, an honest and representative choice for this champion.
///  - BaseStats are all 0 (LoadChampionFromBin, same as AbilitySlotOnHitGatingTests), so "bonus" AD ==
///    total AD == the live stat verbatim.
///  - Champion level 9: every weapon-Q's damage formula is a <c>ByCharLevelBreakpointsCalculationPart</c>
///    (scales by CHAMPION LEVEL, not ability rank — Aphelios' real per-weapon Q levels with the
///    champion, not skill points) with bumps at levels 3/5/7/9/11/13. At level 9 exactly 4 bumps
///    (3,5,7,9) have been reached — a clean, reproducible breakpoint count.
///  - AbilityR = 3 (R's max rank) so the canonical "R" slot's real ability rank resolves R's
///    DataValue index; this alone also makes ComboRunner's HasAbilityData(stats) true, which pins
///    every NON-canonical weapon-Q slot's evaluation rank to the floor of 1 (SkillDamage.ChooseRank:
///    a non-canonical multiform/weapon key always uses rank 1 once ability data is present) — but
///    every weapon-Q DataValue actually read below is rank-invariant (constant across all indices)
///    or reached only via the champion-level breakpoints above, so the exact rank floor value never
///    matters to any number in this file.
///
/// No enemy is placed on the board (<c>PlayerCount = 1</c>) so every hit lands unmitigated
/// (FallbackDefender Armor 0 / MR 0, k = 1) — a combo's total is the exact raw sum of its hits.
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs; see
/// CLAUDE_CODE_TODO.md's build+test entry for the exact `dotnet test --filter`.
/// </summary>
public class ApheliosComboTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ApheliosComboTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "ApheliosComboTests_" + Guid.NewGuid().ToString("N"));
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
    private const int Level = 9; // exactly 4 level-breakpoints (3,5,7,9) reached on every weapon-Q
    private static ActivePlayerStats Stats() => new() { AttackDamage = Ad, AbilityPower = Ap, AbilityR = 3 };

    // ── 1. base AA alone: Aphelios has no P on-hit (P deals zero direct damage — see Aphelios.json's
    // own _noteP: every P calc is a stat-buff counter, not a hit), so AA alone is pure AD. ──────────

    [Fact]
    public void Aphelios_AaAlone_IsPureAd_NoPassiveOnHit()
    {
        var aphelios = LoadChampionFromBin("Aphelios");
        ChampionRepository.Initialize(new[] { aphelios });

        var result = RunCombo("Aphelios", new[] { "AA" }, Snapshot("Aphelios", Stats(), Level));

        Assert.Equal(100.0, result.TotalDamage, precision: 2);
        Assert.Single(result.NodeBreakdown);
    }

    // ── 2. one skill alone per weapon-Q key + R ──────────────────────────────────────────────────

    [Fact]
    public void Aphelios_QCalibrum_Alone_SpellDamage()
    {
        // SpellDamage (ApheliosCalibrumQ, binSpell {9501e989}) = ByCharLevelBreakpoints(Level1Value 70,
        // +15 at each of levels 3/5/7/9/11/13) + StatBySubPart(bonus AD x ByCharLevelBreakpoints(0.42,
        // +0.03 at the same levels)) + StatByCoefficient(1.0, no mStat -> AP).
        // At level 9 (4 bumps reached): flat = 70 + 4*15 = 130; ratio = 0.42 + 4*0.03 = 0.54.
        // SpellDamage = 130 + 0.54*100 (bonus AD, base 0) + 1.0*0 (AP) = 130 + 54 = 184.
        var aphelios = LoadChampionFromBin("Aphelios");
        ChampionRepository.Initialize(new[] { aphelios });

        var result = RunCombo("Aphelios", new[] { "QCalibrum" }, Snapshot("Aphelios", Stats(), Level));

        Assert.Equal(184.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Aphelios_QSeverum_Alone_TotalDamage_ResolvesViaAttackSpeedMapping()
    {
        // Re-pinned (§48 mStat=4 fix, 2026-07-23): this test originally pinned QSeverum's total at 0
        // because "TotalDamage"'s base calc "NumAttacks" references mStat=4 (attack speed), which
        // BuildStatResolver had no mapping for (KeyNotFoundException → hit dropped). StatAttackSpeed=4
        // is now mapped (TOTAL = live attacks/sec, BONUS = the RATIO total/base − 1), so it resolves:
        //   NumAttacks  = mFormulaParts [1 + bonusAS × ASCoeff(0.33334)] × mMultiplier
        //   Re-curated (golden #15, 2026-07-26): guaranteed 6-hit floor on the standalone
        //   per-hit calc {9480e64f} = total AD × ByCharLevelBreakpoints(0.20, +0.035 at
        //   3/5/7/9/11/13) — live-validated at 15 dealt/hit — plus a perAttackCalc knob for
        //   AS-granted extra hits (0 here, knob unset). At level 9 (4 bumps):
        //   0.20 + 4×0.035 = 0.34 → 6 × (100 × 0.34) = 204.
        var aphelios = LoadChampionFromBin("Aphelios");
        ChampionRepository.Initialize(new[] { aphelios });

        var result = RunCombo("Aphelios", new[] { "QSeverum" }, Snapshot("Aphelios", Stats(), Level));

        Assert.Equal(204.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Aphelios_QGravitum_Alone_Damage()
    {
        // Damage (ApheliosGravitumQ, binSpell {b3ce4169}) = ByCharLevelBreakpoints(50, +15@3/5/7/9/…)
        // + StatBySubPart(bonus AD x ByCharLevelBreakpoints(0.32, +0.03@…)) + StatByCoefficient(0.7,
        // no mStat -> AP). At level 9: flat = 50 + 4*15 = 110; ratio = 0.32 + 4*0.03 = 0.44.
        // Damage = 110 + 0.44*100 + 0.7*0 = 110 + 44 = 154.
        var aphelios = LoadChampionFromBin("Aphelios");
        ChampionRepository.Initialize(new[] { aphelios });

        var result = RunCombo("Aphelios", new[] { "QGravitum" }, Snapshot("Aphelios", Stats(), Level));

        Assert.Equal(154.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Aphelios_QInfernum_Alone_QWaveDamage()
    {
        // QWaveDamage (ApheliosInfernumQ, binSpell {d29e7023}) = ByCharLevelBreakpoints(20, +15@…) +
        // StatBySubPart(bonus AD x ByCharLevelBreakpoints(0.15, +0.01@…)) + StatByCoefficient(0.7, no
        // mStat -> AP). At level 9: flat = 20 + 4*15 = 80; ratio = 0.15 + 4*0.01 = 0.19.
        // QWaveDamage = 80 + 0.19*100 + 0.7*0 = 80 + 19 = 99.
        var aphelios = LoadChampionFromBin("Aphelios");
        ChampionRepository.Initialize(new[] { aphelios });

        var result = RunCombo("Aphelios", new[] { "QInfernum" }, Snapshot("Aphelios", Stats(), Level));

        Assert.Equal(99.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Aphelios_QCrescendum_Alone_SummonPattern_HonestZeroWithoutShotCount()
    {
        // Re-curated 2026-07-26 (golden #15 ratio-as-damage fix): the old MiniDamageMin pick was
        // DV(MiniDamageRatioMin)=0.05 — the 5%-per-chakram passive RATIO, not the Q's damage. The
        // slot is now the M22 Phase-3 summon pattern (perAttackCalc=TurretDamage × UserAttackCount),
        // so with NO shot count assumed the node contributes an honest 0; the per-shot value is
        // pinned live in GoldenApheliosTests (49 vs the 50/50 dummy) and in the silent-zero guard.
        var aphelios = LoadChampionFromBin("Aphelios");
        ChampionRepository.Initialize(new[] { aphelios });

        var result = RunCombo("Aphelios", new[] { "QCrescendum" }, Snapshot("Aphelios", Stats(), Level));

        Assert.Equal(0.0, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void Aphelios_R_Alone_MaxDamage()
    {
        // MaxDamage (canonical R slot) = NamedDataValue(RBaseDamage, rank 3) + StatByCoefficient(bonus
        // AD, coefficient 0.2) + StatByCoefficient(1.0, no mStat -> AP). RBaseDamage[3] = 225
        // (aphelios.bin.json R DataValues: [75,125,175,225,275,325,375], index = rank).
        // MaxDamage = 225 + 0.2*100 + 1.0*0 = 225 + 20 = 245.
        var aphelios = LoadChampionFromBin("Aphelios");
        ChampionRepository.Initialize(new[] { aphelios });

        var result = RunCombo("Aphelios", new[] { "R" }, Snapshot("Aphelios", Stats(), Level));

        Assert.Equal(245.0, result.TotalDamage, precision: 2);

        var tags = SkillDamageDb.GetSlotTags("Aphelios", "R");
        Assert.Contains("AoeUltimate", tags);
    }

    // ── 3. full rotation: every weapon-Q + R (the closest thing to a "full kit" a single-slot
    // per-weapon model can express — no live weapon-swap state to pick just one) ────────────────

    [Fact]
    public void Aphelios_FullRotation_AllFiveWeaponQsPlusR_SumsToExpectedTotal()
    {
        // 184 (Calibrum) + 204 (Severum — golden-#15 re-curation: 6-hit floor on the
        // standalone per-hit calc, AS extras via knob) + 154 (Gravitum) + 99 (Infernum) +
        // 0 (Crescendum — summon pattern, no shot count assumed) + 245 (R) = 886.
        var aphelios = LoadChampionFromBin("Aphelios");
        ChampionRepository.Initialize(new[] { aphelios });

        var result = RunCombo(
            "Aphelios",
            new[] { "QCalibrum", "QSeverum", "QGravitum", "QInfernum", "QCrescendum", "R" },
            Snapshot("Aphelios", Stats(), Level));

        Assert.Equal(886.0, result.TotalDamage, precision: 2);
    }
}
