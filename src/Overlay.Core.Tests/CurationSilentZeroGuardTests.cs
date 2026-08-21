using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// Guards the loop-221+ re-curation batch (CLAUDE_CODE_TODO §46-§65) against the "silent zero"
/// failure class found on Akshan E (§48): a curated hit whose calc throws inside
/// ComputeCalcDamage is caught as null and the ability quietly deals 0 in the combo tool while
/// looking curated. The structural validator (tools/validate_skill_damage.py) proves the
/// referenced names EXIST in the BIN; these tests prove they EVALUATE through the real
/// ComboEngine to a positive number. Deliberately no exact golden values — hit shape + sign
/// only — so the tests survive data refreshes but still catch a calc dropping to 0/null.
/// </summary>
public class CurationSilentZeroGuardTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public CurationSilentZeroGuardTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "CurationSilentZeroGuard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── fixtures (PercentHpDamageTests pattern) ─────────────────────────────────────────

    private static string FixturePath(string champion)
        => Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{champion}.bin.json");

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

    private static ChampionData Dummy(string id, double hp = 0) => new()
    {
        Id = id,
        Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp },
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

    private static ComboNode AaNode() => new(
        Id: "aa_0",
        NodeType: ComboNodeType.Aa,
        Name: "AA",
        Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    private static ActivePlayerStats SampleStats() => new()
    {
        AttackDamage = 200,
        AbilityPower = 200,
        ResourceMax = 1000,
        AbilityQ = 5,
        AbilityW = 5,
        AbilityE = 5,
        AbilityR = 3,
    };

    private static GameSnapshot Snapshot(string activeChampion, ActivePlayerStats stats,
        string enemyChampion, int level = 18)
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
        snap.Players[1].Level = level;
        return snap;
    }

    private ComboResult RunCombo(string championId, ComboNode[] nodes, GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo(championId, "c");
        foreach (var n in nodes) editor.AddNode(draft.Id, n);
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

    /// <summary>Runs a single skill slot through the full engine and returns TotalDamage.</summary>
    private double SlotTotal(string championId, string slot, double enemyHp = 1000)
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin(championId), Dummy("Squishy", enemyHp) });
        var snap = Snapshot(championId, SampleStats(), "Squishy");
        return RunCombo(championId, new[] { SkillNode(slot) }, snap).TotalDamage;
    }

    // ── one guaranteed-positive slot per re-curated champion/slot ───────────────────────
    //
    // §46 Mel Q/E/R, §49 Elise Q, §50 Ambessa Q, §51 Nilah Q, §52 Vel'Koz W/R, §53 Caitlyn Q,
    // §54 Sylas Q, §55 Twitch E, §56 Vladimir E, §57 Fizz Q (also pinned in
    // SkillDataCurationTests), §58 Skarner Q, §59 Warwick Q, §60 Shaco R, §62 Darius Q/W,
    // §64 Orianna Q/E, §65 Singed E, §47 Xayah E / Smolder Q / TwistedFate W.
    // (§47 Zeri E, Viego Q/R, XinZhao W already pinned elsewhere; §61/§63 P passives below.)

    [Theory]
    [InlineData("Mel", "Q")]
    [InlineData("Mel", "E")]
    [InlineData("Mel", "R")]
    [InlineData("Elise", "Q")]
    [InlineData("Ambessa", "Q")]
    [InlineData("Nilah", "Q")]
    [InlineData("Velkoz", "W")]
    [InlineData("Velkoz", "R")]
    [InlineData("Caitlyn", "Q")]
    [InlineData("Sylas", "Q")]
    [InlineData("Twitch", "E")]
    [InlineData("Vladimir", "E")]
    [InlineData("Fizz", "Q")]
    [InlineData("Skarner", "Q")]
    [InlineData("Warwick", "Q")]
    [InlineData("Shaco", "R")]
    [InlineData("Darius", "Q")]
    [InlineData("Darius", "W")]
    [InlineData("Orianna", "Q")]
    [InlineData("Orianna", "E")]
    [InlineData("Singed", "E")]
    [InlineData("Xayah", "E")]
    [InlineData("Smolder", "Q")]
    [InlineData("TwistedFate", "W")]
    // Camille Q left this list in loop 482: Precision Protocol deals nothing on CAST — both of its
    // casts empower a basic attack — so it has no direct hits to guard. Its two riders and the
    // attack they land on are covered by MultiCastSplitTests instead.
    // (was: recast-doubled EmpoweredBonusDamage -> first-cast BonusDamage, M26 stage-4,
    // 2026-07-26) — pin that the corrected calc evaluates.
    // Jayce (golden-unlock round 2026-07-26, de-auto'd): both stances, incl. the binSpell-routed
    // Cannon extra slots — pins that extra-slot resolution stays alive end to end.
    [InlineData("Jayce", "Q")]
    [InlineData("Jayce", "E")]
    [InlineData("Jayce", "QCannon")]
    [InlineData("Jayce", "WCannon")]
    // Hwei (golden queue #13, 2026-07-26): the three Q muse branches route via binSpell like
    // Jayce's cannon slots — pin them ahead of the measurement round.
    // Stage-4 audit round (loops 439-440): new/changed slots — Briar WBite (bite re-modeled
    // from an AA rider that double-counted 100% AD), Swain Q range anchor + the RDemonflare
    // recast, Jinx R with its previously-missing missing-health hit.
    // Stage-4 sweep-closure round (loop 442): every new selectable slot + the Malzahar R zone hit.
    [InlineData("Malzahar", "R")]
    [InlineData("Kayn", "QRhaast")]
    [InlineData("Kayn", "RRhaast")]
    [InlineData("Heimerdinger", "W")]
    [InlineData("Yuumi", "QEmpowered")]
    [InlineData("Irelia", "RWall")]
    [InlineData("Kled", "QDismounted")]
    [InlineData("Naafiri", "Q2")]      // renamed from QSecond in loop 478 for one naming convention
    [InlineData("Taliyah", "QWorked")]
    [InlineData("Briar", "WBite")]
    [InlineData("Swain", "Q")]
    [InlineData("Swain", "RDemonflare")]
    [InlineData("Jinx", "R")]
    // Stage-4 audit round (loop 438): slots whose curation changed — Galio W and Gragas Q
    // re-anchored to their conservative Min calc (DistanceScaled range), Gragas W gained the
    // missing 7%-target-maxHP hit.
    [InlineData("Galio", "W")]
    [InlineData("Gragas", "Q")]
    [InlineData("Gragas", "W")]
    // Ivern (golden-unlock prep, loop 434): Q direct + E Triggerseed AoE.
    [InlineData("Ivern", "Q")]
    [InlineData("Ivern", "E")]
    // Aphelios (golden-unlock prep, loop 429): the 5 per-weapon Q slots route binSpell to HASHED
    // object names ({9501e989}...), the highest-risk lookup path in the file; plus both R shapes.
    [InlineData("Aphelios", "QCalibrum")]
    [InlineData("Aphelios", "QSeverum")]
    [InlineData("Aphelios", "QGravitum")]
    [InlineData("Aphelios", "QInfernum")]
    [InlineData("Aphelios", "R")]
    [InlineData("Aphelios", "RInfernum")]
    // Gnar (golden-unlock prep, loop 427): both forms — the Mega slots route via binSpell to the
    // SAME canonical spell object (Riot stores Mini+Mega calcs together), so they also exercise
    // the canonical-binSpell rank rule (Mega casts at the base slot's own rank).
    [InlineData("Gnar", "Q")]
    [InlineData("Gnar", "W")]
    [InlineData("Gnar", "E")]
    [InlineData("Gnar", "R")]
    [InlineData("Gnar", "QMega")]
    [InlineData("Gnar", "WMega")]
    [InlineData("Gnar", "EMega")]
    [InlineData("Hwei", "QQ")]
    [InlineData("Hwei", "QW")]
    [InlineData("Hwei", "QE")]
    // (loop 480) The E muses are three slots of their own now, not one hit mislabelled as E.
    [InlineData("Hwei", "EQ")]
    [InlineData("Hwei", "EW")]
    [InlineData("Hwei", "EE")]
    [InlineData("Hwei", "R")]
    // Naafiri R: the BIN's spellNames order is scrambled (Q, R, E, w) — pin that the R slot
    // still resolves its TotalDamage (slot-shell sweep 2026-07-26 flagged it).
    [InlineData("Naafiri", "R")]
    // RekSai: de-auto'd 2026-07-24 after the M32 evidence round (see skill_damage/RekSai.json _note).
    [InlineData("RekSai", "Q")]
    [InlineData("RekSai", "W")]
    [InlineData("RekSai", "E")]
    [InlineData("RekSai", "R")]
    public void RecuratedSlot_EvaluatesToPositiveDamage(string championId, string slot)
    {
        Assert.True(SlotTotal(championId, slot) > 0,
            $"{championId} {slot}: curated hits evaluated to 0 — silent-zero regression (§48 class)");
    }

    // ── %-HP components actually contribute (they'd be invisible to the >0 check) ──────

    [Theory]
    [InlineData("Skarner", "Q")]   // §58 MaxHPPercent finisher (hpPercentDataValue)
    [InlineData("Warwick", "Q")]   // §59 TargetPercentHPDamage (hpPercentDataValue)
    [InlineData("Singed", "E")]    // §65 MaxHPDamage (hpPercentDataValue)
    [InlineData("Elise", "Q")]     // §49 HumanPercentHealth (%current HP) kept alongside new flat
    // RekSai R basis corrected Missing->Max (2026-07-24, wiki+BIN agree on 15/20/25% MAX health);
    // with a full-HP enemy a Missing-basis regression contributes 0 and kills the scaling below.
    [InlineData("RekSai", "R")]
    // Jayce E carries the 8-22% max-HP magic term (PercHPDamage, evidence MATCH).
    [InlineData("Jayce", "E")]
    public void PercentHpComponent_ScalesWithEnemyHp(string championId, string slot)
    {
        var vsSmall = SlotTotal(championId, slot, enemyHp: 1000);
        var vsBig = SlotTotal(championId, slot, enemyHp: 2000);
        Assert.True(vsBig > vsSmall,
            $"{championId} {slot}: total did not grow with enemy max HP — %HP hit is dead");
    }

    // ── Aphelios QCrescendum: summon-pattern turret (perAttackCalc × user shot count) ────

    [Fact]
    public void ApheliosQCrescendum_TurretShots_ScaleWithUserAttackCount()
    {
        // Re-curated 2026-07-26 (ratio-as-damage fix — the old MiniDamageMin pick evaluated the
        // 5%-per-chakram passive RATIO, ~4 raw, a positivity-guard blind spot). One turret shot
        // must be a real number; unset knob stays an honest 0.
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Aphelios"), Dummy("Squishy", 1000) });
        var snap = Snapshot("Aphelios", SampleStats(), "Squishy");
        var oneShot = SkillNode("QCrescendum") with { UserAttackCount = 1 };
        double dmg = RunCombo("Aphelios", new[] { oneShot }, snap).TotalDamage;
        Assert.True(dmg > 5.0, $"Crescendum turret single shot evaluated to {dmg:0.##} — silent-zero class");
        double unset = RunCombo("Aphelios", new[] { SkillNode("QCrescendum") }, snap).TotalDamage;
        Assert.Equal(0.0, unset, 2); // honest default: no shot count assumed
    }

    // ── Fizz W follow-up on-hit rider (loop-442 audit) ──────────────────────────────────

    [Fact]
    public void FizzW_FollowUpOnHit_AddsDamageToAaAfterCast()
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Fizz"), Dummy("Squishy", 1000) });
        var snap = Snapshot("Fizz", SampleStats(), "Squishy");
        double aaAlone = RunCombo("Fizz", new[] { AaNode() }, snap).TotalDamage;
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Fizz"), Dummy("Squishy", 1000) });
        double wAlone = RunCombo("Fizz", new[] { SkillNode("W") }, snap).TotalDamage;
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Fizz"), Dummy("Squishy", 1000) });
        double wThenAa = RunCombo("Fizz", new[] { SkillNode("W"), AaNode() }, snap).TotalDamage;
        Assert.True(wThenAa > wAlone + aaAlone + 0.5,
            $"Fizz W on-hit rider dead: W {wAlone:0.##} + AA {aaAlone:0.##} vs W→AA {wThenAa:0.##}");
    }

    // ── Alistar E trample proc: full-stack empowered-AA rider added by the loop-439 audit ──

    [Fact]
    public void AlistarE_TrampleProc_AddsDamageToAaAfterCast()
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Alistar"), Dummy("Squishy", 1000) });
        var snap = Snapshot("Alistar", SampleStats(), "Squishy");
        double aaAlone = RunCombo("Alistar", new[] { AaNode() }, snap).TotalDamage;
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Alistar"), Dummy("Squishy", 1000) });
        double eThenAa = RunCombo("Alistar", new[] { SkillNode("E"), AaNode() }, snap).TotalDamage;
        // E's own zone damage also lands, so compare against E alone + AA alone.
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Alistar"), Dummy("Squishy", 1000) });
        double eAlone = RunCombo("Alistar", new[] { SkillNode("E") }, snap).TotalDamage;
        Assert.True(eThenAa > eAlone + aaAlone + 0.5,
            $"Alistar trample proc dead: E {eAlone:0.##} + AA {aaAlone:0.##} vs E→AA {eThenAa:0.##}");
    }

    // ── Elise R spiderlings: summon per-attack slot added by the loop-438 audit ─────────

    [Fact]
    public void EliseRSpiderling_PerAttack_ScalesWithUserAttackCount()
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Elise"), Dummy("Squishy", 1000) });
        var snap = Snapshot("Elise", SampleStats(), "Squishy");
        var oneHit = SkillNode("RSpiderling") with { UserAttackCount = 1 };
        double dmg = RunCombo("Elise", new[] { oneHit }, snap).TotalDamage;
        Assert.True(dmg > 5.0, $"spiderling per-attack evaluated to {dmg:0.##} — silent-zero class");
    }

    // ── Ivern: the W brush onHit rider and the Daisy summon per-attack path ─────────────

    [Fact]
    public void IvernW_BrushRider_AddsDamageToAaAfterCast()
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Ivern"), Dummy("Squishy", 1000) });
        var snap = Snapshot("Ivern", SampleStats(), "Squishy");
        double aaAlone = RunCombo("Ivern", new[] { AaNode() }, snap).TotalDamage;
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Ivern"), Dummy("Squishy", 1000) });
        double wThenAa = RunCombo("Ivern", new[] { SkillNode("W"), AaNode() }, snap).TotalDamage;
        Assert.True(wThenAa > aaAlone + 0.5,
            $"Ivern W brush rider dead: AA alone {aaAlone:0.##}, W→AA {wThenAa:0.##}");
    }

    [Fact]
    public void IvernR_DaisyAttack_ScalesWithUserAttackCount()
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Ivern"), Dummy("Squishy", 1000) });
        var snap = Snapshot("Ivern", SampleStats(), "Squishy");
        var oneHit = SkillNode("R") with { UserAttackCount = 1 };
        double dmg = RunCombo("Ivern", new[] { oneHit }, snap).TotalDamage;
        Assert.True(dmg > 5.0, $"Daisy per-attack evaluated to {dmg:0.##} — silent-zero class");
    }

    // ── Hwei W (WE muse) empowered-AA rider fires on an AA AFTER the W cast ─────────────

    [Fact]
    public void HweiW_OnHitRider_AddsDamageToAaAfterCast()
    {
        // Slot-shell regression fix 2026-07-26: OnHitDamage routed via binSpell=HweiWE. The
        // engine's rule (Locke Q precedent): an ability-slot onHit empowers AAs only AFTER that
        // ability's node — so [WE, AA] must exceed a bare [AA]. (loop 480: the rider moved from the
        // W selector onto the WE sub-cast that actually grants it, which needed extra slots to be
        // scanned for bonus effects at all — SkillDamageDb.BonusSourceSlots.)
        double aaAlone = SlotTotalNodes(new[] { AaNode() });
        double wThenAa = SlotTotalNodes(new[] { SkillNode("WE"), AaNode() });
        Assert.True(wThenAa > aaAlone + 0.5,
            $"Hwei WE rider dead: AA alone {aaAlone:0.##}, WE→AA {wThenAa:0.##}");
    }

    private double SlotTotalNodes(ComboNode[] nodes)
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Hwei"), Dummy("Squishy", 1000) });
        var snap = Snapshot("Hwei", SampleStats(), "Squishy");
        return RunCombo("Hwei", nodes, snap).TotalDamage;
    }

    // ── §46 Mel W is now HONESTLY uncurated (reflect % is not computable, P2) ───────────

    [Fact]
    public void MelW_IsUncurated_AndLoadsWithoutThrowing()
    {
        var ex = Record.Exception(() => SkillDamageDb.GetHits("Mel", "W"));
        Assert.Null(ex);
        Assert.True(SkillDamageDb.GetHits("Mel", "W") is null or { Length: 0 },
            "Mel W must stay uncurated — its only BIN value is a reflect PERCENTAGE, not flat damage");
    }

    // ── §59 Warwick Q damage-type fix: the whole ability is Magic ───────────────────────

    [Fact]
    public void WarwickQ_BothHitsAreMagic()
    {
        var hits = SkillDamageDb.GetHits("Warwick", "Q");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.All(hits, h => Assert.Equal(HitDamageType.Magic, h.Type));
    }

    // ── §61 Aatrox P: onHit %maxHP rider fires on a basic attack ────────────────────────

    [Fact]
    public void AatroxP_OnHitRider_AddsMaxHpScaledDamageToAa()
    {
        double AaTotal(double enemyHp)
        {
            EventBus.EventBus.ResetForTests();
            ChampionRepository.Initialize(new[] { LoadChampionFromBin("Aatrox"), Dummy("Squishy", enemyHp) });
            var snap = Snapshot("Aatrox", SampleStats(), "Squishy");
            return RunCombo("Aatrox", new[] { AaNode() }, snap).TotalDamage;
        }

        // The AA itself is pure AD (identical both runs); any growth is the P %maxHP rider.
        Assert.True(AaTotal(2000) > AaTotal(1000),
            "Aatrox P: AA total did not grow with enemy max HP — the onHit %maxHP rider is dead");
    }

    // ── §63 Katarina P: onAbility proc resolves on the P slot (no Q name collision) ─────

    [Fact]
    public void KatarinaP_OnAbilityProc_ResolvesOnPSlot()
    {
        var onAbility = SkillDamageDb.GetAttachableBonusEffects("Katarina")
            .Where(e => e.Effect.Trigger == BonusTrigger.OnAbility && e.Slot == "P")
            .ToList();
        var eff = Assert.Single(onAbility);
        Assert.Contains(eff.Effect.Hits, h => h.Calc == "TotalDamage" && h.Type == HitDamageType.Magic);

        // The calc must evaluate against Katarina's P slot specifically — Q has its own
        // same-named "TotalDamage"; a cross-slot pick was the §63 risk.
        var kata = LoadChampionFromBin("Katarina");
        var value = SkillDamage.ComputeCalcDamage(kata, "P", "TotalDamage", SampleStats(), level: 18);
        Assert.True(value is > 0, "Katarina P TotalDamage did not resolve to a positive number on the P slot");
    }
}
