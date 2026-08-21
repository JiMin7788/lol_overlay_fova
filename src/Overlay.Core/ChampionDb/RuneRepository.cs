namespace Overlay.Core.ChampionDb;

/// <summary>M11 public API: RuneRepository.get. See <see cref="ChampionRepository"/> for
/// the load-once/cache-in-memory lifecycle this mirrors.</summary>
public static class RuneRepository
{
    private static Dictionary<string, RuneData> _runes = new();

    public static bool IsInitialized { get; private set; }

    public static void Initialize(IEnumerable<RuneData> runes)
    {
        _runes = runes.ToDictionary(r => r.Id);
        IsInitialized = true;
    }

    /// <summary>Returns the rune, or null if unknown.</summary>
    public static RuneData? Get(string runeId)
        => _runes.TryGetValue(runeId, out var rune) ? rune : null;

    /// <summary>All loaded runes (used by callers/tests needing the full 47+ list).</summary>
    public static IReadOnlyCollection<RuneData> GetAll() => _runes.Values;

    internal static void ResetForTests()
    {
        _runes = new Dictionary<string, RuneData>();
        IsInitialized = false;
    }
}
