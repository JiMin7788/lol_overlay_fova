using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 552, user decision) Irelia P (Ionian Fervor) is MANUAL-ONLY: it rides nothing
/// automatically and exists solely in the "+fx" picker. Before, an auto-attached StackGte-4
/// conditional put an unexplained 124~186 range on every plain AA; now a plain AA is exactly the
/// attack damage, and attaching P through the picker adds the full-stack on-hit at face value —
/// the attach IS the stack assertion.
/// </summary>
public class IreliaManualPassiveTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public IreliaManualPassiveTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "IreliaManualPassiveTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ChampionData Irelia()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "irelia.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Irelia", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Irelia" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Irelia", Name = "Irelia", Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static GameSnapshot Snapshot()
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 9, PlayerCount = 1,
            Stats = new ActivePlayerStats { AttackDamage = 124, AbilityQ = 1, AbilityW = 1, AbilityE = 1 },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Irelia";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 9;
        return snap;
    }

    private ComboResult Run(AttachableBonusEffect[]? attach)
    {
        ChampionRepository.Initialize(new[] { Irelia() });
        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Irelia", "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0, UserBonusEffects: attach));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snapshot(), runeEngine: runeEngine);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        return received!;
    }

    [Fact]
    public void PlainAa_IsExactlyAttackDamage_NoAutoRiderNoKnob()
    {
        // No auto rider → no conditional checkbox is offered on AA, and a bare AA is pure AD.
        Assert.Null(SkillDamageDb.GetConditionalHit("Irelia", "AA"));
        Assert.Equal(124.0, Run(attach: null).TotalDamage, 1);
    }

    [Fact]
    public void PassiveIsInThePicker_AndAttachingItAddsTheFullStackOnHit()
    {
        var attachable = SkillDamageDb.GetAttachableBonusEffects("Irelia", "AA");
        var p = attachable.FirstOrDefault(e => e.Slot == "P" && e.Effect.Trigger == BonusTrigger.OnHit);
        Assert.NotNull(p);
        Assert.True(p!.Effect.ManualOnly);

        // Wiki: 10 + 3×(level−1) growth on-hit (+20% bonus AD, zero here: base stats are zero in
        // this fixture so bonus AD = total AD... pin against the calc itself to stay exact).
        double onHit = SkillDamage.ComputeCalcDamage(Irelia(), "P", "OnHitBonus",
            Snapshot().Stats, 9)!.Value;
        Assert.True(onHit > 0);
        Assert.Equal(124.0 + onHit, Run(new[] { p }).TotalDamage, 1);
    }
}
