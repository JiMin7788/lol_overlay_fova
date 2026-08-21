using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 487) The combo editor had no damage display at all. Every knob added since loop 471 — the
/// sweet-spot and upgrade toggles, the wall checkbox, the stack, distance and exposure dials —
/// changed a number nobody could see until they pressed the hotkey in a live game, which is the one
/// moment you are not editing a combo.
///
/// <para>These pin the two things that make a preview trustworthy rather than merely present: it
/// must never publish (previewing over a live game cannot be allowed to make a card appear), and out
/// of game it must say that its basis is a stated reference, not a prediction.</para>
/// </summary>
public class ComboPreviewTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ComboPreviewTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "ComboPreview_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private const string ChampId = "Aatrox";

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
            BaseStats = new ChampionBaseStats { Ad = 60, Hp = 650 },
            StatsPerLevel = new ChampionStatsPerLevel { Ad = 5, Hp = 114 },
        };
    }

    private static ChampionData Dummy() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = 2000, Armor = 40, Mr = 40 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static GameSnapshot LiveSnap()
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2,
            Stats = new ActivePlayerStats
            {
                AttackDamage = 250, AbilityPower = 0,
                AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
            },
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

    private static ComboGraph Graph(params (string Slot, bool? Met)[] nodes)
    {
        var list = new List<ComboNode>();
        for (int i = 0; i < nodes.Length; i++)
            list.Add(new ComboNode(
                Id: $"{nodes[i].Slot}_{i}", NodeType: ComboNodeType.Skill, Name: nodes[i].Slot,
                Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
                RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0)
                with { UserConditionMet = nodes[i].Met });
        return new ComboGraph(list, Array.Empty<ComboEdge>());
    }

    private ComboRunner NewRunner(Func<GameSnapshot?> snapshot)
    {
        ChampionRepository.Initialize(new[] { FromBin(ChampId), Dummy() });
        var config = new ConfigManager(_configPath);
        var runner = new ComboRunner(new ComboEngine(new DamageEngine(), new RuneEngine()), config, snapshot);
        runner.Start();
        return runner;
    }

    // ── out of game: a stated reference, and it says so ──────────────────────────────

    [Fact]
    public void WithNoGameRunning_ThePreviewIsTheStatedReference()
    {
        using var runner = NewRunner(() => null);
        var preview = runner.ComputePreview(ChampId, Graph(("Q", null)));

        Assert.NotNull(preview);
        Assert.False(preview!.IsLive);
        Assert.Equal(ComboRunner.ReferenceLevel, preview.ReferenceLevel);
        // Base attack damage grown to 18 with no items — a real number, not a guess, and enough for
        // an AD ability to resolve to something.
        Assert.True(preview.Resolved > 0);
    }

    [Fact]
    public void WithAGameRunning_ThePreviewUsesIt()
    {
        var snap = LiveSnap();
        using var runner = NewRunner(() => snap);
        var preview = runner.ComputePreview(ChampId, Graph(("Q", null)));

        Assert.NotNull(preview);
        Assert.True(preview!.IsLive);
        Assert.Equal("Target", preview.TargetChampion);

        // …and it is bigger than the reference, because the live attacker has 250 AD against the
        // reference's base growth. The point of the assertion is that the two bases are genuinely
        // different computations, not that one is nicer.
        using var offline = NewRunner(() => null);
        Assert.NotEqual(offline.ComputePreview(ChampId, Graph(("Q", null)))!.Resolved, preview.Resolved, 1);
    }

    // ── the thing that must not happen ───────────────────────────────────────────────

    [Fact]
    public void PreviewingNeverPublishesACard()
    {
        var snap = LiveSnap();
        using var runner = NewRunner(() => snap);

        int published = 0;
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", _ => Interlocked.Increment(ref published));

        for (int i = 0; i < 5; i++)
            Assert.NotNull(runner.ComputePreview(ChampId, Graph(("Q", null), ("W", null))));

        // A preview over a live game must not put a combo card on the player's screen.
        Assert.Equal(0, published);
    }

    // ── the reason the preview exists: a knob you can see ────────────────────────────

    [Fact]
    public void AnUntouchedKnobShowsASpan_AndTickingItCollapsesToOneNumber()
    {
        using var runner = NewRunner(() => null);

        // Aatrox Q carries the sweet-spot toggle (loop 483). Untouched, the preview must span the
        // ordinary hit and the edge; decided, it must be exactly one of them.
        var unset = runner.ComputePreview(ChampId, Graph(("Q", null)))!;
        Assert.True(unset.Max - unset.Min > 1.0, $"no span: {unset.Min:0.##}..{unset.Max:0.##}");

        var ticked = runner.ComputePreview(ChampId, Graph(("Q", true)))!;
        Assert.Equal(ticked.Min, ticked.Max, 1);
        Assert.Equal(unset.Max, ticked.Resolved, 1);

        var unticked = runner.ComputePreview(ChampId, Graph(("Q", false)))!;
        Assert.Equal(unticked.Min, unticked.Max, 1);
        Assert.Equal(unset.Min, unticked.Resolved, 1);
    }

    [Fact]
    public void AnEmptySequenceHasNothingToPreview()
    {
        using var runner = NewRunner(() => null);
        Assert.Null(runner.ComputePreview(ChampId, new ComboGraph(Array.Empty<ComboNode>(), Array.Empty<ComboEdge>())));
        Assert.Null(runner.ComputePreview("", Graph(("Q", null))));
    }
}
