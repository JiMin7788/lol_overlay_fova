using System.Text.Json;
using System.Text.Json.Serialization;
using Overlay.Core.Stats;

namespace Overlay.Core.ChampSelect;

/// <summary>One recommended single item (or boots) row: aggregated Diamond-sample stats for a
/// champion+role from the item aggregation pipeline (<c>tools/aggregate_items.py</c>).</summary>
public sealed record ItemRecEntry(
    [property: JsonPropertyName("itemId")] int ItemId,
    [property: JsonPropertyName("games")] int Games,
    [property: JsonPropertyName("winRate")] double WinRate,
    [property: JsonPropertyName("pickRate")] double PickRate);

/// <summary>One recommended CO-COMPLETED item trio. Honesty contract (the aggregation's own
/// docstring): Match-V5 core payloads carry only final inventories — purchase order needs the
/// timeline endpoint the collector doesn't fetch — so this is "these 3 items were completed
/// together in winning games", never "buy these 3 first". UI copy must say 조합, not 순서.</summary>
public sealed record ItemRecSet(
    [property: JsonPropertyName("items")] IReadOnlyList<int> Items,
    [property: JsonPropertyName("games")] int Games,
    [property: JsonPropertyName("winRate")] double WinRate,
    [property: JsonPropertyName("pickRate")] double PickRate);

/// <summary>Item recommendations for one champion in one role.</summary>
public sealed record ItemRoleRecs(
    string Role,
    int Games,
    IReadOnlyList<ItemRecSet> CoreSets,
    IReadOnlyList<ItemRecEntry> Boots,
    IReadOnlyList<ItemRecEntry> Items);

/// <summary>
/// Item-build recommendations read from the aggregation pipeline's output
/// (<c>tools/aggregate_items.py</c> → <c>rec/{patch}/{bracket}/items/{championKey}.json</c>) — the item
/// sibling of <see cref="FileRecommendationSource"/>, sharing its failure posture (anything
/// missing/corrupt → empty list, never an error) and, deliberately, its PATCH CHOICE:
/// the patch directory is resolved by <see cref="FileRecommendationSource.ResolveLatestPatchDir"/>
/// over the same root, so the item panel and the rune panel can never disagree about which
/// patch they describe. (The items/ subdirectory is invisible to that resolver's coverage
/// count, which scans top-level *.json only — emitting item files never changes the choice.)
/// </summary>
public sealed class FileItemRecommendationSource
{
    private readonly string _recRoot;
    private readonly object _gate = new();
    private readonly Dictionary<int, IReadOnlyList<ItemRoleRecs>> _cache = new();
    private string? _patchDir;
    private bool _patchResolved;

    /// <param name="recRoot">Directory containing per-patch subdirs (e.g. <c>…/rec</c>).
    /// Empty/nonexistent → the source lists nothing.</param>
    private string _bracket;

    /// <param name="bracket">Tier bracket slug (<see cref="RecBrackets"/>),
    /// the same one the rune source reads, so both panels describe one sample.</param>
    public FileItemRecommendationSource(string recRoot, string bracket = "")
    {
        _recRoot = recRoot ?? "";
        _bracket = bracket ?? "";
    }

    /// <summary>Tier bracket read from; setting it discards the cache (see
    /// <see cref="FileRecommendationSource.Bracket"/>).</summary>
    public string Bracket
    {
        get { lock (_gate) return _bracket; }
        set
        {
            lock (_gate)
            {
                string next = value ?? "";
                if (next == _bracket) return;
                _bracket = next;
                _cache.Clear();
                _patchResolved = false;
                _patchDir = null;
            }
        }
    }

    /// <summary>Item recs for a champion, most-played role first. Empty when unavailable.</summary>
    public IReadOnlyList<ItemRoleRecs> List(int championKey)
    {
        if (championKey <= 0 || _recRoot.Length == 0) return Array.Empty<ItemRoleRecs>();
        lock (_gate)
        {
            if (_cache.TryGetValue(championKey, out var cached)) return cached;
            var result = LoadChampion(championKey);
            _cache[championKey] = result;
            return result;
        }
    }

    private sealed class RoleDto
    {
        [JsonPropertyName("games")] public int Games { get; set; }
        [JsonPropertyName("coreSets")] public List<ItemRecSet>? CoreSets { get; set; }
        [JsonPropertyName("boots")] public List<ItemRecEntry>? Boots { get; set; }
        [JsonPropertyName("items")] public List<ItemRecEntry>? Items { get; set; }
    }

    private sealed class ChampionDto
    {
        [JsonPropertyName("roles")] public Dictionary<string, RoleDto>? Roles { get; set; }
    }

    private IReadOnlyList<ItemRoleRecs> LoadChampion(int championKey)
    {
        try
        {
            if (!_patchResolved)
            {
                _patchResolved = true;
                _patchDir = FileRecommendationSource.ResolveLatestPatchDir(_recRoot) is { } patch
                    ? FileRecommendationSource.ContentDir(patch, _bracket)
                    : null;
            }
            if (_patchDir is null) return Array.Empty<ItemRoleRecs>();

            string path = Path.Combine(_patchDir, "items", $"{championKey}.json");
            if (!File.Exists(path)) return Array.Empty<ItemRoleRecs>();

            var doc = JsonSerializer.Deserialize<ChampionDto>(File.ReadAllText(path));
            if (doc?.Roles is null) return Array.Empty<ItemRoleRecs>();

            return doc.Roles
                .Select(kv => new ItemRoleRecs(
                    kv.Key, kv.Value.Games,
                    (IReadOnlyList<ItemRecSet>?)kv.Value.CoreSets ?? Array.Empty<ItemRecSet>(),
                    (IReadOnlyList<ItemRecEntry>?)kv.Value.Boots ?? Array.Empty<ItemRecEntry>(),
                    (IReadOnlyList<ItemRecEntry>?)kv.Value.Items ?? Array.Empty<ItemRecEntry>()))
                .OrderByDescending(r => r.Games) // most-played role first
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<ItemRoleRecs>();
        }
    }
}
