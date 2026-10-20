using System.IO;
using System.Linq;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 44) Jhin's fourth shot crits for sure when its box is ticked, and every Jhin crit deals CritReductionPercent
/// (25%) less - (loop 45) which the live crit-damage stat already carries, so only the unreported 2.0 constant is reduced;
/// Brand R's bolt comes back from Brand to the same target on the count knob (up to two more), none unset.
/// </summary>
public class M45Loop44CritAndBounceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public M45Loop44CritAndBounceTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L44_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static ActivePlayerStats Stats(double critChance) => new()
    {
        AttackDamage = 200, AbilityPower = 100, AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 2,
        CritChance = critChance, CritDamage = 1.75,
    };

    [Fact]
    public void JhinFourthShot_CritsForSure_AtTheLiveMultiplier()
    {
        // (M45 loop 45) the live crit damage already carries Jhin's 25% (GoldenJhinTests): 200 x 1.75, not x 0.75 again
        Assert.Equal(350, Swing(Run("Jhin", Stats(0), Aa(met: true))), 3);
        Assert.Equal(200, Swing(Run("Jhin", Stats(0), Aa(met: false))), 3); // unticked: no crit chance, no crit
    }

    [Fact]
    public void JhinCrit_UnreportedStat_ReducesTheConstant()
    {
        // the 2.0 constant does not carry it: half the time a crit of 2.0 x 0.75
        var unreported = Stats(0.5) with { CritDamage = 0 };
        Assert.Equal(200 * (1 + 0.5 * (2.0 * 0.75 - 1)), Swing(Run("Jhin", unreported, Aa(met: false))), 3);
        Assert.Equal(200 * (1 + 0.5 * 0.75), Swing(Run("Jhin", Stats(0.5), Aa(met: false))), 3); // reported: used as is
    }

    [Fact]
    public void AnotherChampionsCrit_IsUntouched()
        => Assert.Equal(200 * (1 + 0.5 * 0.75), Swing(Run("Garen", Stats(0.5), Aa(met: false))), 3);

    [Fact]
    public void BrandR_BouncesBack_OnTheCountKnob_NoneUnset()
    {
        double once = Run("Brand", Stats(0), R(null)).TotalDamage;
        Assert.True(once > 0);
        Assert.Equal(3 * once, Run("Brand", Stats(0), R(2)).TotalDamage, 3);
        Assert.Equal(3 * once, Run("Brand", Stats(0), R(9)).TotalDamage, 3); // capped at the two that come back
    }

    private static ComboNode Aa(bool met)
        => new("AA_0", ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0) { UserConditionMet = met };

    private static ComboNode R(int? count)
        => new("R_0", ComboNodeType.Skill, "R", 0, 0, 0, ComboDamageType.Magic, 0, 0, 0, 0, 0, 0) { UserAttackCount = count };

    private static double Swing(ComboResult r) => r.NodeBreakdown.Single(n => n.NodeId == "AA_0").Damage;

    private ComboResult Run(string champ, ActivePlayerStats stats, params ComboNode[] nodes)
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin(champ) });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 1, Stats = stats };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champ; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 11;
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "t");
        foreach (var node in nodes)
            editor.AddNode(draft.Id, node);
        editor.SaveCombo(draft.Id);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!;
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
            BaseStats = new ChampionBaseStats { Ad = 60 }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
