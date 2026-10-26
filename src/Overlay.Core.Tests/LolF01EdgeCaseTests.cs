using System.Text.Json.Nodes;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;

namespace Overlay.Core.Tests;

/// <summary>
/// (lolf01-g1) The QA scenarios S4, S12 and S13, one named test per step: tank-ranking edge cases
/// (<see cref="ComboRunner.RankTankItems"/>), schema-v2 migration keeping the user's other values, and a v2 file
/// staying as it is over repeated load/save cycles.
/// </summary>
public class LolF01EdgeCaseTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public LolF01EdgeCaseTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "LolF01G1_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── S4: tank ranking edge cases ──

    private const string ChampId = "Aatrox";
    private const int Level = 11;

    private static readonly string[] Candidates = { "9101", "9102", "9103" };

    private static void SeedItems() => ItemRepository.Initialize(new[]
    {
        new ItemData { Id = "9101", Name = "Armor", GoldTotal = 2900, AvailableOnSummonersRift = true, Stats = new ItemStats { Armor = 80 } },
        new ItemData { Id = "9102", Name = "MR", GoldTotal = 2900, AvailableOnSummonersRift = true, Stats = new ItemStats { Mr = 80 } },
        new ItemData { Id = "9103", Name = "HP", GoldTotal = 3000, AvailableOnSummonersRift = true, Stats = new ItemStats { Hp = 500 } },
    });

    [Fact]
    public void S4_NullOrNoGameSnapshot_ReturnsEmpty_NoException()
    {
        SeedItems();
        var runner = NewRunner(() => null);

        IReadOnlyList<NextItemRow>? fromNull = null, fromNoGame = null;
        var exception = Record.Exception(() =>
        {
            fromNull = runner.RankTankItems(null!, Candidates);
            fromNoGame = runner.RankTankItems(new GameSnapshot(), Candidates); // HasData false: no game
        });

        Assert.Null(exception);
        Assert.Empty(fromNull!);
        Assert.Empty(fromNoGame!);
    }

    [Fact]
    public void S4_NoMePlayer_ReturnsEmpty()
    {
        SeedItems();
        var live = Live(enemies: 1);
        live.ActivePlayerSummonerName = "Nobody"; // no scoreboard row is me
        Assert.Empty(NewRunner(() => live).RankTankItems(live, Candidates));
    }

    [Fact]
    public void S4_AllEnemiesNameless_ReturnsEmpty()
    {
        SeedItems();
        var live = Live(enemies: 3);
        for (int i = 1; i < live.PlayerCount; i++) live.Players[i].ChampionName = string.Empty;
        Assert.Empty(NewRunner(() => live).RankTankItems(live, Candidates));
    }

    [Fact]
    public void S4_NamelessEnemyNextToNamedOnes_IsNotCounted()
    {
        SeedItems();
        var one = Live(enemies: 1);
        var withNameless = Live(enemies: 2);
        withNameless.Players[2].ChampionName = string.Empty;
        var runner = NewRunner(() => one);

        var reference = runner.RankTankItems(one, Candidates);
        var rows = runner.RankTankItems(withNameless, Candidates);

        Assert.Equal(3, reference.Count);
        Assert.Equal(Fingerprint(reference), Fingerprint(rows));
    }

    [Fact]
    public void S4_SevenEnemies_AtMostFiveCounted()
    {
        SeedItems();
        var five = Live(enemies: 5);
        var seven = Live(enemies: 7);
        var runner = NewRunner(() => five);

        var reference = runner.RankTankItems(five, Candidates);
        var rows = runner.RankTankItems(seven, Candidates);

        Assert.Equal(3, reference.Count);
        // identical enemies: each one adds the same share, so a sixth or seventh counted would raise Before
        Assert.Equal(Fingerprint(reference), Fingerprint(rows));
    }

    [Fact]
    public void S4_MeAtMaxHpZero_NoException_AllValuesFinite()
    {
        SeedItems();
        var live = Live(enemies: 1);
        live.Stats.MaxHealth = 0;
        live.Stats.CurrentHealth = 0;

        IReadOnlyList<NextItemRow>? rows = null;
        var exception = Record.Exception(() => rows = NewRunner(() => live).RankTankItems(live, Candidates));

        Assert.Null(exception);
        Assert.All(rows!, r =>
        {
            Assert.True(double.IsFinite(r.Before), $"{r.ItemId} Before {r.Before}");
            Assert.True(double.IsFinite(r.After), $"{r.ItemId} After {r.After}");
            Assert.True(double.IsFinite(r.SurviveGainPercent), $"{r.ItemId} SurviveGainPercent {r.SurviveGainPercent}");
            Assert.True(double.IsFinite(r.SurviveGainPer1000Gold), $"{r.ItemId} SurviveGainPer1000Gold {r.SurviveGainPer1000Gold}");
        });
    }

    private static List<(string Id, double Before, double After)> Fingerprint(IReadOnlyList<NextItemRow> rows)
        => rows.Select(r => (r.ItemId, r.Before, r.After)).ToList();

    /// <summary>Me = Aatrox at level 11 on ORDER; <paramref name="enemies"/> identical level-11 Aatrox on CHAOS.</summary>
    private static GameSnapshot Live(int enemies)
    {
        var (q, w, e, r) = SyntheticAttacker.AssumedRanks(Level, "QWE");
        double maxHp = LevelGrowth.Stat(650, 114, Level);
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = Level, PlayerCount = 1 + enemies,
            Stats = new ActivePlayerStats
            {
                AttackDamage = LevelGrowth.Stat(60, 5, Level),
                MaxHealth = maxHp, CurrentHealth = maxHp,
                Armor = 60, MagicResist = 40,
                AbilityQ = q, AbilityW = w, AbilityE = e, AbilityR = r,
            },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = ChampId; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = Level;
        for (int i = 1; i <= enemies; i++)
        {
            snap.Players[i].SummonerName = "Foe" + i; snap.Players[i].ChampionName = ChampId;
            snap.Players[i].Team = "CHAOS"; snap.Players[i].Level = Level;
        }
        return snap;
    }

    private ComboRunner NewRunner(Func<GameSnapshot?> snapshot)
    {
        ChampionRepository.Initialize(new[] { FromBin(ChampId) });
        var config = new ConfigManager(Path.Combine(_dir, "runner_config.json"));
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

    // ── S12 / S13: config migration ──

    private const string LaneReturnKey = "overlay.items.laneReturn.enabled";
    private const string NextItemHotkeyKey = "overlay.nextItemHotkey";
    private const string ScaleKey = "overlay.scale"; // Settings → 오버레이 크기 (overlay size)
    private const string UnknownKey = "overlay.totallyUnknownOverlayField";

    private void WriteUserConfig(int? schemaVersion)
    {
        var root = new JsonObject
        {
            ["overlay"] = new JsonObject
            {
                ["nextItemHotkey"] = "ALT+N",
                ["scale"] = 1.25,
                ["totallyUnknownOverlayField"] = "should be ignored",
                ["items"] = new JsonObject
                {
                    ["laneReturn"] = new JsonObject { ["enabled"] = false },
                },
            },
            ["someFutureTopLevelFeature"] = new JsonObject { ["nested"] = 123 },
        };
        if (schemaVersion is int v) root["schemaVersion"] = v;
        File.WriteAllText(_path, root.ToJsonString());
    }

    private JsonObject ReadDisk() => JsonNode.Parse(File.ReadAllText(_path))!.AsObject();

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(null)]
    public void S12_OldSchema_LaneReturnTurnedOn_HotkeyAndSizeKept_InMemoryAndOnDiskAsV2(int? schemaVersion)
    {
        WriteUserConfig(schemaVersion);

        using var config = new ConfigManager(_path);

        Assert.Equal(true, config.Get(LaneReturnKey));
        Assert.Equal("ALT+N", config.Get(NextItemHotkeyKey));
        Assert.Equal(1.25, Convert.ToDouble(config.Get(ScaleKey)));

        var disk = ReadDisk();
        Assert.Equal(2, disk["schemaVersion"]!.GetValue<int>());
        Assert.True(disk["overlay"]!["items"]!["laneReturn"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("ALT+N", disk["overlay"]!["nextItemHotkey"]!.GetValue<string>());
        Assert.Equal(1.25, disk["overlay"]!["scale"]!.GetValue<double>());
    }

    /// <summary>The migrated load treats an unknown key exactly as the un-migrated (v2) load does and as
    /// ConfigManagerTests.UnknownKeysInFile_AreIgnored_AndDoNotCrash expects: no exception, the known values load,
    /// and the unknown key is dropped (the typed round-trip keeps schema fields only).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(null)]
    [InlineData(2)]
    public void S12_UnknownExtraKey_IgnoredWithoutCrash_SameAsBeforeMigration(int? schemaVersion)
    {
        WriteUserConfig(schemaVersion);

        var exception = Record.Exception(() =>
        {
            using var config = new ConfigManager(_path);
            Assert.Equal(4d, Convert.ToDouble(config.Get("overlay.hudDisplayDuration")));
            Assert.Equal("ALT+N", config.Get(NextItemHotkeyKey));
            Assert.Null(config.Get(UnknownKey));
            Assert.Null(config.Get("someFutureTopLevelFeature"));
        });

        Assert.Null(exception);
    }

    [Fact]
    public void S13_V2FileLaneReturnOff_ThreeLoadSaveReloadCycles_StaysOffAndV2()
    {
        WriteUserConfig(schemaVersion: 2);

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            using (var config = new ConfigManager(_path))
            {
                Assert.Equal(false, config.Get(LaneReturnKey));
                config.Set("overlay.hudDisplayDuration", 4.0 + cycle); // a save that does not touch the toggle
            } // Dispose flushes the pending write

            var disk = ReadDisk();
            Assert.Equal(2, disk["schemaVersion"]!.GetValue<int>());
            Assert.False(disk["overlay"]!["items"]!["laneReturn"]!["enabled"]!.GetValue<bool>());
        }

        using var reloaded = new ConfigManager(_path);
        Assert.Equal(false, reloaded.Get(LaneReturnKey));
        Assert.Equal(7d, Convert.ToDouble(reloaded.Get("overlay.hudDisplayDuration")));
    }

    [Fact]
    public void S13_CorruptJson_FallsBackToDefaults_NoMigrationCrash()
    {
        File.WriteAllText(_path, "{ \"schemaVersion\": 1, \"overlay\": { not valid json ][");

        ConfigManager? config = null;
        var exception = Record.Exception(() => config = new ConfigManager(_path));

        Assert.Null(exception);
        using (config)
        {
            Assert.Equal(4d, Convert.ToDouble(config!.Get("overlay.hudDisplayDuration")));
            Assert.Equal(true, config.Get(LaneReturnKey));
            Assert.Null(config.Get(NextItemHotkeyKey));
            Assert.Equal(2, ReadDisk()["schemaVersion"]!.GetValue<int>());
        }
    }

    [Fact]
    public void S13_CurrentSchemaVersion_Is2()
        => Assert.Equal(2, ConfigSchema.CurrentSchemaVersion);
}
