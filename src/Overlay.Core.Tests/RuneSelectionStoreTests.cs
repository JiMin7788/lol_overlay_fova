using Overlay.Core.Config;
using Overlay.Core.Runes;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof that a rune selection saved via <see cref="RuneSelectionStore"/> survives a
/// simulated app restart (ConfigManager dispose + reload from the same file) — the same
/// cross-restart guarantee ConfigManagerTests already proves for <c>combos.saved.{id}</c>, applied
/// to the new <c>runes.selections.{championId}</c> schema home (<see cref="RunesConfig"/>).
/// </summary>
public class RuneSelectionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public RuneSelectionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "RuneSelectionStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Save_Then_Restart_LoadsIdenticalSelection_IncludingManualFlags()
    {
        var selection = new RuneSelection(
            "Aatrox",
            new[] { "8126", "9999" }, // one manual (Cheap Shot), one auto-trackable (synthetic)
            new Dictionary<string, bool> { ["8126"] = true });

        using (var config = new ConfigManager(_path))
        {
            RuneSelectionStore.Save(config, selection);
            // Dispose flushes the debounced write — the "restart" boundary.
        }

        using var reloaded = new ConfigManager(_path);
        var loaded = RuneSelectionStore.Load(reloaded, "Aatrox");

        Assert.NotNull(loaded);
        Assert.Equal(selection.ChampionId, loaded!.ChampionId);
        Assert.Equal(selection.SelectedRuneIds, loaded.SelectedRuneIds);
        Assert.True(loaded.ManualFlags["8126"]);
        Assert.False(loaded.ManualFlags.ContainsKey("9999")); // never toggled -> no entry, not a fabricated false
    }

    [Fact]
    public void Load_NoSelectionEverSaved_ReturnsNull_NotAFabricatedDefault()
    {
        using var config = new ConfigManager(_path);
        Assert.Null(RuneSelectionStore.Load(config, "Zed"));
    }

    [Fact]
    public void ToUserRuneConfig_NullSelection_IsEmpty()
    {
        var config = RuneSelectionStore.ToUserRuneConfig(null);
        Assert.Empty(config.SelectedRuneIds);
    }

    [Fact]
    public void DifferentChampions_PersistIndependently_UnderTheirOwnKey()
    {
        using (var config = new ConfigManager(_path))
        {
            RuneSelectionStore.Save(config, new RuneSelection("Aatrox", new[] { "8126" }, new Dictionary<string, bool>()));
            RuneSelectionStore.Save(config, new RuneSelection("Zed", new[] { "8369" }, new Dictionary<string, bool>()));
        }

        using var reloaded = new ConfigManager(_path);
        var aatrox = RuneSelectionStore.Load(reloaded, "Aatrox");
        var zed = RuneSelectionStore.Load(reloaded, "Zed");

        Assert.Equal(new[] { "8126" }, aatrox!.SelectedRuneIds);
        Assert.Equal(new[] { "8369" }, zed!.SelectedRuneIds);
    }
}
