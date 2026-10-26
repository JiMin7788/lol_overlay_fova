using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;

namespace Overlay.Core.Tests;

/// <summary>(M40) The enemy's combo against me goes through the same pipeline as my own, with a
/// <see cref="SyntheticAttacker"/> snapshot. The parity test is the one that matters: the same
/// champion, level, ranks and defender must give the same number whichever side it is computed from.</summary>
public class EnemyThreatTests : IDisposable
{
    private readonly string _dir;

    public EnemyThreatTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "EnemyThreat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests(); // SeedItems() is global state
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData(1, "QWE", 1, 0, 0, 0)]
    [InlineData(2, "QWE", 1, 1, 0, 0)]
    [InlineData(3, "EQW", 1, 1, 1, 0)]
    [InlineData(6, "QWE", 3, 1, 1, 1)]
    [InlineData(11, "QWE", 5, 3, 1, 2)]
    [InlineData(11, "WQE", 3, 5, 1, 2)]
    [InlineData(18, "EQW", 5, 5, 5, 3)]
    public void AssumedRanks(int level, string order, int q, int w, int e, int r)
        => Assert.Equal((q, w, e, r), SyntheticAttacker.AssumedRanks(level, order));

    private const string ChampId = "Aatrox";
    private const int Level = 11;

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

    private static ChampionData Dummy() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = 2000, Armor = 40, Mr = 40, Ad = 50 }, // AD only matters as an attacker
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private ComboRunner NewRunner(Func<GameSnapshot?> snapshot)
    {
        ChampionRepository.Initialize(new[] { FromBin(ChampId), Dummy() });
        var config = new ConfigManager(Path.Combine(_dir, "user_config.json"));
        var runner = new ComboRunner(new ComboEngine(new DamageEngine(), new RuneEngine()), config, snapshot);
        runner.Start();
        return runner;
    }

    /// <summary>Me = Aatrox at level 11 with the ranks the "QWE" assumption gives, stats exactly the
    /// base growth; the scoreboard enemy is the 2000 HP / 40 / 40 dummy.</summary>
    private static GameSnapshot AatroxLive()
    {
        var (q, w, e, r) = SyntheticAttacker.AssumedRanks(Level, "QWE");
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = Level, PlayerCount = 2,
            Stats = new ActivePlayerStats
            {
                AttackDamage = LevelGrowth.Stat(60, 5, Level),
                MaxHealth = LevelGrowth.Stat(650, 114, Level),
                CurrentHealth = LevelGrowth.Stat(650, 114, Level),
                AbilityQ = q, AbilityW = w, AbilityE = e, AbilityR = r,
            },
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = ChampId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = Level;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = Level;
        return snap;
    }

    [Fact]
    public void EnemySide_EqualsTheLiveComputation_ForTheSameChampionAndRanks()
    {
        var live = AatroxLive();
        var runner = NewRunner(() => live);
        var preset = ComboPresets.For(ChampId).First(p => p.Kind == "allin");
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var mine = runner.ComputePreview(ChampId, engine.BuildGraph(ComboEditor.PresetNodes(ChampId, preset)));
        Assert.NotNull(mine);
        Assert.True(mine!.IsLive);

        // The same Aatrox as an enemy row, computed against a "me" with the dummy's numbers.
        var enemy = new ScoreboardEntry { ChampionName = ChampId, Team = "CHAOS", Level = Level, Spell1RawName = "SummonerFlash" };
        var defenderSnap = new GameSnapshot
        {
            HasData = true,
            Stats = new ActivePlayerStats { CurrentHealth = 2000, MaxHealth = 2000, Armor = 40, MagicResist = 40 },
        };
        var threat = runner.ComputeThreat(defenderSnap, enemy, new[] { "QWE" });

        Assert.NotNull(threat);
        Assert.True(mine.Resolved > 0);
        Assert.Equal(mine.Resolved, threat!.Damage, mine.Resolved * 0.05);
    }

    [Fact]
    public void TakesTheHighestOfTheMaxOrders()
    {
        var live = AatroxLive();
        var runner = NewRunner(() => live);
        var enemy = new ScoreboardEntry { ChampionName = ChampId, Team = "CHAOS", Level = 7 };
        var me = new GameSnapshot
        {
            HasData = true,
            Stats = new ActivePlayerStats { CurrentHealth = 1500, MaxHealth = 1500, Armor = 60, MagicResist = 40 },
        };
        var all = runner.ComputeThreat(me, enemy, new[] { "QWE", "WQE", "EQW" })!; // the no-data fallback
        foreach (var order in new[] { "QWE", "WQE", "EQW" })
            Assert.True(all.Damage >= runner.ComputeThreat(me, enemy, new[] { order })!.Damage - 1e-6);
    }

    [Fact]
    public void MoreArmorMeansLessThreat()
    {
        var live = AatroxLive();
        var runner = NewRunner(() => live);
        var enemy = new ScoreboardEntry { ChampionName = ChampId, Team = "CHAOS", Level = Level };
        GameSnapshot Me(double armor) => new()
        {
            HasData = true,
            Stats = new ActivePlayerStats { CurrentHealth = 2000, MaxHealth = 2000, Armor = armor, MagicResist = 40 },
        };
        Assert.True(runner.ComputeThreat(Me(150), enemy)!.Damage < runner.ComputeThreat(Me(30), enemy)!.Damage);
    }

    /// <summary>(M41) A mirror lane is symmetric at every level, and there is a row per level.</summary>
    [Fact]
    public void KillTiming_MirrorIsSymmetric()
    {
        var live = AatroxLive();
        var runner = NewRunner(() => live);
        var rows = runner.ComputeKillTiming(ChampId, ChampId);
        Assert.Equal(Enumerable.Range(1, 18), rows.Select(r => r.Level));
        foreach (var r in rows)
        {
            Assert.Equal(r.MyDamage, r.EnemyDamage, 6);
            Assert.Equal(r.MyKillPercent, r.EnemyKillPercent, 6);
        }
    }

    [Fact]
    public void KillTiming_DamageGrowsWithLevel_AndTheUltimateArrivesAt6()
    {
        var live = AatroxLive();
        var runner = NewRunner(() => live);
        var rows = runner.ComputeKillTiming(ChampId, "Target");
        Assert.Equal(18, rows.Count);
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i].MyDamage >= rows[i - 1].MyDamage - 1e-6, $"level {rows[i].Level} dropped");
        Assert.All(rows, r => Assert.InRange(r.MyKillPercent, 0, 100));
    }

    // ── (M43) next-item effect ─────────────────────────────────────────────────────

    private static void SeedItems() => ItemRepository.Initialize(new[]
    {
        new ItemData { Id = "9001", Name = "AD", GoldTotal = 3000, AvailableOnSummonersRift = true, Stats = new ItemStats { Ad = 60 } },
        new ItemData { Id = "9002", Name = "AP", GoldTotal = 3600, AvailableOnSummonersRift = true, Stats = new ItemStats { Ap = 120 } },
        new ItemData { Id = "9003", Name = "Boots", GoldTotal = 2400, AvailableOnSummonersRift = true, IsBoots = true, Stats = new ItemStats { Ad = 10 } },
        new ItemData { Id = "9004", Name = "Part", GoldTotal = 2300, AvailableOnSummonersRift = true, BuildsInto = new[] { "9001" }, Stats = new ItemStats { Ad = 20 } },
        new ItemData { Id = "9005", Name = "Armor", GoldTotal = 2900, AvailableOnSummonersRift = true, Stats = new ItemStats { Armor = 80 } },
        new ItemData { Id = "9006", Name = "Cheap", GoldTotal = 1300, AvailableOnSummonersRift = true, Stats = new ItemStats { Ad = 25 } },
    });

    [Fact]
    public void NextItem_Candidates_AreFinishedDamageItemsNotOwned()
    {
        SeedItems();
        Assert.Equal(new[] { "9002" }, global::Overlay.Core.Items.NextItemCandidates.For(new[] { 9001 }, null));
        Assert.Equal(new[] { "9001", "9002" }, global::Overlay.Core.Items.NextItemCandidates.For(Array.Empty<int>(), null).OrderBy(x => x));
    }

    [Fact]
    public void NextItem_AnAdItemAddsToAnAdCombo_AndAnApItemAddsNothing()
    {
        SeedItems();
        var live = AatroxLive();
        var runner = NewRunner(() => live);
        var rows = runner.RankNextItems(live, new[] { "9002", "9001" });

        Assert.Equal(2, rows.Count);
        Assert.Equal("9001", rows[0].ItemId);               // best gain first
        Assert.True(rows[0].GainPercent > 0);
        Assert.Equal(0, rows[1].GainPercent, 6);          // Aatrox's combo has no AP ratio
        Assert.Equal("Target", rows[0].TargetChampion);
        Assert.True(rows[0].GainPer1000Gold > 0);
    }

    [Fact]
    public void NextItem_BuyingTheItem_EqualsOwningIt()
    {
        SeedItems();
        var live = AatroxLive();
        var runner = NewRunner(() => live);
        double viaRank = runner.RankNextItems(live, new[] { "9001" }).Single().After;

        var owned = AatroxLive();
        owned.Players[0].ItemIds[0] = 9001;
        owned.Players[0].ItemCount = 1;
        owned.Stats.AttackDamage += 60;
        var ownedRunner = NewRunner(() => owned);
        double viaOwning = ownedRunner.RankNextItems(owned, Array.Empty<string>()).Count == 0
            ? ownedRunner.ComputePreview(ChampId, new ComboEngine(new DamageEngine(), new RuneEngine()).BuildGraph(
                ComboEditor.PresetNodes(ChampId, ComboPresets.For(ChampId).First(p => p.Kind == "allin"))))!.Resolved
            : double.NaN;
        Assert.Equal(viaOwning, viaRank, 6);
    }

    /// <summary>No preset at all → every ability plus an auto, never a missing tick.</summary>
    [Fact]
    public void NoPreset_StillAnEstimate()
    {
        var live = AatroxLive();
        var runner = NewRunner(() => live);
        var enemy = new ScoreboardEntry { ChampionName = "Target", Team = "CHAOS", Level = Level };
        Assert.True(runner.ComputeThreat(live, enemy)?.Damage > 0);
    }

    /// <summary>The live bug (2026-10-17, ticks missing in game): champions with only a trade preset
    /// had no estimate, and Naafiri's all-in names an R her palette lacks, which threw. Every curated
    /// champion must give a positive estimate at every level.</summary>
    [Fact]
    public void EveryCuratedChampion_GetsAnEstimate()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var ddragon = Directory.GetDirectories(Path.Combine(data, "ddragon")).OrderBy(d => d).Last();
        ChampionRepository.InitializeFromCache(ddragon, Path.Combine(data, "communitydragon"), Path.Combine(data, "special_properties"));
        var runner = new ComboRunner(new ComboEngine(new DamageEngine(), new RuneEngine()),
            new ConfigManager(Path.Combine(_dir, "user_config.json")), () => null);
        var me = new GameSnapshot
        {
            HasData = true,
            Stats = new ActivePlayerStats { MaxHealth = 1500, CurrentHealth = 1500, Armor = 50, MagicResist = 40 },
        };
        var missing = new List<string>();
        foreach (var id in ComboPresets.All.Keys)
            foreach (var level in new[] { 3, 9, 16 })
            {
                var enemy = new ScoreboardEntry { ChampionName = id, Team = "CHAOS", Level = level,
                    Spell1RawName = "SummonerFlash", Spell2RawName = "SummonerDot" };
                if (!(runner.ComputeThreat(me, enemy)?.Damage > 0)) missing.Add($"{id}@{level}");
            }
        Assert.Empty(missing);
    }
}

