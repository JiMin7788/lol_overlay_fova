using System.Text.Json;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Items;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 50) The user's Practice Tool rows (2026-10-20) and the bonus stats behind them: Fiendhunter Bolts' true damage
/// on an attack that crits by itself (a 347 crit, 52 true), lethality that stops at 0 armor (the Voltaic rows are
/// GoldenVoltaicKatarinaTests), bonus mana
/// (Ryze, Actualizer) and the base move speed / mana the Data Dragon summary path never parsed.
/// </summary>
public class M45Loop50BonusStatsTests : IDisposable
{
    private static readonly string DDragonDir = Path.Combine(AppContext.BaseDirectory, "data", "ddragon", "16.13.1");
    private static readonly string CDragonDir = Path.Combine(AppContext.BaseDirectory, "data", "communitydragon");
    private readonly string _dir;
    private readonly string _configPath;

    public M45Loop50BonusStatsTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L50_" + Guid.NewGuid().ToString("N"));
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

    // ── measured rows ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FiendhunterBolts_ACritOfItsOwn_DealsFifteenPercentMoreAsTrue_Measured()
    {
        // a 347 crit (AD 173.5 at 200%) with 52 true on the 0-armor dummy: 347 x 1.15
        var r = Run(Annie, new[] { 2512 }, nodes: new[] { Skill("R", 0), Aa(1) }, ad: 173.5, crit: 1.0);
        Assert.InRange(Dmg(r, "AA_1"), 347 + 52 - 0.5, 347 + 52 + 0.5);
    }

    [Fact]
    public void FiendhunterBolts_TheTracks_FloorWithoutACritOfItsOwn_FullWithOne()
    {
        var node = new Damage.ComboNode("AA", Damage: 100, RatioAD: 0, RatioAP: 0, Damage.DamageType.Physical, CanCrit: true,
            PartialCritFloor: 0.8, CritTrueShare: 0.15);
        var attacker = new AttackerStat(Ad: 0, BonusAD: 0, Ap: 0, Level: 1, CriticalChance: 0.5, LifeSteal: 0, CritDamageMultiplier: 2.0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);
        var res = new DamageEngine().Calculate(new DamageCalcInput(new[] { node }, attacker, defender));
        Assert.Equal(180, res.TotalDamageMin, 6);                    // 80% of a crit
        Assert.Equal(230, res.TotalDamageMax, 6);                    // a full crit, + 15% true
        Assert.Equal(0.5 * 180 + 0.5 * 230, res.TotalDamage, 6);
        Assert.Equal(230, res.KillThresholdHP, 1);                   // the fold reads the same rule (the Max track)
    }

    [Fact]
    public void Lethality_StopsAtZeroArmor_Measured()
    {
        // AD 173 with 10 lethality: 173 on the 0-armor dummy, not more
        Assert.Equal(173, Run(Plain, Array.Empty<int>(), nodes: new[] { Aa(0) }, ad: 173, lethality: 10, hp: 1000).TotalDamage, 3);
        Assert.Equal(173, Run(Plain, Array.Empty<int>(), nodes: new[] { Aa(0) }, ad: 173, lethality: 10, hp: 1000, armor: 6).TotalDamage, 3);
    }

