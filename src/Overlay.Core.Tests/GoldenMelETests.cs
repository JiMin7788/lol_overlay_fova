using System.IO;
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
/// Mel E (Solar Snare) golden — the orb-proximity DoT is now reflected, not just the direct hit.
/// Pins two things:
/// <list type="bullet">
/// <item>the curated calc values: direct <c>Damage</c> = 60 and <c>AreaDamagePerSecond</c> = 16 per
/// second at rank 1 / 0 AP, matching the wiki (60/105/150/195/240 direct, 16/28/40/52/64 per s);</item>
/// <item>the root-locked exposure model: because E ROOTS the target, its short DoT is GUARANTEED, so
/// the resolved combo damage includes it — direct 60 + 0.5 s × 16 = 68 — and the exposure ceiling
/// (angle variance) tops out at 0.75 s × 16, i.e. RangeMax = 72. (At the early-game AP a real player
/// carries this is the 74-78 the ability deals a 0-armour dummy.)</item>
/// </list>
/// This is the regression guard for <c>guaranteedSeconds</c>: without it the resolved damage would
/// fall back to the escapable-zone default of 60 (direct only).
/// </summary>
public class GoldenMelETests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;
    private readonly ChampionData _mel;

    public GoldenMelETests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenMelETests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
        _mel = LoadChampionFromBin("Mel");
        ChampionRepository.Initialize(new[] { _mel });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static ActivePlayerStats Rank1() => new()
    {
        AttackDamage = 60, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 0,
    };

    [Fact]
    public void CalcValues_MatchWiki_AtRank1()
    {
        AssertClose(60, SkillDamage.ComputeCalcDamage(_mel, "E", "Damage", Rank1(), 1)!.Value, "direct impact");
        AssertClose(16, SkillDamage.ComputeCalcDamage(_mel, "E", "AreaDamagePerSecond", Rank1(), 1)!.Value, "DoT per second");
    }

    [Fact]
    public void ResolvedDamage_IncludesTheGuaranteedRootLockedDoT_NotDirectOnly()
    {
        var result = RunE();
        // direct 60 + guaranteed 0.5 s of the 16/s field = 68, NOT the direct-only 60 an escapable
        // zone would resolve to.
        AssertClose(68, result.TotalDamage, "E resolved (direct + guaranteed DoT)");
        Assert.True(result.TotalDamage > 60 + 1, "the guaranteed DoT must lift the resolved value above the bare direct hit");
    }

    [Fact]
    public void RangeCeiling_IsTheFullOrbDwell_NotTheWholeSnareRoot()
    {
        var result = RunE();
        // The exposure ceiling is the angle-dependent orb dwell (0.75 s), so 60 + 0.75 × 16 = 72 —
        // not the old 1.5 s root-duration assumption (which would have reached 84).
        AssertClose(72, result.RangeMax, "E range ceiling (direct + max orb dwell)");
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private ComboResult RunE()
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 1, PlayerCount = 1, Stats = Rank1() };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = "Mel";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 1;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Mel", "c");
        editor.AddNode(draft.Id, new ComboNode("E_" + Guid.NewGuid().ToString("N"), ComboNodeType.Skill, "E",
            0, 0, 0, ComboDamageType.Magic, 1.0, 0, 0, 0, 0, 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!;
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
