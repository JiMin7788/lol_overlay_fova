using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 48) Item effects the user named, from the live items BIN (16.20): Bastionbreaker's Shaped Charge on the first
/// ability, Muramana's Shock on attacks and on every cast, Titanic Hydra's active at the item chip, Voltaic Cyclosword's
/// energized attack after a damaging cast and the lethality it grants after.
/// </summary>
public class M45Loop48ItemEffectsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public M45Loop48ItemEffectsTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L48_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Bastionbreaker_OnTheFirstAbility_Once_HalfForRanged()
    {
        // 50 + 1.5 x lethality true - the item's own 22; once in 20 s
        var melee = Run("Garen", ranged: false, items: new[] { 2520 }, Skill("Q", 0), Skill("Q", 1));
        Assert.Equal(50 + 1.5 * 22, Item(melee, "2520"), 3);
        Assert.Equal(1, melee.NodeBreakdown.Count(n => n.NodeId.EndsWith("#item2520", StringComparison.Ordinal)));
        Assert.Equal((50 + 1.5 * 22) * 0.5, Item(Run("Ezreal", ranged: true, items: new[] { 2520 }, Skill("Q", 0)), "2520"), 3);
    }

    [Fact]
    public void Bastionbreaker_ReadsTheLiveLethality_RunesIncluded()
        // (M45 loop 49) the game's physicalLethality (items, runes, buffs) over the item data's 22
        => Assert.Equal(50 + 1.5 * 40, Item(Run("Garen", ranged: false, items: new[] { 2520 }, new[] { Skill("Q", 0) }, 0, lethality: 40), "2520"), 3);

    [Fact]
    public void Muramana_OnAttacks_AndOnEveryCast()
    {
        // 1000 max mana: 1.2% on an attack, 4% (melee) / 3% (ranged) on each damaging cast
        var garen = Run("Garen", ranged: false, items: new[] { 3042 }, Aa(0), Skill("Q", 1), Skill("E", 2));
        Assert.Equal(12, Item(garen, "3042"), 3);
        Assert.Equal(2 * 40, Item(garen, "3042#ability"), 3);
        Assert.Equal(30, Item(Run("Ezreal", ranged: true, items: new[] { 3042 }, Skill("Q", 0)), "3042#ability"), 3);
    }

    [Fact]
    public void TitanicHydraActive_AtTheChip_FourPercentOfMaxHealth()
    {
        Assert.Equal(80, Item(Run("Garen", ranged: false, items: new[] { 3748 }, Chip(0)), "3748#active"), 3);
        Assert.Equal(40, Item(Run("Ezreal", ranged: true, items: new[] { 3748 }, Chip(0)), "3748#active"), 3);
    }

    [Fact]
    public void Voltaic_TheAttackAfterACast_ThenItsLethality()
    {
        var r = Run("Ezreal", ranged: true, items: new[] { 6699 }, Skill("Q", 0), Aa(1), Aa(2), armor: 100);
        double afterQ = 5000 - r.NodeBreakdown.Where(n => n.NodeId.StartsWith("Q_0", StringComparison.Ordinal)).Sum(n => n.Damage);
        // ranged 7% of the health before the attack, through 100 armor less the item's own 10 lethality and (M45 loop 50)
        // the 12 the energized attack grants - on that attack already (the user measured it: 250 against 30 armor)
        Assert.Equal(0.07 * afterQ * 100.0 / 178, Item(r, "6699"), 1);
        Assert.Equal(1, r.NodeBreakdown.Count(n => n.NodeId.EndsWith("#item6699", StringComparison.Ordinal))); // one per cast
        Assert.Equal(100 * 100.0 / 178, Swing(r, "AA_1"), 1);             // the energized attack itself: 10 + 12
        Assert.Equal(100 * 100.0 / 178, Swing(r, "AA_2"), 1);             // and after it
        // no damaging cast before the attack: none
        Assert.Equal(0, Item(Run("Ezreal", ranged: true, items: new[] { 6699 }, Aa(0)), "6699"), 3);
    }

    private static double Item(ComboResult r, string key)
        => r.NodeBreakdown.Where(n => n.NodeId.EndsWith($"#item{key}", StringComparison.Ordinal)).Sum(n => n.Damage);

    private static double Swing(ComboResult r, string id) => r.NodeBreakdown.Single(n => n.NodeId == id).Damage;

    private static ComboNode Skill(string slot, int i)
        => new($"{slot}_{i}", ComboNodeType.Skill, slot, 0, 0, 0, ComboDamageType.Physical, 0, 0, 0, 0, 0, 0);

    private static ComboNode Aa(int i)
        => new($"AA_{i}", ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0);

    private static ComboNode Chip(int i)
        => new(ComboEditor.ItemsNodeId + "_" + i, ComboNodeType.Summoner, ComboEditor.ItemsNodeId, 0, 0, 0,
            ComboDamageType.Physical, 0, 0, 0, 0, 0, 0);

    private ComboResult Run(string champ, bool ranged, int[] items, params ComboNode[] nodes) => Run(champ, ranged, items, nodes, 0);

    private ComboResult Run(string champ, bool ranged, int[] items, ComboNode a, ComboNode b, ComboNode c, double armor)
        => Run(champ, ranged, items, new[] { a, b, c }, armor);

    private ComboResult Run(string champ, bool ranged, int[] items, ComboNode[] nodes, double armor, double lethality = 0)
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin(champ, ranged ? 550 : 175),
            new ChampionData { Id = "Target", Name = "Target",
                BaseStats = new ChampionBaseStats { Hp = 5000, Armor = armor, Mr = 0 }, StatsPerLevel = new ChampionStatsPerLevel() },
        });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 100, MaxHealth = 2000, ResourceMax = 1000, PhysicalLethality = lethality,
                AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 2 } };
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
