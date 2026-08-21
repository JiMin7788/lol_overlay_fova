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
/// Proves T3.1: the champion-intrinsic PASSIVE-damage ("P" slot) + per-skill BONUS-EFFECT
/// (on-hit/on-ability) data model and its application in the combo pipeline. All numbers
/// resolve LIVE from each champion's CommunityDragon BIN (via FormulaInterpreter) — the curated
/// JSON stores only structure (which calc, type, count, trigger); nothing is hardcoded.
///  1. The passive spell is extracted for a champion with passive damage (Darius Hemorrhage),
///     and its curated calc resolves a positive, hand-verified number.
///  2. A combo containing a Passive node adds the passive's damage to the total (end to end).
///  3. A skill/AA with a bonusEffects entry (Warwick's on-hit passive) adds the extra hit and
///     each hit mitigates by its OWN type (AA physical vs armor, on-hit bonus magic vs MR).
///  4. Backward-compat: a champion file WITHOUT P/bonusEffects (Ahri) produces the same total.
/// </summary>
public class PassiveBonusEffectTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public PassiveBonusEffectTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "PassiveBonusEffectTests_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Loads a champion straight from its cached BIN with ZERO base stats — so bonus AD
    /// equals total AD, keeping the hand-verified expecteds clean — and includes the PASSIVE
    /// ("P") slot the parser now emits (plus EffectAmounts) so passive calcs resolve.</summary>
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

    private static ChampionData Dummy(string id, double hp, double armor, double mr) => new()
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

    private static ComboNode AaNode() => new(
        Id: "AA_0",
        NodeType: ComboNodeType.Aa,
        Name: "AA",
        Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    private static GameSnapshot Snapshot(string activeChampion, ActivePlayerStats stats,
        int level = 6, string? enemyChampion = null, int enemyLevel = 6)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = level,
            PlayerCount = enemyChampion is null ? 1 : 2,
            Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = activeChampion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = level;

        if (enemyChampion is not null)
        {
            snap.Players[1].SummonerName = "Foe";
            snap.Players[1].ChampionName = enemyChampion;
            snap.Players[1].Team = "CHAOS";
            snap.Players[1].Level = enemyLevel;
        }
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

    // ── 1. passive spell is extracted and its curated calc resolves a real number ──────

    [Fact]
    public void PassiveSpell_IsExtracted_AndCuratedCalcResolvesPositive()
    {
        var darius = LoadChampionFromBin("Darius");

        // The parser now emits a "P" slot from the BIN's mCharacterPassiveSpell.
        Assert.True(darius.Skills.ContainsKey("P"), "passive spell was not extracted as slot P");

        // Curated: Darius P = Hemorrhage bleed, one physical hit via BleedDamagePerStack.
        var hits = SkillDamageDb.GetHits("Darius", "P");
        Assert.NotNull(hits);
        var hit = Assert.Single(hits!);
        Assert.Equal(HitDamageType.Physical, hit.Type);
        Assert.Equal("BleedDamagePerStack", hit.Calc);

        // BleedDamagePerStack @ level 6 = interpolate(13→30, t=(6-1)/17=5/17 → 18.0)
        //                                 + 0.3 * bonus AD(100, base 0) = 30  ⇒ 48.0.
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        double? value = SkillDamage.ComputeCalcDamage(darius, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(48.0, value!.Value, precision: 3);
    }

    // ── 2. a combo with a Passive node adds the passive's damage to the total ──────────

    [Fact]
    public void ComboWithPassiveNode_AddsPassiveDamageToTotal_EndToEnd()
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Darius") });

        // No enemy on board → FallbackDefender (0 armor): the physical bleed is unmitigated,
        // so the combo total is exactly the passive's raw number (48.0).
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        var snap = Snapshot("Darius", stats, level: 6);

        var result = RunCombo("Darius", new[] { SkillNode("P") }, snap);

        Assert.Equal(48.0, result.TotalDamage, precision: 2);
        Assert.Single(result.NodeBreakdown); // the single passive hit
    }

    // ── 3. a bonusEffects (on-hit passive) adds an extra hit, mitigated by its own type ─

    [Fact]
    public void BonusEffectOnHit_AddsExtraHit_EachMitigatedByOwnType()
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Warwick"),
            Dummy("Dummy", hp: 2000, armor: 100, mr: 25),
        });

        // Warwick P (Eternal Hunger) is curated as a bonusEffect trigger onHit.
        var bonus = SkillDamageDb.GetBonusEffects("Warwick", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);

        // One AA node → AA physical (total AD 100) PLUS the on-hit magic bonus (OnHitDamage @ lvl6
        //   = interpolate(6→55)=20.4118 + 0.15*bonusAD(100)=15 + 0.10*AP(100)=10 = 45.4118).
        // Mitigation differs by type against Dummy(armor 100, MR 25), proving per-type routing:
        //   AA physical  100        * 100/(100+100) = 50.00
        //   bonus magic  45.411765  * 100/(100+25)  = 36.329412
        //   total ≈ 86.33
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100 };
        var snap = Snapshot("Warwick", stats, level: 6, enemyChampion: "Dummy", enemyLevel: 6);

        double onHitRaw = SkillDamage.ComputeCalcDamage(
            LoadChampionFromBin("Warwick"), "P", "OnHitDamage", stats, level: 6)!.Value;
        double expected = Math.Round(
            100.0 * (100.0 / (100.0 + 100.0)) +          // AA physical vs armor 100
            onHitRaw * (100.0 / (100.0 + 25.0)),          // on-hit bonus magic vs MR 25
            2);

        var result = RunCombo("Warwick", new[] { AaNode() }, snap);

        Assert.Equal(2, result.NodeBreakdown.Count);          // AA + one appended on-hit bonus
        Assert.Equal(expected, result.TotalDamage, precision: 2);
        Assert.Equal(86.33, result.TotalDamage, precision: 2);
        // Strictly greater than the bare AA (50) → the bonus really was added.
        Assert.True(result.TotalDamage > 50.0 + 0.01, "on-hit bonus must add to the AA total");
    }

    // ── 4. backward-compat: a file WITHOUT P/bonusEffects is unchanged ─────────────────

    [Fact]
    public void ChampionWithoutPassiveOrBonus_ProducesSameTotalAsBefore()
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Ahri") });

        // Ahri Q "TotalDamage" = BaseDamage[5](135) + 0.5*AP(100) = 185, curated as two hits
        // (magic out + true return). No enemy → unmitigated → 2 * 185 = 370 (unchanged by the
        // schema additions: Ahri.json has neither a "P" slot nor "bonusEffects").
        Assert.Null(SkillDamageDb.GetHits("Ahri", "P"));
        Assert.Null(SkillDamageDb.GetBonusEffects("Ahri", "Q"));

        var stats = new ActivePlayerStats { AbilityPower = 100, AbilityQ = 5 };
        var snap = Snapshot("Ahri", stats, level: 6);

        var result = RunCombo("Ahri", new[] { SkillNode("Q") }, snap);

        Assert.Equal(370.0, result.TotalDamage, precision: 2);
        Assert.Equal(2, result.NodeBreakdown.Count);
    }

    // ── 5. newly curated passives (T3.x pass): hand-verified live BIN numbers ──────────

    [Fact]
    public void KaisaP_PBaseDamage_ResolvesHandVerifiedValue()
    {
        var kaisa = LoadChampionFromBin("Kaisa");

        var bonus = SkillDamageDb.GetBonusEffects("Kaisa", "P");
        Assert.NotNull(bonus);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("PBaseDamage", hit.Calc);

        // PBaseDamage @ level 6 = interpolate(4->24, t=(6-1)/17=5/17 -> 4+20*5/17=9.882353)
        //                          + 0.12 * AP(100) = 12  ⇒  21.882353.
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double? value = SkillDamage.ComputeCalcDamage(kaisa, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(21.88, value!.Value, precision: 2);
    }

    [Fact]
    public void SennaP_BonusOnHitDamage_ResolvesHandVerifiedValue()
    {
        var senna = LoadChampionFromBin("Senna");

        var bonus = SkillDamageDb.GetBonusEffects("Senna", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Physical, hit.Type);
        Assert.Equal("BonusOnHitDamage", hit.Calc);

        // BonusOnHitDamage = 0.2 * total AD(100) = 20.0 exactly (flat coefficient, no
        // interpolation, so level doesn't matter).
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        double? value = SkillDamage.ComputeCalcDamage(senna, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(20.0, value!.Value, precision: 2);
    }

    [Fact]
    public void GangplankP_TotalDamage_ResolvesHandVerifiedValue()
    {
        var gangplank = LoadChampionFromBin("Gangplank");

        var bonus = SkillDamageDb.GetBonusEffects("Gangplank", "P");
        Assert.NotNull(bonus);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        // M26 stage-4: Trial by Fire is TRUE damage per the wiki ("bonus true damage over
        // 2.5 seconds"); the original Magic pick was an unsourced assumption (type is not in the BIN).
        Assert.Equal(HitDamageType.True, hit.Type);
        Assert.Equal("TotalDamage", hit.Calc);

        // TotalDamage @ level 6 = interpolate(50->250, t=5/17 -> 50+200*5/17=108.823529)
        //                          + 1.0 * bonus AD(100, base 0) = 100  ⇒  208.823529.
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        double? value = SkillDamage.ComputeCalcDamage(gangplank, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(208.82, value!.Value, precision: 2);
    }

    // ── 6. T3.x-ext passive pass: further hand-verified live BIN numbers ───────────────

    [Fact]
    public void HweiP_TotalDamage_ResolvesHandVerifiedValue()
    {
        var hwei = LoadChampionFromBin("Hwei");

        var hit = Assert.Single(SkillDamageDb.GetHits("Hwei", "P")!);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("TotalDamage", hit.Calc);

        // TotalDamage @ level 6 = interpolate(40->285, t=5/17 -> 40+245*5/17=112.058824)
        //                          + 0.35 * AP(100) = 35  ⇒  147.058824.
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double? value = SkillDamage.ComputeCalcDamage(hwei, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(147.06, value!.Value, precision: 2);
    }

    [Fact]
    public void KogMawP_PassiveDamage_ResolvesHandVerifiedValue_AndEndToEnd()
    {
        var kogmaw = LoadChampionFromBin("KogMaw");

        var hit = Assert.Single(SkillDamageDb.GetHits("KogMaw", "P")!);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("PassiveDamage", hit.Calc);

        // PassiveDamage @ level 6 = pure interpolate(140->650, t=5/17 -> 140+510*5/17=140+150=290.0)
        // — no stat scaling at all, so stats are irrelevant.
        var stats = new ActivePlayerStats();
        double? value = SkillDamage.ComputeCalcDamage(kogmaw, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(290.0, value!.Value, precision: 2);

        // End-to-end: no enemy on board -> FallbackDefender (0 armor/MR), so the magic explosion
        // is unmitigated and the combo total is exactly the raw passive number.
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("KogMaw") });
        var snap = Snapshot("KogMaw", stats, level: 6);
        var result = RunCombo("KogMaw", new[] { SkillNode("P") }, snap);

        Assert.Equal(290.0, result.TotalDamage, precision: 2);
        Assert.Single(result.NodeBreakdown);
    }

    [Fact]
    public void UdyrP_HasNoCuratedDamage_LightningCalcIsOrphanedBinData()
    {
        // Bridge Between deals ZERO damage (wiki: bonus attack speed + Awakened Spirit cooldown
        // refund only). UdyrPassive.mSpellCalculations still carries a 'LightningDamage' calc, but
        // nothing in the BIN references it and no tooltip attributes damage to the passive — it is
        // orphaned legacy data, so P is deliberately not curated. See Udyr.json's _noteP.
        Assert.Null(SkillDamageDb.GetHits("Udyr", "P"));
        Assert.Null(SkillDamageDb.GetBonusEffects("Udyr", "P"));
    }

    [Fact]
    public void LuluP_OnHitBonus_AddsToAutoAttack_EndToEnd()
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Lulu"),
            Dummy("Dummy", hp: 2000, armor: 100, mr: 25),
        });

        var bonus = SkillDamageDb.GetBonusEffects("Lulu", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);

        // Pix fires all 3 bolts at the same first enemy hit on every basic attack, so the curated
        // calc is CombinedDamage (= TotalDamage x NumberOfBolts(3)), not the single-bolt TotalDamage.
        // TotalDamage @ level 6 = interpolate(5->39, t=5/17 -> 5+34*5/17=5+10=15) + 0.05*AP(100)=5
        //   ⇒ 20.0 per bolt; CombinedDamage = 3 x 20 = 60.0. AA physical (AD 100) vs armor 100
        //   = 50.00; on-hit magic (60.0) vs MR 25 = 60 * 100/125 = 48.00. Total = 98.00.
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100 };
        var snap = Snapshot("Lulu", stats, level: 6, enemyChampion: "Dummy", enemyLevel: 6);

        var lulu = LoadChampionFromBin("Lulu");
        double perBolt = SkillDamage.ComputeCalcDamage(lulu, "P", "TotalDamage", stats, level: 6)!.Value;
        Assert.Equal(20.0, perBolt, precision: 2);

        double onHitRaw = SkillDamage.ComputeCalcDamage(lulu, "P", "CombinedDamage", stats, level: 6)!.Value;
        Assert.Equal(perBolt * 3.0, onHitRaw, precision: 2);

        var result = RunCombo("Lulu", new[] { AaNode() }, snap);

        Assert.Equal(2, result.NodeBreakdown.Count);
        Assert.Equal(98.0, result.TotalDamage, precision: 2);
        Assert.True(result.TotalDamage > 50.0 + 0.01, "on-hit bonus must add to the AA total");
    }

    // ── 7. re-curated after the ByCharLevelBreakpoints InitialBonusPerLevel/PerLevelRate fix ──
    // (BinCalculation.CalculationPart.Breakpoints now carries InitialBonusPerLevel + per-breakpoint
    // PerLevelRate; previously only a one-off AdditionalBonus bump was modeled.)

    [Fact]
    public void KayleP_PassiveWaveDamage_ResolvesHandVerifiedValue_AtBreakpointLevel()
    {
        var kayle = LoadChampionFromBin("Kayle");

        var bonus = SkillDamageDb.GetBonusEffects("Kayle", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("PassiveWaveDamage", hit.Calc);

        // PassiveWaveDamage = ByCharLevelBreakpoints(Level1Value=20, no InitialBonusPerLevel,
        // breakpoint @12 -> PerLevelRate=3) + 0.25*AP + 0.10*bonus AD (mStatFormula=2).
        // Hand-traced level curve (rate=0 until level 12, since no InitialBonusPerLevel):
        //   lvl1..11 = 20 (flat, rate never applied)
        //   lvl12    = 20 + 3 (breakpoint reached, rate becomes 3, then added) = 23
        // At level 12, with AttackDamage=100 (base AD 0 -> bonus AD 100) and AP=100:
        //   23 + 0.25*100 + 0.10*100 = 23 + 25 + 10 = 58.0 exactly.
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100 };
        double? value = SkillDamage.ComputeCalcDamage(kayle, "P", hit.Calc, stats, level: 12);
        Assert.NotNull(value);
        Assert.Equal(58.0, value!.Value, precision: 2);

        // Below the breakpoint (level 11) the value is still the flat Level1Value (20) + stats,
        // proving the rate genuinely only kicks in AT the breakpoint, not before.
        double? below = SkillDamage.ComputeCalcDamage(kayle, "P", hit.Calc, stats, level: 11);
        Assert.NotNull(below);
        Assert.Equal(55.0, below!.Value, precision: 2); // 20 + 25 + 10
    }

    [Fact]
    public void AkaliP_AssassinsMarkDamage_ResolvesHandVerifiedValue_AtBreakpointLevel()
    {
        var akali = LoadChampionFromBin("Akali");

        var bonus = SkillDamageDb.GetBonusEffects("Akali", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnAbility, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("Damage", hit.Calc);

        // Damage = ByCharLevelBreakpoints(Level1Value=35, InitialBonusPerLevel=3,
        // breakpoints @8->rate9, @14->rate15) + 0.60*bonus AD (mStatFormula=2) + 0.55*AP.
        // Hand-traced level curve: 35,38,41,44,47,50,53 (lvl1-7, +3/lvl), then @8 rate->9 = 62.
        // At level 8, with AttackDamage=100 (bonus AD 100) and AP=100:
        //   62 + 0.60*100 + 0.55*100 = 62 + 60 + 55 = 177.0 exactly.
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100 };
        double? value = SkillDamage.ComputeCalcDamage(akali, "P", hit.Calc, stats, level: 8);
        Assert.NotNull(value);
        Assert.Equal(177.0, value!.Value, precision: 2);

        // One level earlier (7, still on the InitialBonusPerLevel=3 rate, base=53) proves the
        // breakpoint's PerLevelRate genuinely replaces the initial rate starting at level 8,
        // not before: 53 + 60 + 55 = 168.
        double? below = SkillDamage.ComputeCalcDamage(akali, "P", hit.Calc, stats, level: 7);
        Assert.NotNull(below);
        Assert.Equal(168.0, below!.Value, precision: 2);
    }

    // ── 8. T3.1-ext2 passive pass: further hand-verified live BIN numbers ──────────────

    [Fact]
    public void RammusP_SpikedShellOnHitDamage_ResolvesHandVerifiedValue()
    {
        var rammus = LoadChampionFromBin("Rammus");

        var bonus = SkillDamageDb.GetBonusEffects("Rammus", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("TotalDamage", hit.Calc);

        // TotalDamage = 0.15 * Armor(mStat=1) + 0.15 * MagicResist(mStat=6), no ByCharLevel
        // part at all, so rank/level is irrelevant. At Armor=100, MR=50:
        //   0.15*100 + 0.15*50 = 15 + 7.5 = 22.5 exactly.
        var stats = new ActivePlayerStats { Armor = 100, MagicResist = 50 };
        double? value = SkillDamage.ComputeCalcDamage(rammus, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(22.5, value!.Value, precision: 2);
    }

    [Fact]
    public void LissandraP_FrozenThrallExplosion_ResolvesHandVerifiedValue_AtLevel()
    {
        var lissandra = LoadChampionFromBin("Lissandra");

        var bonus = SkillDamageDb.GetBonusEffects("Lissandra", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.Self, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("TotalDamage", hit.Calc);

        // TotalDamage = ByCharLevelBreakpoints(Level1Value=120, InitialBonusPerLevel=20,
        // breakpoint @13 -> PerLevelRate=30) + StatByCoefficient(0.5, default stat AP).
        // Hand-traced level curve: 120,140,160,...,340 (lvl1-12, +20/lvl), then @13 rate->30:
        // 340+30=370, +30/lvl to 520 at lvl18. At level 18, AP=100:
        //   520 + 0.5*100 = 570.0 exactly.
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double? value = SkillDamage.ComputeCalcDamage(lissandra, "P", hit.Calc, stats, level: 18);
        Assert.NotNull(value);
        Assert.Equal(570.0, value!.Value, precision: 2);

        // At level 12 (one level before the breakpoint fires), value is still 340 (+50 AP)=390,
        // proving the +30/lvl rate genuinely only starts AT level 13, not before.
        double? below = SkillDamage.ComputeCalcDamage(lissandra, "P", hit.Calc, stats, level: 12);
        Assert.NotNull(below);
        Assert.Equal(390.0, below!.Value, precision: 2);
    }

    [Fact]
    public void SonaP_PowerChordDamage_ResolvesHandVerifiedValue_AtBreakpointLevel()
    {
        var sona = LoadChampionFromBin("Sona");

        var bonus = SkillDamageDb.GetBonusEffects("Sona", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("PowerChordDamage", hit.Calc);

        // PowerChordDamage = ByCharLevelBreakpoints(Level1Value=20, InitialBonusPerLevel=10,
        // breakpoint @9 -> PerLevelRate=15) + StatByCoefficient(0.20, default stat AP).
        // Hand-traced level curve: 20,30,...,90 (lvl1-8, +10/lvl), then @9 rate->15: 90+15=105,
        // +15/lvl to 240 at lvl18. At level 18, AP=100:
        //   240 + 0.20*100 = 260.0 exactly.
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double? value = SkillDamage.ComputeCalcDamage(sona, "P", hit.Calc, stats, level: 18);
        Assert.NotNull(value);
        Assert.Equal(260.0, value!.Value, precision: 2);

        // At level 8 (one level before the breakpoint fires): 90 + 0.20*100 = 110.0, proving the
        // +15/lvl rate genuinely only starts AT level 9, not before.
        double? below = SkillDamage.ComputeCalcDamage(sona, "P", hit.Calc, stats, level: 8);
        Assert.NotNull(below);
        Assert.Equal(110.0, below!.Value, precision: 2);
    }

    // ── 9. T-new passive pass (Locke/Zoe/Khazix): further hand-verified live BIN numbers ──
    // (Locke and Zoe are the first champions curated in this pass whose "P" damage resolves
    // live; Khazix documents the opposite -- a genuine ByCharLevelFormulaCalculationPart gap.)

    [Fact]
    public void ZoeP_PassiveDamage_ResolvesHandVerifiedValue()
    {
        var zoe = LoadChampionFromBin("Zoe");

        var bonus = SkillDamageDb.GetBonusEffects("Zoe", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("PassiveDamage", hit.Calc);

        // PassiveDamage = ByCharLevelBreakpoints(Level1Value=16, InitialBonusPerLevel=4,
        // breakpoints @7->rate6, @12->rate8, @15->rate10) + StatByNamedDataValue(APRatio=0.2).
        // At level 6 (below the first breakpoint, so rate stays 4 for all 5 steps):
        //   16 + 4*5 = 36, + 0.2*AP(100) = 20  ⇒  56.0 exactly.
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double? value = SkillDamage.ComputeCalcDamage(zoe, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(56.0, value!.Value, precision: 2);
    }

    [Fact]
    public void LockeP_MinOnHitDamage_ResolvesHandVerifiedValue()
    {
        var locke = LoadChampionFromBin("Locke");

        var bonus = SkillDamageDb.GetBonusEffects("Locke", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("MinOnHitDamage", hit.Calc);

        // MinOnHitDamage = ByCharLevelInterpolation(5->40) + StatByCoefficient(0.1, no mStat ->
        // defaults to AbilityPower per SkillDamage.BuildStatResolver's StatAbilityPower=0).
        // At level 6: interpolate t=(6-1)/17=5/17 -> 5 + 35*5/17 = 15.294118, + 0.1*AP(100) = 10
        //   ⇒ 25.294118.
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double? value = SkillDamage.ComputeCalcDamage(locke, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(25.29, value!.Value, precision: 2);
    }

    [Fact]
    public void KhazixP_TotalDamage_ResolvesViaByCharLevelFormula()
    {
        var khazix = LoadChampionFromBin("Khazix");

        var bonus = SkillDamageDb.GetBonusEffects("Khazix", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        // §11.E fix (loop 106): Unseen Threat is MAGIC (wiki: 17-136 +50% bonus AD magic), not True.
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("TotalDamage", hit.Calc);

        // TotalDamage = ByCharLevelFormulaCalculationPart(values=[10,17,24,...], step +7,
        // indexed directly by champion level) + StatByNamedDataValueCalculationPart
        // (BonusADRatio flat 0.5 * bonus AD). The ByCharLevelFormula gap this curation pass
        // originally documented was later closed (CalculationPart.Kind.ByCharLevelFormula added),
        // so it now resolves live. At level 6: values[6] = 10 + 6*7 = 52; + 0.5 * bonusAD(100) = 50
        //   ⇒ 102.
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        double? value = SkillDamage.ComputeCalcDamage(khazix, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(102.0, value!.Value, precision: 2);
    }

    // ── 10. T-new2 passive pass (Braum/Nautilus/Bard): further hand-verified live BIN numbers ──
    // (Aphelios/Yunara/Pyke were investigated the same session and found genuinely blocked —
    // see their _noteP entries in Data/skill_damage/*.json; no P slot to test for them.)

    [Fact]
    public void BraumP_TotalDamage_ResolvesViaByCharLevelFormula()
    {
        var braum = LoadChampionFromBin("Braum");

        var bonus = SkillDamageDb.GetBonusEffects("Braum", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("TotalDamage", hit.Calc);

        // TotalDamage = ByCharLevelFormulaCalculationPart(values=[16,26,36,...], step +10,
        // indexed directly by champion level), no stat term at all. At level 6: values[6] = 76.0
        // exactly (stats irrelevant).
        var stats = new ActivePlayerStats();
        double? value = SkillDamage.ComputeCalcDamage(braum, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(76.0, value!.Value, precision: 2);
    }

    [Fact]
    public void NautilusP_TotalDamageTooltip_ResolvesHandVerifiedValue()
    {
        var nautilus = LoadChampionFromBin("Nautilus");

        var bonus = SkillDamageDb.GetBonusEffects("Nautilus", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.OnHit, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Physical, hit.Type);
        Assert.Equal("TotalDamageTooltip", hit.Calc);

        // TotalDamageTooltip = ByCharLevelFormulaCalculationPart(values=[8,14,20,...], step +6,
        // indexed directly by champion level) + StatByCoefficientCalculationPart(mStat=2, no
        // mStatFormula -> TOTAL AD, coefficient=1.0). At level 6: values[6]=44 + 1.0*totalAD(100)
        //   = 144.0 exactly.
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        double? value = SkillDamage.ComputeCalcDamage(nautilus, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(144.0, value!.Value, precision: 2);
    }

    [Fact]
    public void BardP_MeepDamageNoChime_ResolvesHandVerifiedValue()
    {
        var bard = LoadChampionFromBin("Bard");

        var bonus = SkillDamageDb.GetBonusEffects("Bard", "P");
        Assert.NotNull(bonus);
        Assert.Equal(BonusTrigger.Self, Assert.Single(bonus!).Trigger);
        var hit = Assert.Single(Assert.Single(bonus!).Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("MeepDamageNoChime", hit.Calc);

        // MeepDamageNoChime = NamedDataValue('BaseMeepDamage'=30, flat) +
        // StatByNamedDataValue('MeepAPRatio'=0.4, no mStat -> AP). No ByCharLevel part at all, so
        // level is irrelevant. At AP=100: 30 + 0.4*100 = 70.0 exactly.
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double? value = SkillDamage.ComputeCalcDamage(bard, "P", hit.Calc, stats, level: 6);
        Assert.NotNull(value);
        Assert.Equal(70.0, value!.Value, precision: 2);
    }

    // ── 11. Annie Pyromania special-property override: corrected stack-gate formula ────
    // (Data/special_properties/annie.json previously stored a fabricated "PassiveStackDamage"
    // number for Pyromania. Confirmed against the League of Legends Wiki (Annie, Innate:
    // Pyromania) and the cached BIN (AnniePassiveAbility/AnniePassive) that Pyromania deals
    // NO bonus damage — it is a flat 4-stack gate (BIN DataValue "MaxStacks"=4.0, not level-
    // scaled) that empowers a stun. The override now models exactly that: valueFormula is the
    // raw stack-count identity, not a fabricated damage number.)

    [Fact]
    public void AnniePyromania_SpecialPropertyOverride_LoadsCorrectedStackGateFormula()
    {
        var champions = new Dictionary<string, ChampionData>(StringComparer.OrdinalIgnoreCase)
        {
            ["Annie"] = new ChampionData { Id = "Annie", Name = "Annie" },
        };

        var dir = Path.Combine(AppContext.BaseDirectory, "data", "special_properties");
        SpecialPropertyLoader.LoadAndMerge(dir, champions);

        Assert.True(champions["Annie"].SpecialProperties.TryGetValue("PyromaniaStunGate", out var prop));
        Assert.Equal("stack", prop!.ValueFormula);

        // The real Pyromania gate is a flat 4-stack threshold (BIN MaxStacks=4.0 at every
        // champion level) — the formula must pass the raw stack count through unscaled,
        // regardless of level.
        Assert.Equal(4.0, FormulaParser.Evaluate(prop.ValueFormula,
            new Dictionary<string, double> { ["stack"] = 4, ["level"] = 11 }));
        Assert.Equal(2.0, FormulaParser.Evaluate(prop.ValueFormula,
            new Dictionary<string, double> { ["stack"] = 2, ["level"] = 1 }));
    }
}
