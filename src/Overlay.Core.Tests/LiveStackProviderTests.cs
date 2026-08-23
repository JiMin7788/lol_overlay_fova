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
/// (loop 540) <see cref="ComboRunner.LiveStackProvider"/> — the live buff-bar stack source (Nasus Q
/// Siphoning Strike). The Live Client API exposes no buff data (verified live), so the full build's
/// buff-bar vision feeds this; these tests drive the PLUMBING with a fake provider: a stack-scaled
/// hit resolves live stacks when the editor knob is unset, the knob still overrides (the loop-472
/// precedence), and a null provider keeps the pre-existing 0-stack floor byte-identical.
///
/// <para>Nasus Q TotalDamage = BonusDamage + 1.0×totalAD + 1.0×stacks, so against the 0-resist
/// fallback defender the stack term is directly observable as a combo-total difference.</para>
/// </summary>
public class LiveStackProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public LiveStackProviderTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "LiveStackProviderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Nasus") });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void LiveStacks_RaiseAStackScaledHit_WhenTheKnobIsUnset()
    {
        double bare = RunCombo(new[] { "Q" }, liveStacks: null);
        double live = RunCombo(new[] { "Q" }, liveStacks: 300);
        AssertClose(300, live - bare, "Q with 300 live stacks vs 0");
    }

    [Fact]
    public void UserKnob_OverridesTheLiveReading()
    {
        double bare = RunCombo(new[] { "Q" }, liveStacks: null);
        double knobbed = RunCombo(new[] { "Q" }, liveStacks: 300, userStackCount: 50);
        // Loop-472 precedence: a set knob wins over live state.
        AssertClose(50, knobbed - bare, "knob=50 must beat live=300");
    }

    [Fact]
    public void ProviderForAnotherSlot_DoesNotLeakIntoQ()
    {
        double bare = RunCombo(new[] { "Q" }, liveStacks: null);
        double wrongSlot = RunCombo(new[] { "Q" }, liveStacks: 300, providerSlot: "E");
        AssertClose(0, wrongSlot - bare, "stacks keyed to E must not raise Q");
    }

    // ── harness (mirrors AdBuffComboTests) ───────────────────────────────────────

    private double RunCombo(string[] slots, int? liveStacks, int? userStackCount = null,
        string providerSlot = "Q")
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 16, PlayerCount = 1 };
        snap.Stats = new ActivePlayerStats
        {
            AttackDamage = 100, AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = "Nasus";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 16;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Nasus", "c");
        foreach (var s in slots)
        {
            var node = new ComboNode(s + "_" + Guid.NewGuid().ToString("N"), ComboNodeType.Skill, s,
                0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0);
            if (userStackCount is not null) node = node with { UserStackCount = userStackCount };
            editor.AddNode(draft.Id, node);
        }
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        if (liveStacks is int stacks)
            runner.LiveStackProvider = (champ, slot) =>
                champ == "Nasus" && slot.Equals(providerSlot, StringComparison.OrdinalIgnoreCase)
                    ? stacks : null;
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!.TotalDamage;
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
