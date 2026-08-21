using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 480) Hwei casts nine spells, not three. Q, W and E are muse selectors — the second key is
/// the spell — and the curation used to say otherwise: the E slot carried a hit that was really EQ,
/// the W slot carried the rider that is really WE's, and the other five sub-casts did not exist.
///
/// <para>These pin the three things that had to change in the engine for the nine to be sayable: an
/// extra slot may carry ONLY riders and still be a cast, a sub-cast arms what its own rider is gated
/// on, and a curated hit may declare that its base grows with the target's missing health (Severing
/// Bolt's defining mechanic, which was disclosed-omitted until now).</para>
///
/// <para>The measured numbers live in <see cref="GoldenHweiTests"/> and did not move.</para>
/// </summary>
public class HweiMuseSlotsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private const string ChampId = "Hwei";

    public HweiMuseSlotsTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "HweiMuse_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private const double TargetHp = 2000;

    private static ChampionData FromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon",
            $"{championId.ToLowerInvariant()}.bin.json"));
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in ChampionBinParser.ParseChampion(championId, json))
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
            BaseStats = new ChampionBaseStats { Ad = 100 },
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = TargetHp, Armor = 0, Mr = 0 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 100, AbilityPower = 0,
        AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
    };

    private static GameSnapshot Snap()
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2, Stats = Stats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = ChampId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 11;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 11;
        return snap;
    }

    private void Init() => ChampionRepository.Initialize(new[] { FromBin(ChampId), Dummy() });

    /// <summary>Runs a sequence of slot keys; <paramref name="exposureSeconds"/> fills the duration
    /// knob on every node that has one.</summary>
    private double Run(double? exposureSeconds, params string[] slots)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, $"c_{exposureSeconds}_{string.Join("", slots)}");
        for (int i = 0; i < slots.Length; i++)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{slots[i]}_{i}",
                NodeType: slots[i] == "AA" ? ComboNodeType.Aa : ComboNodeType.Skill,
                Name: slots[i], Cooldown: 0, Mana: 0, Damage: 0,
                DamageType: ComboDamageType.Magic, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
                CastTime: 0, Delay: 0, TravelTime: 0) with { UserHitDurationSeconds = exposureSeconds });
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, Snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!.TotalDamage;
    }

    // ── QW: the amplification that was disclosed-omitted for a year ───────────────────

    [Fact]
    public void SeveringBolt_GrowsWithTheTargetsMissingHealth()
    {
        Init();

        // Same two casts, opposite order. Severing Bolt fired into a healthy target is its base
        // number; fired after R has taken a chunk off, the same cast hits harder. Nothing else in
        // this pair is order-dependent, so the whole difference is the amplification.
        double boltFirst = Run(null, "QW", "R");
        double boltLast = Run(null, "R", "QW");
        Assert.True(boltLast > boltFirst + 1,
            $"QW did not scale with missing health: bolt-first {boltFirst:0.##}, bolt-last {boltLast:0.##}");

        // …and it is bounded by the wiki's multiplier rather than unbounded: at rank 5 the bonus
        // tops out at 3.5x the base, which no partial-health target can exceed.
        double baseBolt = Run(null, "QW");
        Assert.True(boltLast - Run(null, "R") <= 4.5 * baseBolt);
    }

    // ── QE: the zone DoT, on the honest knob ─────────────────────────────────────────

    [Fact]
    public void MoltenFissure_ChargesNothingUntilTheUserSaysHowLongTheyStoodInIt()
    {
        Init();
        double explosionOnly = Run(null, "QE");
        double fullExposure = Run(2.5, "QE");
        Assert.True(fullExposure > explosionOnly + 1);

        // The knob is clamped to the BIN's own 2.5s Duration, so claiming longer changes nothing.
        Assert.Equal(fullExposure, Run(10.0, "QE"), 2);
    }

    // ── WE: three empowered attacks, not an aura ─────────────────────────────────────

    [Fact]
    public void StirringLights_EmpowersExactlyThreeAttacks()
    {
        Init();
        double aa = Run(null, "AA");
        double three = Run(null, "WE", "AA", "AA", "AA");
        double four = Run(null, "WE", "AA", "AA", "AA", "AA");

        double perProc = (three - 3 * aa) / 3;
        Assert.True(perProc > 0);
        // The fourth attack is plain: the buff is three attacks, and before the maxProcs cap an
        // ability-slot rider rode every attack for the rest of the combo.
        Assert.Equal(four - three, aa, 1);
    }

    // ── QQ: the health ratio, and the rank the golden used to pin by accident ────────

    [Fact]
    public void DevastatingFire_TakesAShareOfTheTargetsMaximumHealth()
    {
        Init();
        var hwei = ChampionRepository.Get(ChampId)!;
        double flat = SkillDamage.ComputeCalcDamage(hwei, "HweiQQ", "Damage", Stats(), 11, rankSlot: "QQ")!.Value;

        // Rank 5 is 7% of the target's maximum health on top of the flat half. Against this 2000 HP
        // dummy that is 140 — the term that GOLDEN #13 appeared to rule out until the reading was
        // re-read at Q rank 1, where it lands exactly.
        Assert.Equal(flat + 0.07 * TargetHp, Run(null, "QQ"), 1);
    }

    [Fact]
    public void AMuseCastsAtItsSelectorsRank()
    {
        // An extra slot has no rank of its own; ChooseRank inherits it from the first letter of the
        // key. The QQ golden row used to be the only thing pinning that, as a side effect of being
        // (wrongly) read at rank 2 — now that it sits at rank 1 like the rest of its session, this
        // is the direct check.
        Init();
        var hwei = ChampionRepository.Get(ChampId)!;

        double AtQRank(int rank) => SkillDamage.ComputeCalcDamage(
            hwei, "HweiQQ", "Damage", new ActivePlayerStats { AttackDamage = 100, AbilityQ = rank }, 11,
            rankSlot: "QQ")!.Value;

        // BaseDamage 50/80/110/140/170 by Q rank — a muse pinned at the rank-1 floor would return
        // the same number three times.
        Assert.Equal(50, AtQRank(1), 1);
        Assert.Equal(80, AtQRank(2), 1);
        Assert.Equal(170, AtQRank(5), 1);
    }

    // ── the palette ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AllNineMusesArePresent_EachBesideItsSelector()
    {
        Init();
        var ids = ComboEditor.LoadPalette(ChampId).AvailableNodes.Select(n => n.Id).ToList();

        foreach (var muse in new[] { "QQ", "QW", "QE", "WQ", "WW", "WE", "EQ", "EW", "EE" })
            Assert.Contains(muse, ids);

        // Each trio sits between its own selector and the next one (loop 478's ordering rule, which
        // this inherits for free because a muse key starts with its selector's letter).
        Assert.True(ids.IndexOf("QQ") > ids.IndexOf("Q") && ids.IndexOf("QE") < ids.IndexOf("W"));
        Assert.True(ids.IndexOf("WQ") > ids.IndexOf("W") && ids.IndexOf("WE") < ids.IndexOf("E"));
        Assert.True(ids.IndexOf("EQ") > ids.IndexOf("E") && ids.IndexOf("EE") < ids.IndexOf("R"));
    }

    [Fact]
    public void TheSelectorsThemselvesDealNothing()
    {
        Init();
        // Pressing Q only opens the muse choice. Reading a number off it was the original complaint.
        foreach (var selector in new[] { "Q", "W", "E" })
            Assert.Equal(0, Run(null, selector), 2);

        // The two boons are real casts with real cooldowns and no damage — which is different from
        // being absent, and is why they are curated with an empty hits array rather than omitted.
        Assert.Equal(0, Run(null, "WQ"), 2);
        Assert.Equal(0, Run(null, "WW"), 2);
    }
}
