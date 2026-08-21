using Overlay.Core.Combo;
using Overlay.Core.Config;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof that a captured defender snapshot saved via <see cref="TargetSnapshotStore"/>
/// survives a simulated app restart (ConfigManager dispose + reload from the same file) — the same
/// cross-restart guarantee <c>ItemBuildStoreTests</c> proves for <c>items.builds.{championId}</c>,
/// applied to the new <c>targetSnapshots.captures.{comboId}</c>/<c>targetSnapshots.useSnapshot.{comboId}</c>
/// schema homes (<see cref="TargetSnapshotsConfig"/>).
/// </summary>
public class TargetSnapshotStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public TargetSnapshotStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TargetSnapshotStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Save_Then_Restart_LoadsIdenticalSnapshot()
    {
        var snapshot = new TargetSnapshot("Zed", Armor: 45, Mr: 32, MaxHp: 2100, CapturedAtUtcMs: 1_700_000_000_000);

        using (var config = new ConfigManager(_path))
        {
            TargetSnapshotStore.Save(config, "combo-1", snapshot);
            // Dispose flushes the debounced write — the "restart" boundary.
        }

        using var reloaded = new ConfigManager(_path);
        var loaded = TargetSnapshotStore.Load(reloaded, "combo-1");

        Assert.NotNull(loaded);
        Assert.Equal(snapshot, loaded);
    }

    [Fact]
    public void Load_NoSnapshotEverCaptured_ReturnsNull_NotAFabricatedDefault()
    {
        using var config = new ConfigManager(_path);
        Assert.Null(TargetSnapshotStore.Load(config, "combo-none"));
    }

    [Fact]
    public void DifferentCombos_PersistIndependently_UnderTheirOwnKey_EvenForTheSameChampion()
    {
        // Two different combos built for the same champion may be theory-crafted against two
        // different hypothetical targets — the defining difference from the per-CHAMPION item/rune
        // stores (see TargetSnapshotStore's class doc comment).
        using (var config = new ConfigManager(_path))
        {
            TargetSnapshotStore.Save(config, "combo-a", new TargetSnapshot("Zed", 45, 32, 2100, 1000));
            TargetSnapshotStore.Save(config, "combo-b", new TargetSnapshot("Garen", 60, 40, 2600, 2000));
        }

        using var reloaded = new ConfigManager(_path);
        var a = TargetSnapshotStore.Load(reloaded, "combo-a");
        var b = TargetSnapshotStore.Load(reloaded, "combo-b");

        Assert.Equal("Zed", a!.ChampionName);
        Assert.Equal("Garen", b!.ChampionName);
    }

    [Fact]
    public void UseSnapshot_DefaultsFalse_NeverImplicitlyOn()
    {
        // CLAUDE.md Policy P2: an untouched toggle must read false, not a fabricated true.
        using var config = new ConfigManager(_path);
        Assert.False(TargetSnapshotStore.GetUseSnapshot(config, "combo-never-toggled"));
    }

    [Fact]
    public void UseSnapshot_Save_Then_Restart_RoundTrips()
    {
        using (var config = new ConfigManager(_path))
        {
            TargetSnapshotStore.SetUseSnapshot(config, "combo-1", true);
        }

        using var reloaded = new ConfigManager(_path);
        Assert.True(TargetSnapshotStore.GetUseSnapshot(reloaded, "combo-1"));
        Assert.False(TargetSnapshotStore.GetUseSnapshot(reloaded, "combo-other")); // untouched combo unaffected
    }
}
