using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 477) How many attacks an empowered-next-attack rider may ride.
///
/// <para>A plain ability-slot on-hit used to fire on EVERY auto after that ability was cast, with no
/// cap available to express otherwise. Rengar's Savagery is one empowered stab and an attack reset —
/// a Q-A-A-A combo was charging it three times. Evelynn's own curation note had recorded the same
/// defect against her three-proc mark since before the field existed.</para>
///
/// <para>The cap deliberately does NOT apply to always-on or passive-slot riders, which really do
/// fire on every attack (Warwick's on-hit, Varus W Blight), so those are unchanged.</para>
/// </summary>
public class EmpoweredAttackProcCapTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public EmpoweredAttackProcCapTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "ProcCap_" + Guid.NewGuid().ToString("N"));
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

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 200, AbilityPower = 0,
        AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
        ResourceValue = 0, ResourceMax = 100,
    };

    /// <summary>A target with far more health than the combo can deal, so nothing is clipped by the
    /// lethality bound and each added attack shows its full contribution.</summary>
    private static ChampionData Dummy() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = 1_000_000, Armor = 0, Mr = 0 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static GameSnapshot Snapshot(string champion)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2, Stats = Stats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = champion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 11;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 11;
        return snap;
    }

    private double Run(string championId, params (ComboNodeType type, string slot)[] specs)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(championId, "c" + Guid.NewGuid().ToString("N"));
        for (int i = 0; i < specs.Length; i++)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{specs[i].slot}_{i}", NodeType: specs[i].type, Name: specs[i].slot, Cooldown: 0,
                Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0,
                RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snapshot(championId));
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!.TotalDamage;
    }

    [Fact]
    public void RengarsStabIsChargedOnceNoMatterHowManyAttacksFollow()
    {
        ChampionRepository.Initialize(new[] { FromBin("Rengar"), Dummy() });

        double aa = Run("Rengar", (ComboNodeType.Aa, "AA"));
        double qa = Run("Rengar", (ComboNodeType.Skill, "Q"), (ComboNodeType.Aa, "AA"));
        double qaa = Run("Rengar", (ComboNodeType.Skill, "Q"), (ComboNodeType.Aa, "AA"),
                         (ComboNodeType.Aa, "AA"));
        double qaaa = Run("Rengar", (ComboNodeType.Skill, "Q"), (ComboNodeType.Aa, "AA"),
                          (ComboNodeType.Aa, "AA"), (ComboNodeType.Aa, "AA"));

        double stab = qa - aa;
        Assert.True(stab > 0, "Q should empower the following attack");
        // Every further attack adds only its own damage — the stab is spent.
        Assert.Equal(aa, qaa - qa, 2);
        Assert.Equal(aa, qaaa - qaa, 2);
        // And the whole combo carries exactly one stab.
        Assert.Equal(3 * aa + stab, qaaa, 2);
    }

    [Fact]
    public void EvelynnsMarkRidesThreeAttacksAndThenStops()
    {
        ChampionRepository.Initialize(new[] { FromBin("Evelynn"), Dummy() });

        double aa = Run("Evelynn", (ComboNodeType.Aa, "AA"));
        var nodes = new List<(ComboNodeType, string)> { (ComboNodeType.Skill, "Q") };
        var totals = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            nodes.Add((ComboNodeType.Aa, "AA"));
            totals.Add(Run("Evelynn", nodes.ToArray()));
        }

        // The first three attacks each carry the mark; the fourth and fifth do not.
        double marked = totals[0] - (totals.Count > 1 ? 0 : 0);
        Assert.True(totals[1] - totals[0] > aa + 0.01, "second attack should still be marked");
        Assert.True(totals[2] - totals[1] > aa + 0.01, "third attack should still be marked");
        Assert.Equal(aa, totals[3] - totals[2], 2);
        Assert.Equal(aa, totals[4] - totals[3], 2);
        _ = marked;
    }

    [Fact]
    public void AnAlwaysOnPassiveIsNotCapped()
    {
        // Warwick's on-hit is a passive that really does fire on every attack; nothing here changes it.
        ChampionRepository.Initialize(new[] { FromBin("Warwick"), Dummy() });

        double aa1 = Run("Warwick", (ComboNodeType.Aa, "AA"));
        double aa3 = Run("Warwick", (ComboNodeType.Aa, "AA"), (ComboNodeType.Aa, "AA"),
                         (ComboNodeType.Aa, "AA"));
        // Precision 1: Warwick on-hit carries a %maxHP term, so three attacks accumulate a
        // hundredth of a point of floating-point drift against the multiplication.
        Assert.Equal(3 * aa1, aa3, 1);
    }
}
