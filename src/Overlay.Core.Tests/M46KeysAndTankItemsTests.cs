using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Hotkeys;
using Overlay.Core.Runes;

namespace Overlay.Core.Tests;

/// <summary>
/// (M46, user requests 2026-10-20) Chords of up to three keys (ALT+Q+W, Q+W+E) and the backtick key; the tank side of
/// the next-item card - defensive items ranked by how much of the enemies' combined full combos my max HP takes.
/// Docs: docs/modules/M46_KEYS_COMPACT_TANK_ITEMS.md.
/// </summary>
public class M46KeysAndTankItemsTests : IDisposable
{
    private readonly string _dir;

    public M46KeysAndTankItemsTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M46_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── chords ──

    [Theory]
    [InlineData("alt+1+2", "ALT+1+2")]
    [InlineData("w+q", "Q+W")]                  // the keys are sorted: the order they were pressed in does not matter
    [InlineData("q+w+e", "E+Q+W")]
    [InlineData("alt+`", "ALT+`")]              // the backtick key: the skill overlay's default
    [InlineData("shift+a+ctrl+alt", "CTRL+ALT+SHIFT+A")] // modifiers stay unlimited
    public void Parse_ChordsOfUpToThreeKeys(string input, string canonical)
        => Assert.Equal(canonical, HotkeyCombo.Parse(input).Canonical);

    [Fact]
    public void Parse_KeyOrderDoesNotMatter()
        => Assert.Equal(HotkeyCombo.Parse("ALT+Q+W"), HotkeyCombo.Parse("w+alt+q"));

    [Fact]
    public void Candidates_HeldKeysTogetherFirst_ThenThePressedKeyAlone()
    {
        // Space held for the camera, ALT+1 pressed: the chord with Space first, then ALT+1 itself - never blocked
        var c = HotkeyCombo.Candidates(HotkeyModifiers.Alt, new[] { "SPACE" }, "1").Select(x => x.Canonical).ToArray();
        Assert.Equal(new[] { "ALT+1+SPACE", "ALT+1" }, c);
        // nothing else held: one candidate
        Assert.Equal(new[] { "ALT+1" }, HotkeyCombo.Candidates(HotkeyModifiers.Alt, Array.Empty<string>(), "1").Select(x => x.Canonical));
        // the key held while it auto-repeats: still the same chord
        Assert.Equal(new[] { "Q+W" }, HotkeyCombo.Candidates(HotkeyModifiers.None, new[] { "Q", "W" }, "W").Select(x => x.Canonical).Take(1));
    }

    [Fact]
    public void Registry_TakesAMultiKeyChord_AndSeesItInEitherOrder()
    {
        var registry = new HotkeyRegistry(new NullHook());
        registry.Register("q+w", () => { }, "M46");
        Assert.True(registry.IsConflicting("W+Q"));
        Assert.False(registry.IsConflicting("Q"));
    }

    private sealed class NullHook : IHotkeyHook
    {
        public void Register(int osId, HotkeyCombo combo) { }
        public void Unregister(int osId) { }
    }

    // ── tank items ──

    private const string ChampId = "Aatrox";
    private const int Level = 11;

    private static void SeedItems() => ItemRepository.Initialize(new[]
    {
        new ItemData { Id = "9101", Name = "Armor", GoldTotal = 2900, AvailableOnSummonersRift = true, Stats = new ItemStats { Armor = 80 } },
        new ItemData { Id = "9102", Name = "MR", GoldTotal = 2900, AvailableOnSummonersRift = true, Stats = new ItemStats { Mr = 80 } },
        new ItemData { Id = "9103", Name = "HP", GoldTotal = 3000, AvailableOnSummonersRift = true, Stats = new ItemStats { Hp = 500 } },
        new ItemData { Id = "9104", Name = "AD", GoldTotal = 3000, AvailableOnSummonersRift = true, Stats = new ItemStats { Ad = 60 } },
        new ItemData { Id = "9105", Name = "Boots", GoldTotal = 2400, AvailableOnSummonersRift = true, IsBoots = true, Stats = new ItemStats { Armor = 20 } },
        new ItemData { Id = "9106", Name = "Part", GoldTotal = 2300, AvailableOnSummonersRift = true, BuildsInto = new[] { "9101" }, Stats = new ItemStats { Armor = 40 } },
        new ItemData { Id = "9107", Name = "Cheap", GoldTotal = 1100, AvailableOnSummonersRift = true, Stats = new ItemStats { Hp = 200 } },
    });

    [Fact]
    public void TankCandidates_AreFinishedDefensiveItemsNotOwned()
    {
        SeedItems();
        Assert.Equal(new[] { "9101", "9102", "9103" }, global::Overlay.Core.Items.NextItemCandidates.ForTank(Array.Empty<int>()).OrderBy(x => x));
        Assert.Equal(new[] { "9102", "9103" }, global::Overlay.Core.Items.NextItemCandidates.ForTank(new[] { 9101 }).OrderBy(x => x));
    }

