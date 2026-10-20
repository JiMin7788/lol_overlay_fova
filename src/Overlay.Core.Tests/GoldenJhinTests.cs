using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode exists in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN — Jhin's crits (M45 loop 45, docs/reports/golden/GOLDEN_JHIN_MEASUREMENT.md). Measured by the user on
/// 2026-10-20 in the Practice Tool: Jhin level 1, the dummy at 0 armor (the hits read the attack damage itself).
///   The Collector only:       an attack 134, a crit 202, the fourth shot against a 1/1000 dummy 352
///   + Infinity Edge:          an attack 238, a crit 411
/// The Live Client API in the same game (Collector + Infinity Edge): attackDamage 238.293, critChance 0.5,
/// critDamage 172.5 = (200 + Infinity Edge's 30) x 0.75. So the live stat ALREADY carries Jhin's 25% crit reduction
/// (411 / 238 = 1.725), and the engine must not apply it again (loop 44 did: 1.294). The fourth shot is a crit at the
/// same multiplier plus FourthShotExecutePercent (15% at level 1) of the missing health, the bonus not crit-multiplied:
/// 352 = 202 + 0.15 x 999. The Collector-only API row was not read; its stats are the measured ones (AD between 134.33
/// and 135 by the 134 / 202 pair, critDamage 1.5 = 2.0 x 0.75 as the Infinity Edge row shows). ±1 for the displayed
/// rounding.
/// </summary>
public class GoldenJhinTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public GoldenJhinTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenJhinTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // the API row, Collector + Infinity Edge
    private static ActivePlayerStats InfinityEdge() => new() { AttackDamage = 238.293, CritChance = 0.5, CritDamage = 1.725 };

    // the Collector only (measured attack 134, crit 202)
    private static ActivePlayerStats CollectorOnly() => new() { AttackDamage = 134.5, CritChance = 0.25, CritDamage = 1.5 };

    [Fact]
    public void Golden_Jhin_InfinityEdge_Crit_Is411()
        // the fourth shot crits for sure; at full health its missing-health bonus is 0, so it reads the crit itself
        => Assert.InRange(Swing(Run(InfinityEdge(), fourthShot: true, missing: 0)), 410, 412);

    [Fact]
    public void Golden_Jhin_InfinityEdge_ExpectedAttack_IsHalfAttackHalfCrit()
        // 50% crit: 0.5 x 238 + 0.5 x 411
        => Assert.InRange(Swing(Run(InfinityEdge(), fourthShot: false, missing: 0)), 323.5, 325.5);

    [Fact]
    public void Golden_Jhin_CollectorOnly_Crit_Is202()
        // the fourth shot at full health: its bonus reads the health before the hit (0 missing), not the swing's own damage
        => Assert.InRange(Swing(Run(CollectorOnly(), fourthShot: true, missing: 0)), 201, 203);

    [Fact]
    public void Golden_Jhin_CollectorOnly_FourthShot_999Missing_Is352()
        // 202 + 0.15 x 999 missing. The displayed 352 on a 1-HP dummy is overkill, and the combo stops counting once the
        // target is dead - so the same 999 missing on a dummy with room for the hit (the bonus reads missing health only).
        // It also pins the order: a bonus read after the swing would see 999 + 202 missing (382).
        => Assert.InRange(Swing(Run(CollectorOnly(), fourthShot: true, missing: 999, maxHp: 10000)), 351, 353);

    private static double Swing(ComboResult r)
        => r.NodeBreakdown.Where(n => n.NodeId.StartsWith("AA_0", StringComparison.Ordinal)).Sum(n => n.Damage);

    private ComboResult Run(ActivePlayerStats stats, bool fourthShot, double missing, double maxHp = 1000)
    {
        ChampionRepository.Initialize(new[] { LoadJhinFromBin(), Dummy(maxHp) });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 1, PlayerCount = 2, Stats = stats };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Jhin"; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 1;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 1;

        using var tracker = new TargetHealthTracker();
        if (missing > 0) tracker.RecordDamageDealt("Target", missing);
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Jhin", "g");
        editor.AddNode(draft.Id, new ComboNode("AA_0", ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0)
            { UserConditionMet = fourthShot });
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

    private static ChampionData Dummy(double maxHp) => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = maxHp, Armor = 0, Mr = 0 }, StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ChampionData LoadJhinFromBin()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "jhin.bin.json"));
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in ChampionBinParser.ParseChampion("Jhin", json))
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Jhin" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations, EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Jhin", Name = "Jhin", Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = 59 }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
