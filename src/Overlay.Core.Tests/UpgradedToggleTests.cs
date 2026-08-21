using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 479) The empowered FORM of an ability, which had no way to be stated. Viktor's evolution,
/// Syndra's Transcendent W, Udyr's Awakened claw and Jhin's fourth shot are all facts about the
/// champion or the shot rather than a number on a bar, so the Live Client API reports none of them
/// — <see cref="ConditionType.Upgraded"/> is the assertion, UserAssumed and default unmet.
///
/// <para>Also pinned here: two shapes that had to exist first. A MET half that is a %HP FRACTION
/// (Jhin's 15% of missing health, Udyr's %maxHP lightning) resolves through the %HP path via
/// <c>metHpPercentCalc</c> — read flat it would have contributed 0.15 damage. And a conditional
/// RIDER — an on-hit bonus with no hit of its own on any ability slot — has to offer its checkbox on
/// the AA node, or Jhin's fourth shot would be curated and unreachable.</para>
/// </summary>
public class UpgradedToggleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public UpgradedToggleTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "Upgraded_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private const double TargetHp = 3000;

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
        // Base AD == total AD so every bonus-AD coefficient drops out and the flat rank terms are
        // the whole number, the same readability trick BriarWallSlamTests uses.
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

    /// <summary>Runs a combo of <paramref name="slots"/> ("Q", "AA", …) for one champion, with every
    /// node carrying the same <paramref name="upgraded"/> assertion.</summary>
    private ComboResult Run(string championId, bool? upgraded, params string[] slots)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(championId, $"c_{upgraded?.ToString() ?? "unset"}_{string.Join("", slots)}");
        for (int i = 0; i < slots.Length; i++)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{slots[i]}_{i}",
                NodeType: slots[i] == "AA" ? ComboNodeType.Aa : ComboNodeType.Skill,
                Name: slots[i], Cooldown: 0, Mana: 0, Damage: 0,
                DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
                CastTime: 0, Delay: 0, TravelTime: 0) with { UserConditionMet = upgraded });
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

    private void Init(string championId) =>
        ChampionRepository.Initialize(new[] { FromBin(championId), Dummy() });

    // ── the plain flat-MetCalc form ───────────────────────────────────────────────────

    [Fact]
    public void ViktorE_AftershockIsTheOnlyEvolutionThatAddsDamage()
    {
        Init("Viktor");
        var viktor = ChampionRepository.Get("Viktor")!;
        double laser = SkillDamage.ComputeCalcDamage(viktor, "E", "LaserDamage", Stats(), 11)!.Value;
        double after = SkillDamage.ComputeCalcDamage(viktor, "E", "AftershockDamage", Stats(), 11)!.Value;
        Assert.True(after > 0);

        var unset = Run("Viktor", null, "E");
        var off = Run("Viktor", false, "E");
        var on = Run("Viktor", true, "E");

        // Unticked resolves to the un-evolved beam, and the range reaches the evolved total.
        Assert.Equal(off.TotalDamage, unset.TotalDamage, 2);
        Assert.True(unset.RangeMax > unset.RangeMin + 1);
        // Ratio, not absolute: the engine mitigates by the dummy's resists, and both halves are
        // magic, so the mitigated split keeps the raw proportion.
        Assert.Equal((laser + after) / laser, on.TotalDamage / off.TotalDamage, 3);
    }

    [Fact]
    public void SyndraW_TranscendentAddsTrueDamage()
    {
        Init("Syndra");
        var syndra = ChampionRepository.Get("Syndra")!;
        double throwDmg = SkillDamage.ComputeCalcDamage(syndra, "W", "ThrowDamage", Stats(), 11)!.Value;
        double bonus = SkillDamage.ComputeCalcDamage(syndra, "W", "PassiveBonusDamage", Stats(), 11)!.Value;
        // The wiki's 12% (+2% per 100 AP); no AP in these stats, so exactly 12%.
        Assert.Equal(0.12, bonus / throwDmg, 3);

        double off = Run("Syndra", false, "W").TotalDamage;
        double on = Run("Syndra", true, "W").TotalDamage;
        // The added half is TRUE damage against a zero-resist dummy, so it lands at face value on
        // top of the (also unmitigated here) magic throw.
        Assert.Equal(off + bonus, on, 2);
    }

    // ── the %HP MET half (metHpPercentCalc) ──────────────────────────────────────────

    [Fact]
    public void UdyrQ_AwakenedLightningIsPercentMaxHealth_NotAFlatFraction()
    {
        Init("Udyr");
        var udyr = ChampionRepository.Get("Udyr")!;
        double perAttack = SkillDamage.ResolveHpPercentCalc(
            udyr, "Q", "EmpoweredLightningBonusMax", Stats(), 11)!.Value;
        Assert.InRange(perAttack, 0.05, 0.30); // a fraction of max HP, not a damage number

        double off = Run("Udyr", false, "Q").TotalDamage;
        double on = Run("Udyr", true, "Q").TotalDamage;

        // Two empowered attacks, each carrying the six-strike total. Read as a flat MetCalc this
        // difference would have been ~0.4 instead of ~2 × 18% × 3000.
        Assert.Equal(2 * perAttack * TargetHp, on - off, 1);
        Assert.True(on - off > 100);
    }

    [Fact]
    public void JhinFourthShot_IsAMissingHealthRiderOnTheAutoAttack()
    {
        Init("Jhin");

        double plain = Run("Jhin", false, "AA").TotalDamage;
        double fourth = Run("Jhin", true, "AA").TotalDamage;
        Assert.True(fourth > plain + 1);

        // 15/20/25% of MISSING health: the dummy starts at full health, so the bonus can only come
        // from what the auto itself took off first. That is what makes it a missing-health term
        // rather than a flat one — it is far smaller than the same percentage of max health.
        Assert.True(fourth - plain < 0.25 * TargetHp);
    }

    [Fact]
    public void AConditionalRiderOffersItsCheckboxOnTheAutoAttack()
    {
        // The trap this closes: Jhin's fourth shot has no hit on any Q/W/E/R slot, so a badge lookup
        // that only read GetHits would leave it curated and unreachable.
        Init("Jhin");
        Assert.NotNull(SkillDamageDb.GetConditionalHit("Jhin", "AA"));
        Assert.Null(SkillDamageDb.GetConditionalHit("Jhin", "Q"));
    }

    [Fact]
    public void UpgradedIsUserAssumed()
    {
        // Section 76's trap: a new UserAssumed ConditionType must be registered in BOTH the enum and
        // ConditionResolution.IsUserAssumed, or it silently falls through to AutoResolvable, the
        // checkbox never appears, and the bonus resolves off live state it cannot read.
        Assert.True(ConditionResolution.IsUserAssumed(ConditionType.Upgraded));
    }

    // ── the empowered-auto riders (no new condition needed — the engine already had this) ──

    [Fact]
    public void DravenQ_ScoresOnTheAutoItEmpowers_NotOnTheCast()
    {
        Init("Draven");

        // A Spinning Axe that is never thrown at anything deals nothing.
        Assert.Equal(0, Run("Draven", null, "Q").TotalDamage, 2);

        double aa = Run("Draven", null, "AA").TotalDamage;
        double qaa = Run("Draven", null, "Q", "AA").TotalDamage;
        Assert.True(qaa > aa + 1);

        // Two axes is the cap the wiki states, so a third auto in the same combo is plain.
        double axe = qaa - aa;
        double twoAxes = Run("Draven", null, "Q", "Q", "AA", "AA", "AA").TotalDamage;
        Assert.Equal(3 * aa + 2 * axe, twoAxes, 1);
    }

    [Fact]
    public void YunaraQ_PassiveRidesEveryAuto_AndTheActiveDoublesIt()
    {
        Init("Yunara");

        double aa = Run("Yunara", null, "AA").TotalDamage;
        double bare = Run("Yunara", null, "Q").TotalDamage;
        double qaa = Run("Yunara", null, "Q", "AA").TotalDamage;

        // Casting Q deals no damage of its own — it grants attack speed and doubles the on-hit.
        Assert.Equal(0, bare, 2);

        // The passive half rides a plain auto already (it does not need Q to have been pressed),
        // and the active adds exactly the same amount again.
        double passive = aa - Stats().AttackDamage;
        Assert.True(passive > 0);
        Assert.Equal(aa + passive, qaa, 1);
    }
}
