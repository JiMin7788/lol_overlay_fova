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
/// A multi-cast ability is curated as a base slot plus numbered sub-slots — Akali E splits into
/// <c>E</c> (throw) and <c>E2</c> (recast dash). The overlay sequence used to recognise only bare
/// canonical letters (<c>IsAbilitySlot</c>), so every cast AFTER the first was dropped from the
/// sequence entirely: no chip, hence no icon, on the combo card. These pin that a curated sub-slot
/// now earns its own token (and command-label letter), so the overlay draws a chip for it — the
/// icon itself then resolves to the base ability's art via <c>AbilityIconProvider.CanonicalIconSlot</c>.
/// </summary>
public class MultiCastSequenceTokenTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public MultiCastSequenceTokenTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "MultiCastSeq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Akali") });
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void MultiCastSubSlot_EarnsItsOwnSequenceToken_AndCommandLetter()
    {
        var hud = RunCombo("E", "E2");

        // Both casts are present as ability tokens — the recast (E2) is no longer dropped.
        Assert.NotNull(hud.Sequence);
        var abilities = hud.Sequence!.Where(t => t.IsAbility).Select(t => t.Label).ToList();
        Assert.Equal(new[] { "E", "E2" }, abilities);
        Assert.All(hud.Sequence!, t => Assert.True(t.IsAbility));

        // The command label carries the recast too, so it is not silently missing from the string.
        Assert.Equal("E-E2", hud.CommandLabel);

        // The caster champion is set, so the overlay can resolve each chip's icon.
        Assert.Equal("Akali", hud.CasterChampion);
    }

    [Fact]
    public void SkillPanel_FoldsMultiCastSubSlots_IntoTheBaseSlotTotal()
    {
        // Akali + a 0-resist dummy so magic damage is unmitigated (E box == raw E1 + E2).
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Akali"),
            new ChampionData { Id = "Dummy", Name = "Dummy",
                BaseStats = new ChampionBaseStats { Hp = 1000, Armor = 0, Mr = 0, Ad = 50 },
                StatsPerLevel = new ChampionStatsPerLevel() },
        });

        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 0, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Akali"; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 6;
        snap.Players[1].SummonerName = "Foe"; snap.Players[1].ChampionName = "Dummy"; snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 6;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        using var runner = new ComboRunner(engine, config, () => snap);

        var panel = runner.ComputeSkillPanel(snap);
        Assert.NotNull(panel);
        double e = panel!.Slots.Single(s => s.Slot == "E").Damage;
        // E1 (throw) 21 + E2 (recast) 49 at rank 1 / 0 AP = 70 — the box now folds the recast in,
        // instead of showing only the throw (21).
        Assert.True(System.Math.Abs(e - 70) <= 1.0, $"E box should be E1+E2 ≈ 70, got {e:0.##}");
        Assert.True(e > 22, "the recast (E2) must be included, not just the throw (E1 ≈ 21)");
    }

    // ── harness ──────────────────────────────────────────────────────────────────

    private ComboHudResult RunCombo(params string[] slots)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 6, PlayerCount = 1,
            Stats = new ActivePlayerStats { AttackDamage = 60, AbilityPower = 0, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1 } };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = "Akali";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Akali", "c");
        foreach (var slot in slots)
            editor.AddNode(draft.Id, new ComboNode(slot + "_" + Guid.NewGuid().ToString("N"), ComboNodeType.Skill, slot,
                0, 0, 0, ComboDamageType.Magic, 1.0, 0, 0, 0, 0, 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboHudResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = evt.Payload as ComboHudResult; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!;
    }

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
