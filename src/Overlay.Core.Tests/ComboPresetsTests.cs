using Overlay.Core.Combo;
using Overlay.Core.ChampionDb;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;

namespace Overlay.Core.Tests;

/// <summary>The bundled combo presets only use slots the champion's palette really has, and
/// every entry is sourced. A preset with an unknown slot would fail at [추가] in front of a user.</summary>
public class ComboPresetsTests
{
    private static readonly string PresetPath =
        Path.Combine(AppContext.BaseDirectory, "data", "combo_presets.json");

    private static readonly HashSet<string> Universal = new(StringComparer.Ordinal)
        { "P", "Q", "W", "E", "R", "AA", "Ignite", "Flash" };

    [Fact]
    public void EveryPresetSlot_IsOnTheChampionsPalette()
    {
        var all = ComboPresets.Parse(File.ReadAllText(PresetPath));
        var bad = new List<string>();
        foreach (var (champion, presets) in all)
        {
            var curated = SkillDamageDb.GetCuratedSlotKeys(champion);
            if (curated.Count == 0) { bad.Add($"{champion}: no curation file (wrong id?)"); continue; }
            foreach (var preset in presets)
                foreach (var slot in preset.Sequence)
                    if (!Universal.Contains(slot) && !curated.Contains(slot))
                        bad.Add($"{champion} '{preset.Name}': {slot}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void EveryPreset_ListsASource()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(PresetPath));
        var missing = new List<string>();
        foreach (var champ in doc.RootElement.EnumerateObject())
        {
            if (champ.Name.StartsWith('_')) continue;
            foreach (var p in champ.Value.EnumerateArray())
                if (!p.TryGetProperty("sources", out var s) || s.GetArrayLength() == 0)
                    missing.Add($"{champ.Name} '{p.GetProperty("nameKo").GetString()}'");
        }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void Parse_SkipsNotesAndIncompleteEntries()
    {
        var all = ComboPresets.Parse("""
            { "_note": "x",
              "Zed": [ {"kind":"trade","nameKo":"견제","sequence":["W","E","Q"]},
                       {"kind":"allin","nameKo":"","sequence":["R"]},
                       {"kind":"allin","nameKo":"빈 순서","sequence":[]} ] }
            """);
        var zed = Assert.Single(all);
        Assert.Equal("Zed", zed.Key);
        var preset = Assert.Single(zed.Value);
        Assert.Equal(new[] { "W", "E", "Q" }, preset.Sequence);
    }
}

/// <summary>ComboEditor.AddPreset: a preset becomes a normal saved combo built from the palette.</summary>
public class ComboEditorPresetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ComboEditorPresetTests_" + Guid.NewGuid().ToString("N"));

    public ComboEditorPresetTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        Directory.CreateDirectory(_dir);
        ChampionRepository.Initialize(new[]
        {
            new ChampionData
            {
                Id = "Zed", Name = "Zed",
                Skills = new Dictionary<string, SkillData>
                {
                    ["Q"] = new() { Key = "Q", Name = "Razor Shuriken" },
                    ["W"] = new() { Key = "W", Name = "Living Shadow" },
                    ["E"] = new() { Key = "E", Name = "Shadow Slash" },
                    ["R"] = new() { Key = "R", Name = "Death Mark" },
                },
            },
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ComboEditor NewEditor(out ConfigManager config)
    {
        config = new ConfigManager(Path.Combine(_dir, "user_config.json"));
        return new ComboEditor(new ComboEngine(new DamageEngine(), new RuneEngine()), config);
    }

    [Fact]
    public void AddPreset_SavesComboWithPaletteNodesAndSummoners()
    {
        var editor = NewEditor(out var config);
        var (draft, hotkey) = editor.AddPreset("Zed",
            new ComboPresets.Preset("allin", "풀콤", new[] { "W", "R", "E", "Q", "Ignite", "AA" }));
        Assert.Equal("ALT+1", hotkey);

        Assert.Equal("풀콤", draft.Name);
        Assert.Equal(new[] { "W_0", "R_1", "E_2", "Q_3", "Ignite_4", "AA_5" }, draft.Graph.Nodes.Select(n => n.Id));
        Assert.Equal(ComboNodeType.Summoner, draft.Graph.Nodes[4].NodeType);
        Assert.Equal(ComboNodeType.Aa, draft.Graph.Nodes[5].NodeType);
        Assert.NotNull(config.Get("combos.saved." + draft.Id));
        config.Dispose();
    }

    [Fact]
    public void AddPreset_BindsTheNextFreeAltNumber_PerChampion()
    {
        var editor = NewEditor(out var config);
        var own = editor.CreateCombo("Zed", "내 콤보");
        editor.SaveCombo(own.Id);
        editor.BindHotkey(own.Id, "ALT+1");                  // this champion already uses ALT+1
        config.Set("hotkeys.comboSlots.ALT+2::Ahri", "x");   // another champion's ALT+2 does not count

        var trade = new ComboPresets.Preset("trade", "견제", new[] { "W", "E", "Q" });
        var allin = new ComboPresets.Preset("allin", "풀콤", new[] { "W", "R", "E", "Q" });
        Assert.Equal("ALT+2", editor.AddPreset("Zed", trade).Hotkey);
        Assert.Equal("ALT+3", editor.AddPreset("Zed", allin).Hotkey);
        config.Dispose();
    }

    [Fact]
    public void AddPreset_UnknownSlot_SavesNothing()
    {
        var editor = NewEditor(out var config);
        Assert.Throws<ComboImportException>(() =>
            editor.AddPreset("Zed", new ComboPresets.Preset("trade", "잘못됨", new[] { "W", "Q2" })));
        Assert.Empty(config.Get("combos.saved") as IDictionary<string, object?> ?? new Dictionary<string, object?>());
        config.Dispose();
    }
}
