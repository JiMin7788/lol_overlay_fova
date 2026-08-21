using Overlay.Core.Stats;

namespace Overlay.Core.Tests;

/// <summary>
/// (benchmarks, loop 459) <see cref="FileBenchmarkSource"/> + <see cref="BenchmarkEntry"/> — the
/// Diamond CS/min distributions behind the status card's live benchmark line. Pins the percentile
/// interpolation (including the honesty clamp at 5/95 — the stored p10–p90 span cannot support
/// claims beyond "top ~5%"), the name normalization that lets a scoreboard display name match
/// Match-V5's internal form, and the shared failure posture (missing/corrupt → null).
/// </summary>
public class FileBenchmarkSourceTests : IDisposable
{
    private readonly string _root;

    public FileBenchmarkSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BenchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ── percentile interpolation (pure math, no files) ────────────────────────────

    private static readonly double[] P = { 4.0, 5.0, 6.0, 7.0, 8.0 }; // p10 p25 p50 p75 p90

    [Theory]
    [InlineData(6.0, 50)]   // exactly the median
    [InlineData(5.0, 25)]   // exactly p25
    [InlineData(5.5, 37.5)] // halfway p25→p50 → halfway 25→50
    [InlineData(6.5, 62.5)]
    [InlineData(3.0, 5)]    // below p10 → clamped to 5, never "top 100%"
    [InlineData(9.0, 95)]   // above p90 → clamped to 95
    [InlineData(4.0, 5)]    // AT p10: <= boundary clamps low (conservative)
    public void EstimatePercentile_InterpolatesAndClamps(double value, double expected)
    {
        Assert.Equal(expected, BenchmarkEntry.EstimatePercentile(P, value), 3);
    }

    [Fact]
    public void EstimatePercentile_DegenerateInputs_ReturnNeutralOrClamped()
    {
        // Wrong-length array (corrupt file survived parsing): neutral 50, never a throw.
        Assert.Equal(50, BenchmarkEntry.EstimatePercentile(new double[] { 1, 2 }, 5));
        // All-equal distribution (tiny sample): any value resolves without dividing by zero.
        double flat = BenchmarkEntry.EstimatePercentile(new double[] { 5, 5, 5, 5, 5 }, 5);
        Assert.InRange(flat, 5, 95);
    }

    // ── file source ───────────────────────────────────────────────────────────────

    private void WritePatch(string patch, int runeChampions, string? benchmarksJson = null)
    {
        string dir = Path.Combine(_root, patch);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < runeChampions; i++)
            File.WriteAllText(Path.Combine(dir, $"{100 + i}.json"), "[]");
        if (benchmarksJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "stats"));
            File.WriteAllText(Path.Combine(dir, "stats", "benchmarks.json"), benchmarksJson);
        }
    }

    private const string SampleJson = """
        {"champions": {"36": {
          "name": "DrMundo", "mainRole": "TOP",
          "roles": {
            "TOP": {"games": 400, "csPerMin": [5.0, 5.8, 6.5, 7.2, 7.9],
                    "goldPerMin": [300, 340, 380, 420, 460], "kdaMedian": 2.1},
            "JUNGLE": {"games": 60, "csPerMin": [5.5, 6.0, 6.6, 7.1, 7.6],
                       "goldPerMin": [350, 380, 420, 460, 500], "kdaMedian": 2.4}
          }}}}
        """;

    [Fact]
    public void LooksUpByKey_AndByEitherNameForm()
    {
        WritePatch("16.15", runeChampions: 5, benchmarksJson: SampleJson);
        var source = new FileBenchmarkSource(_root);

        var byKey = source.GetMainRole(36);
        Assert.NotNull(byKey);
        Assert.Equal("TOP", byKey!.Role);      // mainRole, not the smaller JUNGLE sample
        Assert.Equal(400, byKey.Games);
        Assert.Equal(6.5, byKey.CsPerMin[2], 2);

        // Scoreboard display form vs Match-V5 internal form — both normalize to "drmundo".
        Assert.NotNull(source.GetMainRole("Dr. Mundo"));
        Assert.NotNull(source.GetMainRole("DrMundo"));
        // Candidate list skips nulls/misses and takes the first hit.
        Assert.NotNull(source.GetMainRole(null, "no-such-champ", "Dr. Mundo"));
        Assert.Null(source.GetMainRole("Aatrox"));
    }

    [Fact]
    public void MissingOrCorrupt_DegradesToNull()
    {
        Assert.Null(new FileBenchmarkSource(Path.Combine(_root, "nope")).GetMainRole(36));

        WritePatch("16.15", runeChampions: 3); // no stats file
        Assert.Null(new FileBenchmarkSource(_root).GetMainRole(36));

        WritePatch("16.16", runeChampions: 3, benchmarksJson: "{broken");
        Assert.Null(new FileBenchmarkSource(_root).GetMainRole(36));

        Assert.Null(new FileBenchmarkSource("").GetMainRole(36));
    }
}
