using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 471) Aatrox's Q split into its three casts, and the per-cast ramp that made the split
/// necessary. A MODEL LOCK, not a measurement: nobody has run this in a practice tool, so it pins
/// the RATIO the wiki states — second cast ×1.25, third ×1.5 — against the live BIN rather than
/// freezing absolute numbers a patch would invalidate.
///
/// <para>Why the ratio is the right thing to pin: the ramp lives in the BIN as the bare DataValue
/// QRampBonus (0.25) with no calc of its own, so it is exactly the class of damage that is
/// invisible to calc-name-driven curation. If a patch retunes it, this test must FOLLOW the new
/// value rather than fail — which is what reading the DataValue live, on both sides, achieves. The
/// test would fail if the ramp stopped being applied at all, or became compounding.</para>
/// </summary>
public class AatroxQRampTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "Aatrox";
    private const double TotalAd = 100.0;

    public AatroxQRampTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "AatroxRamp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ChampionData Aatrox()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon",
            $"{ChampId.ToLowerInvariant()}.bin.json"));
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in ChampionBinParser.ParseChampion(ChampId, json))
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
            BaseStats = new ChampionBaseStats { Ad = TotalAd },
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = 2000, Armor = 0, Mr = 0 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = TotalAd, AbilityPower = 0,
        AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static GameSnapshot Snap()
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
        snap.Players[1].Level = 6;
        return snap;
    }

    private static ComboNode SkillNode(string slot) => new(
        Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    private double Damage(string slot)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, "c_" + slot);
        editor.AddNode(draft.Id, SkillNode(slot));
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

    [Fact]
    public void SecondAndThirdCastsRampAdditively()
    {
        ChampionRepository.Initialize(new[] { Aatrox(), Dummy() });

        double q1 = Damage("Q"), q2 = Damage("Q2"), q3 = Damage("Q3");

        Assert.True(q1 > 0, "Q resolved to nothing");
        // The wiki's "each subsequent cast gains 25% more damage", read as ratios so a retune of
        // QRampBonus moves the expectation with the game instead of breaking the test.
        Assert.Equal(1.25, q2 / q1, 6);
        Assert.Equal(1.50, q3 / q1, 6);
        // Additive, not compounding: 1.5, not 1.25² = 1.5625.
        Assert.NotEqual(1.5625, q3 / q1, 6);
    }

    [Fact]
    public void TheRampIsReadFromTheBinRatherThanBakedIn()
    {
        ChampionRepository.Initialize(new[] { Aatrox(), Dummy() });

        // The multiplier the engine applies must be exactly 1 + steps × the live QRampBonus, so a
        // patch that retunes that DataValue retunes the tier list with it (project Hard Rule:
        // patch-dependent values are always a dynamic lookup, never a literal in curation).
        var aatrox = Aatrox();
        double ramp = SkillDamage.ComputeFlatDataValue(aatrox, "Q", "QRampBonus", Stats(), level: 6) ?? 0;
        Assert.Equal(0.25, ramp, 6);

        double q1 = Damage("Q");
        Assert.Equal(1 + ramp, Damage("Q2") / q1, 6);
        Assert.Equal(1 + 2 * ramp, Damage("Q3") / q1, 6);
    }
}
