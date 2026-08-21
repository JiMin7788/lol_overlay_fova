using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 472) Briar E's wall slam, the first of the conditional variants this project had
/// deliberately left uncurated.
///
/// <para>The old policy was a conservative floor: a positional bonus was omitted entirely rather
/// than assumed to land. These tests pin what replaced it — the bonus exists, it is UserAssumed, and
/// it defaults UNMET, so the floor is byte-identical to the old behaviour and the ceiling is only
/// reached when the user says the slam happened. That is the property that makes curating the rest
/// of the 67 documented omissions safe.</para>
///
/// <para>Numbers are read from the BIN and cross-checked against the Korean wiki text the user
/// supplied: base 80/115/150/185/220 (+1.0 bonus AD)(+1.0 AP), wall bonus 140/215/290/365/440
/// (+2.4)(+2.4), the latter stated as 추가 (ADDITIONAL) — hence a second hit, not a replacement.</para>
/// </summary>
public class BriarWallSlamTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string ChampId = "Briar";

    public BriarWallSlamTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "BriarWall_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ChampionData Briar()
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
        // Base AD equal to total AD, so bonus AD is 0 and both coefficients drop out — the flat
        // rank terms are then the whole number and the expectations stay readable.
        return new ChampionData
        {
            Id = ChampId, Name = ChampId, Skills = skills,
            BaseStats = new ChampionBaseStats { Ad = 100 },
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy() => new()
    {
        Id = "Target", Name = "Target",
        BaseStats = new ChampionBaseStats { Hp = 3000, Armor = 0, Mr = 0 },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 100, AbilityPower = 0,
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

    private double EDamage(bool? wallHit)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(ChampId, "c_" + (wallHit?.ToString() ?? "unset"));
        editor.AddNode(draft.Id, new ComboNode(
            Id: "E_0", NodeType: ComboNodeType.Skill, Name: "E", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Magic, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0) with { UserConditionMet = wallHit });
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
    public void UnsetIsTheOldFloor_AndTickingTheBoxAddsTheSlam()
    {
        ChampionRepository.Initialize(new[] { Briar(), Dummy() });

        double floor = EDamage(null);
        double stated = EDamage(false);
        double ceiling = EDamage(true);

        // Rank 1, no bonus AD, no AP, no resists: base 80, wall bonus 140.
        Assert.Equal(80, floor, 1);
        // An unset knob and an explicit "no" are the same claim.
        Assert.Equal(floor, stated, 6);
        Assert.Equal(220, ceiling, 1);
    }

    [Fact]
    public void TheSlamIsUserAssumed_SoLiveStateCannotTurnItOn()
    {
        // The trap section 76 recorded: a condition missing from ConditionResolution.IsUserAssumed
        // falls through to AutoResolvable, the checkbox never appears, and the bonus silently
        // resolves off live state it has no business reading. Terrain is not in the Live Client API
        // at all, so HitsWall must be user-assumed.
        Assert.True(ConditionResolution.IsUserAssumed(ConditionType.HitsWall));
    }
}
