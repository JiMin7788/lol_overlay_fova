using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode exists in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// §19 / GOLDEN_02_KSANTE.md §7 — K'Sante All-Out W charge-scaled TRUE rider.
///
/// In All-Out ONLY, W gains a third hit: TRUE damage = f × W_base_raw, where W_base_raw = W's own
/// flat BaseDamage + %maxHP TotalMaxHealthDamage terms and f ramps RDamageIncreaseMin(0.10)→
/// RDamageIncreaseMax(0.80) by the charge knob (UserDistanceFraction), floored below MinChargeTime
/// and capped at TimeToFullCharge (all live BIN DataValues on KSanteW).
///
/// These are MECHANIC tests (formula shape, floor/cap, TRUE-unmitigated, All-Out-only), NOT a frozen
/// golden datapoint — per §19 the measured ±1 golden encoding is DEFERRED to a clean session-C
/// re-measurement (session-1 numbers are exploit-noised). The reused fixture is GoldenKsanteTests':
/// K'Sante L6 (AD 78, base Armor/MR 57/38, no items ⇒ 0 bonus resist), target 36 armor / 32 MR, so
/// physical hits mitigate by k = 100/136 = 0.7353 and a TRUE rider is unmitigated — the two are
/// distinguishable by that factor alone.
/// </summary>
// Shares the always-on calc_trace.log sink with GoldenKsanteTests (which deletes+counts blocks in
// it): one collection serializes the two so neither races the other's log read/delete.
[Collection("CalcTraceLog")]
public class KsanteAllOutWRiderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "KSante";
    private const double BaseArmorL6 = 57;
    private const double BaseMrL6 = 38;
    private const double BaseHpL6 = 1219;
    private const double Ad = 78;
    private const double TargetHp = 755;
    private const double KPhys = 100.0 / 136.0; // target armor 36 → physical multiplier

    public KsanteAllOutWRiderTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "KsanteAllOutWRiderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── fixtures (mirrors GoldenKsanteTests) ───────────────────────────────────────

    private static ChampionData Ksante()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{ChampId.ToLowerInvariant()}.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion(ChampId, json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = ChampId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = ChampId, Name = ChampId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = Ad, Armor = BaseArmorL6, Mr = BaseMrL6, Hp = BaseHpL6 },
            StatsPerLevel = new ChampionStatsPerLevel(),
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
        AttackDamage = Ad, AbilityPower = 0,
        Armor = BaseArmorL6, MagicResist = BaseMrL6, MaxHealth = BaseHpL6,
        AbilityQ = 3, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static GameSnapshot Snapshot()
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2, Stats = Stats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = ChampId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 3;
        return snap;
    }

    private void InitRepo() =>
        ChampionRepository.Initialize(new[] { Ksante(), Dummy("Target", TargetHp, 36, 32) });

    private static ComboNode Skill(string slot, double? charge = null) => new(
        Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0, UserDistanceFraction: charge);

    /// <summary>Runs a combo of the given nodes and returns the result.</summary>
    private ComboResult Run(params ComboNode[] nodes)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, "c");
        foreach (var node in nodes)
            editor.AddNode(draft.Id, node);
        editor.SaveCombo(draft.Id);

        var snap = Snapshot();
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    // W node breakdown rows (base flat h0, base %maxHP h1, rider h2), by NodeId prefix.
    private static IReadOnlyList<NodeBreakdownEntry> WNodes(ComboResult r)
        => r.NodeBreakdown.Where(n => n.NodeId.StartsWith("W_0#")).ToList();

    // ── tests ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void BaseStanceW_HasNoRider_AndTotalUnchanged_GoldenInvariant()
    {
        // A plain W (no preceding R) must be byte-identical to before the rider was curated:
        // exactly the two base physical hits, total 77 (= the existing W golden).
        InitRepo();
        var result = Run(Skill("W", charge: 1.0)); // charge knob set but ignored: base stance has no rider

        Assert.Equal(2, WNodes(result).Count);
        Assert.All(WNodes(result), n => Assert.True(n.Damage < 70, "base W hits are mitigated physical, never the TRUE rider"));
        Assert.True(Math.Abs(result.TotalDamage - 77) <= 1.0,
            $"base-stance W total must stay 77 ±1 (golden-invariant); got {result.TotalDamage:0.##}");
    }

    [Fact]
    public void AllOutW_AddsThirdHit_TrueRider()
    {
        // After R, W gains the rider → three W rows, exactly one of which is unmitigated TRUE.
        InitRepo();
        var w = WNodes(Run(Skill("R"), Skill("W", charge: 1.0)));

        Assert.Equal(3, w.Count);
        var rider = Assert.Single(w, n => n.NodeId.EndsWith("h2c0")); // hits[2] = the rider
        // The two base hits are physical (× k); the rider is TRUE (unmitigated), so it is far larger
        // than either base hit despite being a fraction of their raw sum.
        Assert.True(rider.Damage > w.Where(n => !n.NodeId.EndsWith("h2c0")).Max(n => n.Damage),
            "the TRUE rider (unmitigated) should exceed each mitigated physical base hit");
    }

    [Fact]
    public void AllOutW_FullCharge_RiderIsEightyPercentOfBaseRaw_Unmitigated()
    {
        // baseRaw = flat 45 + 8% × maxHP (the two base hits' RAW). The base hits appear mitigated in
        // the breakdown, so reconstruct baseRaw = (h0 + h1)/k; the TRUE rider must equal 0.80 × baseRaw
        // with NO mitigation applied. This proves both the 0.80 full-charge factor and TRUE-typing in
        // one assertion, independent of the exact resolved maxHP.
        InitRepo();
        var w = WNodes(Run(Skill("R"), Skill("W", charge: 1.0)));

        double baseMitigated = w.Where(n => !n.NodeId.EndsWith("h2c0")).Sum(n => n.Damage);
        double baseRaw = baseMitigated / KPhys;
        double rider = w.Single(n => n.NodeId.EndsWith("h2c0")).Damage;

        Assert.Equal(0.80 * baseRaw, rider, 1); // full-charge = 80% of base raw, TRUE (unmitigated)
        // Cross-check vs GOLDEN_02 §7's session-1 fit (config-1 base 105.4 → 84.3); MECHANIC only, the
        // ±1 golden encode is deferred to session-C, so this is a sanity band, not a frozen assert.
        Assert.True(Math.Abs(rider - 84.3) <= 2.0, $"full-charge rider ≈ 84 (GOLDEN_02 §7); got {rider:0.##}");
    }

    [Fact]
    public void AllOutW_FloorDefault_IsMinTenPercent_EightTimesBelowFullCharge()
    {
        // Unset charge knob → the Min floor (10%). Full charge is Max (80%). 0.80/0.10 = 8× exactly —
        // a pure ratio independent of baseRaw, proving the floor default and the Min/Max endpoints.
        InitRepo();
        double floorRider = WNodes(Run(Skill("R"), Skill("W"))).Single(n => n.NodeId.EndsWith("h2c0")).Damage;
        double fullRider = WNodes(Run(Skill("R"), Skill("W", charge: 1.0))).Single(n => n.NodeId.EndsWith("h2c0")).Damage;

        Assert.True(floorRider > 0, "floor rider is the Min (10%) fraction, not zero");
        Assert.Equal(8.0, fullRider / floorRider, 2); // 0.80 / 0.10
    }

    [Fact]
    public void AllOutW_ChargeRamp_FloorsBelowMinChargeTime_CapsAtTimeToFullCharge()
    {
        // MinChargeTime = 0.4, TimeToFullCharge = 0.9 (normalized). t ≤ 0.4 → Min; t ≥ 0.9 → Max
        // (capped); the ramp is linear between. Verify the three regimes against the endpoints.
        InitRepo();
        double atMin = WNodes(Run(Skill("R"), Skill("W", charge: 0.4))).Single(n => n.NodeId.EndsWith("h2c0")).Damage;
        double atFull = WNodes(Run(Skill("R"), Skill("W", charge: 0.9))).Single(n => n.NodeId.EndsWith("h2c0")).Damage;
        double capped = WNodes(Run(Skill("R"), Skill("W", charge: 1.0))).Single(n => n.NodeId.EndsWith("h2c0")).Damage;
        double floor = WNodes(Run(Skill("R"), Skill("W"))).Single(n => n.NodeId.EndsWith("h2c0")).Damage;
        double mid = WNodes(Run(Skill("R"), Skill("W", charge: 0.65))).Single(n => n.NodeId.EndsWith("h2c0")).Damage;

        Assert.Equal(floor, atMin, 2);       // t = MinChargeTime is still the Min floor
        Assert.Equal(atFull, capped, 2);      // t past TimeToFullCharge is capped (0.9 == 1.0)
        Assert.True(mid > atMin && mid < atFull, "midway charge lies strictly between floor and cap");
        // t = 0.65 is exactly halfway through [0.4, 0.9] → f = midpoint of [0.10, 0.80] = 0.45.
        Assert.Equal((atMin + atFull) / 2.0, mid, 1);
    }

    [Fact]
    public void AllOutW_Trace_EmitsDistinctTrueRiderRow()
    {
        // §19 verification: the trace carries the TRUE rider as its own row (branch=charge-rider) plus
        // the engine's own type=True mitigation line for the rider node. Uses Contains (not block
        // count) so it is robust to other combo tests appending to the shared always-on log; the
        // CalcTraceLog collection keeps GoldenKsanteTests' log-delete from racing this read.
        string tracePath = Path.Combine(AppContext.BaseDirectory, "logs", "calc_trace.log");

        InitRepo();
        Run(Skill("R"), Skill("W", charge: 1.0));

        string trace = File.ReadAllText(tracePath);
        // ExpandCuratedSkill emits the branch row on the base node id (the #cast..h2c0 suffix is added
        // to the per-hit nodes afterward); the DamageEngine mitigation row carries the expanded id.
        Assert.Contains("node=W_0 branch=charge-rider type=True slot=W", trace); // ExpandCuratedSkill row
        Assert.Contains("node=W_0#cast0h2c0 type=True", trace);                  // DamageEngine mitigation row
    }
}
