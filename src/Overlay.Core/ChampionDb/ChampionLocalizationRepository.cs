namespace Overlay.Core.ChampionDb;

/// <summary>
/// M11 public API for champion display-name localization: a thin championId -&gt; localized-name
/// lookup, populated once from CommunityDragon's champion-summary.json (see
/// <see cref="ChampionLocalizationParser"/>). Deliberately NOT a rich data model like
/// <see cref="ChampionRepository"/> — this exists solely to back
/// <c>Overlay.Client.Localization.ChampionName</c>'s Korean display names for the full
/// ~173-champion cached roster (the old 5-entry hand-typed table there becomes a last-resort
/// fallback for when this repository is uninitialized or missing an id). Mirrors
/// <see cref="ItemRepository"/>'s load-once/cache-in-memory shape.
/// </summary>
public static class ChampionLocalizationRepository
{
    private static IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();

    /// <summary>Inverse of <see cref="_names"/> (localized name -&gt; championId), built once in
    /// <see cref="Initialize"/> to back <see cref="GetEnglishId"/> — the reverse-translation step
    /// <c>ChampionSummary.ResolveKoreanName</c> needs for the full roster (loop-38/43 continuation:
    /// the Live Client API can return a Korean display name for ANY of the ~173 champions, not just
    /// the 5 covered by the old hand-typed table). If two champions ever shared a localized name
    /// (not currently the case), the last one wins — acceptable since this is a display-name reverse
    /// lookup, not gameplay data.</summary>
    private static IReadOnlyDictionary<string, string> _reverse = new Dictionary<string, string>();

    public static bool IsInitialized { get; private set; }

    public static void Initialize(IReadOnlyDictionary<string, string> names)
    {
        // (loop 177) Case-INSENSITIVE id lookup: Data Dragon's champion id casing doesn't always match
        // the CommunityDragon summary's alias casing (e.g. DDragon "Fiddlesticks" vs CDragon
        // "FiddleSticks"), which silently dropped the Korean name for those champions. Ordinal-ignore-case
        // keying reconciles the two so ChampionName() resolves regardless of source casing.
        _names = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (championId, localizedName) in names)
            reverse[localizedName] = championId;
        _reverse = reverse;
        IsInitialized = true;
    }

    /// <summary>Localized display name for a champion id ("Aatrox" -&gt; "아트록스"), or null when
    /// unknown or not yet initialized — callers should fall back to another source, never crash.</summary>
    public static string? Get(string championId)
        => _names.TryGetValue(championId, out var name) ? name : null;

    /// <summary>Reverse of <see cref="Get"/>: the championId for a localized display name
    /// ("아트록스" -&gt; "Aatrox"), or null when unknown or not yet initialized.</summary>
    public static string? GetEnglishId(string localizedName)
        => _reverse.TryGetValue(localizedName, out var id) ? id : null;

    internal static void ResetForTests()
    {
        _names = new Dictionary<string, string>();
        _reverse = new Dictionary<string, string>();
        IsInitialized = false;
    }
}
