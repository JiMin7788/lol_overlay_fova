namespace Overlay.Core.ChampionDb;

/// <summary>M11 public API: ItemRepository.get. See <see cref="ChampionRepository"/> for
/// the load-once/cache-in-memory lifecycle this mirrors.</summary>
public static class ItemRepository
{
    private static Dictionary<string, ItemData> _items = new();

    public static bool IsInitialized { get; private set; }

    public static void Initialize(IEnumerable<ItemData> items)
    {
        _items = items.ToDictionary(i => i.Id);
        IsInitialized = true;
    }

    /// <summary>Returns the item, or null if unknown.</summary>
    public static ItemData? Get(string itemId)
        => _items.TryGetValue(itemId, out var item) ? item : null;

    /// <summary>Every loaded item, deduplicated by <see cref="ItemData.Name"/> (keeping the entry
    /// with the smallest numeric <see cref="ItemData.Id"/> per name). Data Dragon's item.json lists
    /// several same-name entries for one real item — e.g. an Ornn Masterwork forge-upgrade variant at
    /// a large id offset (base + 320000/660000), or in one case (The Collector) a byte-for-byte
    /// duplicate entry — which previously showed up twice in the M04 item picker. Verified against
    /// the cached 16.13.1 item.json: among <see cref="ItemData.AvailableOnSummonersRift"/> items,
    /// every one of the 27 same-name groups has its "normal"/base item at the smallest id. Added for
    /// the M04 search-to-add item picker (loop 38 pending change item 2), which filters this list by
    /// name as the user types. Does NOT affect <see cref="Get(string)"/> — a direct lookup by a
    /// specific (even deduped-away) id still resolves, since a saved combo/build may reference one.</summary>
    public static IReadOnlyList<ItemData> GetAll()
        => _items.Values
            .GroupBy(i => i.Name)
            .Select(g => g.OrderBy(i => int.TryParse(i.Id, out var n) ? n : int.MaxValue)
                          .ThenBy(i => i.Id, StringComparer.Ordinal)
                          .First())
            .ToList();

    internal static void ResetForTests()
    {
        _items = new Dictionary<string, ItemData>();
        IsInitialized = false;
    }
}
