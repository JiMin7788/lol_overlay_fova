using Overlay.Core.Stats;

namespace Overlay.Core.Tests;

/// <summary>
/// <see cref="FileMatchupSource"/> — the statistics view's counter / anti-counter data. Pins the
/// parse of <c>stats/matchups.json</c> (best/worst opponents with re-derived win rates, order
/// preserved), the shared patch resolution and failure posture, and the per-lane lookup the view
/// uses (<see cref="FileMatchupSource.Get"/>).
/// </summary>
public class FileMatchupSourceTests : IDisposable
{
    private readonly string _root;

    public FileMatchupSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "MatchupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Mirrors the tier-source tests: rune champion files at the patch root so patch
    /// resolution succeeds, plus the matchups file under stats/.</summary>
    private void WritePatch(string patch, int runeChampions, string? matchupsJson)
    {
        string dir = Path.Combine(_root, patch);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < runeChampions; i++)
            File.WriteAllText(Path.Combine(dir, $"{100 + i}.json"), "[]");
        if (matchupsJson is not null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "stats"));
            File.WriteAllText(Path.Combine(dir, "stats", "matchups.json"), matchupsJson);
        }
    }

    private const string SampleJson = """
        {"patch":"16.16","minGames":30,"topK":6,
         "matchups":{
           "TOP":{
             "266":{"name":"Aatrox",
                    "best":[{"name":"Nasus","games":50,"wins":30},
                            {"name":"Yone","games":40,"wins":22}],
                    "worst":[{"name":"Darius","games":60,"wins":20},
                             {"name":"Garen","games":45,"wins":18}]}},
           "MIDDLE":{
             "266":{"name":"Aatrox",
                    "best":[{"name":"Lux","games":80,"wins":50}],
                    "worst":[]}}
         }}
        """;

    [Fact]
    public void ParsesBestAndWorst_WithReDerivedRates_InFileOrder()
    {
        WritePatch("16.16", runeChampions: 5, SampleJson);
        var source = new FileMatchupSource(_root);

        Assert.Equal("16.16", source.Patch);

        var top = source.Get("TOP", 266);
        Assert.NotNull(top);
        Assert.Equal(new[] { "Nasus", "Yone" }, top!.Best.Select(m => m.Name));
        Assert.Equal(30.0 / 50, top.Best[0].WinRate, 6);   // most favourable first
        Assert.Equal(50, top.Best[0].Games);
        Assert.Equal(22.0 / 40, top.Best[1].WinRate, 6);
        Assert.Equal("Darius", top.Worst[0].Name);          // most unfavourable first
        Assert.Equal(20.0 / 60, top.Worst[0].WinRate, 6);
    }

    [Fact]
    public void Get_IsNull_ForUnknownLaneOrChampion()
    {
        WritePatch("16.16", runeChampions: 5, SampleJson);
        var source = new FileMatchupSource(_root);

        Assert.Null(source.Get("JUNGLE", 266));   // champion not recorded in this lane
        Assert.Null(source.Get("TOP", 999));      // no such champion
    }

    [Fact]
    public void MissingOrCorrupt_DegradesToNull()
    {
        Assert.Null(new FileMatchupSource(Path.Combine(_root, "nope")).Get("TOP", 266));

        WritePatch("16.15", runeChampions: 3, matchupsJson: null);         // no matchups file
        Assert.Null(new FileMatchupSource(_root).Get("TOP", 266));

        WritePatch("16.16", runeChampions: 3, matchupsJson: "not json");   // corrupt
        Assert.Null(new FileMatchupSource(_root).Get("TOP", 266));

        Assert.Null(new FileMatchupSource("").Get("TOP", 266));
    }
}
