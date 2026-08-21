using Overlay.Core.Logging;
using Overlay.Core.Overlay;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M18 Logging (docs/modules/M18_LOGGING.md). Exercises
/// <see cref="Logger"/> and <see cref="Metrics"/> entirely against a temp directory and a virtual
/// clock, so daily rotation, 7-day retention, and metric readback are deterministic with no real
/// wall-clock waiting and no files left in the repo tree.
///
///  - ERROR entry is written to logs/YYYY-MM-DD.log with level/module/message (Acceptance #1).
///  - The active file name matches the injected clock's date; a new date rotates to a new file.
///  - Retention: a file dated older than the window is deleted while a recent one survives
///    (Acceptance #2).
///  - Metrics.Record then Snapshot/Recent returns name/value/tags (Acceptance #3).
///  - All four levels format correctly; Flush/Dispose are deterministic.
/// </summary>
public class LoggingTests : IDisposable
{
    private readonly string _dir;

    public LoggingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "M18_LoggingTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class FakeClock : IClock
    {
        public long NowMs { get; set; }
    }

    /// <summary>Unix-epoch ms for the given UTC calendar day at midnight.</summary>
    private static long UtcDayMs(int year, int month, int day)
        => new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private string FilePath(string dateName) => Path.Combine(_dir, dateName + ".log");