public class EnemyThreatMonitorTests
{
    private static GameSnapshot Snap(int enemyLevel, double myMaxHp = 1000)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", PlayerCount = 3,
            Stats = new ActivePlayerStats { MaxHealth = myMaxHp, CurrentHealth = myMaxHp, Armor = 30, MagicResist = 30 },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].Team = "ORDER";
        snap.Players[1].SummonerName = "Ally"; snap.Players[1].Team = "ORDER"; snap.Players[1].ChampionName = "Garen";
        snap.Players[2].SummonerName = "Foe"; snap.Players[2].Team = "CHAOS"; snap.Players[2].ChampionName = "Zed";
        snap.Players[2].Level = enemyLevel;
        return snap;
    }

    [Fact]
    public void RecomputesOnlyWhenAnInputChanges()
    {
        var snap = Snap(5);
        int calls = 0;
        var monitor = new EnemyThreatMonitor(() => snap, (_, e) => { calls++; return new ThreatEstimate(e.ChampionName, e.Level, 100 * e.Level, "x"); });

        Assert.True(monitor.Tick());
        Assert.Equal(1, calls); // the enemy only, never the ally
        Assert.False(monitor.Tick());
        snap.Stats.CurrentHealth = 400; // taking damage is not an input
        snap.Stats.MaxHealth = 1010;    // under one bucket
        Assert.False(monitor.Tick());
        Assert.Equal(1, calls);

        snap.Players[2].Level = 6;
        Assert.True(monitor.Tick());
        Assert.Equal(2, calls);
        Assert.Equal(600, monitor.Latest.Single().Estimate.Damage);

        snap.Players[2].ItemIds[0] = 3142; snap.Players[2].ItemCount = 1;
        Assert.True(monitor.Tick());
        Assert.Equal(3, calls);
    }

    [Fact]
    public void NoGame_NothingComputed()
    {
        int calls = 0;
        var monitor = new EnemyThreatMonitor(() => null, (_, _) => { calls++; return null; });
        Assert.False(monitor.Tick());
        Assert.Empty(monitor.Latest);
        Assert.Equal(0, calls);
    }
}

