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
/// (2026-10-19, user report) Two Garen numbers the engine did not have:
/// <list type="bullet">
/// <item>Judgment (E): the sixth strike on a target shreds 25% armor (BIN StacksToShred 6, ShredAmount
/// 0.25), so the seventh strike and anything after it hit softer.</item>
/// <item>A target's Plated Steelcaps (3047) cuts damage from attacks by 10% — Decisive Strike (Q) is an
/// empowered attack, so it is cut; an ability is not.</item>
/// </list>
/// Read through the per-skill panel, the same ApplyBinDamage path combos use.
/// </summary>
public class GarenShredSteelcapsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public GarenShredSteelcapsTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests(); // no item stats: Steelcaps' armor must not move the mitigation here
        _dir = Path.Combine(Path.GetTempPath(), "GarenShred_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Garen"),
            new ChampionData { Id = "Dummy", Name = "Dummy",
                BaseStats = new ChampionBaseStats { Hp = 5000, Armor = 100, Mr = 0, Ad = 50 },
                StatsPerLevel = new ChampionStatsPerLevel() },
        });
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void GarenE_SeventhStrike_HitsTheShreddedArmor()
    {
        double e = Box("E", steelcaps: false);

        // Rank 1, 100 AD, no attack speed: 7 strikes of 4 + 0.4 × 100 = 44 against 100 armor.
        // Strikes 1-6 at 100/200; the sixth shreds 25%, so the seventh lands at 100/175.
        double expected = 6 * 44 * 0.5 + 44 * 100.0 / 175.0;
        Assert.True(System.Math.Abs(e - expected) <= 0.5, $"E should be {expected:0.##} with the shred, got {e:0.##}");
    }

    [Fact]
    public void Steelcaps_CutsGarenQ_ButNotE()
    {
        double qPlain = Box("Q", steelcaps: false), qCapped = Box("Q", steelcaps: true);
        double ePlain = Box("E", steelcaps: false), eCapped = Box("E", steelcaps: true);

        Assert.Equal(qPlain * 0.9, qCapped, 1); // Decisive Strike is an attack
        Assert.Equal(ePlain, eCapped, 1);       // Judgment is not
    }

    private double Box(string slot, bool steelcaps)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Garen"; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = "Dummy"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 6;
        if (steelcaps)
        {
            snap.Players[1].ItemIds[0] = 3047;
            snap.Players[1].ItemCount = 1;
        }

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        using var runner = new ComboRunner(engine, config, () => snap);

        var panel = runner.ComputeSkillPanel(snap);
        Assert.NotNull(panel);
        return panel!.Slots.Single(s => s.Slot == slot).Damage;
    }

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
