using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Items;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 49) Every remaining Summoner's Rift item that deals or changes damage on the target, from the live items BIN
/// (16.20), by one rule: what the combo can settle applies by itself, the rest fires at the "모든 아이템" chip. A plain
/// attacker (100 AD, 2000 max health of which all is bonus, no skills) and Annie for the ability items, on a 5000-health
/// dummy.
/// </summary>
public class M45Loop49ItemCoverageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public M45Loop49ItemCoverageTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L49_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("3071")] [InlineData("8010")] [InlineData("6676")] [InlineData("2512")] [InlineData("3168")]
    [InlineData("3147")] [InlineData("2523")] [InlineData("2522")] [InlineData("3077")] [InlineData("3068")]
    [InlineData("6664")] [InlineData("6660")] [InlineData("2502")] [InlineData("3050")] [InlineData("3107")]
    [InlineData("1043")] [InlineData("3144")] [InlineData("3145")] [InlineData("2508")] [InlineData("3871")]
    [InlineData("3877")] [InlineData("6699")]
    public void EveryEntryLoads(string id) => Assert.NotEmpty(ItemEffectDb.GetAll(id));

    [Fact]
    public void BlackCleaver_EachPhysicalHit_StacksSixPercentArmor()
    {
        var r = Run(Plain, new[] { 3071 }, armor: 100, nodes: new[] { Aa(0), Aa(1), Aa(2) });
        Assert.InRange(Dmg(r, "AA_0"), 49.99, 50.01);
        Assert.InRange(Dmg(r, "AA_1"), 100 * 100.0 / 194 - 0.01, 100 * 100.0 / 194 + 0.01);
        Assert.InRange(Dmg(r, "AA_2"), 100 * 100.0 / 188 - 0.01, 100 * 100.0 / 188 + 0.01);
    }

    [Fact]
    public void BloodlettersCurse_AMagicAbility_StacksSevenAndAHalfPercentMagicResist()
    {
        var r = Run(Annie, new[] { 8010 }, mr: 100, nodes: new[] { Skill("Q", 0), Skill("Q", 1) });
        Assert.Equal(200 / 192.5, Dmg(r, "Q_1") / Dmg(r, "Q_0"), 2);
    }

    [Fact]
    public void Collector_ExecutesBelowFivePercent_TheKillThresholdToo()
    {
        Assert.Equal(100, Run(Plain, Array.Empty<int>(), hp: 1000, nodes: new[] { Aa(0) }).KillThresholdHP, 1);
        Assert.Equal(100 + 50, Run(Plain, new[] { 6676 }, hp: 1000, nodes: new[] { Aa(0) }).KillThresholdHP, 1);
    }

    [Fact]
    public void FiendhunterBolts_AnAttackAfterTheUltimate_CritsForEightyPercent()
    {
        // 50% crit chance, 200%: half 80% of a crit (180), half a crit of its own with (M45 loop 50) 15% more as true
        // (200 x 1.15, measured 347 + 52) -> 205; without the ultimate 150
        var r = Run(Annie, new[] { 2512 }, crit: 0.5, nodes: new[] { Skill("R", 0), Aa(1), Aa(2), Aa(3), Aa(4) });
        Assert.Equal(205, Dmg(r, "AA_1"), 1);
        Assert.Equal(205, Dmg(r, "AA_3"), 1);
        Assert.Equal(150, Dmg(r, "AA_4"), 1); // only three
    }

    [Fact]
    public void ImmortalPath_FourPercent_AboveHalfHealth()
    {
        Assert.Equal(104, Run(Plain, new[] { 3168 }, currentHealth: 1500, nodes: new[] { Aa(0) }).TotalDamage, 1);
        Assert.Equal(100, Run(Plain, new[] { 3168 }, currentHealth: 800, nodes: new[] { Aa(0) }).TotalDamage, 1);
    }

    [Fact]
    public void ChipAmps_HauntingGuise_Hexoptics_Actualizer()
    {
        Assert.Equal(106, Dmg(Run(Plain, new[] { 3147 }, nodes: new[] { Chip(0), Aa(1) }), "AA_1"), 1);
        Assert.Equal(110, Dmg(Run(Plain, new[] { 2523 }, nodes: new[] { Chip(0), Aa(1) }), "AA_1"), 1);
        var annie = Run(Annie, new[] { 2522 }, nodes: new[] { Skill("Q", 0), Chip(1), Skill("Q", 2) });
        Assert.Equal(1.15, Dmg(annie, "Q_2") / Dmg(annie, "Q_0"), 2);
    }

    [Theory]
    [InlineData(3077, 75.0)]          // Tiamat: 75% AD
    [InlineData(3068, 3 * 50.0)]      // Sunfire: 3 s x (20 + 1.5% of 2000 bonus health)
    [InlineData(6664, 3 * 35.0)]      // Hollow Radiance: 3 s x (15 + 1%)
    [InlineData(6660, 3 * 15.0)]      // Bami's Cinder: 3 s x 15
    [InlineData(2502, 60.0)]          // Unending Despair: 3% bonus health
    [InlineData(3050, 5 * 30.0)]      // Zeke's: 5 s x 30
    [InlineData(3107, 500.0)]         // Redemption: 10% of the dummy's 5000, true
    public void ChipDamage_FiresOnce(int item, double added)
        => Assert.Equal(added, Run(Plain, new[] { item }, nodes: new[] { Chip(0) }).TotalDamage, 1);

    [Fact]
    public void RecurveBow_FifteenOnHit() => Assert.Equal(115, Run(Plain, new[] { 1043 }, nodes: new[] { Aa(0) }).TotalDamage, 1);

    [Fact]
    public void FirstDamage_Slingshot_Alternator_Once()
    {
        Assert.Equal(200 + 40, Run(Plain, new[] { 3144 }, nodes: new[] { Aa(0), Aa(1) }).TotalDamage, 1);
        var annie = Run(Annie, new[] { 3145 }, nodes: new[] { Skill("Q", 0), Skill("Q", 1) });
        Assert.Equal(65, Item(annie, "3145"), 1);
    }

    [Fact]
    public void FirstAbility_FatedAshes_ZazZaks()
    {
        Assert.Equal(15, Item(Run(Annie, new[] { 2508 }, nodes: new[] { Skill("Q", 0), Skill("Q", 1) }), "2508"), 1);
        var z = Run(Annie, new[] { 3871 }, nodes: new[] { Skill("Q", 0) });
        Assert.Equal(10 + 0.15 * 100, Item(z, "3871"), 1);
        Assert.Equal(0.03 * 5000, Item(z, "3871#hp"), 1);
    }

    [Fact]
    public void Bloodsong_Spellblade_ThenMaim()
    {
        // Annie (ranged): the spellblade is 100% base AD (60), then the target takes 5% more
        var r = Run(Annie, new[] { 3877 }, nodes: new[] { Skill("Q", 0), Aa(1), Aa(2) });
        Assert.Equal(60, Item(r, "3877"), 1);
        Assert.Equal(105, Dmg(r, "AA_2"), 1);
    }

    private const string Plain = "Plain";
    private const string Annie = "Annie";

    // a node's own hits (a cast resolves to "Q_0#cast0h0c0"), not the item procs riding it
    private static double Dmg(ComboResult r, string id)
        => r.NodeBreakdown.Where(n => (n.NodeId == id || n.NodeId.StartsWith(id + "#", StringComparison.Ordinal))
                                      && !n.NodeId.Contains("#item", StringComparison.Ordinal)).Sum(n => n.Damage);

    private static double Item(ComboResult r, string key)
        => r.NodeBreakdown.Where(n => n.NodeId.EndsWith($"#item{key}", StringComparison.Ordinal)).Sum(n => n.Damage);

    private static ComboNode Skill(string slot, int i)
        => new($"{slot}_{i}", ComboNodeType.Skill, slot, 0, 0, 0, ComboDamageType.Magic, 0, 0, 0, 0, 0, 0);

    private static ComboNode Aa(int i)
        => new($"AA_{i}", ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0);

    private static ComboNode Chip(int i)
        => new(ComboEditor.ItemsNodeId + "_" + i, ComboNodeType.Summoner, ComboEditor.ItemsNodeId, 0, 0, 0,
            ComboDamageType.Physical, 0, 0, 0, 0, 0, 0);

    private ComboResult Run(string champ, int[] items, ComboNode[] nodes, double armor = 0, double mr = 0, double hp = 5000,
        double crit = 0, double currentHealth = 2000)
    {
        ChampionRepository.Initialize(new[]
        {
            champ == Annie ? LoadChampionFromBin("Annie", 625)
                : new ChampionData { Id = Plain, Name = Plain, Skills = new Dictionary<string, SkillData>(),
                    BaseStats = new ChampionBaseStats { Ad = 60, AttackRange = 175 }, StatsPerLevel = new ChampionStatsPerLevel() },
            new ChampionData { Id = "Target", Name = "Target",
                BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr }, StatsPerLevel = new ChampionStatsPerLevel() },
        });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100, MaxHealth = 2000, CurrentHealth = currentHealth,
                CritChance = crit, CritDamage = 2.0, AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 2 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champ; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 11;
        for (int i = 0; i < items.Length; i++) snap.Players[0].ItemIds[i] = items[i];
        snap.Players[0].ItemCount = items.Length;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 11;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "t");
        foreach (var node in nodes) editor.AddNode(draft.Id, node);
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

    private static ChampionData LoadChampionFromBin(string championId, double attackRange)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{championId.ToLowerInvariant()}.bin.json"));
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in ChampionBinParser.ParseChampion(championId, json))
            skills[slot] = new SkillData
            {
                Key = slot, Name = championId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations, EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = championId, Name = championId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = 60, AttackRange = attackRange }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