public class EnemyKeystoneParseTests
{
    [Fact]
    public void ParsesEachPlayersKeystone_AndSkipsTheRestOfRunes()
    {
        const string json = """
        {
          "activePlayer": { "summonerName": "Me", "level": 1, "championStats": { "maxHealth": 500 } },
          "allPlayers": [
            { "summonerName": "Me", "championName": "Ahri", "team": "ORDER", "level": 5, "items": [] },
            { "summonerName": "Foe", "championName": "Zed", "team": "CHAOS", "level": 6,
              "runes": { "keystone": { "displayName": "Electrocute", "id": 8112, "rawDescription": "x" },
                         "primaryRuneTree": { "id": 8100 }, "secondaryRuneTree": { "id": 8300 } },
              "items": [ { "itemID": 3142, "slot": 0 } ] }
          ],
          "events": { "Events": [] },
          "gameData": { "gameTime": 100 }
        }
        """;
        var snap = new GameSnapshot();
        Assert.True(LiveDataParser.Parse(System.Text.Encoding.UTF8.GetBytes(json), snap));
        Assert.Equal(0, snap.Players[0].KeystoneId);
        Assert.Equal(8112, snap.Players[1].KeystoneId);
        Assert.Equal(6, snap.Players[1].Level);          // fields after "runes" still parsed
        Assert.Equal(1, snap.Players[1].ItemCount);
    }
}

