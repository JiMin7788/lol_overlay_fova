using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 483) The last of the combo-editor reports. Three of them are the same idea wearing different
/// names — Aatrox's Q edge, Xerath's W epicentre and Lillia's W inner circle are all a SWEET SPOT,
/// a narrow part of the shape worth a multiplier the BIN states outright — and two are terrain:
/// Poppy's charge deals its damage again on a wall, and Skarner's deals its damage ONLY on one.
///
/// <para>Skarner is the one that was wrong rather than incomplete. Ixtal's Impact has no initial
/// hit at all, so curating its wall damage unconditionally meant every E node claimed a slam that
/// may never have happened.</para>
/// </summary>
public class SweetSpotAndWallTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public SweetSpotAndWallTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "SweetSpot_" + Guid.NewGuid().ToString("N"));
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
            BaseStats = new ChampionBaseStats { Ad = 100, Hp = 1000 },
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
        AttackDamage = 200, AbilityPower = 100, MaxHealth = 2500,
        AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
    };

    private static GameSnapshot Snap(string championId)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2, Stats = Stats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = championId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 11;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 11;
        return snap;
    }

    private void Init(string championId) =>
        ChampionRepository.Initialize(new[] { FromBin(championId), Dummy() });

    private ComboResult Run(string championId, string slot, bool? met)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(championId, $"c_{slot}_{met?.ToString() ?? "unset"}");
        editor.AddNode(draft.Id, new ComboNode(
            Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Magic, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0) with { UserConditionMet = met });
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => Snap(championId));
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

    // ── the sweet spots, each against the multiplier the BIN states ──────────────────

    [Theory]
    [InlineData("Aatrox", "Q", 1.75)]     // QEdgeDamage = QDamage × (1 + QSweetSpotBonus 0.75)
    [InlineData("Xerath", "W", 1.667)]    // SweetSpotTotalDamage = TotalDamage × SweetSpotMultiplier
    [InlineData("Lillia", "W", 3.0)]      // FlatDamageSweetSpot — the wiki's "200% increased"
    public void ASweetSpotIsWorthExactlyItsBinMultiplier(string championId, string slot, double multiplier)
    {
        Init(championId);
        double ordinary = Run(championId, slot, false).TotalDamage;
        double sweet = Run(championId, slot, true).TotalDamage;
        Assert.True(ordinary > 0);
        Assert.Equal(multiplier, sweet / ordinary, 2);
    }

    [Fact]
    public void AnUntouchedSweetSpotSpansBothHalves_AndDefaultsToTheOrdinaryHit()
    {
        Init("Aatrox");
        var unset = Run("Aatrox", "Q", null);
        double ordinary = Run("Aatrox", "Q", false).TotalDamage;

        // Default unmet: the resolved number is the ordinary hit, and the range reaches the edge.
        Assert.Equal(ordinary, unset.TotalDamage, 2);
        Assert.Equal(ordinary, unset.RangeMin, 2);
        Assert.Equal(1.75 * ordinary, unset.RangeMax, 1);
    }

    [Fact]
    public void AllThreeAatroxCastsHaveTheEdge()
    {
        Init("Aatrox");
        foreach (var slot in new[] { "Q", "Q2", "Q3" })
        {
            double ordinary = Run("Aatrox", slot, false).TotalDamage;
            Assert.Equal(1.75, Run("Aatrox", slot, true).TotalDamage / ordinary, 2);
        }
        // …and the sweet spot composes with the per-cast ramp rather than replacing it: the third
        // cast's edge is still bigger than the first's.
        Assert.True(Run("Aatrox", "Q3", true).TotalDamage > Run("Aatrox", "Q", true).TotalDamage + 1);
    }

    [Fact]
    public void SweetSpotIsUserAssumed()
    {
        // Section 76's trap again: registered in the enum AND in ConditionResolution, or the checkbox
        // never appears and cast geometry gets resolved off live state that does not exist.
        Assert.True(ConditionResolution.IsUserAssumed(ConditionType.SweetSpot));
    }

    // ── terrain ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PoppyE_DealsItsDamageAgainOnTheWall()
    {
        Init("Poppy");
        double charge = Run("Poppy", "E", false).TotalDamage;
        double slam = Run("Poppy", "E", true).TotalDamage;
        Assert.True(charge > 0);
        // The wiki's "deals the same physical damage again": exactly double, not a separate formula.
        Assert.Equal(2 * charge, slam, 1);
    }

    [Fact]
    public void SkarnerE_DealsNothingWithoutTerrain()
    {
        Init("Skarner");
        // Ixtal's Impact has NO initial collision damage — if the charge hits no wall, the ability
        // deals nothing at all. The old curation scored the slam unconditionally.
        Assert.Equal(0, Run("Skarner", "E", false).TotalDamage, 2);

        var unset = Run("Skarner", "E", null);
        Assert.Equal(0, unset.TotalDamage, 2);
        // …and the full number is still visible as the ceiling of an untouched node.
        Assert.True(unset.RangeMax > 50);
        Assert.Equal(Run("Skarner", "E", true).TotalDamage, unset.RangeMax, 1);
    }

    // ── the two riders ──────────────────────────────────────────────────────────────

    [Fact]
    public void NidaleeCougarQ_GrowsWithTheTargetsMissingHealth()
    {
        Init("Nidalee");
        double full = Run("Nidalee", "QCougar", null).TotalDamage;
        Assert.True(full > 0);

        // Takedown's whole identity: at rank 5 the amplification tops out at 2.25×, so a cast that
        // follows something else in the sequence is worth more than one that opens it.
        var hit = Assert.Single(SkillDamageDb.GetHits("Nidalee", "QCougar")!);
        Assert.Equal("TakedownDamageAmp", hit.MissingHpBonusDataValue);
    }

    [Fact]
    public void IreliaOnHit_ExistsNow_AndRidesBladesurgeAsWellAsTheAttack()
    {
        Init("Irelia");
        var effects = SkillDamageDb.GetBonusEffects("Irelia", "P");
        var effect = Assert.Single(effects!);
        Assert.Equal(BonusTrigger.OnHit, effect.Trigger);
        // Bladesurge applies on-hit effects, so the dash carries this the same way an attack does.
        Assert.True(effect.AppliesToSlot("AA"));
        Assert.True(effect.AppliesToSlot("Q"));
        Assert.False(effect.AppliesToSlot("W"));

        // Gated at four stacks and defaulting unmet, so a plain attack is unchanged.
        var hit = Assert.Single(effect.Hits);
        Assert.Equal("StackGte", hit.ConditionType);
        Assert.Equal(4, hit.ConditionValue);
        Assert.True(ConditionResolution.IsUserAssumed(ConditionType.StackGte));
    }
}
