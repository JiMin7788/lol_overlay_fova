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
/// (M45 loop 42) The clauses the measured goldens were thought to pin, each restated so the golden's own scenario reads
/// the same number (Hwei R, Ivern R, Varus W, Gnar W), the every-Nth passives that rode every attack, and the two
/// numbers no BIN value carries (Nidalee's Hunted Takedown, Briar E's charge).
/// </summary>
public class M45Loop42StructureTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public M45Loop42StructureTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L42_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly ActivePlayerStats Stats = new()
    {
        AttackDamage = 200, AbilityPower = 100, AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 2,
    };

    private static double Resolve(string champ, string slot, SkillHit hit, bool? met = null, double? seconds = null,
        int stacks = 0, int? attacks = null, double? charge = null)
    {
        Assert.True(ComboRunner.ResolveHitForAudit(hit, LoadChampionFromBin(champ), slot, Stats, 11, 1000.0,
            seconds, met, stacks, allOut: false, out double damage, out _, out _, attackCount: attacks, distanceFraction: charge));
        return damage;
    }

    [Fact]
    public void HweiR_StuckDotPlusShatter_IsTotalMaxDamage()
    {
        var hits = SkillDamageDb.GetHits("Hwei", "R")!;
        double dot = Resolve("Hwei", "R", hits[0]);   // unset knob: the guaranteed 3 s the vision sticks
        double shatter = Resolve("Hwei", "R", hits[1]);
        double total = SkillDamage.ComputeCalcDamage(LoadChampionFromBin("Hwei"), "R", "TotalMaxDamage", Stats, 11)!.Value;
        Assert.Equal(total, dot + shatter, 4); // float coefficients: 0.95f vs 0.8f + 3 x 0.05f
        Assert.Equal(dot / 3.0, Resolve("Hwei", "R", hits[0], seconds: 1.0), 6); // the knob can still lower it
    }

    [Fact]
    public void IvernR_Shockwave_OnlyTickedAndOnTheThirdAttack()
    {
        var wave = SkillDamageDb.GetHits("Ivern", "R")!.Single(h => h.LandsOnNthAttack == 3);
        double one = SkillDamage.ComputeCalcDamage(LoadChampionFromBin("Ivern"), "R", "TotalShockwaveDamage", Stats, 11)!.Value;
        Assert.Equal(0, Resolve("Ivern", "R", wave, met: null, attacks: 3));  // unticked: none (the golden's 3 x one)
        Assert.Equal(0, Resolve("Ivern", "R", wave, met: true, attacks: 2));  // not yet the third
        Assert.Equal(one, Resolve("Ivern", "R", wave, met: true, attacks: 3), 6);
        Assert.Equal(one, Resolve("Ivern", "R", wave, met: true, attacks: 9), 6); // a second one is not counted (P2)
        Assert.NotNull(SkillDamageDb.GetConditionalHit("Ivern", "R")); // the box is offered on the R node
        Assert.True(ConditionResolution.IsUserAssumed(ConditionType.Consecutive)); // RECURRING_TRAPS 1.3
    }

    [Fact]
    public void VarusBlight_IsPerStack_UnsetReadsTheCap()
    {
        var blight = SkillDamageDb.GetBonusEffects("Varus", "W")!.SelectMany(b => b.Hits).Single(h => h.PerStack);
        double one = Resolve("Varus", "W", blight, stacks: 1);
        Assert.True(one > 0);
        Assert.Equal(2 * one, Resolve("Varus", "W", blight, stacks: 2), 6);
        Assert.Equal(3 * one, Resolve("Varus", "W", blight, stacks: 0), 6); // unset = MaxStacks
        Assert.Equal(3 * one, Resolve("Varus", "W", blight, stacks: 7), 6); // capped
        Assert.NotNull(SkillDamageDb.GetStackScaledHit("Varus", "E"));     // the knob rides the detonating ability
    }

    [Fact]
    public void ViW_ProcsEveryThirdHit_VaultBreakerCounts()
    {
        var bonus = Bonuses(RunResult("Vi", "AA", "AA", "AA", "AA", "AA", "AA"));
        Assert.Equal(new[] { "AA_2", "AA_5" }, bonus);
        // Vault Breaker applies a stack: Q, AA, AA procs on the second attack
        Assert.Equal(new[] { "AA_2" }, Bonuses(RunResult("Vi", "Q", "AA", "AA")));
    }

    [Theory]
    [InlineData("Kennen", 5)]
    [InlineData("TwistedFate", 4)]
    [InlineData("Diana", 3)]
    [InlineData("XinZhao", 3)]
    [InlineData("Neeko", 3)]
    [InlineData("Jax", 3)]
    public void EveryNthPassive_RidesOnlyItsNthAttack(string champ, int n)
    {
        var bonus = Bonuses(RunResult(champ, Enumerable.Repeat("AA", n).ToArray()));
        Assert.Equal(new[] { $"AA_{n - 1}" }, bonus);
    }

    [Fact]
    public void EkkoP_AbilitiesCountToo_AndCarryTheProc()
    {
        Assert.Equal(new[] { "Q_2" }, Bonuses(RunResult("Ekko", "AA", "AA", "Q")));
    }

    [Fact]
    public void GnarW_AutoCadence_ManualNodeAndMegaCastStopIt()
    {
        Assert.Equal(new[] { "AA_2" }, Bonuses(RunResult("Gnar", "AA", "AA", "AA")));
        Assert.Equal(new[] { "E_2" }, Bonuses(RunResult("Gnar", "Q", "AA", "E")));      // Mini casts count
        Assert.Empty(Bonuses(RunResult("Gnar", "AA", "AA", "AA", "W")));               // the W node states the proc itself
        Assert.Empty(Bonuses(RunResult("Gnar", "QMega", "AA", "AA", "AA")));          // Mega: Mini Gnar's passive is off
    }

    [Fact]
    public void NidaleeTakedown_HuntedIsThirtyPercentMore()
    {
        var hit = SkillDamageDb.GetHits("Nidalee", "QCougar")![0];
        double plain = Resolve("Nidalee", "QCougar", hit, met: null);
        Assert.Equal(plain * 1.3, Resolve("Nidalee", "QCougar", hit, met: true), 6);
        Assert.NotNull(SkillDamageDb.GetConditionalHit("Nidalee", "QCougar"));
    }

    [Fact]
    public void BriarE_IsDamageTimesCharge_UnsetFull()
    {
        var hit = SkillDamageDb.GetHits("Briar", "E")![0];
        double full = Resolve("Briar", "E", hit);
        Assert.Equal(SkillDamage.ComputeCalcDamage(LoadChampionFromBin("Briar"), "E", "Damage", Stats, 11)!.Value, full, 6);
        Assert.Equal(full / 2, Resolve("Briar", "E", hit, charge: 0.5), 6);
        Assert.Equal(full, Resolve("Briar", "E", hit, charge: 1.0), 6);
        Assert.NotNull(SkillDamageDb.GetDistanceScaledHit("Briar", "E")); // the charge slider is offered
    }

    /// <summary>The nodes that carried a bonus row, in order (one entry per node).</summary>
    private static string[] Bonuses(ComboResult r)
        => r.NodeBreakdown.Where(n => n.NodeId.Contains("#bonus")).Select(n => n.NodeId[..n.NodeId.IndexOf('#')]).Distinct().ToArray();

    private ComboResult RunResult(string champ, params string[] slots)
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin(champ) });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 1, Stats = Stats };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champ; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 11;
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "t" + string.Join("", slots));
        int i = 0;
        foreach (var slot in slots)
        {
            bool aa = slot == "AA";
            editor.AddNode(draft.Id, new ComboNode($"{slot}_{i++}", aa ? ComboNodeType.Aa : ComboNodeType.Skill, slot,
                0, 0, 0, aa ? ComboDamageType.Physical : ComboDamageType.Magic, aa ? 1.0 : 0, 0, 0, 0, 0, 0));
        }
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