    [Fact]
    public void TankRanking_ArmorAndHealthCutAPhysicalEnemysShare_MoreThanMagicResist()
    {
        SeedItems();
        var live = Live();
        var runner = NewRunner(() => live);
        var rows = runner.RankTankItems(live, new[] { "9102", "9101", "9103" });

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.Tank));
        Assert.True(rows[0].Before > 0);
        Assert.Equal(rows.OrderBy(r => r.After).Select(r => r.ItemId), rows.Select(r => r.ItemId)); // best (lowest share) first
        var byId = rows.ToDictionary(r => r.ItemId);
        Assert.True(byId["9101"].ShareCutPoints > 1);                  // armor against Aatrox's physical combo
        Assert.True(byId["9103"].ShareCutPoints > 1);                  // health against anything
        // MR only meets Deathbringer Stance (magic, a share of max health): less than armor does
        Assert.True(byId["9102"].ShareCutPoints > 0);
        Assert.True(byId["9101"].ShareCutPoints > byId["9102"].ShareCutPoints);
        Assert.Equal("9102", rows[^1].ItemId);
        Assert.True(byId["9101"].ShareCutPer1000Gold > 0);
    }

    [Fact]
    public void TankRanking_NoEnemy_NoRows()
    {
        SeedItems();
        var live = Live();
        live.PlayerCount = 1;
        Assert.Empty(NewRunner(() => live).RankTankItems(live, new[] { "9101" }));
    }

    [Fact]
    public void Config_NextItemMode_DefaultsToDamage()
    {
        using var config = new ConfigManager(Path.Combine(_dir, "user_config.json"));
        Assert.Equal("damage", config.Get("overlay.nextItemMode"));
        Assert.Null(config.Get("overlay.nextItemHotkey"));        // the client default, P
        Assert.Null(config.Get("overlay.globalGoldHotkey"));      // the client default, TAB = follow the scoreboard
    }

    // ── crit expectation ──

    [Theory]
    [InlineData(0.0, false)]   // nothing crits: no middle to mark
    [InlineData(0.5, true)]
    [InlineData(1.0, false)]   // every crit lands: the expectation is the max
    public void CritExpected_OnlyWhenCritMovesTheCombo_TheBlendInBetween(double critChance, bool marked)
    {
        var result = RunAutos(critChance, ComboNodeType.Aa);
        if (!marked) { Assert.Null(result.CritExpected); return; }
        Assert.Equal(result.TotalDamage, result.CritExpected!.Value, 6);
        Assert.InRange(result.CritExpected.Value, result.TotalDamageMin + 1, result.TotalDamageMax - 1);
    }

    [Fact]
    public void CritExpected_NullWithNoHitThatCanCrit()
        => Assert.Null(RunAutos(0.5, ComboNodeType.Skill).CritExpected);

    /// <summary>Three 100-damage hits (1.0 AD at 100 AD) into a 5000-HP target with no resists.</summary>
    private static ComboResult RunAutos(double critChance, ComboNodeType type)
    {
        RuneRepository.Initialize(Array.Empty<RuneData>());
        using var runes = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runes);
        var nodes = Enumerable.Range(0, 3).Select(i => new Combo.ComboNode(
            Id: "AA_" + i, NodeType: type, Name: "AA", Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
            RatioAD: 1, RatioBonusAD: 0, RatioAP: 0, CastTime: 0.5, Delay: 0.1, TravelTime: 0)).ToArray();
        var context = new global::Overlay.Core.Combo.ExecutionContext("TestChamp",
            new AttackerStat(Ad: 100, BonusAD: 0, Ap: 0, Level: 11, CriticalChance: critChance, LifeSteal: 0),
            new DefenderStat(CurrentHP: 5000, MaxHP: 5000, Armor: 0, Mr: 0, Shield: 0),
            new UserRuneConfig(Array.Empty<string>()));
        return engine.Execute(engine.BuildGraph(nodes), context);
    }

    /// <summary>Me = Aatrox at level 11; the enemy is an Aatrox too (a physical combo).</summary>
    private static GameSnapshot Live()
    {
        var (q, w, e, r) = SyntheticAttacker.AssumedRanks(Level, "QWE");
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = Level, PlayerCount = 2,
            Stats = new ActivePlayerStats
            {
                AttackDamage = LevelGrowth.Stat(60, 5, Level),
                MaxHealth = LevelGrowth.Stat(650, 114, Level), CurrentHealth = LevelGrowth.Stat(650, 114, Level),
                Armor = 60, MagicResist = 40,
                AbilityQ = q, AbilityW = w, AbilityE = e, AbilityR = r,
            },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = ChampId; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = Level;
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = ChampId; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = Level;
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
