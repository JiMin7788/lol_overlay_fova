using System.IO;
using System.Linq;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // disambiguate from Overlay.Core.Damage.ComboNode

namespace Overlay.Core.Tests;

/// <summary>
/// Akali's passive (Assassin's Mark) is an ON-ABILITY effect curated <c>appliesTo: ["AA"]</c> — it
/// rides her NEXT BASIC ATTACK, not the ability that spawns the ring. The combo runner's combo-wide
/// auto-attach of on-ability bonuses used to ignore <c>appliesTo</c> and staple the passive onto
/// every Q/W/E/R cast, so a bare Q read as Q + P (roughly double) and every ability over-counted.
///
/// <para>These pin the fix: an <c>appliesTo</c>-restricted on-ability rider is NOT auto-appended to
/// an ability it does not apply to, while an UNRESTRICTED on-ability passive (Vel'Koz, null
/// appliesTo) still attaches to every ability exactly as before.</para>
/// </summary>
public class GoldenAkaliPassiveTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private ChampionData _akali = null!;

    public GoldenAkaliPassiveTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenAkaliPassiveTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // AP 100, no AD, rank 1 — Akali Q resolves to 45 + 0.60×100 = 105, E1 to 21 + 0.33×100 = 54.
    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 0, AbilityPower = 100, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    [Fact]
    public void AkaliAbility_DoesNotStapleTheAAOnlyPassive_OnCast()
    {
        _akali = LoadChampionFromBin("Akali");
        ChampionRepository.Initialize(new[] { _akali });

        var q = RunCombo("Akali", "Q");
        // Q alone, against a 0-resist fallback target, is exactly its own calc (105) — NOT Q + a
        // phantom passive proc (~210). And no bonus node was appended.
        AssertClose(105, q.Total, "Akali Q resolved");
        Assert.DoesNotContain(q.NodeIds, id => id.Contains("#bonus"));

        var e = RunCombo("Akali", "E");
        AssertClose(54, e.Total, "Akali E resolved");
        Assert.DoesNotContain(e.NodeIds, id => id.Contains("#bonus"));
    }

    [Fact]
    public void UnrestrictedOnAbilityPassive_StillAttaches_ToEveryAbility()
    {
        // Vel'Koz's passive is an on-ability effect with NO appliesTo restriction, so it must still
        // ride every ability cast — the fix filters only the explicitly-restricted riders.
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Velkoz") });

        var q = RunCombo("Velkoz", "Q");
        Assert.Contains(q.NodeIds, id => id.Contains("#bonus"));
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private (double Total, System.Collections.Generic.List<string> NodeIds) RunCombo(string champ, string slot)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 1, Stats = Stats() };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = champ;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "c");
        editor.AddNode(draft.Id, new ComboNode(slot + "_" + Guid.NewGuid().ToString("N"), ComboNodeType.Skill, slot,
            0, 0, 0, ComboDamageType.Magic, 1.0, 0, 0, 0, 0, 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return (received!.TotalDamage, received.NodeBreakdown.Select(n => n.NodeId).ToList());
    }

    private static void AssertClose(double expected, double actual, string what)
        => Assert.True(System.Math.Abs(actual - expected) <= 1.0,
            $"{what}: expected {expected} ±1, got {actual:0.##} (Δ={actual - expected:0.##})");

    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{championId.ToLowerInvariant()}.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
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
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
