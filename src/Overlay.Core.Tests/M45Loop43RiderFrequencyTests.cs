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
/// (M45 loop 43) How many attacks a rider rides, counted by RUNNING combos: a "next Attack" rider once per its cast, a
/// cooldown / once-per-target passive once in a burst, an after-any-ability rider for its attacks per arm, a charge per
/// cast spent one per attack, a mark another cast applies spent by the next ability, and the states the combo cannot know
/// left to the user's "+fx" attach. Each of these rode every attack (or every cast) before.
/// </summary>
public class M45Loop43RiderFrequencyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public M45Loop43RiderFrequencyTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L43_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly ActivePlayerStats Stats = new()
    {
        AttackDamage = 200, AbilityPower = 100, AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 2,
    };

    [Theory]
    [InlineData("Jayce", "R")]
    [InlineData("Viktor", "Q")]
    [InlineData("Rell", "W")]
    public void NextAttackRider_RidesOneAttackAfterItsCast(string champ, string slot)
    {
        // one bonus row more on the attack right after the cast than on the ones after it (Rell's passive rides them all)
        var r = Run(champ, slot, "AA", "AA", "AA");
        Assert.Equal(Rows(r, "AA_2") + 1, Rows(r, "AA_1"));
        Assert.Equal(Rows(r, "AA_3"), Rows(r, "AA_2"));
    }

    private static int Rows(ComboResult r, string node) => r.NodeBreakdown.Count(n => n.NodeId.StartsWith(node + "#bonus"));

    [Theory]
    [InlineData("Poppy")]
    [InlineData("Ziggs")]
    [InlineData("Nocturne")]
    [InlineData("Aatrox")]
    [InlineData("Urgot")]
    [InlineData("JarvanIV")]
    [InlineData("Nautilus")]
    [InlineData("MissFortune")]
    [InlineData("Khazix")]
    [InlineData("Galio")]
    public void CooldownPassive_ProcsOnceInABurst(string champ)
        => Assert.Equal(new[] { "AA_0" }, Bonuses(Run(champ, "AA", "AA", "AA")));

    [Fact]
    public void GalioP_RidesTheAttack_NotTheCast()
        => Assert.Equal(new[] { "AA_1" }, Bonuses(Run("Galio", "Q", "AA")));

    [Fact]
    public void ZoeP_TheAttackAfterAnAbility()
        => Assert.Equal(new[] { "AA_2" }, Bonuses(Run("Zoe", "AA", "Q", "AA", "AA")));

    [Fact]
    public void TaricP_TwoAttacksPerCast_ReArmedByTheNext()
    {
        Assert.Equal(new[] { "AA_2", "AA_3" }, Bonuses(Run("Taric", "AA", "E", "AA", "AA", "AA")));
        // E arms two; one is spent, then Q re-arms two
        Assert.Equal(new[] { "AA_1", "AA_3", "AA_4" }, Bonuses(Run("Taric", "E", "AA", "Q", "AA", "AA", "AA")));
    }

    [Fact]
    public void NamiE_ThreeAttacks()
        => Assert.Equal(new[] { "AA_1", "AA_2", "AA_3" }, Bonuses(Run("Nami", "E", "AA", "AA", "AA", "AA", "AA")));

    [Fact]
    public void RivenP_ACastStoresACharge_EachAttackSpendsOne_UpToThree()
    {
        Assert.Equal(new[] { "AA_2" }, Bonuses(Run("Riven", "AA", "W", "AA", "AA")));
        // Q folds three casts (castCount 3): three charges
        Assert.Equal(new[] { "AA_1", "AA_2", "AA_3" }, Bonuses(Run("Riven", "Q", "AA", "AA", "AA", "AA")));
        // four stored, capped at Charges (3)
        Assert.Equal(new[] { "AA_2", "AA_3", "AA_4" }, Bonuses(Run("Riven", "Q", "W", "AA", "AA", "AA", "AA")));
    }

    [Fact]
    public void AmbessaP_OneChargePerCast()
        => Assert.Equal(new[] { "AA_1" }, Bonuses(Run("Ambessa", "E", "AA", "AA")));

    [Fact]
    public void SettP_EverySecondAttackIsTheRightPunch()
        => Assert.Equal(new[] { "AA_1", "AA_3" }, Bonuses(Run("Sett", "AA", "AA", "AA", "AA")));

    [Fact]
    public void KassadinW_PassiveRidesAttacks_NotCasts()
    {
        Assert.Equal(new[] { "AA_0", "AA_1" }, Bonuses(Run("Kassadin", "AA", "AA")));
        Assert.Empty(Bonuses(Run("Kassadin", "Q")));
    }

    [Theory]
    [InlineData("Pantheon", "Q")]
    [InlineData("Pantheon", "W")]
    [InlineData("Katarina", "Q")]
    [InlineData("Katarina", "E")]
    public void UnknowableState_RidesOnlyWhereTheUserAttachesIt(string champ, string slot)
    {
        Assert.Empty(Bonuses(Run(champ, slot, "AA")));
        var attachable = SkillDamageDb.GetAttachableBonusEffects(champ, slot);
        Assert.NotEmpty(attachable);
        var attached = SkillNode(slot, 0) with { UserBonusEffects = attachable };
        Assert.Equal(new[] { $"{slot}_0" }, Bonuses(RunNodes(champ, attached)));
    }

    [Fact]
    public void OrnnW_Brittle_SpentByTheNextImmobilizingCast()
    {
        Assert.Equal(new[] { "E_3" }, Bonuses(Run("Ornn", "Q", "W", "Q", "E", "R")));
        Assert.Empty(Bonuses(Run("Ornn", "E", "R")));  // nothing applied it
    }

    [Fact]
    public void VexP_Gloom_ArmedByE_SpentByTheNextQOrW()
    {
        Assert.Equal(new[] { "Q_2" }, Bonuses(Run("Vex", "Q", "E", "Q", "W")));
        Assert.Empty(Bonuses(Run("Vex", "Q", "W")));
    }

    [Theory]
    [InlineData("RekSai", "QUnburrowed", "TotalDamageTooltip")]
    [InlineData("Skarner", "Q", "AbilityDamage")]
    public void NextThreeAttacks_DirectHitCountsThree(string champ, string slot, string calc)
        => Assert.Equal(3, SkillDamageDb.GetHits(champ, slot)!.Single(h => h.Calc == calc).Count);

    /// <summary>The nodes that carried a bonus row, in order (one entry per node).</summary>
    private static string[] Bonuses(ComboResult r)
        => r.NodeBreakdown.Where(n => n.NodeId.Contains("#bonus")).Select(n => n.NodeId[..n.NodeId.IndexOf('#')]).Distinct().ToArray();

    private static ComboNode SkillNode(string slot, int i)
    {
        bool aa = slot == "AA";
        return new ComboNode($"{slot}_{i}", aa ? ComboNodeType.Aa : ComboNodeType.Skill, slot,
            0, 0, 0, aa ? ComboDamageType.Physical : ComboDamageType.Magic, aa ? 1.0 : 0, 0, 0, 0, 0, 0);
    }

    private ComboResult Run(string champ, params string[] slots)
        => RunNodes(champ, slots.Select(SkillNode).ToArray());

    private ComboResult RunNodes(string champ, params ComboNode[] nodes)
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin(champ) });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 1, Stats = Stats };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champ; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 11;
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "t" + string.Join("", nodes.Select(n => n.Id)));
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