public class SkillOrdersDataTests
{
    [Fact]
    public void EveryPresetChampion_HasAValidOrderWithSources()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "skill_orders.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var orders = SkillOrders.Parse(File.ReadAllText(path));
        foreach (var champion in ComboPresets.All.Keys)
        {
            Assert.True(orders.ContainsKey(champion), $"{champion} has no skill order");
            Assert.True(doc.RootElement.GetProperty(champion).GetProperty("sources").GetArrayLength() > 0,
                $"{champion} lists no source");
        }
    }

    [Theory]
    [InlineData("QEW", true)]
    [InlineData("eqw", true)]
    [InlineData("QQW", false)]
    [InlineData("QWER", false)]
    public void OrderMustBeAPermutationOfQWE(string order, bool ok)
        => Assert.Equal(ok, SkillOrders.IsPermutation(order));

    [Fact]
    public void MalformedEntriesAreSkipped()
    {
        var parsed = SkillOrders.Parse("""{ "_note": "x", "A": { "order": "QEW" }, "B": { "order": "QQQ" }, "C": {} }""");
        Assert.Equal("QEW", parsed["A"]);
        Assert.False(parsed.ContainsKey("B"));
        Assert.False(parsed.ContainsKey("C"));
    }
}

public class EnemyThreatMonitorRetryTests
{
    [Fact]
    public void AFailedEstimate_IsRetriedAfterTheRetryWindow_NotEveryTick()
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", PlayerCount = 2,
            Stats = new ActivePlayerStats { MaxHealth = 1000, CurrentHealth = 1000 },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].Team = "ORDER";
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].Team = "CHAOS"; snap.Players[1].ChampionName = "Zed";
        long now = 0;
        int calls = 0;
        bool ready = false;
        var monitor = new EnemyThreatMonitor(() => snap,
            (_, e) => { calls++; return ready ? new ThreatEstimate("Zed", 1, 500, "x") : null; }, () => now);

        Assert.True(monitor.Tick());
        Assert.Empty(monitor.Latest);
        now = EnemyThreatMonitor.RetryMs - 1;
        Assert.False(monitor.Tick());
        Assert.Equal(1, calls);

        ready = true;
        now = EnemyThreatMonitor.RetryMs;
        Assert.True(monitor.Tick());
        Assert.Equal(2, calls);
        Assert.Single(monitor.Latest);

        now += 10 * EnemyThreatMonitor.RetryMs; // a good estimate is never retried by time alone
        Assert.False(monitor.Tick());
        Assert.Equal(2, calls);
    }
}

