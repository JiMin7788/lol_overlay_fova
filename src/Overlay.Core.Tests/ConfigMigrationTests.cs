using System.Text.Json.Nodes;
using Overlay.Core.Config;

namespace Overlay.Core.Tests;

/// <summary>
/// Schema v2 (lolF01): the lane-return timer is turned on once for configs written before v2,
/// the migrated file is persisted on load, and a later user choice is never overridden again.
/// </summary>
public class ConfigMigrationTests : IDisposable
{
    private const string LaneReturnKey = "overlay.items.laneReturn.enabled";

    private readonly string _dir;
    private readonly string _path;

    public ConfigMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "M14_ConfigMigrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteConfig(int? schemaVersion, bool laneReturnEnabled)
    {
        var root = new JsonObject
        {
            ["overlay"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["laneReturn"] = new JsonObject { ["enabled"] = laneReturnEnabled },
                },
            },
        };
        if (schemaVersion is int v) root["schemaVersion"] = v;
        File.WriteAllText(_path, root.ToJsonString());
    }

    private JsonObject ReadDisk() => JsonNode.Parse(File.ReadAllText(_path))!.AsObject();

    private static bool DiskLaneReturn(JsonObject disk)
        => disk["overlay"]!["items"]!["laneReturn"]!["enabled"]!.GetValue<bool>();

    [Fact]
    public void V1File_LaneReturnOff_IsTurnedOn_AndPersistedAsV2BeforeAnySet()
    {
        WriteConfig(schemaVersion: 1, laneReturnEnabled: false);

        using var config = new ConfigManager(_path);

        Assert.Equal(true, config.Get(LaneReturnKey));
        var disk = ReadDisk();
        Assert.Equal(2, disk["schemaVersion"]!.GetValue<int>());
        Assert.True(DiskLaneReturn(disk));
    }

    [Fact]
    public void AfterMigration_UserTurnsItOff_StaysOffOnReopen()
    {
        WriteConfig(schemaVersion: 1, laneReturnEnabled: false);

        using (var config = new ConfigManager(_path))
        {
            Assert.Equal(true, config.Get(LaneReturnKey));
            config.Set(LaneReturnKey, false);
        }

        using var reopened = new ConfigManager(_path);
        Assert.Equal(false, reopened.Get(LaneReturnKey));
    }

    [Fact]
    public void V2File_LaneReturnOff_StaysOff_AndFileIsNotRewritten()
    {
        WriteConfig(schemaVersion: 2, laneReturnEnabled: false);
        var stamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_path, stamp);
        var before = File.ReadAllText(_path);

        using var config = new ConfigManager(_path);

        Assert.Equal(false, config.Get(LaneReturnKey));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_path));
        Assert.Equal(before, File.ReadAllText(_path));
    }

    [Fact]
    public void FileWithoutSchemaVersion_IsTreatedAsV0_AndMigrated()
    {
        WriteConfig(schemaVersion: null, laneReturnEnabled: false);

        using var config = new ConfigManager(_path);

        Assert.Equal(true, config.Get(LaneReturnKey));
        var disk = ReadDisk();
        Assert.Equal(2, disk["schemaVersion"]!.GetValue<int>());
        Assert.True(DiskLaneReturn(disk));
    }

    [Fact]
    public void V1File_WithoutOverlaySection_GetsLaneReturnOn()
    {
        File.WriteAllText(_path, new JsonObject { ["schemaVersion"] = 1 }.ToJsonString());

        using var config = new ConfigManager(_path);

        Assert.Equal(true, config.Get(LaneReturnKey));
        Assert.True(DiskLaneReturn(ReadDisk()));
    }

    [Fact]
    public void FreshInstall_LaneReturnOn_AndSchemaVersion2()
    {
        using var config = new ConfigManager(_path);

        Assert.Equal(true, config.Get(LaneReturnKey));
        var disk = ReadDisk();
        Assert.Equal(2, disk["schemaVersion"]!.GetValue<int>());
        Assert.True(DiskLaneReturn(disk));
    }
}