    // ── bonus mana ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RyzeQ_ReadsBonusMana_MaxMinusBase()
    {
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);
        var q = ChampionRepository.GetSkill("Ryze", "Q")!;
        Func<int, int?, double> ap = (id, _) => id == 0 ? 100 : 0;
        double atBase = FormulaInterpreter.Evaluate(q, "QDamageCalc", 3, ap, casterResource: 800, casterBaseResource: 800);
        double bonus500 = FormulaInterpreter.Evaluate(q, "QDamageCalc", 3, ap, casterResource: 1300, casterBaseResource: 800);
        Assert.Equal(0.02 * 500, bonus500 - atBase, 3);
        // without a base the max is read (direct callers' old behavior)
        Assert.Equal(0.02 * 800, FormulaInterpreter.Evaluate(q, "QDamageCalc", 3, ap, casterResource: 800) - atBase, 3);
    }

    [Fact]
    public void BaseResource_IsTheSummaryManaAlongTheGrowthCurve()
    {
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);
        var ryze = ChampionRepository.Get("Ryze")!;
        var s = SummaryStats("Ryze");
        Assert.Equal(s.GetProperty("mp").GetDouble(), SkillDamage.BaseResource(ryze, 1), 6);
        Assert.Equal(s.GetProperty("mp").GetDouble() + s.GetProperty("mpperlevel").GetDouble() * 17, SkillDamage.BaseResource(ryze, 18), 6);
    }

    [Theory]
    [InlineData(0.0, 1.15)]       // no bonus mana: the 15% floor
    [InlineData(1000.0, 1.20)]    // 15% + 0.5% per 100 bonus mana
    public void Actualizer_ItsAmpGrowsWithBonusMana(double bonusMana, double ratio)
    {
        var annie = Run(Annie, new[] { 2522 }, nodes: new[] { Skill("Q", 0), Chip(1), Skill("Q", 2) },
            baseMana: 400, resourceMax: 400 + bonusMana);
        Assert.Equal(ratio, Dmg(annie, "Q_2") / Dmg(annie, "Q_0"), 3);
    }

    // ── bases the summary path never parsed ──────────────────────────────────────────────────────

    [Fact]
    public void EveryChampion_CarriesTheSummaryBaseStats_TheResolverSubtracts()
    {
        // bonus = live total - base for AD, health, armor, magic resist, move speed and mana; a base left at 0 makes the
        // bonus the total (loop 551 base AD, loop 50 move speed and mana)
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DDragonDir, "champion.json")));
        int n = 0;
        foreach (var champ in doc.RootElement.GetProperty("data").EnumerateObject())
        {
            if (ChampionRepository.Get(champ.Name) is not { } c) continue;
            var s = champ.Value.GetProperty("stats");
            double D(string k) => s.GetProperty(k).GetDouble();
            var want = new (string Name, double Want, double Got)[]
            {
                ("attackdamage", D("attackdamage"), c.BaseStats.Ad), ("attackdamageperlevel", D("attackdamageperlevel"), c.StatsPerLevel.Ad),
                ("hp", D("hp"), c.BaseStats.Hp), ("hpperlevel", D("hpperlevel"), c.StatsPerLevel.Hp),
                ("armor", D("armor"), c.BaseStats.Armor), ("armorperlevel", D("armorperlevel"), c.StatsPerLevel.Armor),
                ("spellblock", D("spellblock"), c.BaseStats.Mr), ("spellblockperlevel", D("spellblockperlevel"), c.StatsPerLevel.Mr),
                ("attackspeed", D("attackspeed"), c.BaseStats.AttackSpeed), ("movespeed", D("movespeed"), c.BaseStats.Ms),
                ("mp", D("mp"), c.BaseStats.Mp), ("mpperlevel", D("mpperlevel"), c.StatsPerLevel.Mp),
            };
            foreach (var (name, w, g) in want)
                Assert.True(Math.Abs(w - g) < 1e-9, $"{champ.Name} {name}: summary {w}, champion {g}");
            n++;
        }
        Assert.True(n > 160, $"only {n} champions loaded");
    }

    [Fact]
    public void Janna_Tailwind_ReadsBonusMoveSpeed()
    {
        ChampionRepository.InitializeFromCache(DDragonDir, CDragonDir);
        var janna = ChampionRepository.Get("Janna")!;
        Assert.True(janna.BaseStats.Ms > 0);
        var resolver = SkillDamage.BuildStatResolver(new ActivePlayerStats { MoveSpeed = 400 }, janna, 1);
        Assert.Equal(400 - janna.BaseStats.Ms, resolver(7, 2), 6);
    }

    // ── harness (as M45Loop49ItemCoverageTests) ──────────────────────────────────────────────────

    private const string Plain = "Plain";
    private const string Annie = "Annie";

    private static JsonElement SummaryStats(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(DDragonDir, "champion.json")));
        return doc.RootElement.GetProperty("data").GetProperty(id).GetProperty("stats").Clone();
    }

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

    private ComboResult Run(string champ, int[] items, ComboNode[] nodes, double ad = 100, double armor = 0, double hp = 5000,
        double crit = 0, double lethality = 0, double baseMana = 0, double resourceMax = 0)
    {
        ChampionRepository.Initialize(new[]
        {
            champ == Annie ? LoadChampionFromBin("Annie", 625, baseMana)
                : new ChampionData { Id = Plain, Name = Plain, Skills = new Dictionary<string, SkillData>(),
                    BaseStats = new ChampionBaseStats { Ad = 60, AttackRange = 175 }, StatsPerLevel = new ChampionStatsPerLevel() },
            new ChampionData { Id = "Target", Name = "Target",
                BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor }, StatsPerLevel = new ChampionStatsPerLevel() },
        });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = ad, AbilityPower = 100, MaxHealth = 2000, CurrentHealth = 2000,
                CritChance = crit, CritDamage = 2.0, PhysicalLethality = lethality, ResourceMax = resourceMax,
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

    private static ChampionData LoadChampionFromBin(string championId, double attackRange, double baseMana)
    {
        var json = File.ReadAllText(Path.Combine(CDragonDir, $"{championId.ToLowerInvariant()}.bin.json"));
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
            BaseStats = new ChampionBaseStats { Ad = 60, AttackRange = attackRange, Mp = baseMana }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
