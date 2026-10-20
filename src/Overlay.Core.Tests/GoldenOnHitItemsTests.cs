using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode exists in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN — on-hit items that read the target's health (M45 loop 45, docs/reports/golden/GOLDEN_JHIN_MEASUREMENT.md §2).
/// Measured by the user on 2026-10-20 in the Practice Tool: Ezreal (ranged), a 1000-health dummy at 0 armor.
///   Blade of the Ruined King, 105 AD: attacks 165, 155, 146 = 105 + 6% of the health BEFORE each attack (1000, 835, 680)
///   Kraken Slayer, 110 AD, the third attack: 250 at 780 / 1000, 292 at 309 / 1000, 320 at 1 / 1000
///     = 110 + 120 x (1 + 0.75 x the missing fraction BEFORE the attack) (0.22, 0.691, 0.999)
/// Read after the attack's own damage (the engine before loop 45) they were 158.7 and 259.7. ±1 for the displayed rounding.
/// </summary>
public class GoldenOnHitItemsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const int RuinedKing = 3153;
    private const int Kraken = 6672;

    public GoldenOnHitItemsTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests(); // the attack damage is the measured one, no item stats on top
        _dir = Path.Combine(Path.GetTempPath(), "GoldenOnHitItems_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Golden_RuinedKing_Ezreal_165_155_146()
    {
        var r = Run(RuinedKing, ad: 105, missing: 0);
        Assert.InRange(Attack(r, 0), 164, 166);
        Assert.InRange(Attack(r, 1), 154, 156);
        Assert.InRange(Attack(r, 2), 145, 147);
    }

    [Fact]
    public void Golden_Kraken_Ezreal_ThirdAttack_At780_Is250()
        // a full dummy: the first two attacks leave it at 780 / 1000
        => Assert.InRange(Attack(Run(Kraken, ad: 110, missing: 0), 2), 249, 251);

    [Fact]
    public void Golden_Kraken_Ezreal_ThirdAttack_At309_Is292()
        => Assert.InRange(Attack(Run(Kraken, ad: 110, missing: 1000 - 309 - 220), 2), 291, 293);

    [Fact]
    public void Golden_Kraken_Ezreal_ThirdAttack_At1_Is320()
        // 320 on a 1-health dummy is overkill and the combo stops counting at the kill: the proc, which now lands
        // first, is the 320 less the attack's 110
        => Assert.InRange(Run(Kraken, ad: 110, missing: 1000 - 1 - 220).NodeBreakdown.Single(n => n.NodeId == $"AA_2#item{Kraken}").Damage,
            209, 211);

    private static double Attack(ComboResult r, int i)
        => r.NodeBreakdown.Where(n => n.NodeId.StartsWith($"AA_{i}", StringComparison.Ordinal)).Sum(n => n.Damage);

    private ComboResult Run(int itemId, double ad, double missing)
    {
        ChampionRepository.Initialize(new[]
        {
            LoadEzrealFromBin(),
            new ChampionData { Id = "Target", Name = "Target",
                BaseStats = new ChampionBaseStats { Hp = 1000, Armor = 0, Mr = 0 }, StatsPerLevel = new ChampionStatsPerLevel() },
        });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 1, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = ad } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Ezreal"; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 1;
        snap.Players[0].ItemIds[0] = itemId; snap.Players[0].ItemCount = 1;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 1;

        using var tracker = new TargetHealthTracker();
        if (missing > 0) tracker.RecordDamageDealt("Target", missing);
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Ezreal", "g");
        for (int i = 0; i < 3; i++)
            editor.AddNode(draft.Id, new ComboNode($"AA_{i}", ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap, targetHealthTracker: tracker);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        return received!;
    }

    private static ChampionData LoadEzrealFromBin()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "ezreal.bin.json"));
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in ChampionBinParser.ParseChampion("Ezreal", json))
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Ezreal" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations, EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Ezreal", Name = "Ezreal", Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = 60, AttackRange = 550 }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
