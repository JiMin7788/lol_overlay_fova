using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 473) Empowered skills read like Talon Q now: leave the checkbox alone and the node shows a
/// [base, empowered] span; tick it and the number is fixed at the empowered value.
///
/// <para>Renekton's fury is AutoResolvable, which is why this took a change. The resolved number has
/// always followed the live bar and still does — what was missing is that the RANGE ignored the
/// empowered half entirely, on the reasoning that a live value is deterministic. It is, at the
/// instant you read it; a combo is planned before it is cast, so the fury at cast time is not the
/// fury while building. Hiding the upper half made the empowered variant invisible in the editor,
/// which is where combos are actually written.</para>
/// </summary>
public class EmpoweredRangeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public EmpoweredRangeTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "EmpoweredRange_" + Guid.NewGuid().ToString("N"));
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
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ActivePlayerStats Stats(double fury) => new()
    {
        AttackDamage = 250, AbilityPower = 0, AbilityQ = 5, ResourceValue = fury, ResourceMax = 100,
    };

    private static GameSnapshot Snapshot(ActivePlayerStats stats, int level)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = level, PlayerCount = 1, Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = "Renekton";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = level;
        return snap;
    }

    private ComboResult RunQ(bool? conditionMet, double fury, int level = 5)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Renekton", "c" + (conditionMet?.ToString() ?? "unset") + fury);
        editor.AddNode(draft.Id, new ComboNode(
            Id: "Q_0", NodeType: ComboNodeType.Skill, Name: "Q", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0) with { UserConditionMet = conditionMet });
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snapshot(Stats(fury), level));
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

    [Fact]
    public void UncheckedSpansTheEmpowerment_CheckedFixesIt()
    {
        var renekton = FromBin("Renekton");
        ChampionRepository.Initialize(new[] { renekton });

        double basic = SkillDamage.ComputeCalcDamage(renekton, "Q", "BasicDamage", Stats(0), 5)!.Value;
        double emp = SkillDamage.ComputeCalcDamage(renekton, "Q", "EmpDamage", Stats(0), 5)!.Value;
        Assert.True(emp > basic + 1);

        // Untouched: a span, whatever the bar currently reads.
        var unset = RunQ(null, fury: 0);
        Assert.Equal(basic, unset.RangeMin, 2);
        Assert.Equal(emp, unset.RangeMax, 2);

        // Ticked: one number, and it is the empowered one — even with an empty fury bar, which is
        // the whole point of being able to assert it while building a combo out of game.
        var ticked = RunQ(true, fury: 0);
        Assert.Equal(emp, ticked.TotalDamage, 2);
        Assert.Equal(ticked.TotalDamage, ticked.RangeMin, 2);
        Assert.Equal(ticked.TotalDamage, ticked.RangeMax, 2);
    }

    [Fact]
    public void UnsetStillResolvesFromTheLiveBar()
    {
        var renekton = FromBin("Renekton");
        ChampionRepository.Initialize(new[] { renekton });

        double basic = SkillDamage.ComputeCalcDamage(renekton, "Q", "BasicDamage", Stats(0), 5)!.Value;
        double emp = SkillDamage.ComputeCalcDamage(renekton, "Q", "EmpDamage", Stats(0), 5)!.Value;

        // The resolved number is unchanged by any of this: below the threshold it is the base value,
        // at or above it the empowered one. Only the range around it grew.
        Assert.Equal(basic, RunQ(null, fury: 0).TotalDamage, 2);
        Assert.Equal(emp, RunQ(null, fury: 60).TotalDamage, 2);
    }
}
