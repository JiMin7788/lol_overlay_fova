using System.IO;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // disambiguate from Overlay.Core.Damage.ComboNode

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 536) Sylas Petricite Burst — a basic attack that FOLLOWS a spell cast in the combo is
/// automatically swapped for the empowered attack: MAGIC <c>PassiveDamage</c> (1.3 × total AD + 0.3
/// × AP) replaces the physical total-AD auto. Driven end to end through <see cref="ComboRunner"/> so
/// it exercises the real node rewrite, not just <see cref="SkillDamage.ComputeCalcDamage"/>.
///
/// <para>Method: with no enemy on board the fallback defender has 0 armour/MR, so nothing is
/// mitigated and the combo total is the raw sum. Adding one auto to a combo therefore adds exactly
/// that auto's own damage — physical AD when unarmed, the P value when a spell armed it — which is
/// what each case measures as a difference against the same combo without the trailing auto.</para>
/// </summary>
public class SylasEmpoweredAutoTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public SylasEmpoweredAutoTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "SylasEmpoweredAutoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Sylas") });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private const double AD = 200, AP = 100;
    private const double Passive = 1.3 * AD + 0.3 * AP;   // 290 — empowered auto (magic)

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = AD, AbilityPower = AP, AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
    };

    [Fact]
    public void AutoAfterSpell_DealsTheEmpoweredMagicValue_NotPhysicalAD()
    {
        double q = RunCombo(new[] { "Q" });
        double qa = RunCombo(new[] { "Q", "A" });
        // The added auto follows a cast, so it is the empowered attack: qa − q ≈ PassiveDamage (290),
        // NOT the plain physical AD (200).
        AssertClose(Passive, qa - q, "Q→A swapped auto");
        Assert.True(qa - q > AD + 1, "the post-spell auto must exceed a physical AD auto");
    }

    [Fact]
    public void AutoWithNoPriorSpell_IsAPlainPhysicalAttack()
    {
        double a = RunCombo(new[] { "A" });
        AssertClose(AD, a, "bare auto is physical AD");
    }

    [Fact]
    public void OnlyOneAutoIsEmpoweredPerCast()
    {
        double qa = RunCombo(new[] { "Q", "A" });
        double qaa = RunCombo(new[] { "Q", "A", "A" });
        // The second auto has no fresh cast before it, so it is a normal physical attack: the
        // difference between Q A A and Q A is one physical AD auto, not another empowered one.
        AssertClose(AD, qaa - qa, "second auto after a single cast");
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private double RunCombo(string[] slots)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 1, Stats = Stats() };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = "Sylas";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Sylas", "c");
        foreach (var s in slots)
            editor.AddNode(draft.Id, s == "A"
                ? new ComboNode("A_" + Guid.NewGuid().ToString("N"), ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0)
                : new ComboNode(s + "_" + Guid.NewGuid().ToString("N"), ComboNodeType.Skill, s, 0, 0, 0, ComboDamageType.Magic, 1.0, 0, 0, 0, 0, 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!.TotalDamage;
    }

    private static void AssertClose(double expected, double actual, string what)
        => Assert.True(System.Math.Abs(actual - expected) <= 1.0,
            $"{what}: expected {expected} ±1, got {actual:0.##} (Δ={actual - expected:0.##})");

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
