using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Items;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN (M45 loop 50) — measured by the user on 2026-10-20 in the Practice Tool: Katarina (melee) with Voltaic Cyclosword,
/// AD 173, 10 lethality (the item's own), the dummy at 1000 / 1000. A basic attack 173 at 0 armor; the energized attack 263 at
/// 0 armor and 250 at 30 armor. Charged by moving (no damage before it: 263 = 173 + 9% of the full 1000), so it is the
/// "모든 아이템" chip's energized attack. Docs: docs/reports/golden/GOLDEN_JHIN_MEASUREMENT.md section 3.
/// The rows say: lethality does not take armor below 0 (173, not more), and the 15 lethality the energized attack grants
/// counts on that attack itself (263 x 100 / (100 + 30 - 10 - 15) = 250.5; from the next hit on it would be 219).
/// </summary>
public class GoldenVoltaicKatarinaTests : IDisposable
{
    private static readonly string DDragonDir = Path.Combine(AppContext.BaseDirectory, "data", "ddragon", "16.13.1");
    private static readonly string CDragonDir = Path.Combine(AppContext.BaseDirectory, "data", "communitydragon");
    private const int Voltaic = 6699;
    private readonly string _dir;
    private readonly string _configPath;

    public GoldenVoltaicKatarinaTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenVoltaic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Golden_Katarina_BasicAttack_ZeroArmor_Is173()
        => Assert.InRange(Run(armor: 0, Aa(0)).TotalDamage, 172.5, 173.5);

    [Fact]
    public void Golden_Katarina_VoltaicEnergizedAttack_ZeroArmor_Is263()
        => Assert.InRange(Energized(Run(armor: 0, Chip(0), Aa(1))), 262.5, 263.5);

    [Fact]
    public void Golden_Katarina_VoltaicEnergizedAttack_ThirtyArmor_Is250()
        => Assert.InRange(Energized(Run(armor: 30, Chip(0), Aa(1))), 249.5, 251);

    // the energized attack: the attack plus the item's share of the current health the chip fires
    private static double Energized(ComboResult r)
        => r.NodeBreakdown.Where(n => n.NodeId == "AA_1" || n.NodeId.EndsWith($"#item{Voltaic}#chip", StringComparison.Ordinal))
            .Sum(n => n.Damage);

    private static ComboNode Aa(int i)
        => new($"AA_{i}", ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0);

    private static ComboNode Chip(int i)
        => new(ComboEditor.ItemsNodeId + "_" + i, ComboNodeType.Summoner, ComboEditor.ItemsNodeId, 0, 0, 0,
            ComboDamageType.Physical, 0, 0, 0, 0, 0, 0);

    private ComboResult Run(double armor, params ComboNode[] nodes)
    {
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);
        var katarina = ChampionRepository.Get("Katarina")!;
        ChampionRepository.Initialize(new[]
        {
            katarina,
            new ChampionData { Id = "Target", Name = "Target",
                BaseStats = new ChampionBaseStats { Hp = 1000, Armor = armor }, StatsPerLevel = new ChampionStatsPerLevel() },
        });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 1, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 173, MaxHealth = 700, CurrentHealth = 700, CritDamage = 2.0,
                PhysicalLethality = 10, AbilityQ = 1 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Katarina"; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 1;
        snap.Players[0].ItemIds[0] = Voltaic;
        snap.Players[0].ItemCount = 1;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 1;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Katarina", "golden");
        foreach (var node in nodes) editor.AddNode(draft.Id, node);
        editor.SaveCombo(draft.Id);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        return received!;
    }
}