    // ── Logger ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Error_IsWritten_ToClockDatedFile_WithLevelModuleMessage()
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };
        using var logger = new Logger(_dir, clock);

        logger.Log(LogLevel.Error, "M01", "boom");
        logger.Flush();

        string path = FilePath("2026-07-08");
        Assert.True(File.Exists(path));
        string content = File.ReadAllText(path);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("M01:", content);
        Assert.Contains("boom", content);
    }

    [Fact]
    public void DifferentClockDate_RotatesToNewFile()
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };
        using var logger = new Logger(_dir, clock);

        logger.Log(LogLevel.Info, "M02", "day one");
        logger.Flush();

        clock.NowMs = UtcDayMs(2026, 7, 9);
        logger.Log(LogLevel.Info, "M02", "day two");
        logger.Flush();

        Assert.True(File.Exists(FilePath("2026-07-08")));
        Assert.True(File.Exists(FilePath("2026-07-09")));
        Assert.Contains("day one", File.ReadAllText(FilePath("2026-07-08")));
        Assert.Contains("day two", File.ReadAllText(FilePath("2026-07-09")));
        Assert.DoesNotContain("day two", File.ReadAllText(FilePath("2026-07-08")));
    }

    [Fact]
    public void Retention_DeletesFilesOlderThanWindow_KeepsRecent()
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };

        // Pre-seed a stale file (8 days old > 7-day window) and a recent one (2 days old).
        File.WriteAllText(FilePath("2026-06-30"), "stale\n");
        File.WriteAllText(FilePath("2026-07-06"), "recent\n");

        using var logger = new Logger(_dir, clock, retentionDays: 7);
        logger.Log(LogLevel.Info, "M18", "trigger prune");
        logger.Flush();

        Assert.False(File.Exists(FilePath("2026-06-30")), "stale file should be pruned");
        Assert.True(File.Exists(FilePath("2026-07-06")), "in-window file should survive");
        Assert.True(File.Exists(FilePath("2026-07-08")), "today's file should exist");
    }

    [Fact]
    public void Retention_KeepsFileExactlyAtWindowBoundary()
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };
        // Exactly 7 days old == cutoff; cutoff is inclusive (deleted only if strictly older).
        File.WriteAllText(FilePath("2026-07-01"), "boundary\n");

        using var logger = new Logger(_dir, clock, retentionDays: 7);
        logger.Log(LogLevel.Info, "M18", "trigger prune");
        logger.Flush();

        Assert.True(File.Exists(FilePath("2026-07-01")), "file exactly at the boundary should survive");
    }

    [Fact]
    public void Retention_IgnoresNonDailyLogFiles()
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };
        File.WriteAllText(Path.Combine(_dir, "notes.log"), "not a daily file\n");

        using var logger = new Logger(_dir, clock, retentionDays: 7);
        logger.Log(LogLevel.Info, "M18", "trigger prune");
        logger.Flush();

        Assert.True(File.Exists(Path.Combine(_dir, "notes.log")));
    }

    [Theory]
    [InlineData(LogLevel.Debug, "[DEBUG]")]
    [InlineData(LogLevel.Info, "[INFO]")]
    [InlineData(LogLevel.Warn, "[WARN]")]
    [InlineData(LogLevel.Error, "[ERROR]")]
    public void AllLevels_FormatWithUppercaseTag(LogLevel level, string expectedTag)
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };
        using var logger = new Logger(_dir, clock);

        logger.Log(level, "MX", "msg");
        logger.Flush();

        Assert.Contains(expectedTag, File.ReadAllText(FilePath("2026-07-08")));
    }

    [Fact]
    public void Meta_IsSerializedAsJson_OnTheLine()
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };
        using var logger = new Logger(_dir, clock);

        logger.Log(LogLevel.Warn, "M16", "frame drop", new { fps = 42, scene = "teamfight" });
        logger.Flush();

        string content = File.ReadAllText(FilePath("2026-07-08"));
        Assert.Contains("\"fps\":42", content);
        Assert.Contains("\"scene\":\"teamfight\"", content);
    }

    [Fact]
    public void LineTimestamp_IsIsoUtc_FromClock()
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };
        using var logger = new Logger(_dir, clock);

        logger.Log(LogLevel.Info, "M0", "x");
        logger.Flush();

        string firstLine = File.ReadAllLines(FilePath("2026-07-08"))[0];
        Assert.StartsWith("2026-07-08T00:00:00.000Z ", firstLine);
    }

    [Fact]
    public void Dispose_FlushesBufferedEntries()
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };
        var logger = new Logger(_dir, clock);

        logger.Log(LogLevel.Error, "M09", "on dispose");
        logger.Dispose(); // must flush without an explicit Flush() call.

        Assert.Contains("on dispose", File.ReadAllText(FilePath("2026-07-08")));
    }

    [Fact]
    public void Log_AfterDispose_Throws()
    {
        var logger = new Logger(_dir, new FakeClock { NowMs = UtcDayMs(2026, 7, 8) });
        logger.Dispose();
        Assert.Throws<ObjectDisposedException>(() => logger.Log(LogLevel.Info, "M", "late"));
    }

    [Fact]
    public void MultipleEntries_SameDay_AllAppended_InOrder()
    {
        var clock = new FakeClock { NowMs = UtcDayMs(2026, 7, 8) };
        using var logger = new Logger(_dir, clock);

        logger.Log(LogLevel.Info, "M", "first");
        logger.Log(LogLevel.Info, "M", "second");
        logger.Flush();

        string[] lines = File.ReadAllLines(FilePath("2026-07-08"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("first", lines[0]);
        Assert.Contains("second", lines[1]);
    }

    // ── Metrics ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Record_ThenSnapshot_ReturnsNameValueTagsTimestamp()
    {
        var clock = new FakeClock { NowMs = 1234 };
        var metrics = new Metrics(clock);
        var tags = new Dictionary<string, string> { ["endpoint"] = "liveclient" };

        metrics.Record("api.latency_ms", 12.5, tags);

        MetricEntry entry = Assert.Single(metrics.Snapshot());
        Assert.Equal("api.latency_ms", entry.Name);
        Assert.Equal(12.5, entry.Value);
        Assert.Equal("liveclient", entry.Tags["endpoint"]);
        Assert.Equal(1234, entry.Timestamp);
    }

    [Fact]
    public void Record_WithoutTags_DefaultsToEmpty()
    {
        var metrics = new Metrics(new FakeClock { NowMs = 0 });
        metrics.Record("render.frame_drop_rate", 0.01);

        MetricEntry entry = Assert.Single(metrics.Snapshot());
        Assert.Empty(entry.Tags);
    }

    [Fact]
    public void Recent_FiltersByName()
    {
        var metrics = new Metrics(new FakeClock { NowMs = 0 });
        metrics.Record("a", 1);
        metrics.Record("b", 2);
        metrics.Record("a", 3);

        var a = metrics.Recent("a");
        Assert.Equal(2, a.Count);
        Assert.All(a, e => Assert.Equal("a", e.Name));
        Assert.Equal(new[] { 1.0, 3.0 }, a.Select(e => e.Value));
    }

    [Fact]
    public void Snapshot_IsACopy_NotAffectedByLaterRecords()
    {
        var metrics = new Metrics(new FakeClock { NowMs = 0 });
        metrics.Record("a", 1);
        var snap = metrics.Snapshot();
        metrics.Record("a", 2);

        Assert.Single(snap); // earlier snapshot unaffected by the later Record.
    }
}
