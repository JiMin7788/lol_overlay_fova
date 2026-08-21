using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 497) A hit that is "equal parts physical and magic". Yone's Spirit Cleave says so in its
/// tooltip and both of its curated hits were typed wholly Magic, so half of the ability met magic
/// resist when it should have met armour.
///
/// <para>The point of the split is mitigation, not labelling: against a target with one resistance
/// and not the other, a wholly-typed hit is wrong by whatever that lopsided target leans on. These
/// pin that the halves actually meet different resistances.</para>
/// </summary>
public class SplitDamageTypeTests : IDisposable
{
    private readonly string _dir;

    public SplitDamageTypeTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "SplitType_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private const string ChampId = "Yone";

    private static ChampionData FromBin(string championId, double hp = 0, double armor = 0, double mr = 0)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon",
            $"{championId.ToLowerInvariant()}.bin.json"));
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in ChampionBinParser.ParseChampion(championId, json))
            skills[slot] = new SkillData
            {
                Key = slot, Name = championId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = championId, Name = championId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = 100, Hp = hp, Armor = armor, Mr = mr },
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy(string id, double armor, double mr) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = 2000, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 200, AbilityPower = 0, AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
    };

    private static GameSnapshot Snap(string target)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2, Stats = Stats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = ChampId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 11;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = target;
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 11;
        return snap;
    }

    /// <summary>W against a target built one way or the other.</summary>
    private double W(string target, double armor, double mr)
    {
        ChampionRepository.ResetForTests();
        SkillDamageDb.ResetForTests();
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { FromBin(ChampId), Dummy(target, armor, mr) });

        using var config = new ConfigManager(Path.Combine(_dir, target + ".json"));
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        using var runner = new ComboRunner(engine, config, () => Snap(target));
        runner.Start();
        var graph = new ComboGraph(new[]
        {
            new ComboNode("W_0", ComboNodeType.Skill, "W", 0, 0, 0, ComboDamageType.Magic, 0, 0, 0, 0, 0, 0),
        }, Array.Empty<ComboEdge>());
        return runner.ComputePreview(ChampId, graph)?.Resolved ?? 0;
    }

    [Fact]
    public void SpiritCleaveMeetsBothResistances()
    {
        // Armour-stacked and MR-stacked targets with the SAME total resistance. A wholly-magic hit
        // would be identical against the second and untouched by the first; a wholly-physical one the
        // reverse. An even split is symmetric — which is exactly what "equal parts" means.
        double vsArmour = W("Tanky", armor: 200, mr: 0);
        double vsMr = W("Mage", armor: 0, mr: 200);
        Assert.True(vsArmour > 0);
        Assert.Equal(vsArmour, vsMr, 1);

        // …and both are strictly between the two extremes: a target with neither resistance takes
        // more, one with both takes less.
        double vsNothing = W("Naked", armor: 0, mr: 0);
        double vsBoth = W("Both", armor: 200, mr: 200);
        Assert.True(vsNothing > vsArmour + 1);
        Assert.True(vsBoth < vsArmour - 1);
    }

    [Fact]
    public void TheSplitDoesNotChangeTheRawTotal()
    {
        // Against a target with no resistances at all the two halves add back up: the split is about
        // WHICH resistance each half meets, never about how much damage there is.
        double vsNothing = W("Naked", armor: 0, mr: 0);

        var hits = SkillDamageDb.GetHits(ChampId, "W")!;
        Assert.Equal(2, hits.Length);
        Assert.All(hits, h =>
        {
            Assert.Equal(HitDamageType.Physical, h.Type);
            Assert.Equal(HitDamageType.Magic, h.SplitType);
        });

        var yone = FromBin(ChampId);
        double flat = SkillDamage.ComputeCalcDamage(yone, "W", "WDamage", Stats(), 11)!.Value;
        double frac = SkillDamage.ResolveHpPercent(yone, "W", "MaxHealthDamage", Stats(), 11)!.Value;
        Assert.Equal(flat + frac * 2000, vsNothing, 1);
    }

    [Fact]
    public void AHitWithNoSplitIsUntouched()
    {
        // Yone's Q declares no split, so it stays one node of one type — the field is opt-in and
        // every other champion resolves exactly as before.
        var q = SkillDamageDb.GetHits(ChampId, "Q");
        Assert.NotNull(q);
        Assert.All(q!, h => Assert.Null(h.SplitType));
    }
}
