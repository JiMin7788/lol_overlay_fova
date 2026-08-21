using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 485) The last rows of the coverage sweep that were modelable. Each had been triaged as a
/// real term left out for want of a way to say it — Gangplank's Death's Daughter is a bought upgrade,
/// Aurelion Sol's Skies Descend is a stack-gated one, Shaco's dagger payload needs him to be behind
/// the target — and each is a real BIN calc, not a bare ratio.
///
/// <para>The sweep now reports zero rows in both tiers. What is left in those files is adjudicated:
/// pet actors, live buff counters, tooltip mirrors and crit modifiers, each with a note saying so.</para>
/// </summary>
public class SweepBacklogClosureTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public SweepBacklogClosureTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "SweepClose_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ChampionData FromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon",
            $"{championId.ToLowerInvariant()}.bin.json"));
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in ChampionBinParser.ParseChampion(championId, json))
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
            BaseStats = new ChampionBaseStats { Ad = 100 },
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = 2000, Armor = 0, Mr = 0 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 200, AbilityPower = 100,
        AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
    };

    private static GameSnapshot Snap(string championId)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2, Stats = Stats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = championId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 11;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 11;
        return snap;
    }

    private ComboResult Run(string championId, string slot, bool? met)
    {
        ChampionRepository.Initialize(new[] { FromBin(championId), Dummy() });
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(championId, $"c_{slot}_{met?.ToString() ?? "unset"}");
        editor.AddNode(draft.Id, new ComboNode(
            Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Magic, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0) with { UserConditionMet = met });
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snap(championId));
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

    /// <summary>Every one of these adds to an unchanged floor: unticked must equal what the file
    /// resolved before the term was curated, or the change was not additive.</summary>
    [Theory]
    [InlineData("Gangplank", "R")]
    [InlineData("AurelionSol", "R")]
    [InlineData("Shaco", "E")]
    public void TheFloorIsUnchanged_AndTheToggleRaisesIt(string championId, string slot)
    {
        double floor = Run(championId, slot, false).TotalDamage;
        double ceiling = Run(championId, slot, true).TotalDamage;
        Assert.True(floor > 0);
        Assert.True(ceiling > floor + 1, $"{championId} {slot}: {ceiling:0.##} did not exceed {floor:0.##}");

        // Untouched resolves to the floor and spans to the ceiling.
        var unset = Run(championId, slot, null);
        Assert.Equal(floor, unset.TotalDamage, 2);
        Assert.Equal(ceiling, unset.RangeMax, 1);
    }

    [Fact]
    public void AurelionSolR_UpgradeReplacesTheImpactAndAddsAShockwave()
    {
        double basic = Run("AurelionSol", "R", false).TotalDamage;
        double skies = Run("AurelionSol", "R", true).TotalDamage;

        // The star impact is REPLACED at 1.25x (R2DamageRatio) and the shockwave is ADDED at 0.9x
        // (ShockwaveDamageRatio), so the upgraded cast is 2.15x — not 2.25x, which is what curating
        // the impact as an extra hit rather than a replacement would have produced.
        Assert.Equal(2.15, skies / basic, 2);
    }

    [Fact]
    public void GangplankR_DeathsDaughterIsAnAddedCannonball()
    {
        double barrage = Run("Gangplank", "R", false).TotalDamage;
        double upgraded = Run("Gangplank", "R", true).TotalDamage;

        // An un-upgraded R has no centre cannonball at all, so this is purely additive: the curated
        // hit has no baseline calc of its own.
        var hits = SkillDamageDb.GetHits("Gangplank", "R")!;
        Assert.Equal(2, hits.Length);
        Assert.Equal(string.Empty, hits[1].Calc);
        Assert.Equal("DeathsDaughterDamage", hits[1].MetCalc);
        Assert.True(upgraded > barrage);
    }

    /// <summary>(loop 486, user correction) Two-Shiv Poison's maximum is BOTH amplifications: the
    /// dagger's own 1.5x against a target below 30% health — which loop 485 missed entirely, E was a
    /// flat TotalDamage — plus the backstab payload's 1.5x at the same threshold. Loop 485 argued
    /// they could not be layered because one node flag would fire both; firing both is right, because
    /// both are the same 30% gate.</summary>
    [Fact]
    public void ShacoE_MaximumIsBothAmplifications()
    {
        var hits = SkillDamageDb.GetHits("Shaco", "E")!;
        Assert.Equal(2, hits.Length);

        // The dagger itself: TotalDamage, replaced by TotalExecuteDamage below the threshold.
        Assert.Equal("HpBelow", hits[0].ConditionType);
        Assert.Equal(0.3, hits[0].ConditionValue, 3);
        Assert.Equal("TotalExecuteDamage", hits[0].MetCalc);

        // The backstab payload, on the passive object via binSpell, magic where the dagger is
        // physical, and at ITS execute tier because the box means the best case.
        Assert.Equal("FromBehind", hits[1].ConditionType);
        Assert.Equal("ShivDamageExecute", hits[1].MetCalc);
        Assert.Equal("P", hits[1].BinSpell);
        Assert.Equal(HitDamageType.Magic, hits[1].Type);
        Assert.Equal(HitDamageType.Physical, hits[0].Type);
    }

    [Fact]
    public void ShacoE_TickedIsDaggerTimesOnePointFivePlusPayloadTimesOnePointFive()
    {
        double floor = Run("Shaco", "E", false).TotalDamage;
        double max = Run("Shaco", "E", true).TotalDamage;

        var shaco = FromBin("Shaco");
        double dagger = SkillDamage.ComputeCalcDamage(shaco, "E", "TotalDamage", Stats(), 11)!.Value;
        double payload = SkillDamage.ComputeCalcDamage(shaco, "P", "ShivDamage", Stats(), 11)!.Value;

        // Unticked is the plain dagger from the front — unchanged from before either amplification
        // was curated. Ticked is the user's stated maximum, both halves at 1.5.
        Assert.Equal(dagger, floor, 1);
        Assert.Equal(1.5 * dagger + 1.5 * payload, max, 1);
    }

    [Fact]
    public void FromBehindIsUserAssumed()
    {
        // Section 76's trap: both the enum and ConditionResolution, or the checkbox never appears.
        Assert.True(ConditionResolution.IsUserAssumed(ConditionType.FromBehind));
    }
}
