using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 47) An ability that applies on-hit effects carries the build's on-hit items once per strike, at the tooltip's
/// effectiveness. Titanic Hydra's on-hit (1% / 0.5% of the holder's 2000 max health = 20 melee, 10 ranged) against a
/// 0-armor dummy counts them.
/// </summary>
public class M45Loop47OnHitEffectivenessTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private const int Titanic = 3748;

    public M45Loop47OnHitEffectivenessTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L47_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void EzrealQ_AppliesOnHit_At100() => Assert.Equal(10, OnHit("Ezreal", ranged: true, Skill("Q")), 3);

    [Fact]
    public void KatarinaR_EachDagger_At35Percent_Rank3()
        // the first dagger and two counted ones, 20 x OnHitRatio each (25 / 30 / 35% by rank, the BIN's own index)
        => Assert.Equal(3 * 20 * 0.35, OnHit("Katarina", ranged: false, Skill("R", count: 2)), 3);

    [Fact]
    public void BelvethE_SixStrikes_At12Percent() => Assert.Equal(6 * 20 * 0.12, OnHit("Belveth", ranged: false, Skill("E")), 3);

    [Fact]
    public void WarwickR_ThreeDistinctTimes() => Assert.Equal(3 * 20, OnHit("Warwick", ranged: false, Skill("R")), 3);

    [Fact]
    public void MasterYiQ_LaterStrikes_AtTheirShareOf75Percent()
        // 75% on the first strike, 25% of that on each of the three repeats
        => Assert.Equal(20 * 0.75 + 3 * 20 * 0.75 * 0.25, OnHit("MasterYi", ranged: false, Skill("Q", count: 3)), 3);

    [Fact]
    public void UrgotW_ThirteenShots_AtHalf() => Assert.Equal(13 * 10 * 0.5, OnHit("Urgot", ranged: true, Skill("W")), 3);

    [Fact]
    public void AkshanE_AShot_At25Percent_WithoutHisSecondShot()
    {
        var r = Run("Akshan", ranged: true, Skill("E"));
        Assert.Equal(10 * 0.25, ItemDamage(r, "E_0"), 3);
        // his passive's second shot is part of his attack, not an on-hit effect the ability applies
        Assert.DoesNotContain(r.NodeBreakdown, n => n.NodeId.StartsWith("E_0#", StringComparison.Ordinal) && n.NodeId.Contains("#bonus"));
    }

    private double OnHit(string champ, bool ranged, ComboNode node) => ItemDamage(Run(champ, ranged, node), node.Id);

    private static double ItemDamage(ComboResult r, string nodeId)
        => r.NodeBreakdown.Where(n => n.NodeId.StartsWith(nodeId, StringComparison.Ordinal) && n.NodeId.Contains($"#item{Titanic}"))
            .Sum(n => n.Damage);

    private static ComboNode Skill(string slot, int? count = null)
        => new($"{slot}_0", ComboNodeType.Skill, slot, 0, 0, 0, ComboDamageType.Physical, 0, 0, 0, 0, 0, 0) { UserAttackCount = count };

    private ComboResult Run(string champ, bool ranged, ComboNode node)
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin(champ, ranged ? 550 : 125),
            new ChampionData { Id = "Target", Name = "Target",
                BaseStats = new ChampionBaseStats { Hp = 100000, Armor = 0, Mr = 0 }, StatsPerLevel = new ChampionStatsPerLevel() },
        });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 16, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 100, MaxHealth = 2000, AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champ; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 16;
        snap.Players[0].ItemIds[0] = Titanic; snap.Players[0].ItemCount = 1;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 16;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "t");
        editor.AddNode(draft.Id, node);
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

    private static ChampionData LoadChampionFromBin(string championId, double attackRange)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{championId.ToLowerInvariant()}.bin.json"));
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in ChampionBinParser.ParseChampion(championId, json))
            skills[slot] = new SkillData
            {
                Key = slot, Name = championId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations, EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = championId, Name = championId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = 60, AttackRange = attackRange }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