/// <summary>(lolf01-t3) Overlapping Ticks must not race on the cache: one compute per enemy for an
/// unchanged snapshot, never two at once.</summary>
public class EnemyThreatMonitorConcurrencyTests
{
    [Fact]
    public async Task ParallelTicks_NeverThrow_AndComputeEachEnemyOnce()
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", PlayerCount = 3,
            Stats = new ActivePlayerStats { MaxHealth = 1000, CurrentHealth = 1000, Armor = 30, MagicResist = 30 },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].Team = "ORDER";
        snap.Players[1].SummonerName = "Foe1"; snap.Players[1].Team = "CHAOS"; snap.Players[1].ChampionName = "Zed";
        snap.Players[2].SummonerName = "Foe2"; snap.Players[2].Team = "CHAOS"; snap.Players[2].ChampionName = "Ahri";
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        int inFlight = 0, maxInFlight = 0;
        var monitor = new EnemyThreatMonitor(() => snap, (_, e) =>
        {
            int now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref maxInFlight, now);
            Thread.Sleep(20); // a slow compute widens the race window
            calls.AddOrUpdate(e.ChampionName, 1, (_, n) => n + 1);
            Interlocked.Decrement(ref inFlight);
            return new ThreatEstimate(e.ChampionName, 1, 100, "x");
        });

        using var start = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 50; i++) monitor.Tick();
        })).ToArray();
        start.Set();
        await Task.WhenAll(threads); // rethrows any Tick exception

        Assert.Equal(1, calls["Zed"]);
        Assert.Equal(1, calls["Ahri"]);
        Assert.Equal(1, maxInFlight);
        Assert.Equal(new[] { "Zed", "Ahri" }, monitor.Latest.Select(t => t.ScoreboardName));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}

public class LaneOpponentTests
{
    [Theory]
    [InlineData("top", "Ahri", "top")]
    [InlineData("middle", "Garen", "mid")]
    [InlineData("bottom", "Garen", "adc")]
    [InlineData("utility", "Garen", "support")]
    [InlineData("", "Ahri", "mid")]      // blind pick: my champion's most-played role
    public void MyRole(string position, string champion, string expected)
        => Assert.Equal(expected, global::Overlay.Core.ChampSelect.LaneOpponent.MyRole(position, champion));

    [Fact]
    public void GuessesTheEnemyPlayingMyRole_ElseTheFirst()
    {
        var enemies = new[] { "Garen", "LeeSin", "Ahri", "Jinx", "Thresh" };
        Assert.Equal("Ahri", global::Overlay.Core.ChampSelect.LaneOpponent.Guess("mid", enemies));
        Assert.Equal("Thresh", global::Overlay.Core.ChampSelect.LaneOpponent.Guess("support", enemies));
        Assert.Equal("Garen", global::Overlay.Core.ChampSelect.LaneOpponent.Guess(null, enemies));
        Assert.Null(global::Overlay.Core.ChampSelect.LaneOpponent.Guess("mid", Array.Empty<string>()));
    }

    [Fact]
    public void BoardCarriesMyAssignedPosition()
    {
        const string json = """
        { "localPlayerCellId": 2,
          "myTeam": [ { "cellId": 1, "championId": 86, "assignedPosition": "top" },
                      { "cellId": 2, "championId": 0, "championPickIntent": 103, "assignedPosition": "middle" } ],
          "theirTeam": [ { "cellId": 5, "championId": 238 } ],
          "bans": { "myTeamBans": [], "theirTeamBans": [] } }
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var board = global::Overlay.Core.Lcu.LcuConnector.ExtractBoard(doc.RootElement);
        Assert.Equal("middle", board.MyPosition);
        Assert.Equal(new[] { 86, 103 }, board.MyTeam);
    }
}
