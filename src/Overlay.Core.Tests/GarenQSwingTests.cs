using System.IO;
using System.Linq;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;

namespace Overlay.Core.Tests;

/// <summary>
/// (2026-10-19, user report: "가렌 q데미지에 평타피해가 더해진 값으로 나옴") Garen Q's BIN TotalDamage is
/// 30 + 150% TOTAL AD — the whole empowered attack, swing included. Loop 551 also prepended a folded
/// swing (basicAttack hit) as for Camille Q, whose calc is bonus-only, so Garen's Q counted the attack
/// twice: 30 + 250% AD. Read through the per-skill panel, the same ApplyBinDamage path combos use.
/// </summary>
public class GarenQSwingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public GarenQSwingTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GarenQSwing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
        // Garen + a 0-armor dummy so physical damage is unmitigated (a box == its raw number).
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Garen"),
            new ChampionData { Id = "Dummy", Name = "Dummy",
                BaseStats = new ChampionBaseStats { Hp = 3000, Armor = 0, Mr = 0, Ad = 50 },
                StatsPerLevel = new ChampionStatsPerLevel() },
        });
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void GarenQ_IsTheEmpoweredAttackOnce_NotPlusAnotherSwing()
    {
        var (q100, a100) = QAndAaBoxes(attackDamage: 100);
        var (q200, a200) = QAndAaBoxes(attackDamage: 200);

        // Rank 1, 0 armour: 30 + 1.5 × 100 = 180 (was 280 with the extra swing).
        Assert.True(System.Math.Abs(q100 - 180) <= 1.0, $"Q at 100 AD should be 30 + 150% AD = 180, got {q100:0.##}");
        // +100 AD adds 100 to the AA and 150 to Q (the swing once + Q's own 50%) — 250 was the double.
        Assert.True(System.Math.Abs((a200 - a100) - 100) <= 1.0, $"AA box should grow by 100, grew {a200 - a100:0.##}");
        Assert.True(System.Math.Abs((q200 - q100) - 150) <= 1.0, $"Q should grow by 150% AD (150), grew {q200 - q100:0.##}");
    }

    private (double q, double a) QAndAaBoxes(double attackDamage)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = attackDamage, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Garen"; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = "Dummy"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 6;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        using var runner = new ComboRunner(engine, config, () => snap);

        var panel = runner.ComputeSkillPanel(snap);
        Assert.NotNull(panel);
        return (panel!.Slots.Single(s => s.Slot == "Q").Damage, panel.Slots.Single(s => s.Slot == "A").Damage);
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
