using System.IO;
using System.Linq;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 7) The shapes the "instead" / repeat-hit tooltips needed: an unmet half that is the hit's
/// own %HP share (Ambessa Q2), a met half that is a different per-second rate (Anivia R), a repeat-hit
/// count with the tooltip's cap (Syndra R) that the range ceiling reaches, and a per-stack bonus with a
/// BIN cap (Yasuo E). Loop 8: a met half that is a plain DataValue (Evelynn E). Loop 9: a met half
/// with its own damage type (Rek'Sai E) and a split hit (Yone R).
/// </summary>
public class M45Loop7PrimitiveTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public M45Loop7PrimitiveTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L7_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly ActivePlayerStats Stats = new()
    {
        AttackDamage = 200, AbilityPower = 100, AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 2,
    };

    private static double Resolve(string champ, string slot, int index, bool? met = null, double? seconds = null,
        int stacks = 0, int? attacks = null)
    {
        var hit = SkillDamageDb.GetHits(champ, slot)![index];
        Assert.True(ComboRunner.ResolveHitForAudit(hit, LoadChampionFromBin(champ), slot, Stats, 11, 1000.0,
            seconds, met, stacks, allOut: false, out double damage, out _, out _, attackCount: attacks));
        return damage;
    }

    [Fact]
    public void AmbessaQ2_PercentHpShare_IsHalfOffTheSweetSpot()
    {
        double edge = Resolve("Ambessa", "Q2", 1, met: true);
        double other = Resolve("Ambessa", "Q2", 1, met: false);
        Assert.True(edge > 0);
        Assert.Equal(edge * 0.5, other, 6); // Damage_2_Min_Ratio
    }

    [Fact]
    public void AniviaR_FullyFormedStorm_IsTripleTheRateOverTheSameSeconds()
    {
        double plain = Resolve("Anivia", "R", 0, met: false, seconds: 2);
        Assert.True(plain > 0);
        Assert.Equal(plain * 3, Resolve("Anivia", "R", 0, met: true, seconds: 2), 6);
        Assert.Equal(0, Resolve("Anivia", "R", 0, met: true)); // unset exposure is still 0 s (M24)
    }

    [Fact]
    public void SyndraR_ExtraSpheres_StopAtTheTooltipCap()
    {
        double four = Resolve("Syndra", "R", 1, attacks: 4);
        Assert.True(four > 0);
        Assert.Equal(four, Resolve("Syndra", "R", 1, attacks: 9), 6);
        Assert.Equal(0, Resolve("Syndra", "R", 1));
    }

    [Fact]
    public void YasuoE_StacksAddAQuarterEach_UpToMaxStacks()
    {
        double plain = Resolve("Yasuo", "E", 0);
        Assert.Equal(plain * 1.5, Resolve("Yasuo", "E", 0, stacks: 2), 6);
        Assert.Equal(plain * 2.0, Resolve("Yasuo", "E", 0, stacks: 9), 6); // MaxStacks 4
    }

    [Fact]
    public void EvelynnE_EmpoweredWhip_IsTheEmpoweredDataValue()
    {
        // rank 3: BaseDamage 120, EmpoweredDamage 160 (BIN DataValues, no calc of their own)
        Assert.Equal(120, Resolve("Evelynn", "E", 0, met: false), 6);
        Assert.Equal(160, Resolve("Evelynn", "E", 0, met: true), 6);
    }

    [Fact]
    public void RekSaiE_MaxFuryBite_IsTrueDamage_YoneR_IsHalfAndHalf()
    {
        var bite = SkillDamageDb.GetHits("RekSai", "E")![0];
        Assert.Equal(new[] { ComboDamageType.Physical }, ComboRunner.HitTypesForAudit(bite, conditionMet: false));
        Assert.Equal(new[] { ComboDamageType.True }, ComboRunner.HitTypesForAudit(bite, conditionMet: true));
        var fate = SkillDamageDb.GetHits("Yone", "R")![0];
        Assert.Equal(new[] { ComboDamageType.Physical, ComboDamageType.Magic }, ComboRunner.HitTypesForAudit(fate, conditionMet: false));
    }

    [Fact]
    public void AmumuW_PerSecondDataValue_IsTheTooltipRate()
    {
        // (loop 11) BaseDamage 10 per second (the hashed calc is the 0.5 s tick of 5)
        Assert.Equal(20, Resolve("Amumu", "W", 0, seconds: 2), 6);
        Assert.Equal(0, Resolve("Amumu", "W", 0)); // unset exposure: 0 s (M24)
    }

    [Fact]
    public void DariusR_HemorrhageStacks_AddTwentyPercentEach_UpToDouble()
    {
        // (loop 12) +20% per stack, capped at 5 stacks = MaximumDamage (x2)
        double plain = Resolve("Darius", "R", 0);
        Assert.Equal(plain * 1.6, Resolve("Darius", "R", 0, stacks: 3), 3); // the BIN stores 0.2 as 0.20000000298
        Assert.Equal(plain * 2.0, Resolve("Darius", "R", 0, stacks: 9), 3);
    }

    [Fact]
    public void StatProgressionInterpolation_FollowsTheGrowthCurve()
    {
        // (loop 15) Malzahar's Voidling damage interpolates 5 -> 64.5 along the stat-growth curve
        // (mScaleByStatProgressionMultiplier): at level 11, (10)(0.7025 + 0.0175 x 10)/17 = 0.5162, not 10/17.
        var malz = LoadChampionFromBin("Malzahar");
        var noScaling = new ActivePlayerStats { AbilityW = 3 };
        double v = SkillDamage.ComputeCalcDamage(malz, "W", "{c2505620}", noScaling, level: 11)!.Value;
        Assert.Equal(5 + 59.5 * (10 * (0.7025 + 0.0175 * 10) / 17.0), v, 3);
    }

    [Fact]
    public void VayneW_MaxHealthShare_TakesItsFloor()
    {
        // (loop 16) rank 3: 7% max health (35 on a 500 HP target), at least DamageFloor 70 - the floor wins.
        var bolts = SkillDamageDb.GetHits("Vayne", "W")![0];
        Assert.True(ComboRunner.ResolveHitForAudit(bolts, LoadChampionFromBin("Vayne"), "W", Stats, 11, 500.0,
            null, null, 0, allOut: false, out double damage, out _, out _));
        Assert.Equal(70, damage, 3);
    }

    [Fact]
    public void GravesAttack_IsTheFirstBullet_PlusBulletsOnTheCountKnob()
    {
        // (loop 18) attackShape: the AA node resolves Graves' bullets, not total AD
        var graves = LoadChampionFromBin("Graves");
        var shape = SkillDamageDb.GetAttackShape("Graves")!;
        // (loop 36) the third part is the two extra crit bullets, crit-only
        Assert.Equal(3, shape.Count);
        Assert.True(shape[2].CritOnly);
        Assert.True(ComboRunner.ResolveHitForAudit(shape[0], graves, "P", Stats, 11, 1000.0, null, null, 0, false,
            out double first, out _, out _));
        Assert.True(ComboRunner.ResolveHitForAudit(shape[1], graves, "P", Stats, 11, 1000.0, null, null, 0, false,
            out double extra, out _, out _, attackCount: 9));
        Assert.True(first > 0 && first < Stats.AttackDamage * 1.5);
        Assert.Equal(3 * first * 0.333, extra, 0); // capped at 3 bullets of MultiBulletDamage (x0.333 of the first)
        Assert.Null(SkillDamageDb.GetAttackShape("Garen")); // an ordinary attack stays total AD
    }

    [Fact]
    public void SyndraR_RangeCeiling_ReachesAllSevenSpheres()
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Syndra") });
        var result = RunCombo("Syndra", "R");
        Assert.True(result.RangeMax > result.TotalDamage * 2.0,
            $"range max {result.RangeMax} should cover 7 spheres vs resolved {result.TotalDamage} (3)");
    }

    private ComboResult RunCombo(string champ, params string[] slots)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 1, Stats = Stats };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champ; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 11;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "c");
        int i = 0;
        foreach (var slot in slots)
            editor.AddNode(draft.Id, new ComboNode($"{slot}_{i++}", ComboNodeType.Skill, slot,
                0, 0, 0, ComboDamageType.Magic, 0, 0, 0, 0, 0, 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!;
    }

    private static double Share(IReadOnlyList<(double Damage, ComboDamageType Type)> shares, ComboDamageType type)
        => shares.Where(s => s.Type == type).Sum(s => s.Damage);

    [Fact]
    public void CamilleQ2_ConvertsTheLevelScaledShare_GwenQ_ConvertsHalfOnlyAtTheCentre()
    {
        // (loop 23) DamageConversionPercentage: 40% at level 1, +4% a level -> 80% at level 11
        var camille = LoadChampionFromBin("Camille");
        var q2 = SkillDamageDb.GetHits("Camille", "Q2")![1];
        var shares = ComboRunner.TypeSharesForAudit(q2, 100, camille, "Q2", Stats, 11, null);
        Assert.Equal(80, Share(shares, ComboDamageType.True), 3);
        Assert.Equal(20, Share(shares, ComboDamageType.Physical), 3);

        // TrueDamageConversion 50%, only with the SweetSpot box ticked (unset = off-centre, P2)
        var gwen = LoadChampionFromBin("Gwen");
        var snip = SkillDamageDb.GetHits("Gwen", "Q")![2];
        Assert.Equal(100, Share(ComboRunner.TypeSharesForAudit(snip, 100, gwen, "Q", Stats, 11, null), ComboDamageType.Magic), 6);
        var centred = ComboRunner.TypeSharesForAudit(snip, 100, gwen, "Q", Stats, 11, true);
        Assert.Equal(50, Share(centred, ComboDamageType.True), 3);
        Assert.Equal(50, Share(centred, ComboDamageType.Magic), 3);
        Assert.NotNull(SkillDamageDb.GetConditionalHit("Gwen", "Q")); // the box is offered
    }

    [Theory]
    [InlineData("TahmKench", "P", "TotalDamage", 60)]               // wiki 5 - 60
    [InlineData("Senna", "P", "BonusCurentHealthDamage", 10)]       // 1 - 10%
    [InlineData("Camille", "Q", "DamageConversionPercentage", 1.0)] // 40 - 100%
    [InlineData("Renata", "P", "PercentAmpCalc", 0.02)]             // 1 - 2%
    public void ABareLevelBreakpoint_StopsTheGrowth_AtThePublishedMaximum(string champ, string slot, string calc, double atEighteen)
    {
        // (loop 23) {mLevel: N} with no mBonusPerLevelAtAndAfter is the BIN omitting a 0 rate
        var bare = new ActivePlayerStats { AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3 };
        Assert.Equal(atEighteen, SkillDamage.ComputeCalcDamage(LoadChampionFromBin(champ), slot, calc, bare, 18)!.Value, 4);
    }

    [Fact]
    public void XerathR_LaterMissiles_RampByTheEarlierHits_UpToTheRankCount()
    {
        // (loop 27) R rank 2 (Stats.AbilityR = 2): NumberOfShots 5 -> 4 repeats after the first missile.
        // Repeat j carries j stacks of RampDamageCalc: k repeats = k x base + ramp x k(k+1)/2.
        var xerath = LoadChampionFromBin("Xerath");
        double baseShot = SkillDamage.ComputeCalcDamage(xerath, "R", "TooltipTotalDamage", Stats, 11)!.Value;
        double ramp = SkillDamage.ComputeCalcDamage(xerath, "R", "RampDamageCalc", Stats, 11)!.Value;
        Assert.Equal(2 * baseShot + 3 * ramp, Resolve("Xerath", "R", 1, attacks: 2), 3);
        Assert.Equal(4 * baseShot + 10 * ramp, Resolve("Xerath", "R", 1, attacks: 9), 3); // capped at 4 repeats
        Assert.Equal(0, Resolve("Xerath", "R", 1)); // unset: the first missile only
    }

    [Fact]
    public void Loop28_TotalOverDuration_PenaltyAfterFirst_MissingHpMax()
    {
        // Jayce's Lightning Field: the whole Damage over 4 s, so 2 s of exposure is half of it
        var jayce = LoadChampionFromBin("Jayce");
        double field = SkillDamage.ComputeCalcDamage(jayce, "W", "Damage", Stats, 11)!.Value;
        Assert.Equal(field / 2, Resolve("Jayce", "W", 0, seconds: 2), 3);
        Assert.Equal(0, Resolve("Jayce", "W", 0));

        // Taliyah's stones: the first at full, the later ones 25% less - three = the tooltip's 2.5x
        var taliyah = LoadChampionFromBin("Taliyah");
        double stone = SkillDamage.ComputeCalcDamage(taliyah, "E", "DetonationDamage", Stats, 11)!.Value;
        Assert.Equal(stone, Resolve("Taliyah", "E", 1, attacks: 1), 3);
        Assert.Equal(stone * 2.5, Resolve("Taliyah", "E", 1, attacks: 3), 3);

        // Samira's melee bonus grows to 2x at no health left: the live growth scalar is 1
        var bonus = SkillDamageDb.GetBonusEffects("Samira", "P")![0].Hits[0];
        Assert.True(ComboRunner.ResolveHitForAudit(bonus, LoadChampionFromBin("Samira"), "P", Stats, 11, 1000.0,
            null, true, 0, false, out double melee, out var exec, out double growth));
        Assert.True(melee > 0);
        Assert.Equal(ComboExecuteType.BaseWithMissingHpBonus, exec);
        Assert.Equal(1.0, growth, 4);
    }

    [Fact]
    public void ShacoQ_FromBehind_CritsForQCritDamageMod()
    {
        // (loop 35) the bonus hit x QCritDamageMod only with the FromBehind box ticked
        var shaco = LoadChampionFromBin("Shaco");
        double mod = SkillDamage.ComputeCalcDamage(shaco, "Q", "QCritDamageMod", Stats, 11)!.Value;
        Assert.True(mod > 1);
        Assert.Equal(Resolve("Shaco", "Q", 1, met: false) * mod, Resolve("Shaco", "Q", 1, met: true), 3);
        Assert.Equal(Resolve("Shaco", "Q", 1, met: false), Resolve("Shaco", "Q", 1), 6);
    }

    [Fact]
    public void GravesCritShape_AndYunaraCritBonus_FollowTheirCritCalcs()
    {
        // (loop 36) Graves: crits deal (1 + CritDamageMult) x normal -> scalar = CritDamageMult / (m - 1) = CritDamageRatio
        var crit = new ActivePlayerStats { AttackDamage = 200, CritChance = 0.5, CritDamage = 200, AbilityPower = 100 };
        var graves = LoadChampionFromBin("Graves");
        var first = SkillDamageDb.GetAttackShape("Graves")![0];
        Assert.Equal(0.5, ComboRunner.CritScalarForAudit(first, graves, "P", crit, 11, 100, 0), 4);

        // Yunara: the rider is Calc_Damage_Amp of total AD (its crit weight is applied where it rides)
        var bonus = SkillDamageDb.GetBonusEffects("Yunara", "P")![0].Hits[0];
        var yunara = LoadChampionFromBin("Yunara");
        Assert.True(bonus.CritOnly);
        double amp = SkillDamage.ComputeCalcDamage(yunara, "P", "Calc_Damage_Amp", crit, 11)!.Value;
        Assert.True(ComboRunner.ResolveHitForAudit(bonus, yunara, "P", crit, 11, 1000.0, null, null, 0, false, out double share, out _, out _));
        Assert.Equal(amp * 200, share, 3);
    }

    [Fact]
    public void KledDismounted_AttacksLoseTheirPenalty_OnlyWhenTicked()
    {
        // (loop 41) the attack at total AD; ticked (dismounted) it deals 1 - DismountedAttackPenalty of that
        var kled = LoadChampionFromBin("Kled");
        var attack = SkillDamageDb.GetAttackShape("Kled")![0];
        double penalty = SkillDamage.ComputeCalcDamage(kled, "P", "DismountedAttackPenalty", Stats, 11)!.Value;
        Assert.True(ComboRunner.ResolveHitForAudit(attack, kled, "P", Stats, 11, 1000.0, null, null, 0, false, out double mounted, out _, out _));
        Assert.True(ComboRunner.ResolveHitForAudit(attack, kled, "P", Stats, 11, 1000.0, null, true, 0, false, out double dismounted, out _, out _));
        Assert.Equal(Stats.AttackDamage, mounted, 6);
        Assert.Equal(Stats.AttackDamage * (1 - penalty), dismounted, 3);
        Assert.True(ConditionResolution.IsUserAssumed(ConditionType.AltForm));
    }

    [Fact]
    public void FiddlesticksW_Finale_NeedsTheFullDrain()
    {
        // (loop 25) the missing-health burst lands only at 2 s of exposure; unset (0 s) has none
        var finale = SkillDamageDb.GetHits("Fiddlesticks", "W")![1];
        var fiddle = LoadChampionFromBin("Fiddlesticks");
        Assert.False(ComboRunner.ResolveHitForAudit(finale, fiddle, "W", Stats, 11, 1000.0, 1.0, null, 0, false, out _, out _, out _));
        Assert.False(ComboRunner.ResolveHitForAudit(finale, fiddle, "W", Stats, 11, 1000.0, null, null, 0, false, out _, out _, out _));
        Assert.True(ComboRunner.ResolveHitForAudit(finale, fiddle, "W", Stats, 11, 1000.0, 2.0, null, 0, false,
            out _, out var exec, out double frac));
        Assert.Equal(ComboExecuteType.MissingHp, exec);
        Assert.Equal(0.17, frac, 3); // PercentForTooltip at rank 3
    }

    [Fact]
    public void ZaahenE_Edge_AddsAMetMaxHealthDataValue()
    {
        // (loop 23) PercentHPDamage at rank 3 = 5% of 1000 max health; nothing off the edge
        Assert.Equal(50, Resolve("Zaahen", "E", 1, met: true), 3);
        var hit = SkillDamageDb.GetHits("Zaahen", "E")![1];
        Assert.False(ComboRunner.ResolveHitForAudit(hit, LoadChampionFromBin("Zaahen"), "E", Stats, 11, 1000.0,
            null, false, 0, allOut: false, out _, out _, out _));
        Assert.True(Resolve("Zaahen", "E", 0, met: true) > Resolve("Zaahen", "E", 0, met: false));
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
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = championId, Name = championId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = 60 }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
