using System.IO;
using System.Linq;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Items;
using Overlay.Core.Runes;

namespace Overlay.Core.Tests;

/// <summary>
/// (2026-10-19, user: "abilities don't crit unless the tooltip says so, and each crits for its own
/// amount") Every ability tagged crit-capable must crit for what its tooltip names, at the 200% base:
/// the panel box at 100% crit chance over the box at 0%. Rank 1, 100 AD, a 0-resist dummy. The
/// expected ratios are the tooltips' own numbers (CommunityDragon string table + BIN, 2026-10-19).
/// </summary>
public class AbilityCritTagTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public AbilityCritTagTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "AbilityCrit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Theory]
    [InlineData("Akshan", "E", 1.5)]             // 1 + CritDamageMod 0.5 × (2 − 1)
    [InlineData("MasterYi", "Q", 2.0)]           // + CritBonus = TotalDamage × 1.0 × (2 − 1)
    [InlineData("Gangplank", "Q", 2.0)]          // 1 + CritDamageMod 1.0 × (2 − 1)
    [InlineData("Samira", "Q", 1.5)]             // 1 + CritDamageMod 0.5 × (2 − 1)
    [InlineData("Samira", "R", 2.0)]             // DamageCalc × critDamage, every shot
    [InlineData("MissFortune", "R", 1.3)]        // per wave 1 + 0.3 × (2 − 1)
    [InlineData("Shaco", "Q", 2.0)]              // a normal crit
    [InlineData("Zeri", "Q", 2.0)]               // a normal crit
    [InlineData("Yasuo", "Q", 230.0 / 125.0)]    // 20 + 1.05 × 100 × 2 over 20 + 105: only the AD part crits
    [InlineData("Yone", "Q", 245.0 / 135.0)]     // 25 + 1.1 × 100 × 2 over 25 + 110
    public void TaggedAbility_CritsForItsTooltipAmount(string champion, string slot, double ratio)
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin(champion), Dummy() });

        double noCrit = Box(champion, slot, critChance: 0);
        double allCrit = Box(champion, slot, critChance: 1.0);

        Assert.True(noCrit > 0, $"{champion} {slot} must resolve");
        Assert.Equal(ratio, allCrit / noCrit, 2);
    }

    [Fact]
    public void KennenPassive_CritCalc_Is140Percent()
    {
        var kennen = LoadChampionFromBin("Kennen");
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 50, AbilityW = 1, CritDamage = 2.0 };

        double normal = SkillDamage.ComputeCalcDamage(kennen, "W", "TotalDamagePassive", stats, 6)!.Value;
        double crit = SkillDamage.ComputeCalcDamage(kennen, "W", "TotalDamagePassiveCrit", stats, 6)!.Value;

        Assert.Equal(1.4, crit / normal, 3);
    }

    private double Box(string champion, string slot, double critChance)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
                CritChance = critChance, CritDamage = 2.0 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champion; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = "Dummy"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 6;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        using var runner = new ComboRunner(engine, config, () => snap);
        var panel = runner.ComputeSkillPanel(snap);
        Assert.NotNull(panel);
        return panel!.Slots.Single(s => s.Slot == slot).Damage;
    }

    private static ChampionData Dummy() => new()
    {
        Id = "Dummy", Name = "Dummy",
        BaseStats = new ChampionBaseStats { Hp = 100000, Armor = 0, Mr = 0, Ad = 50 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

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
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
