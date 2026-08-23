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
/// (loop 539) Curated AD-buff slots (<see cref="SkillDamageDb.GetAdBuffDataValue"/>): casting
/// Aatrox R (World Ender, RTotalADAmp 0.2/0.3/0.4) or Riven R (Blade of the Exile, PercentBonusAD
/// 0.2) raises the working AD for the buff node and everything after it, so a combo containing the
/// ultimate reads higher than one without — the user-reported gap this closes. Driven end to end
/// through <see cref="ComboRunner"/> like <see cref="SylasEmpoweredAutoTests"/>.
///
/// <para>Method: the fallback defender has 0 armour/MR, so totals are raw sums. An auto-attack node
/// contributes exactly the working AttackDamage (plus any AD-independent P rider, which cancels in
/// the difference-of-differences below), which makes the buff fraction directly observable.</para>
/// </summary>
public class AdBuffComboTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public AdBuffComboTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "AdBuffComboTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Aatrox"), LoadChampionFromBin("Riven") });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void AatroxR_BuffsTheAutoAfterIt_ByRTotalADAmp()
    {
        const double ad = 200;
        double r = RunCombo("Aatrox", ad, new[] { "R" });
        double ra = RunCombo("Aatrox", ad, new[] { "R", "A" });
        double a = RunCombo("Aatrox", ad, new[] { "A" });
        // The auto after R deals AD×(1+0.4) (R rank 3 → RTotalADAmp=0.4); the bare auto deals AD.
        // Aatrox's P on-hit rider is %maxHP (AD-independent) and rides both autos, so it cancels in
        // the difference of differences, leaving exactly the buff's own contribution.
        AssertClose(ad * 0.4, (ra - r) - a, "World Ender AD amp on the following auto");
    }

    [Fact]
    public void AatroxR_AppliesOncePerCombo_NotPerCast()
    {
        const double ad = 200;
        double rr = RunCombo("Aatrox", ad, new[] { "R", "R" });
        double rra = RunCombo("Aatrox", ad, new[] { "R", "R", "A" });
        double a = RunCombo("Aatrox", ad, new[] { "A" });
        // A second R refreshes World Ender in game — it does not stack. The auto after R R is still
        // AD×1.4, not AD×1.96.
        AssertClose(ad * 0.4, (rra - rr) - a, "second R must not stack the amp");
    }

    [Fact]
    public void RivenR_BuffsItsOwnWindSlashHit()
    {
        const double ad = 100;
        double r = RunCombo("Riven", ad, new[] { "R" });
        // The R node is curated as Wind Slash (MinDamage = MinBase[rank 3]=200 + 0.55×bonusAD — the
        // coefficient read from the live BIN's StatByCoefficientCalculationPart) and the blade's
        // +20% AD is active when it is thrown, so bonusAD is the BUFFED 120, not 100. (The harness
        // champion has zero base AD, so bonusAD == the working AttackDamage.)
        AssertClose(200 + 0.55 * (ad * 1.2), r, "Wind Slash thrown with the empowered blade");
    }

    [Fact]
    public void RivenR_BuffsTheAbilitiesAfterIt()
    {
        const double ad = 100;
        double q = RunCombo("Riven", ad, new[] { "Q" });
        double rq = RunCombo("Riven", ad, new[] { "R", "Q" });
        double r = RunCombo("Riven", ad, new[] { "R" });
        // Q's AD-ratio casts after R must resolve against the buffed AD, so Q-after-R exceeds bare Q.
        Assert.True(rq - r > q + 1,
            $"Q after R ({rq - r:0.##}) must exceed bare Q ({q:0.##}) via the +20% AD buff");
    }

    // ── harness (mirrors SylasEmpoweredAutoTests) ────────────────────────────────

    private double RunCombo(string championId, double ad, string[] slots)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 16, PlayerCount = 1 };
        snap.Stats = new ActivePlayerStats
        {
            AttackDamage = ad, AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = championId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 16;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(championId, "c");
        foreach (var s in slots)
            editor.AddNode(draft.Id, s == "A"
                ? new ComboNode("A_" + Guid.NewGuid().ToString("N"), ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0)
                : new ComboNode(s + "_" + Guid.NewGuid().ToString("N"), ComboNodeType.Skill, s, 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0));
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
