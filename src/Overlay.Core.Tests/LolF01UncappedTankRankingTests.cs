using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using DamageType = Overlay.Core.Damage.DamageType;

namespace Overlay.Core.Tests;

/// <summary>
/// (lolF01) Tank item ranking at my max HP with no HP cap: the opt-in <see cref="DefenderStat.Uncapped"/> engine mode,
/// <see cref="ComboRunner.RankTankItems"/> on that basis (the same rows at any current HP, dead or alive), and the
/// survival-gain values on <see cref="NextItemRow"/>. Docs: docs/modules/M46_KEYS_COMPACT_TANK_ITEMS.md section 3.
/// </summary>
public class LolF01UncappedTankRankingTests : IDisposable
{
    private readonly string _dir;

    public LolF01UncappedTankRankingTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "LolF01_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── engine ──

    private static readonly AttackerStat NoStats = new(Ad: 0, BonusAD: 0, Ap: 0, Level: 1, CriticalChance: 0, LifeSteal: 0);

    private static DefenderStat Target(bool uncapped, double hp = 100, double armor = 0)
        => new(CurrentHP: hp, MaxHP: hp, Armor: armor, Mr: 0, Shield: 0, Uncapped: uncapped);

    private static double Total(DefenderStat defender, params Damage.ComboNode[] nodes)
        => new DamageEngine().Calculate(new DamageCalcInput(nodes, NoStats, defender)).TotalDamage;

    private static Damage.ComboNode Flat(string id, double damage, DamageType type = DamageType.True)
        => new(id, damage, 0, 0, type);

    [Fact]
    public void Uncapped_CountsEveryNodePastZeroHp_CappedStopsAtTheKillingNode()
    {
        // three 60-damage physical hits at 100 armor (30 each after mitigation) into 50 HP: the second kills
        var hits = new[] { Flat("a", 60, DamageType.Physical), Flat("b", 60, DamageType.Physical), Flat("c", 60, DamageType.Physical) };
        Assert.Equal(90, Total(Target(uncapped: true, hp: 50, armor: 100), hits), 6);  // 3 x 30, none skipped
        Assert.Equal(60, Total(Target(uncapped: false, hp: 50, armor: 100), hits), 6); // today: the third is skipped

        // 60 true damage x 3 into 100 HP: capped counts the killing hit in full and skips the third
        var three = new[] { Flat("a", 60), Flat("b", 60), Flat("c", 60) };
        Assert.Equal(180, Total(Target(uncapped: true), three), 6);
        Assert.Equal(120, Total(Target(uncapped: false), three), 6);
    }

    [Fact]
    public void Uncapped_StartsAliveAtZeroCurrentHp()
    {
        var atZero = Target(uncapped: true) with { CurrentHP = 0 };
        Assert.Equal(60, Total(atZero, Flat("a", 60)), 6);
        Assert.Equal(0, Total(Target(uncapped: false) with { CurrentHP = 0 }, Flat("a", 60)), 6);
    }

    [Fact]
    public void Uncapped_CurrentHpHitBelowZeroHp_AddsOnlyItsFloor()
    {
        // 150 true damage takes 100 HP to -50; then 50% of current HP: 0, never negative - the floor alone
        var floored = new Damage.ComboNode("pct", 0, 0, 0, DamageType.True, ExecuteType: ExecuteType.CurrentHp,
            ExecutePercent: 0.5, ExecuteFloor: 20);
        var unfloored = floored with { ExecuteFloor = 0 };
        Assert.Equal(170, Total(Target(uncapped: true), Flat("a", 150), floored), 6);
        Assert.Equal(150, Total(Target(uncapped: true), Flat("a", 150), unfloored), 6);
    }

    [Fact]
    public void Uncapped_MissingHpHit_CountsAtMostMaxHpMissing()
    {
        // HP at -50 after 150: missing counts as 100 (max HP), so 10 + 0.1 x 100 = 20, not 10 + 0.1 x 150 = 25
        var missing = new Damage.ComboNode("miss", 10, 0, 0, DamageType.True, ExecuteType: ExecuteType.MissingHp,
            ExecutePercent: 0.1);
        Assert.Equal(170, Total(Target(uncapped: true), Flat("a", 150), missing), 6);

        // Kraken's shape: base x (1 + 0.75 x missing fraction), the fraction at most 1
        var kraken = new Damage.ComboNode("kraken", 100, 0, 0, DamageType.True,
            ExecuteType: ExecuteType.BaseWithMissingHpBonus, ExecutePercent: 0.75);
        Assert.Equal(325, Total(Target(uncapped: true), Flat("a", 150), kraken), 6);
    }

    [Fact]
    public void Uncapped_ThresholdExecute_DoesNotEndTheSequence()
    {
        // a 60 hit that executes below 50 HP, then 30 more: capped stops at the execute, uncapped counts both hits only
        var execute = new Damage.ComboNode("exec", 60, 0, 0, DamageType.True, ExecuteType: ExecuteType.Threshold,
            ExecuteThreshold: 50);
        Assert.Equal(90, Total(Target(uncapped: true), execute, Flat("b", 30)), 6);
        Assert.Equal(60, Total(Target(uncapped: false), execute, Flat("b", 30)), 6);
    }

