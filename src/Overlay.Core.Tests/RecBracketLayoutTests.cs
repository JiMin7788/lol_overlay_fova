using Overlay.Core.ChampSelect;
using Overlay.Core.Stats;

namespace Overlay.Core.Tests;

/// <summary>
/// (brackets, loop 463) The per-bracket rec layout — <c>rec/{patch}/{bracket}/…</c> — as the three
/// file sources see it. Pins the three things that layout change could quietly break:
///
/// <para>1. The coverage guard still compares like with like. It counted champion files at the
/// patch root, and bracketed output puts them one level down, so a patch whose files all moved
/// must not read as zero coverage and lose to an older patch.</para>
///
/// <para>2. A rec directory written before brackets existed keeps working untouched.</para>
///
/// <para>3. Switching bracket switches the answer — the sources cache aggressively, so the setter
/// has to drop that cache or the panel would keep showing the previous tier band.</para>
/// </summary>
public class RecBracketLayoutTests : IDisposable
{
    private readonly string _root;

    public RecBracketLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BracketTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>One champion's rune file in one bracket, carrying a win rate we can identify.</summary>
    private void WriteRune(string patch, string bracket, int championKey, double winRate)
    {
        string dir = bracket.Length == 0
            ? Path.Combine(_root, patch)
            : Path.Combine(_root, patch, bracket);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{championKey}.json"),
            $$"""
              [{"championKey": {{championKey}}, "name": "TOP", "source": "remote",
                "page": {"primaryStyleId": 8000, "subStyleId": 8100, "perkIds": [1,2,3,4,5,6,7,8,9]},
                "games": 100, "winRate": {{winRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}]
              """);
    }

    [Fact]
    public void BracketsAreOfferedHighestFirst_AndOnlyWhatExistsOnDisk()
    {
        WriteRune("16.16", "platinum_plus", 86, 0.51);
        WriteRune("16.16", "diamond_plus", 86, 0.55);
        WriteRune("16.16", "all", 86, 0.50);

        Assert.Equal(new[] { "all", "diamond_plus", "platinum_plus" },
                     FileRecommendationSource.AvailableBrackets(_root).Select(b => b.Slug));
    }

    [Fact]
    public void AThinlyCoveredBracketIsOfferedButMarked()
    {
        // Measured on the real data: a newly-collected tier produced 2 champions in gold_minus
        // against 147 in platinum_plus. It is still offered — hiding a band the user asked for is
        // worse than showing it honestly — but flagged, so nobody picks it blind.
        for (int i = 0; i < 100; i++) WriteRune("16.16", "platinum_plus", 100 + i, 0.51);
        for (int i = 0; i < 80; i++) WriteRune("16.16", "all", 100 + i, 0.50);
        WriteRune("16.16", "gold_minus", 100, 0.52);
        Directory.CreateDirectory(Path.Combine(_root, "16.16", "iron"));

        var offered = FileRecommendationSource.AvailableBrackets(_root);
        // 70% of the best-covered bracket (100) is 70: "all" clears it at 80, gold_minus does not
        // at 1. An empty directory is not offered at all — there is nothing to mark.
        Assert.Equal(new[] { "all", "platinum_plus", "gold_minus" }, offered.Select(b => b.Slug));
        Assert.Equal(new[] { false, false, true }, offered.Select(b => b.Thin));
    }

    [Fact]
    public void CoverageIsTheYardstickBecauseItSaturates()
    {
        // Once every bracket's sample is real they all reach the same roster, so a narrow bracket
        // is offered beside a wide one. Judging by match count instead would rule the narrow one
        // out forever, since the brackets are nested by construction.
        for (int i = 0; i < 150; i++)
        {
            WriteRune("16.16", "all", 100 + i, 0.50);
            WriteRune("16.16", "platinum_plus", 100 + i, 0.52);
            WriteRune("16.16", "gold_minus", 100 + i, 0.48);
        }

        var saturated = FileRecommendationSource.AvailableBrackets(_root);
        Assert.Equal(new[] { "all", "platinum_plus", "gold_minus" }, saturated.Select(b => b.Slug));
        Assert.All(saturated, b => Assert.False(b.Thin));
    }

    [Fact]
    public void ReadsTheSelectedBracket_AndSwitchingDropsTheCache()
    {
        WriteRune("16.16", "platinum_plus", 86, 0.51);
        WriteRune("16.16", "diamond_plus", 86, 0.55);

        var source = new FileRecommendationSource(_root, "platinum_plus");
        Assert.Equal(0.51, source.List(86).Single().WinRate!.Value, 6);

        // Read once (now cached), then switch: the next read must come from the other sample.
        source.Bracket = "diamond_plus";
        Assert.Equal(0.55, source.List(86).Single().WinRate!.Value, 6);
    }

    [Fact]
    public void UnknownBracketFallsBackToThePatchDirectory()
    {
        // Nothing named "gold_minus" was written, so the source reads the patch directory itself.
        // That directory holds no champion files here, hence an empty list rather than another
        // bracket's numbers.
        WriteRune("16.16", "platinum_plus", 86, 0.51);
        var source = new FileRecommendationSource(_root, "gold_minus");
        Assert.Empty(source.List(86));
    }

    [Fact]
    public void LegacyLayoutWithoutBracketsStillReads()
    {
        WriteRune("16.16", "", 86, 0.53);

        Assert.Empty(FileRecommendationSource.AvailableBrackets(_root));
        Assert.Equal(0.53, new FileRecommendationSource(_root).List(86).Single().WinRate!.Value, 6);
        // Asking for a bracket the layout does not have falls back to the same files.
        Assert.Equal(0.53, new FileRecommendationSource(_root, "platinum_plus")
                              .List(86).Single().WinRate!.Value, 6);
    }

    [Fact]
    public void CoverageGuardCountsInsideBrackets_SoAMovedPatchDoesNotLose()
    {
        // 16.15 in the old layout with a broad roster; 16.16 bracketed with an equally broad one.
        for (int i = 0; i < 10; i++) WriteRune("16.15", "", 100 + i, 0.50);
        for (int i = 0; i < 10; i++) WriteRune("16.16", "platinum_plus", 100 + i, 0.60);

        // Counting only patch-root files would score 16.16 at zero and keep serving 16.15.
        Assert.Equal(0.60, new FileRecommendationSource(_root, "platinum_plus")
                              .List(100).Single().WinRate!.Value, 6);

        // The guard itself is unchanged: a genuinely thin new patch is still skipped.
        for (int i = 0; i < 10; i++) WriteRune("16.17", "platinum_plus", 200 + i, 0.70);
        Directory.Delete(Path.Combine(_root, "16.17", "platinum_plus"), recursive: true);
        WriteRune("16.17", "platinum_plus", 200, 0.70);
        Assert.Equal(0.60, new FileRecommendationSource(_root, "platinum_plus")
                              .List(100).Single().WinRate!.Value, 6);
    }

    [Fact]
    public void BracketDefinitionsAreCumulativeAndOrderedHighestFirst()
    {
        Assert.Equal("platinum_plus", RecBrackets.Default);
        Assert.Equal(RecBrackets.TierOrder, RecBrackets.TiersOf("all"));
        Assert.Equal(new[] { "DIAMOND", "MASTER", "GRANDMASTER", "CHALLENGER" },
                     RecBrackets.TiersOf("diamond_plus"));
        Assert.Equal(new[] { "GOLD", "PLATINUM", "EMERALD", "DIAMOND", "MASTER", "GRANDMASTER",
                             "CHALLENGER" }, RecBrackets.TiersOf("gold_plus"));
        Assert.Equal(new[] { "IRON", "BRONZE", "SILVER", "GOLD" }, RecBrackets.TiersOf("gold_minus"));
        Assert.Empty(RecBrackets.TiersOf("nonsense"));

        // A single collected tier answers every bracket that contains it, and no other.
        // gold_plus starts AT Gold, so an Iron-only sample cannot answer it.
        Assert.Equal(new[] { "all", "gold_minus", "silver_minus", "bronze_minus", "iron" },
                     RecBrackets.Available(new[] { "IRON" }));
        Assert.Contains("gold_plus", RecBrackets.Available(new[] { "GOLD" }));
    }
}
