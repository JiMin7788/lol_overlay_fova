using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (M25 §11.G step 2) End-to-end proof of the conditional-bonus hit mechanism against REAL curated
/// data + live BIN numbers. A conditional hit resolves its baseline <c>Calc</c> by default (the
/// conservative UNMET anchor = RangeMin, P2) and its <c>MetCalc</c> when the condition holds
/// (RangeMax); the exposure-graph spans [unmet, met] for a UserAssumed condition.
///
/// Renekton Q "Cull the Meek" (ResourceGte 50, AutoResolvable — Fury is the caster's own resource,
/// exposed live via GameSnapshot.ResourceValue): baseline BasicDamage (&lt;50 Fury), met EmpDamage
/// (&gt;=50 Fury). The RESOLVED number still comes from live state — it picks the right calc for the
/// reported Fury — but since loop 473 the range spans [base, empowered] as well, because a combo is
/// planned before it is cast and the resource at cast time is not the resource while building.
/// No enemy on board -> zero resistance.
/// </summary>
public class M25ConditionalHitTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public M25ConditionalHitTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M25ConditionalHitTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{championId.ToLowerInvariant()}.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = championId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, // needed for EffectValueCalculationPart (e.g. Kha'Zix Q)
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = championId, Name = championId, Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static GameSnapshot Snapshot(string champion, ActivePlayerStats stats, int level)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = level, PlayerCount = 1, Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = champion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = level;
        return snap;
    }

    private ComboResult RunSingleSkill(string championId, string slot, GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(championId, "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0));
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

    private ComboResult RunCombo(string championId, GameSnapshot snap, params (ComboNodeType type, string slot)[] specs)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(championId, "c");
        for (int i = 0; i < specs.Length; i++)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{specs[i].slot}_{i}", NodeType: specs[i].type, Name: specs[i].slot, Cooldown: 0, Mana: 0,
                Damage: 0, DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
                CastTime: 0, Delay: 0, TravelTime: 0));
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

    private ComboResult RunSingleSkillStacks(string championId, string slot, GameSnapshot snap, int? stacks)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(championId, "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0, UserStackCount: stacks));
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

    [Fact]
    public void NasusQ_BuffCounterStackKnob_AddsPerStackDamage_DefaultsToUnStackedFloor()
    {
        // (M25 §11.G) End-to-end proof of the stack-count knob. Nasus Q "Siphoning Strike" TotalDamage
        // = BonusDamage + 1.0×(total AD) + BuffCounterByCoefficient(1.0) × Siphoning stacks. The knob
        // (ComboNode.UserStackCount) feeds the stack count; unset = 0 = the conservative un-stacked
        // floor. Deterministic (no range widening, like the summon "몇 대" knob), so the resolved total
        // moves exactly with the knob. Modest AD keeps both readings under the 1000-HP lethality bound.
        var nasus = LoadChampionFromBin("Nasus");
        ChampionRepository.Initialize(new[] { nasus });

        const int level = 9;
        var stats = new ActivePlayerStats { AttackDamage = 200, AbilityPower = 0, AbilityQ = 5 };

        double floor = SkillDamage.ComputeCalcDamage(nasus, "Q", "TotalDamage", stats, level, stackCount: 0)!.Value;
        double at50 = SkillDamage.ComputeCalcDamage(nasus, "Q", "TotalDamage", stats, level, stackCount: 50)!.Value;
        Assert.Equal(floor + 50, at50, precision: 2);   // each Siphoning stack = +1 (mCoefficient 1.0)

        // No knob -> default 0 stacks -> the un-stacked floor (BonusDamage + total AD).
        var unstacked = RunSingleSkillStacks("Nasus", "Q", Snapshot("Nasus", stats, level), stacks: null);
        Assert.Equal(floor, unstacked.TotalDamage, precision: 2);
        // Knob at 50 stacks -> floor + 50, resolved live through the combo.
        var stacked = RunSingleSkillStacks("Nasus", "Q", Snapshot("Nasus", stats, level), stacks: 50);
        Assert.Equal(at50, stacked.TotalDamage, precision: 2);
    }

    [Fact]
    public void Varus_MultiAxis_ChargeAndOnHitAndBlight_ComposeIndependently()
    {
        // M25 §11.G integration target (the flagship 3-axis champion): Varus combines
        //   (1) Q charge (ChargeHold: [TotalDamageMinTooltip .. TotalDamageMax] via UserDistanceFraction),
        //   (2) W on-hit magic (OnHitDamage, alwaysOn, added to each auto-attack), and
        //   (3) Blight detonation (%maxHP magic, fired once per ABILITY node, upper-bound 3 stacks).
        // The deliverable is proving these three curated axes compose as INDEPENDENT data in one combo
        // (no interference), not any new curation -- the axes were curated in loops 39/102, so this is a
        // zero-regression verification. Rank-1 abilities + modest AD keep the whole combo under the
        // fallback dummy's 1000-HP lethality bound so the additive composition is exact.
        var varus = LoadChampionFromBin("Varus");
        ChampionRepository.Initialize(new[] { varus });

        const int level = 9;
        var stats = new ActivePlayerStats { AttackDamage = 50, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1 };

        double qMax = SkillDamage.ComputeCalcDamage(varus, "Q", "TotalDamageMax", stats, level)!.Value;
        double qMin = SkillDamage.ComputeCalcDamage(varus, "Q", "TotalDamageMinTooltip", stats, level)!.Value;
        double eBase = SkillDamage.ComputeCalcDamage(varus, "E", "TotalDamage", stats, level)!.Value;
        double onHit = SkillDamage.ComputeCalcDamage(varus, "W", "OnHitDamage", stats, level)!.Value;
        Assert.True(qMax > qMin + 1, "Q must widen with charge");

        // Derive the single-detonation Blight value from an [E]-only combo: E resolves as its base plus
        // exactly one onAbility Blight detonation, so blight = total - eBase (no need to hand-compute %HP).
        double blight = RunCombo("Varus", Snapshot("Varus", stats, level), (ComboNodeType.Skill, "E")).TotalDamage - eBase;
        Assert.True(blight > 1, $"Blight detonation ({blight}) must add magic %HP damage");

        var combo = RunCombo("Varus", Snapshot("Varus", stats, level),
            (ComboNodeType.Skill, "Q"), (ComboNodeType.Aa, "AA"), (ComboNodeType.Skill, "E"));

        // Composition: Q(full-charge) + Blight, AA(AD + on-hit), E(base + Blight) -- Blight fires on BOTH
        // ability nodes (2x), the on-hit fires on the AA only. All unmitigated (fallback 0/0 defender).
        double expected = qMax + blight + stats.AttackDamage + onHit + eBase + blight;
        Assert.True(expected < 950, $"guard: keep the combo under the lethality bound (was {expected})");
        Assert.Equal(expected, combo.TotalDamage, precision: 2);

        // The charge axis widens the range independently: everything else is constant across the graphs,
        // so the whole-combo range delta equals exactly Q's charge delta.
        Assert.Equal(qMax - qMin, combo.RangeMax - combo.RangeMin, precision: 2);
    }

    [Fact]
    public void RenektonQ_ResourceGte_ResolvesBaseVsEmpowered_FromLiveFury()
    {
        var renekton = LoadChampionFromBin("Renekton");
        ChampionRepository.Initialize(new[] { renekton });

        const int level = 5;
        // Bonus AD drives the difference (Basic = +100% bonusAD, Emp = +140% bonusAD), so set a real AD.
        ActivePlayerStats Stats(double fury) => new()
        {
            AttackDamage = 250, AbilityPower = 0, AbilityQ = 5, ResourceValue = fury, ResourceMax = 100,
        };

        double basic = SkillDamage.ComputeCalcDamage(renekton, "Q", "BasicDamage", Stats(0), level)!.Value;
        double emp = SkillDamage.ComputeCalcDamage(renekton, "Q", "EmpDamage", Stats(0), level)!.Value;
        Assert.True(emp > basic + 1, $"empowered ({emp}) must exceed basic ({basic})");

        // < 50 Fury: the un-empowered baseline (value identity vs the prior conservative curation).
        var low = RunSingleSkill("Renekton", "Q", Snapshot("Renekton", Stats(fury: 0), level));
        Assert.Equal(basic, low.TotalDamage, precision: 2);
        // >= 50 Fury (live): the empowered value — resolved from ResourceValue, no user knob.
        var high = RunSingleSkill("Renekton", "Q", Snapshot("Renekton", Stats(fury: 60), level));
        Assert.Equal(emp, high.TotalDamage, precision: 2);

        // (loop 473) The resolved value still follows live Fury, but an untouched checkbox now spans
        // [base, empowered] — the user has not said which one the cast will be.
        Assert.Equal(basic, low.RangeMin, precision: 2);
        Assert.Equal(emp, low.RangeMax, precision: 2);
        Assert.Equal(basic, high.RangeMin, precision: 2);
        Assert.Equal(emp, high.RangeMax, precision: 2);
    }

    [Fact]
    public void RenektonE_ResourceGte_ResolvesBaseVsEmpowered_FromLiveFury()
    {
        // Renekton E "Slice and Dice" completes his Fury kit (Q proven above): the empowered EmpDamage
        // (EnragedBaseDamage + EnragedADRatio) lives in the SAME spell object as the base BasicDamage,
        // so it's a clean ResourceGte-50 conditional. AutoResolvable (Fury is live) -> no range widening.
        var renekton = LoadChampionFromBin("Renekton");
        ChampionRepository.Initialize(new[] { renekton });

        const int level = 5;
        ActivePlayerStats Stats(double fury) => new()
        {
            AttackDamage = 250, AbilityPower = 0, AbilityE = 5, ResourceValue = fury, ResourceMax = 100,
        };

        double basic = SkillDamage.ComputeCalcDamage(renekton, "E", "BasicDamage", Stats(0), level)!.Value;
        double emp = SkillDamage.ComputeCalcDamage(renekton, "E", "EmpDamage", Stats(0), level)!.Value;
        Assert.True(emp > basic + 1, $"empowered E ({emp}) must exceed basic E ({basic})");

        // < 50 Fury: the un-empowered baseline (value identity vs the prior conservative curation).
        var low = RunSingleSkill("Renekton", "E", Snapshot("Renekton", Stats(fury: 0), level));
        Assert.Equal(basic, low.TotalDamage, precision: 2);
        // >= 50 Fury (live): the empowered value — resolved from ResourceValue, no user knob.
        var high = RunSingleSkill("Renekton", "E", Snapshot("Renekton", Stats(fury: 60), level));
        Assert.Equal(emp, high.TotalDamage, precision: 2);
        // (loop 473) The resolved value still follows the live resource, but an untouched checkbox
        // spans [base, empowered]: the user has not said which the cast will be.
        Assert.Equal(basic, low.RangeMin, precision: 2);
        Assert.Equal(emp, low.RangeMax, precision: 2);
    }

    [Fact]
    public void KindredE_CritMultiplier_ResolvesToNoCritBase_IgnoresCritBuild()
    {
        // (§12②) Kindred E "BaseBiteDamage" carries a crit mMultiplier using mStat 8/9 (crit chance /
        // crit damage) the resolver previously left unmapped -> the calc threw -> the slot fell back to
        // the tooltip heuristic. Ability crit is modeled as NO crit (crit-neutral 0/1), so the
        // mMultiplier collapses to 1 and the calc resolves to its BIN no-crit base (BaseDamage +
        // 1.0*bonusAD). Setting a full crit build proves the resolver IGNORES live crit for abilities
        // (RangeMin must stay the conservative no-crit floor -- only auto-attacks crit in this engine).
        var kindred = LoadChampionFromBin("Kindred");
        ChampionRepository.Initialize(new[] { kindred });

        const int level = 9;
        var stats = new ActivePlayerStats
        {
            AttackDamage = 200, AbilityPower = 0, AbilityE = 5, CritChance = 1.0, CritDamage = 2.0,
        };

        double? d = SkillDamage.ComputeCalcDamage(kindred, "E", "BaseBiteDamage", stats, level);
        Assert.NotNull(d);
        // BaseDamage[rank5]=200 + BonusADRatio(1.0)*bonusAD(200) = 400, crit mMultiplier collapsed to 1.
        Assert.Equal(400.0, d!.Value, precision: 2);
    }

    [Fact]
    public void KindredE_MarkBite_PercentMissingHp_ScalesWithMarkStacks()
    {
        // (§11.G %HP stack) Kindred E's mark bite deals "5% (+0.5% per Mark) of the target's MISSING
        // health" (wiki). The %HP fraction PercentBiteDamage carries a BuffCounter per-stack term (Marks)
        // now threaded through the %HP path from the 몇 스택 knob; 0 Marks = the 5% base (which correctly
        // always applies -- the %HP does NOT require the target to be marked). Also exercises §12②: its
        // crit mMultiplier collapses to 1 (ability crit = no crit), so a full crit build doesn't inflate it.
        var kindred = LoadChampionFromBin("Kindred");

        const int level = 9;
        var stats = new ActivePlayerStats
        {
            AttackDamage = 100, AbilityPower = 0, AbilityE = 5, CritChance = 1.0, CritDamage = 2.0,
        };

        double? f0 = SkillDamage.ResolveHpPercentCalc(kindred, "E", "PercentBiteDamage", stats, level, stackCount: 0);
        double? f10 = SkillDamage.ResolveHpPercentCalc(kindred, "E", "PercentBiteDamage", stats, level, stackCount: 10);
        Assert.NotNull(f0);
        Assert.NotNull(f10);
        Assert.Equal(0.05, f0!.Value, precision: 4);                 // 5% missing HP base at 0 Marks (no crit inflation)
        Assert.Equal(0.05 + 0.005 * 10, f10!.Value, precision: 4);   // +0.5% missing HP per Mark
    }

    [Fact]
    public void RengarW_ResourceGte_ResolvesBaseVsFerocityEmpowered_LevelScaled()
    {
        // (§12③ payoff) Rengar W: at >=4 Ferocity (his resource) the empowered TotalDamageEmpowered
        // (ByCharLevelFormula 50..340 by CHAMPION level) REPLACES the base TotalDamage (a swap, not a
        // sum). AutoResolvable from live ResourceValue (Ferocity). This also exercises §12③: the
        // empowered value is level-scaled, so it resolves correctly only now that ByCharLevel* indexes
        // by champion level on a non-P slot.
        var rengar = LoadChampionFromBin("Rengar");
        ChampionRepository.Initialize(new[] { rengar });

        const int level = 18;
        ActivePlayerStats Stats(double fero) => new()
        {
            AbilityPower = 100, AttackDamage = 0, AbilityW = 5, ResourceValue = fero, ResourceMax = 4,
        };

        double baseD = SkillDamage.ComputeCalcDamage(rengar, "W", "TotalDamage", Stats(0), level)!.Value;
        double emp = SkillDamage.ComputeCalcDamage(rengar, "W", "TotalDamageEmpowered", Stats(0), level)!.Value;
        Assert.True(emp > baseD + 1, $"empowered W ({emp}) must exceed base ({baseD}) at level 18");

        // < 4 Ferocity: the base value (identity vs the prior base-only curation).
        var low = RunSingleSkill("Rengar", "W", Snapshot("Rengar", Stats(fero: 0), level));
        Assert.Equal(baseD, low.TotalDamage, precision: 2);
        // >= 4 Ferocity (live): the empowered, level-scaled value — resolved from ResourceValue, no knob.
        var high = RunSingleSkill("Rengar", "W", Snapshot("Rengar", Stats(fero: 4), level));
        Assert.Equal(emp, high.TotalDamage, precision: 2);
        // (loop 473) The resolved value still follows the live resource, but an untouched checkbox
        // spans [base, empowered]: the user has not said which the cast will be.
        Assert.Equal(baseD, low.RangeMin, precision: 2);
        Assert.Equal(emp, low.RangeMax, precision: 2);
    }

    [Fact]
    public void RumbleE_ResourceGte_ResolvesBaseVsDangerZone_FromLiveHeat()
    {
        // A second AutoResolvable champ (heat instead of fury): Rumble E is empowered +50% in the
        // Danger Zone (Heat >= 50), and the empowered value is a resolvable GameCalculationModified
        // (EmpDamage = TotalDamage × 1.5, BIN-sourced). Same proven ResourceGte shape as Renekton Q.
        var rumble = LoadChampionFromBin("Rumble");
        ChampionRepository.Initialize(new[] { rumble });

        const int level = 11;
        ActivePlayerStats Stats(double heat) => new()
        {
            AbilityPower = 300, AbilityE = 5, ResourceValue = heat, ResourceMax = 100,
        };

        double baseD = SkillDamage.ComputeCalcDamage(rumble, "E", "TotalDamage", Stats(0), level)!.Value;
        double emp = SkillDamage.ComputeCalcDamage(rumble, "E", "EmpDamage", Stats(0), level)!.Value;
        Assert.Equal(baseD * 1.5, emp, precision: 2); // Danger Zone is a BIN-sourced ×1.5

        // Heat < 50: the un-empowered baseline (value identity vs the prior curation).
        var cool = RunSingleSkill("Rumble", "E", Snapshot("Rumble", Stats(heat: 0), level));
        Assert.Equal(baseD, cool.TotalDamage, precision: 2);
        // Heat >= 50 (live): the Danger Zone value — resolved from ResourceValue, no user knob.
        var hot = RunSingleSkill("Rumble", "E", Snapshot("Rumble", Stats(heat: 60), level));
        Assert.Equal(emp, hot.TotalDamage, precision: 2);
        // (loop 473) Resolved from live Heat as before; the range now spans [base, Danger Zone].
        Assert.Equal(baseD, cool.RangeMin, precision: 2);
        Assert.Equal(emp, cool.RangeMax, precision: 2);
    }

    [Fact]
    public void MordekaiserQ_VsIsolated_DefaultsToGrouped_RangeSpansToIsolated()
    {
        // The UserAssumed path end-to-end: enemy isolation isn't API-observable, so the default is the
        // conservative grouped value and the exposure-graph widens the range up to the isolated value.
        // The isolated calc EmpoweredDamageTooltip = QDamage × IsolationScalar (BIN-sourced multiplier),
        // resolvable because its referenced base QDamage resolves.
        var morde = LoadChampionFromBin("Mordekaiser");
        ChampionRepository.Initialize(new[] { morde });

        const int level = 9;
        var stats = new ActivePlayerStats { AbilityPower = 300, AbilityQ = 5 };

        double grouped = SkillDamage.ComputeCalcDamage(morde, "Q", "QDamage", stats, level)!.Value;
        double isolated = SkillDamage.ComputeCalcDamage(morde, "Q", "EmpoweredDamageTooltip", stats, level)!.Value;
        Assert.True(isolated > grouped + 1, $"isolated ({isolated}) must exceed grouped ({grouped})");

        // No enemy -> FallbackDefender (0 MR): the magic hit lands unmitigated.
        var result = RunSingleSkill("Mordekaiser", "Q", Snapshot("Mordekaiser", stats, level));

        // Default (UserConditionMet unset) = the UNMET grouped baseline (value identity, zero regression).
        Assert.Equal(grouped, result.TotalDamage, precision: 2);
        // UserAssumed widens the range: floor = grouped, ceiling = isolated.
        Assert.Equal(grouped, result.RangeMin, precision: 2);
        Assert.Equal(isolated, result.RangeMax, precision: 2);
    }

    [Fact]
    public void KhazixQ_VsIsolated_IsolatedIsTwiceTenPercentOfBase()
    {
        // A second UserAssumed VsIsolated champ. Kha'Zix Q's isolated value IsoDamage is a
        // GameCalculationModified(BaseDamage × 2.1), resolvable once EffectAmounts are loaded (the base
        // uses EffectValueCalculationPart). Confirms the mechanism handles a champ whose base itself
        // needs the effect table — and that the prior "not modeled" isolation bonus is now modeled.
        var khazix = LoadChampionFromBin("Khazix");
        ChampionRepository.Initialize(new[] { khazix });

        const int level = 9;
        var stats = new ActivePlayerStats { AttackDamage = 250, AbilityPower = 0, AbilityQ = 5 };

        double grouped = SkillDamage.ComputeCalcDamage(khazix, "Q", "BaseDamage", stats, level)!.Value;
        double isolated = SkillDamage.ComputeCalcDamage(khazix, "Q", "IsoDamage", stats, level)!.Value;
        Assert.Equal(grouped * 2.1, isolated, precision: 2); // wiki: 210% vs isolated, BIN-sourced ×2.1

        var result = RunSingleSkill("Khazix", "Q", Snapshot("Khazix", stats, level));
        Assert.Equal(grouped, result.TotalDamage, precision: 2);  // default grouped (value identity)
        Assert.Equal(grouped, result.RangeMin, precision: 2);
        Assert.Equal(isolated, result.RangeMax, precision: 2);    // widens to the isolated ×2.1
    }

    [Fact]
    public void VolibearW_VsDebuffed_DefaultsToBase_RangeSpansToBite()
    {
        // First VsDebuffed curation (third UserAssumed condition type after VsIsolated / HpBelow).
        // Frenzied Maul's empowered "bite" (EmpoweredDamage = TotalDamage x BIN multiplier) applies only
        // when the target is already Wounded by a prior W. That debuff is not API-observable, so
        // VsDebuffed is UserAssumed: default = base TotalDamage (not wounded, value identity), and the
        // exposure-graph widens RangeMax to the bite value. Single hit (no bonus), modest stats keep both
        // ends under the 1000-HP dummy's lethality bound so RangeMax equals the raw bite exactly.
        var voli = LoadChampionFromBin("Volibear");
        ChampionRepository.Initialize(new[] { voli });

        const int level = 9;
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 0, AbilityW = 5 };

        double baseDmg = SkillDamage.ComputeCalcDamage(voli, "W", "TotalDamage", stats, level)!.Value;
        double bite = SkillDamage.ComputeCalcDamage(voli, "W", "EmpoweredDamage", stats, level)!.Value;
        Assert.True(bite > baseDmg + 1, $"wounded bite ({bite}) must exceed base ({baseDmg})");

        var result = RunSingleSkill("Volibear", "W", Snapshot("Volibear", stats, level));

        // Default (UserConditionMet unset) = the UNMET not-wounded base (value identity, zero regression).
        Assert.Equal(baseDmg, result.TotalDamage, precision: 2);
        // UserAssumed widens the range: floor = base, ceiling = the wounded bite.
        Assert.Equal(baseDmg, result.RangeMin, precision: 2);
        Assert.Equal(bite, result.RangeMax, precision: 2);
    }

    [Fact]
    public void PantheonQ_VsLowHp_DefaultsToBase_RangeSpansToAmplified()
    {
        // The VsLowHp archetype (category A): Comet Spear amplifies (~2x) against a target below 20%
        // max HP (BIN CritHealthThreshold = 0.20). It is a damage BONUS, not a kill-line execute
        // (must still exceed the target's HP to kill). Target HP is not API-observable -> HpBelow is
        // UserAssumed: default = base TapDamageCalc (unmet, value identity), the exposure-graph widens
        // RangeMax up to the amplified ExecuteDamageCalcModified. Unlike Morde/Kha'Zix, Pantheon Q also
        // carries a constant onAbility Mortal-Will bonus, so assert on the RANGE DELTA (which cancels
        // the constant) rather than absolute totals.
        var pantheon = LoadChampionFromBin("Pantheon");
        ChampionRepository.Initialize(new[] { pantheon });

        const int level = 5;
        // Deliberately modest bonus AD: the fallback dummy has 1000 max HP, and the engine correctly
        // bounds a combo by lethality (an already-lethal primary hit makes the trailing Mortal-Will
        // bonus pure overkill, so it would NOT show up in the total). Keeping both range ends under
        // 1000 lets the constant onAbility bonus cancel cleanly in the delta below.
        var stats = new ActivePlayerStats { AttackDamage = 50, AbilityPower = 0, AbilityQ = 5 };

        double baseTap = SkillDamage.ComputeCalcDamage(pantheon, "Q", "TapDamageCalc", stats, level)!.Value;
        double amplified = SkillDamage.ComputeCalcDamage(pantheon, "Q", "ExecuteDamageCalcModified", stats, level)!.Value;
        Assert.True(amplified > baseTap * 1.5, $"low-HP amplified ({amplified}) must roughly double the base ({baseTap})");

        var result = RunSingleSkill("Pantheon", "Q", Snapshot("Pantheon", stats, level));

        // Default (UserConditionMet unset) = the UNMET base end (value identity, zero regression).
        Assert.Equal(result.RangeMin, result.TotalDamage, precision: 2);
        // UserAssumed widens the range by exactly (amplified - base); the constant onAbility bonus,
        // present in both graphs, cancels in the delta.
        Assert.Equal(amplified - baseTap, result.RangeMax - result.RangeMin, precision: 2);
    }
}