    // ── survival gain ──

    [Fact]
    public void SurviveGain_IsTheSharesRatio_PerThousandGold()
    {
        var row = new NextItemRow("x", 2500, Before: 1.5, After: 1.2, 0, string.Empty, Tank: true);
        Assert.Equal(25, row.SurviveGainPercent, 6);
        Assert.Equal(10, row.SurviveGainPer1000Gold, 6);
        Assert.Equal(0, (row with { After = 0 }).SurviveGainPercent);
        Assert.Equal(0, (row with { Gold = 0 }).SurviveGainPer1000Gold);
    }

    // ── tank ranking ──

    private const string ChampId = "Aatrox";
    private const int Level = 11;

    private static void SeedItems() => ItemRepository.Initialize(new[]
    {
        new ItemData { Id = "9101", Name = "Armor", GoldTotal = 2900, AvailableOnSummonersRift = true, Stats = new ItemStats { Armor = 80 } },
        new ItemData { Id = "9102", Name = "MR", GoldTotal = 2900, AvailableOnSummonersRift = true, Stats = new ItemStats { Mr = 80 } },
        new ItemData { Id = "9103", Name = "HP", GoldTotal = 3000, AvailableOnSummonersRift = true, Stats = new ItemStats { Hp = 500 } },
    });

    private static readonly string[] Candidates = { "9102", "9101", "9103" };

    [Fact]
    public void TankRanking_SameRowsAtAnyCurrentHp_DeadOrAlive()
    {
        SeedItems();
        var live = Live();
        var runner = NewRunner(() => live);
        double max = live.Stats.MaxHealth;

        var reference = Fingerprint(runner.RankTankItems(live, Candidates));
        Assert.Equal(3, reference.Count);

        foreach (var (hp, dead) in new[] { (max, false), (1.0, false), (0.0, false), (max, true), (0.0, true) })
        {
            live.Stats.CurrentHealth = hp;
            live.Players[0].IsDead = dead;
            Assert.Equal(reference, Fingerprint(runner.RankTankItems(live, Candidates)));
        }
    }

    private static List<(string Id, double Before, double After)> Fingerprint(IReadOnlyList<NextItemRow> rows)
        => rows.Select(r => (r.ItemId, r.Before, r.After)).ToList();

    [Fact]
    public void TankRanking_LethalPhysicalEnemy_ArmorAndHealthStillGain_ArmorAboveMagicResist()
    {
        SeedItems();
        var live = Live(myMaxHp: 400, foeLevel: 18);
        var rows = NewRunner(() => live).RankTankItems(live, Candidates);

        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].Before > 1, $"the enemy's combo must exceed my max HP, share {rows[0].Before}");
        var byId = rows.ToDictionary(r => r.ItemId);
        Assert.True(byId["9101"].After < byId["9101"].Before);
        Assert.True(byId["9101"].SurviveGainPercent > 0);
        Assert.True(byId["9103"].After < byId["9103"].Before);
        Assert.True(byId["9103"].SurviveGainPercent > 0);
        var order = rows.Select(r => r.ItemId).ToList();
        Assert.True(order.IndexOf("9101") < order.IndexOf("9102"), string.Join(",", order));
    }

    /// <summary>Me = Aatrox at level 11; the enemy is an Aatrox too (a physical combo).</summary>
    private static GameSnapshot Live(double? myMaxHp = null, int foeLevel = Level)
    {
        var (q, w, e, r) = SyntheticAttacker.AssumedRanks(Level, "QWE");
        double maxHp = myMaxHp ?? LevelGrowth.Stat(650, 114, Level);
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = Level, PlayerCount = 2,
            Stats = new ActivePlayerStats
            {
                AttackDamage = LevelGrowth.Stat(60, 5, Level),
                MaxHealth = maxHp, CurrentHealth = maxHp,
                Armor = 60, MagicResist = 40,
                AbilityQ = q, AbilityW = w, AbilityE = e, AbilityR = r,
            },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = ChampId; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = Level;
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = ChampId; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = foeLevel;
        return snap;
    }

    private ComboRunner NewRunner(Func<GameSnapshot?> snapshot)
    {
        ChampionRepository.Initialize(new[] { FromBin(ChampId) });
        var config = new ConfigManager(Path.Combine(_dir, "user_config.json"));
        var runner = new ComboRunner(new ComboEngine(new DamageEngine(), new RuneEngine()), config, snapshot);
        runner.Start();
        return runner;
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
            BaseStats = new ChampionBaseStats { Ad = 60, Hp = 650, Armor = 38, Mr = 32 },
            StatsPerLevel = new ChampionStatsPerLevel { Ad = 5, Hp = 114, Armor = 4.8, Mr = 2 },
        };
    }
}
