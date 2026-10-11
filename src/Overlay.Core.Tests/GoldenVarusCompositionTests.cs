using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode exists in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #7b — Varus_MultiAxis, the COMPOSITION half of the Varus golden. <see cref="GoldenVarusTests"/>
/// pins the raw BIN calcs; this drives the same numbers end-to-end through the real curated
/// <c>skill_damage/Varus.json</c> via <c>ComboRunner</c>, so curation wiring (min/max charge
/// interpolation, the onAbility blight bonus effect) is covered, not just formula resolution.
///
/// <para>Fixture = the measured 2026-07-14 session (training bot 1000 HP / Armor 20 / MR 20, Varus L6,
/// AD 78 so bonus AD = 78 − 72.67 = 5.33, AP 0, all abilities rank 1). Mitigation ×100/120 = 0.8333
/// for BOTH schools, since Armor and MR are equal — every expectation below is a live (post-mitigation)
/// HP delta from that session, which is what the golden is for.</para>
///
/// <para>SCOPE — all four composable axes (§40 closed, loop 548): Q charge interpolation, the blight
/// detonation, the Q-charge blight AMPLIFICATION (×1→×1.5 via <c>chargeAmpMaxDataValue</c> riding the
/// Q node's own charge knob), and the W-active missing-HP Q empower (Upgraded-gated rider, 6%→9% via
/// <c>metHpPercentMaxCalc</c>, dynamic Missing evaluated AFTER Q base + blight land). The dynamic rows
/// run against a FULL-HP target (this harness has no target-health tracker, so the defender starts at
/// MaxHP); the measured session started at 518/1000, and the identity that validated the fraction —
/// 49 ≈ 9% × 666 missing × 0.8333 — is the same fraction/basis/order these rows pin at a different
/// start HP.</para>
/// </summary>
public class GoldenVarusCompositionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const double BaseAdL6 = 72.67;  // bonus AD = 78 − 72.67 = 5.33, as in GoldenVarusTests
    private const double Mitigation = 100.0 / 120.0;   // resist 20, both schools

    public GoldenVarusCompositionTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenVarusCompositionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────

    private static ChampionData LoadVarusFromBin()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "varus.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Varus", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Varus" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Varus", Name = "Varus", Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = BaseAdL6 }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy(string id, double hp, double armor, double mr) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 78, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static GameSnapshot Snapshot()
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2, Stats = Stats(),
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Varus";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 6;
        return snap;
    }

    /// <summary>Runs one skill slot, optionally with a charge fraction (Q's min→max knob), the
    /// node's conditional checkbox (the W-active empower's Upgraded toggle), and — via the loop-553
    /// <see cref="TargetSnapshot.CurrentHp"/> seed — a pre-damaged virtual target.</summary>
    private ComboResult RunSlot(string slot, double? chargeFraction = null, bool? conditionMet = null,
        double? targetCurrentHp = null)
    {
        using var config = new ConfigManager(_configPath);
        var runeEngine = new RuneEngine();
        var engine = new ComboEngine(new DamageEngine(), runeEngine);
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Varus", "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0, UserDistanceFraction: chargeFraction,
            UserConditionMet: conditionMet));
        editor.SaveCombo(draft.Id);
        if (targetCurrentHp is { } hp)
        {
            TargetSnapshotStore.Save(config, draft.Id,
                new TargetSnapshot("Target", Armor: 20, Mr: 20, MaxHp: 1000, CapturedAtUtcMs: 0, CurrentHp: hp));
            TargetSnapshotStore.SetUseSnapshot(config, draft.Id, true);
        }

        using var runner = new ComboRunner(engine, config, () => Snapshot(), runeEngine: runeEngine);
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

    private static double Sum(ComboResult result, string nodeIdPrefix)
        => result.NodeBreakdown.Where(n => n.NodeId.StartsWith(nodeIdPrefix, StringComparison.Ordinal))
                               .Sum(n => n.Damage);

    private static void Init() =>
        ChampionRepository.Initialize(new[] { LoadVarusFromBin(), Dummy("Target", hp: 1000, armor: 20, mr: 20) });

    // ── Axis 1 (composed): Q charge interpolates min → max through the curated minCalc/maxCalc ──

    [Theory]
    [InlineData(0.0, 48.0)]   // instant recast: 57.6 raw × 0.8333
    [InlineData(1.0, 72.0)]   // full charge:    86.4 raw × 0.8333
    [InlineData(0.5, 60.0)]   // linear midpoint between the two measured ends
    public void Varus_MultiAxis_QCharge_InterpolatesMinToMax(double charge, double expectedLive)
    {
        Init();
        // Q's own hit only — the blight bonus rides along on the same cast and is asserted separately.
        double q = Sum(RunSlot("Q", charge), "Q_0#cast0h0");
        Assert.Equal(expectedLive, q, 1);
    }

    [Fact]
    public void Varus_MultiAxis_QCharge_UnsetDefaultsToFullChargeAnchor()
    {
        Init();
        // Varus.json keeps calc=TotalDamageMax as the resolved default, so an un-charged node must
        // still read as the full-charge value (the "zero regression" claim in _noteQ).
        Assert.Equal(72.0, Sum(RunSlot("Q", chargeFraction: null), "Q_0#cast0h0"), 1);
    }

    // ── Axis 3 (composed): the onAbility blight detonation rides every ability cast ──────────────

    [Theory]
    [InlineData("E")]
    [InlineData("R")]
    public void Varus_MultiAxis_BlightDetonates_OnChargelessAbilities(string slot)
    {
        Init();
        // Blight = 3% max HP per stack, capped at 3 stacks → 9% max. Measured 2026-07-14: a
        // full-stack detonation on a 1000 HP / MR 20 target = 75 live (0.09 × 1000 × 0.8333).
        // This is the UPPER BOUND by construction — the curation carries the capped value and the
        // engine does not live-track stack count, so partial stacks are not expressible.
        // E/R have no charge mechanic, so the §40 charge amp is inert on them — plain 75.
        Assert.Equal(75.0, Sum(RunSlot(slot), $"{slot}_0#bonus"), 1);
    }

    // ── Axis 4 (§40, loop 548): Q charge AMPLIFIES the blight detonation ×1 → ×1.5 ────────────────

    [Theory]
    [InlineData(0.0, 75.0)]    // uncharged Q: plain detonation — the measured E-detonate value
    [InlineData(1.0, 112.5)]   // full charge: ×1.5 (VarusWMaxChargeHPDamage) — measured 112
    [InlineData(null, 112.5)]  // unset knob = Q's anchor (full charge, _noteQ) — ONE consistent cast
    public void Varus_MultiAxis_QCharge_AmplifiesBlight(double? charge, double expectedLive)
    {
        Init();
        // (§40) This row previously pinned 75 on a knob-less Q — an ARTIFACT of the amp not being
        // modeled, not a measurement (75 was measured on E-detonate). The measured Q number is 112
        // (75 × 1.5), which the amp now reproduces; the label and rows moved, the measured values
        // (75, 112) did not (RECURRING_TRAPS 2.2).
        Assert.Equal(expectedLive, Sum(RunSlot("Q", charge), "Q_0#bonus"), 1);
    }

    [Fact]
    public void Varus_MultiAxis_R_BaseAndBlightCompose()
    {
        Init();
        var result = RunSlot("R");
        Assert.Equal(125.0, Sum(result, "R_0#cast0h0"), 1);   // 150 base × 0.8333
        Assert.Equal(200.0, result.TotalDamage, 1);           // + 75 blight
    }

    // ── Axis 5 (§40, loop 548): W-active empowered Q — dynamic missing-HP, post-blight, 6%→9% ────

    [Fact]
    public void Varus_MultiAxis_WActiveEmpower_DefaultsUnmet_NothingAdded()
    {
        Init();
        // The Upgraded checkbox defaults UNMET (P2 floor): a plain full-charge Q is its own hit +
        // the amplified blight and NOTHING else — the pre-§40 cast, byte-identical.
        var result = RunSlot("Q", chargeFraction: 1.0);
        Assert.Equal(72.0 + 112.5, result.TotalDamage, 1);
    }

    [Theory]
    [InlineData(1.0, 13.8)]   // full charge: 9% × missing(72+112.5=184.5) × 0.8333
    [InlineData(0.0, 6.2)]    // uncharged:   6% × missing(48+75=123)      × 0.8333
    public void Varus_MultiAxis_WActiveEmpower_DynamicMissingAfterBlight(double charge, double expectedLive)
    {
        Init();
        // Checkbox ON: the rider lands as a DYNAMIC missing-health execute node ordered AFTER Q
        // base + blight (rider declaration order in Varus.json W — load-bearing, see _noteW), so
        // its fraction reads the running HP those hits produced. This harness has no target-health
        // tracker, so the target starts FULL (1000): full charge → 9% × 184.5 missing × 0.8333 =
        // 13.8. The measured session (2026-07-14) started at 518/1000 and the SAME fraction/basis/
        // order gave 49 ≈ 9% × 666 × 0.8333 — the fraction is pinned by GoldenVarusTests' raw rows,
        // the order and dynamism by this one.
        var result = RunSlot("Q", chargeFraction: charge, conditionMet: true);
        double blight = charge >= 1.0 ? 112.5 : 75.0;
        double qOwn = charge >= 1.0 ? 72.0 : 48.0;
        Assert.Equal(qOwn + blight + expectedLive, result.TotalDamage, 1);
    }

    [Fact]
    public void Varus_MultiAxis_WActiveEmpower_ReproducesTheMeasuredSession()
    {
        Init();
        // The 2026-07-14 measurement itself, end to end (loop 553: TargetSnapshot.CurrentHp seeds
        // the 518/1000 start): full-charge Q at 518 HP → Q 72 + blight 112.5 lands the target at
        // 333.5 (measured 334), missing 666.5 → 9% × 666.5 × 0.8333 = 49.99 ≈ the measured 49.
        var result = RunSlot("Q", chargeFraction: 1.0, conditionMet: true, targetCurrentHp: 518);
        double empower = result.TotalDamage - 72.0 - 112.5;
        Assert.True(Math.Abs(empower - 49.9) <= 1.5,
            $"W-active empower at 518/1000 should reproduce the measured 49 (±1.5), got {empower:0.##}");
    }

    [Fact]
    public void Varus_MultiAxis_WActiveEmpower_OffersItsCheckboxOnQOnly()
    {
        Init();
        // Editor discovery (M28): the Upgraded-gated rider is appliesTo:["Q"], so the checkbox
        // must surface on Q nodes and NOT on E (same membership rule as the damage — loop 479).
        Assert.NotNull(SkillDamageDb.GetConditionalHit("Varus", "Q"));
        Assert.Null(SkillDamageDb.GetConditionalHit("Varus", "E"));
    }
}
