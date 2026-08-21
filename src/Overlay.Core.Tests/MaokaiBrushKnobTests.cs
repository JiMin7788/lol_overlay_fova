using System.Threading;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (CLAUDE_CODE_TODO §76) Maokai E's BRUSH-enhanced sapling, curated as the M25 conditional-bonus
/// shape — baseline <c>TotalDamage</c> (the ordinary sapling, the P2 floor) switching to
/// <c>TotalEmpoweredDamage</c> when the new <see cref="ConditionType.InBrush"/> condition is assumed
/// met. Structurally identical to Kha'Zix Q (BaseDamage → IsoDamage) and Mordekaiser Q
/// (QDamage → EmpoweredDamageTooltip); no new mechanism.
///
/// <para>The enhanced value is already golden-locked at the calc level —
/// <c>GoldenMaokaiTests.Golden_Maokai_EnhancedE_CasterBonusHealth_Is106</c> pins raw 106 at
/// L1 / AP 9 / +10 bonus HP. This file proves the same number arrives through the COMBO path once the
/// knob is on, which is the link the curation change actually adds.</para>
///
/// <para>The trap this file also guards (§76's own warning): a new UserAssumed condition must be added
/// to <see cref="ConditionType"/> AND to <see cref="ConditionResolution.IsUserAssumed"/>. Miss the
/// second and it falls through to <c>_ =&gt; false</c>, gets routed as AutoResolvable, and the editor
/// checkbox never appears — a silent failure with no error anywhere.</para>
/// </summary>
public class MaokaiBrushKnobTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public MaokaiBrushKnobTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "MaokaiBrushKnobTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        ChampionRepository.ResetForTests();
        SkillDamageDb.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ChampionData Maokai()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "maokai.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Maokai", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Maokai" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Maokai", Name = "Maokai", Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    /// <summary>Target with ZERO resistances so post-mitigation damage IS the raw calc value and the
    /// golden's raw 106 can be compared directly.</summary>
    private static ChampionData BareTarget() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = 2000, Armor = 0, Mr = 0 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    /// <summary>Maokai L1, AP 9, MaxHealth 10 — with BaseStats.Hp = 0 that is +10 BONUS health, the
    /// exact stat line the enhanced-E golden was recorded at.</summary>
    private static GameSnapshot Snapshot()
    {
        var stats = new ActivePlayerStats
        {
            AttackDamage = 0, AbilityPower = 9, MaxHealth = 10,
            AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
        };
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 1, PlayerCount = 2, Stats = stats };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Maokai";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 1;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 1;
        return snap;
    }

    private ComboResult RunE(bool? brushAssumed)
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.Initialize(new[] { Maokai(), BareTarget() });
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Maokai", "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "E_0", NodeType: ComboNodeType.Skill, Name: "E", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Magic, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0,
            UserConditionMet: brushAssumed));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, Snapshot);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    /// <summary>The trap. Both halves of the wiring, asserted where a miss is loud instead of silent.</summary>
    [Fact]
    public void InBrush_IsClassifiedUserAssumed()
    {
        Assert.True(ConditionResolution.IsUserAssumed(ConditionType.InBrush),
            "InBrush must be UserAssumed — routed AutoResolvable it would never surface a checkbox");
    }

    /// <summary>Curation shape guard: E is one conditional hit whose MET calc is the brush value.
    /// This is what makes the editor grow a 최대 checkbox and the exposure graph widen E's range.</summary>
    [Fact]
    public void MaokaiE_IsCuratedAsAnInBrushConditionalHit()
    {
        var hits = SkillDamageDb.GetHits("Maokai", "E");
        Assert.NotNull(hits);
        var hit = Assert.Single(hits!);
        Assert.True(hit.IsConditional);
        Assert.Equal("InBrush", hit.ConditionType);
        Assert.Equal("TotalDamage", hit.Calc);
        Assert.Equal("TotalEmpoweredDamage", hit.MetCalc);
    }

    /// <summary>
    /// Knob ON ⇒ the combo path resolves the brush value, and it is the SAME number
    /// <c>GoldenMaokaiTests</c> locked at the calc level (raw 106 at L1/AP9/+10HP; the target has zero
    /// resistances here so post-mitigation == raw). Knob OFF ⇒ the ordinary sapling, strictly less —
    /// the conservative P2 default, unchanged from before this curation.
    /// </summary>
    [Fact]
    public void BrushKnob_On_ResolvesTheGoldenEnhancedValue_Off_StaysOnTheNormalSapling()
    {
        double enhanced = RunE(brushAssumed: true).TotalDamage;
        double normal = RunE(brushAssumed: false).TotalDamage;

        Assert.True(Math.Abs(enhanced - 106) <= 1.0,
            $"brush-enhanced E through the combo path: expected the golden 106 ±1, got {enhanced:0.##}");
        Assert.True(normal > 0 && normal < enhanced,
            $"the normal sapling must be a real, strictly smaller floor (normal {normal:0.##} vs enhanced {enhanced:0.##})");
    }

    /// <summary>
    /// The user-visible payoff of §76: with the knob UNSET, <c>BuildExposureGraph</c> pushes it to both
    /// ends, so E — and only E — reports an honest [normal, brush-enhanced] range while the resolved
    /// number stays the conservative floor. Q/W/R carry no knob and no crit, so they stay certain.
    /// </summary>
    [Fact]
    public void BrushKnob_Unset_WidensTheRangeToNormalToEnhanced()
    {
        var result = RunE(brushAssumed: null);

        Assert.Equal(result.RangeMin, result.TotalDamage, precision: 2); // resolved stays at the floor
        Assert.True(result.RangeMax > result.RangeMin, "E must now span [normal, brush-enhanced]");
        Assert.True(Math.Abs(result.RangeMax - 106) <= 1.0,
            $"the ceiling is the brush value: expected 106 ±1, got {result.RangeMax:0.##}");
    }
}
