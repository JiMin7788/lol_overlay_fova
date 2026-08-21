using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 482) Four abilities that are more than one cast, and were curated as one. Camille's
/// Precision Protocol, Lee Sin's Sonic Wave, Wukong's Cyclone and Shyvana's Emberstrike each fold a
/// recast the player has to earn — and folding it into the base node meant the calculator could only
/// ever show the whole thing, never the half that actually landed.
///
/// <para>Two of them were also attributing damage to the wrong node: Camille's Q, like Draven's in
/// loop 479, deals nothing on cast and empowers an attack, so its number belonged to the attack.</para>
/// </summary>
public class MultiCastSplitTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public MultiCastSplitTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "MultiCast_" + Guid.NewGuid().ToString("N"));
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
        AttackDamage = 200, AbilityPower = 0,
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

    private double Run(string championId, params string[] slots)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(championId, $"c_{string.Join("", slots)}");
        for (int i = 0; i < slots.Length; i++)
            editor.AddNode(draft.Id, new ComboNode(
                Id: $"{slots[i]}_{i}",
                NodeType: slots[i] == "AA" ? ComboNodeType.Aa : ComboNodeType.Skill,
                Name: slots[i], Cooldown: 0, Mana: 0, Damage: 0,
                DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
                CastTime: 0, Delay: 0, TravelTime: 0));
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
        return received!.TotalDamage;
    }

    // ── every new slot resolves to a real number ─────────────────────────────────────

    [Theory]
    [InlineData("Camille", "Q2")]
    [InlineData("LeeSin", "Q2")]
    [InlineData("MonkeyKing", "R2")]
    [InlineData("Shyvana", "Q2")]
    [InlineData("Shyvana", "Q3")]
    [InlineData("Shyvana", "EDragon")]
    public void EveryNewSlotIsInThePaletteAndResolves(string championId, string slot)
    {
        Init(championId);
        Assert.Contains(slot, ComboEditor.LoadPalette(championId).AvailableNodes.Select(n => n.Id));

        // Camille's Q casts are riders, so they need the attack they empower to show a number.
        double dmg = championId == "Camille" ? Run(championId, slot, "AA") - Run(championId, "AA")
                                             : Run(championId, slot);
        Assert.True(dmg > 0, $"{championId} {slot} resolved to {dmg:0.##} — silent-zero class");
    }

    // ── Camille: the cast carries nothing, the attack carries it ─────────────────────

    [Fact]
    public void CamilleQ_EmpowersOneAttackEach_AndTheRecastIsDouble()
    {
        Init("Camille");
        Assert.Equal(0, Run("Camille", "Q"), 2);

        double aa = Run("Camille", "AA");
        double first = Run("Camille", "Q", "AA") - aa;
        double second = Run("Camille", "Q2", "AA") - aa;
        Assert.True(first > 0);
        // 20-40% AD on the first cast, doubled on the recast (BIN QEmpoweredAmp 2.0).
        Assert.Equal(2 * first, second, 1);

        // One cast empowers ONE attack: the second attack after a single Q is plain.
        Assert.Equal(aa, Run("Camille", "Q", "AA", "AA") - Run("Camille", "Q", "AA"), 1);

        // …and a Q2 node does not also arm Q's rider just because it starts with the same letter.
        Assert.Equal(second, Run("Camille", "Q2", "AA") - aa, 1);
    }

    // ── Lee Sin: the recast reads the health it actually lands on ────────────────────

    [Fact]
    public void LeeSinQ2_ScalesWithMissingHealth_AndQAloneIsJustTheFirstCast()
    {
        Init("LeeSin");
        double q = Run("LeeSin", "Q");
        double q2 = Run("LeeSin", "Q2");
        Assert.True(q > 0);

        // Against a full-health target the recast is its minimum, which is the same formula as the
        // first cast (both Q1/Q2 BaseDamage and ADRatio are identical in the BIN).
        Assert.Equal(q, q2, 1);

        // Land it after the first cast has taken health off and it grows, up to double at 0% health.
        double sequence = Run("LeeSin", "Q", "Q2");
        Assert.True(sequence > q + q2, $"Q2 did not grow after Q: {sequence:0.##} vs {q + q2:0.##}");
        Assert.True(sequence - q <= 2 * q2 + 0.5);
    }

    // ── Wukong: two spins ───────────────────────────────────────────────────────────

    [Fact]
    public void WukongR2_IsTheSecondSpin_IdenticalToTheFirst()
    {
        Init("MonkeyKing");
        double r = Run("MonkeyKing", "R");
        double both = Run("MonkeyKing", "R", "R2");
        Assert.True(r > 0);
        // The %maxHP half is read against the target's max health, which does not change, so the two
        // spins are the same number and the pair is exactly twice one.
        Assert.Equal(2 * r, both, 1);
    }

    // ── Shyvana: three casts, and the dragon bite is true damage ─────────────────────

    [Fact]
    public void ShyvanaQ_IsThreeCasts_TheThirdBeingDragonFormTrueDamage()
    {
        Init("Shyvana");
        double q = Run("Shyvana", "Q");
        double q2 = Run("Shyvana", "Q2");
        double q3 = Run("Shyvana", "Q3");

        // The tail slam repeats the slash exactly; the dragon bite is 150% of it.
        Assert.Equal(q, q2, 1);
        Assert.Equal(1.5 * q, q3, 1);

        Assert.Equal(HitDamageType.True, SkillDamageDb.GetHits("Shyvana", "Q3")![0].Type);
        Assert.Equal(HitDamageType.Physical, SkillDamageDb.GetHits("Shyvana", "Q2")![0].Type);
    }

    [Fact]
    public void ShyvanaEDragon_IsTheHumanCastScaledUp_PlusItsBurn()
    {
        Init("Shyvana");
        double e = Run("Shyvana", "E");
        double eDragon = Run("Shyvana", "EDragon");

        // Both halves of E — the flat hit and the 5% of maximum health — carry the same 1.25 dragon
        // multiplier, and the burn contributes nothing until the user states an exposure. So the
        // dragon cast is exactly a quarter more than the human one.
        Assert.Equal(1.25 * e, eDragon, 1);
    }
}
