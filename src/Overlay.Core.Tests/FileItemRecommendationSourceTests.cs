using Overlay.Core.ChampSelect;

namespace Overlay.Core.Tests;

/// <summary>
/// (item recs, loop 459) <see cref="FileItemRecommendationSource"/> — the item sibling of
/// <see cref="FileRecommendationSource"/>. Pins the three contracts that matter:
/// (1) it parses the aggregation's real output shape (roles → coreSets/boots/items) and orders
/// roles most-played-first; (2) it follows the SAME patch directory the rune source resolves —
/// including the coverage guard, so a thin new patch's items are skipped together with its runes;
/// (3) every failure (no dir, no file, corrupt JSON) degrades to an empty list.
/// </summary>
public class FileItemRecommendationSourceTests : IDisposable
{
    private readonly string _root;

    public FileItemRecommendationSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ItemRecTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Writes a patch dir with `runeChampions` top-level champion files (what the patch
    /// resolver counts) and optionally one items/ file for champion 266.</summary>
    private void WritePatch(string patch, int runeChampions, string? itemsJsonFor266 = null)
    {
        string dir = Path.Combine(_root, patch);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < runeChampions; i++)
            File.WriteAllText(Path.Combine(dir, $"{100 + i}.json"), "[]");
        if (itemsJsonFor266 is not null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "items"));
            File.WriteAllText(Path.Combine(dir, "items", "266.json"), itemsJsonFor266);
        }
    }

    private const string SampleJson = """
        {"roles": {
          "MIDDLE": {"games": 40,
            "coreSets": [{"items": [3161, 6333, 6610], "games": 12, "winRate": 0.583, "pickRate": 0.3}],
            "boots": [{"itemId": 3047, "games": 25, "winRate": 0.52, "pickRate": 0.625}],
            "items": [{"itemId": 3161, "games": 30, "winRate": 0.55, "pickRate": 0.75}]},
          "TOP": {"games": 500,
            "coreSets": [{"items": [3161, 3156, 6610], "games": 60, "winRate": 0.6, "pickRate": 0.12}],
            "boots": [], "items": []}
        }}
        """;

    [Fact]
    public void ParsesAggregationShape_MostPlayedRoleFirst()
    {
        WritePatch("16.15", runeChampions: 5, itemsJsonFor266: SampleJson);
        var source = new FileItemRecommendationSource(_root);

        var roles = source.List(266);

        Assert.Equal(2, roles.Count);
        Assert.Equal("TOP", roles[0].Role); // 500 games beats MIDDLE's 40 regardless of JSON order
        Assert.Equal(new[] { 3161, 3156, 6610 }, roles[0].CoreSets[0].Items);
        Assert.Equal(0.6, roles[0].CoreSets[0].WinRate, 3);
        Assert.Equal("MIDDLE", roles[1].Role);
        Assert.Equal(3047, roles[1].Boots[0].ItemId);
        Assert.Equal(0.625, roles[1].Boots[0].PickRate, 3);
    }

    /// <summary>The item source must not pick a patch the RUNE source would refuse: a fresh, thin
    /// patch (coverage below the guard's floor) is skipped for BOTH, even when only the thin patch
    /// carries an items/ file — the two panels always describe the same patch.</summary>
    [Fact]
    public void FollowsTheRuneSourcesCoverageGuard()
    {
        WritePatch("16.14", runeChampions: 10); // well-covered, no items emitted for it
        WritePatch("16.15", runeChampions: 2, itemsJsonFor266: SampleJson); // thin: 2 < 0.7*10

        var source = new FileItemRecommendationSource(_root);

        // 16.14 is the resolved patch; it has no items/266.json → empty, NOT 16.15's data.
        Assert.Empty(source.List(266));
    }

    [Fact]
    public void MissingDirMissingFileCorruptJson_AllDegradeToEmpty()
    {
        Assert.Empty(new FileItemRecommendationSource(
            Path.Combine(_root, "does-not-exist")).List(266));

        WritePatch("16.15", runeChampions: 3); // no items file at all
        Assert.Empty(new FileItemRecommendationSource(_root).List(266));

        WritePatch("16.16", runeChampions: 3, itemsJsonFor266: "{ not json ]");
        Assert.Empty(new FileItemRecommendationSource(_root).List(266));

        Assert.Empty(new FileItemRecommendationSource("").List(266));
        Assert.Empty(new FileItemRecommendationSource(_root).List(0));
    }
}
