using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 45) An item's on-hit is copied from the attack node it rides and kept the attack's crit: with crit chance
/// every on-hit item read x (1 + c x (M - 1)). On-hit damage does not crit (Jhin's fourth-shot bonus, GoldenJhinTests).
/// </summary>
public class M45Loop45OnHitNoCritTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public M45Loop45OnHitNoCritTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L45_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ItemOnHit_DoesNotCrit()
    {
        // Titanic Hydra: 1% of the (melee) holder's max health on-hit = 20, whatever the crit chance
        double plain = ItemHit(critChance: 0), crits = ItemHit(critChance: 1.0);
        Assert.Equal(20, plain, 3);
        Assert.Equal(plain, crits, 3);
    }

    private double ItemHit(double critChance)
    {
        ChampionRepository.Initialize(new[]
        {
            new ChampionData { Id = "Garen", Name = "Garen", Skills = new Dictionary<string, SkillData>(),
                BaseStats = new ChampionBaseStats { Ad = 60, AttackRange = 175 }, StatsPerLevel = new ChampionStatsPerLevel() },
            new ChampionData { Id = "Target", Name = "Target",
                BaseStats = new ChampionBaseStats { Hp = 5000, Armor = 0, Mr = 0 }, StatsPerLevel = new ChampionStatsPerLevel() },
        });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 100, MaxHealth = 2000, CritChance = critChance, CritDamage = 2.0 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Garen"; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 11;
        snap.Players[0].ItemIds[0] = 3748; snap.Players[0].ItemCount = 1;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 11;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Garen", "t");
        editor.AddNode(draft.Id, new ComboNode("AA_0", ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0));
        editor.SaveCombo(draft.Id);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!.NodeBreakdown.Single(n => n.NodeId == "AA_0#item3748").Damage;
    }
}
