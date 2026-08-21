using Overlay.Core.Config;
using Overlay.Core.Items;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof that a hypothetical item build saved via <see cref="ItemBuildStore"/>
/// survives a simulated app restart (ConfigManager dispose + reload from the same file) —
/// the same cross-restart guarantee <c>RuneSelectionStoreTests</c> already proves for
/// <c>runes.selections.{championId}</c>, applied to the new
/// <c>items.builds.{championId}</c> schema home (<see cref="ItemsConfig"/>).
/// </summary>
public class ItemBuildStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ItemBuildStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ItemBuildStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Save_Then_Restart_LoadsIdenticalBuild()
    {
        var itemIds = new[] { "1038", "3031" }; // synthetic ids; ItemBuildStore does not validate

        using (var config = new ConfigManager(_path))
        {
            ItemBuildStore.Save(config, "Aatrox", itemIds);
            // Dispose flushes the debounced write — the "restart" boundary.
        }

        using var reloaded = new ConfigManager(_path);
        var loaded = ItemBuildStore.Load(reloaded, "Aatrox");

        Assert.NotNull(loaded);
        Assert.Equal("Aatrox", loaded!.ChampionId);
        Assert.Equal(itemIds, loaded.ItemIds);
    }

    [Fact]
    public void Load_NoBuildEverSaved_ReturnsNull_NotAFabricatedDefault()
    {
        using var config = new ConfigManager(_path);
        Assert.Null(ItemBuildStore.Load(config, "Zed"));
    }

    [Fact]
    public void DifferentChampions_PersistIndependently_UnderTheirOwnKey()
    {
        using (var config = new ConfigManager(_path))
        {
            ItemBuildStore.Save(config, "Aatrox", new[] { "1038" });
            ItemBuildStore.Save(config, "Zed", new[] { "3031" });
        }

        using var reloaded = new ConfigManager(_path);
        var aatrox = ItemBuildStore.Load(reloaded, "Aatrox");
        var zed = ItemBuildStore.Load(reloaded, "Zed");

        Assert.Equal(new[] { "1038" }, aatrox!.ItemIds);
        Assert.Equal(new[] { "3031" }, zed!.ItemIds);
    }

    [Fact]
    public void Save_EmptyBuild_RoundTripsAsEmpty()
    {
        using (var config = new ConfigManager(_path))
        {
            ItemBuildStore.Save(config, "Aatrox", Array.Empty<string>());
        }

        using var reloaded = new ConfigManager(_path);
        var loaded = ItemBuildStore.Load(reloaded, "Aatrox");

        Assert.NotNull(loaded);
        Assert.Empty(loaded!.ItemIds);
    }
}
