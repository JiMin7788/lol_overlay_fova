using System.Text.Json.Nodes;
using Overlay.Core.Config;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M14 Config Manager Acceptance Criteria:
///  1. A change persists across an app "restart" (dispose + reload from the same file).
///  2. Unknown keys present in the on-disk JSON do not crash load.
///  3. CONFIG_CHANGED fires within 100ms of Set().
/// Plus the two Reviewer Checklist items called out explicitly in the module spec:
///  - Atomic write is temp-file+rename, not an in-place overwrite (proved by observing
///    the temp file swap, not just asserting the final content).
///  - Rapid Set() calls debounce to exactly one file write of the latest value.
///
/// Each test uses its own temp directory (via <see cref="TempConfigDir"/>) so tests
/// never collide on a shared config/user_config.json, matching the M15 test suite's
/// per-test isolation approach for its own static shared state.
/// </summary>
public class ConfigManagerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public ConfigManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "M14_ConfigTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void MissingFile_CreatesDefaults_WithSpecMandatedHudDuration()
    {
        using var config = new ConfigManager(_path);

        Assert.True(File.Exists(_path));
        Assert.Equal(4d, Convert.ToDouble(config.Get("overlay.hudDisplayDuration")));
    }

    [Fact]
    public void Set_Then_Restart_PersistsAcrossReload()
    {
        using (var config = new ConfigManager(_path))
        {
            config.Set("overlay.hudDisplayDuration", 7.5);
            config.Set("general.language", "ko-KR");
            // Dispose flushes any pending debounced write synchronously.
        }

        using var reloaded = new ConfigManager(_path);
        Assert.Equal(7.5, Convert.ToDouble(reloaded.Get("overlay.hudDisplayDuration")));
        Assert.Equal("ko-KR", reloaded.Get("general.language"));
    }

    [Fact]
    public void Combos_And_TtsCooldown_PersistAcrossReload()
    {
        // Regression for E2: combos.saved.{id} (M04) and voice.ttsCooldownSeconds (M09)
        // now have a ConfigSchema home, so the typed round-trip on load preserves them
        // instead of dropping them as unknown keys.
        using (var config = new ConfigManager(_path))
        {
            config.Set("combos.saved.abc123", """{"Nodes":[],"Edges":[]}""");
            config.Set("voice.ttsCooldownSeconds", 8.0);
        }

        using var reloaded = new ConfigManager(_path);
        Assert.Equal("""{"Nodes":[],"Edges":[]}""", reloaded.Get("combos.saved.abc123"));
        Assert.Equal(8.0, Convert.ToDouble(reloaded.Get("voice.ttsCooldownSeconds")));
    }

    [Fact]
    public void RuneSelections_PersistAcrossReload()
    {
        // Regression for the M06 rune-selection UI: runes.selections.{championId} (schema home:
        // RunesConfig.Selections) must have the same typed-round-trip survival as combos.saved.{id}
        // already gets — otherwise a saved rune page would silently vanish on the next app start.
        using (var config = new ConfigManager(_path))
        {
            config.Set("runes.selections.Aatrox", """{"ChampionId":"Aatrox","SelectedRuneIds":["8126"],"ManualFlags":{"8126":true}}""");
        }

        using var reloaded = new ConfigManager(_path);
        Assert.Equal(
            """{"ChampionId":"Aatrox","SelectedRuneIds":["8126"],"ManualFlags":{"8126":true}}""",
            reloaded.Get("runes.selections.Aatrox"));
    }

    [Fact]
    public void PerElementHudPositionsAndItemToggles_PersistAcrossReload()
    {
        // M02 pending-change #1 (modular per-element HUD positioning): overlay.positions.{key}
        // (Dictionary<string,PositionConfig>, arbitrary keys, no fixed schema slot per element)
        // and the new opt-out overlay.items.{key}.enabled toggles beyond the original 3 must
        // both survive a config reload the same way the pre-existing combos.saved.{id} /
        // runes.selections.{championId} string-dictionary keys do.
        using (var config = new ConfigManager(_path))
        {
            config.Set("overlay.positions.comboResult.x", 42.5);
            config.Set("overlay.positions.comboResult.y", -13.0);
            config.Set("overlay.items.comboResult.enabled", false);
            config.Set("overlay.items.statusCard.enabled", false);
        }

        using var reloaded = new ConfigManager(_path);
        Assert.Equal(42.5, Convert.ToDouble(reloaded.Get("overlay.positions.comboResult.x")));
        Assert.Equal(-13.0, Convert.ToDouble(reloaded.Get("overlay.positions.comboResult.y")));
        Assert.Equal(false, reloaded.Get("overlay.items.comboResult.enabled"));
        Assert.Equal(false, reloaded.Get("overlay.items.statusCard.enabled"));
    }

    [Fact]
    public void CoreHudItemToggles_DefaultToEnabled_UnlikeTheOptInEngineItems()
    {
        // Opt-OUT default for the M02 pending-change #1 additions (previously-unconditional
        // core HUD elements) and inhibitor timers (switched to default-ON), vs. the remaining
        // opt-IN M19 §3 engine items which stay disabled by default.
        using var config = new ConfigManager(_path);

        Assert.Equal(true, config.Get("overlay.items.comboResult.enabled"));
        Assert.Equal(true, config.Get("overlay.items.itemAlert.enabled"));
        Assert.Equal(true, config.Get("overlay.items.recallTimer.enabled"));
        Assert.Equal(true, config.Get("overlay.items.notification.enabled"));
        Assert.Equal(true, config.Get("overlay.items.statusCard.enabled"));
        Assert.Equal(true, config.Get("overlay.items.inhibitorTimers.enabled"));
        Assert.Equal(true, config.Get("overlay.items.nexusTurretTimers.enabled")); // patch 15.1, default-ON like inhibitors

        Assert.Equal(false, config.Get("overlay.items.globalGold.enabled"));
    }

    [Fact]
    public void MinimapVision_DefaultsOn_WithThirtyFpsCap()
    {
        // M31 §5 kill switch: activated by default (loop 165 user decision) so the live perf
        // matrix can run; the prefilter cap defaults to 30 fps. (Pre-release, reconcile with the
        // §8 Riot-submission gate — see CLAUDE_CODE_TODO §38-F/I.)
        using var config = new ConfigManager(_path);

        Assert.Equal(true, config.Get("minimap.vision"));
        Assert.Equal(30, Convert.ToInt32(config.Get("minimap.captureFps")));
    }

    [Fact]
    public void UnknownKeysInFile_AreIgnored_AndDoNotCrash()
    {
        var handCrafted = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["overlay"] = new JsonObject
            {
                ["hudDisplayDuration"] = 4,
                ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
                ["opacity"] = 1.0,
                ["totallyUnknownOverlayField"] = "should be ignored",
            },
            ["hotkeys"] = new JsonObject { ["comboSlots"] = new JsonObject() },
            ["voice"] = new JsonObject
            {
                ["ttsEnabled"] = false,
                ["ttsVolume"] = 0.8,
                ["voicePack"] = "default",
                ["sttEnabled"] = false,
            },
            ["general"] = new JsonObject { ["autoStartWithGame"] = false, ["language"] = "en-US" },
            ["someFutureTopLevelFeature"] = new JsonObject { ["nested"] = 123 },
        };
        File.WriteAllText(_path, handCrafted.ToJsonString());

        var exception = Record.Exception(() =>
        {
            using var config = new ConfigManager(_path);
            Assert.Equal(4d, Convert.ToDouble(config.Get("overlay.hudDisplayDuration")));
        });

        Assert.Null(exception);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults_InsteadOfCrashing()
    {
        File.WriteAllText(_path, "{ not valid json ][");

        using var config = new ConfigManager(_path);
        Assert.Equal(4d, Convert.ToDouble(config.Get("overlay.hudDisplayDuration")));
    }

    [Fact]
    public void RapidSets_Debounce_ToExactlyOneWrite_WithFinalValue()
    {
        using var config = new ConfigManager(_path);

        // Each real disk write creates exactly one distinct *.tmp file before swapping
        // it into place (see AtomicWrite_UsesTempFileThenSwap_NeverInPlaceOverwrite).
        // Counting *.tmp creations during the burst + debounce window is therefore a
        // direct count of how many times the file was actually written, independent of
        // final content -- proving coalescing, not just "the final value is right".
        int tempFileCreations = 0;
        using var watcher = new FileSystemWatcher(_dir) { EnableRaisingEvents = true };
        watcher.Created += (_, e) =>
        {
            if (e.FullPath.Contains(".tmp")) Interlocked.Increment(ref tempFileCreations);
        };

        for (int i = 0; i < 20; i++)
        {
            config.Set("overlay.hudDisplayDuration", i); // 20 rapid sets, well under 200ms
        }

        // Wait past the debounce window for the single coalesced write to land.
        Thread.Sleep(500);

        Assert.Equal(1, tempFileCreations);

        var contentAfter = File.ReadAllText(_path);
        var parsed = JsonNode.Parse(contentAfter)!.AsObject();
        Assert.Equal(19, parsed["overlay"]!["hudDisplayDuration"]!.GetValue<int>());

        // Also verify via the API surface.
        Assert.Equal(19d, Convert.ToDouble(config.Get("overlay.hudDisplayDuration")));
    }

    [Fact]
    public void AtomicWrite_UsesTempFileThenSwap_NeverInPlaceOverwrite()
    {
        using var config = new ConfigManager(_path);

        // Watch the directory for the duration of one Set() + debounce flush and record
        // every file system event. A true temp+rename implementation creates a *.tmp
        // file distinct from the final path and then the final path is replaced
        // atomically -- we should observe a temp file name that never equals the final
        // config file name, proving the write does not happen in-place.
        var seenTempFile = false;
        using var watcher = new FileSystemWatcher(_dir)
        {
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, e) =>
        {
            if (e.FullPath != _path && e.FullPath.Contains(".tmp"))
            {
                seenTempFile = true;
            }
        };

        config.Set("overlay.opacity", 0.42);
        Thread.Sleep(500); // past the 200ms debounce window

        Assert.True(seenTempFile, "Expected a distinct *.tmp file to be created before the final file was swapped into place (atomic write), but none was observed.");
        Assert.Equal(0.42, Convert.ToDouble(config.Get("overlay.opacity")));
    }

    [Fact]
    public void Set_Publishes_ConfigChanged_Within100Milliseconds()
    {
        using var config = new ConfigManager(_path);
        var received = new ManualResetEventSlim(false);
        object? receivedValue = null;

        var subId = config.OnChange("overlay.opacity", newValue =>
        {
            receivedValue = newValue;
            received.Set();
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        config.Set("overlay.opacity", 0.5);

        bool signaled = received.Wait(TimeSpan.FromMilliseconds(100));
        sw.Stop();

        Assert.True(signaled, "CONFIG_CHANGED handler for the changed key did not fire within 100ms.");
        Assert.Equal(0.5, Convert.ToDouble(receivedValue));

        EventBus.EventBus.Unsubscribe(subId);
    }

    [Fact]
    public void OnChange_WithDifferentKey_IsNotInvoked()
    {
        using var config = new ConfigManager(_path);
        bool invoked = false;

        var subId = config.OnChange("voice.ttsVolume", _ => invoked = true);

        config.Set("overlay.opacity", 0.9);
        Thread.Sleep(150);

        Assert.False(invoked);
        EventBus.EventBus.Unsubscribe(subId);
    }

    [Fact]
    public void Reset_SingleKey_RestoresDefault()
    {
        using var config = new ConfigManager(_path);
        config.Set("general.language", "ko-KR");
        Assert.Equal("ko-KR", config.Get("general.language"));

        config.Reset("general.language");

        Assert.Equal("en-US", config.Get("general.language"));
    }

    [Fact]
    public void Reset_Null_RestoresEntireConfigToDefaults()
    {
        using var config = new ConfigManager(_path);
        config.Set("general.language", "ko-KR");
        config.Set("overlay.hudDisplayDuration", 99);

        config.Reset(null);

        Assert.Equal("en-US", config.Get("general.language"));
        Assert.Equal(4d, Convert.ToDouble(config.Get("overlay.hudDisplayDuration")));
    }
}
