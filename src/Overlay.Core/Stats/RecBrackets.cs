namespace Overlay.Core.Stats;

/// <summary>
/// The cumulative tier brackets the aggregation emits and the client reads — the mirror of
/// <c>tools/_staging_tiers.py</c>'s <c>BRACKETS</c>. Two languages, one list: the slugs here are
/// the directory names on disk, so the two definitions have to agree.
///
/// <para>Cumulative, not single-tier, because that is the question people actually ask — "is this
/// good in my elo" means "at my tier and around it", and once a fixed collection budget is split
/// ten ways no single tier carries a readable sample on its own. Upward from the middle
/// (<c>platinum_plus</c> = Platinum through Challenger) and downward at the bottom
/// (<c>gold_minus</c> = Gold and below), plus <c>all</c>.</para>
/// </summary>
public static class RecBrackets
{
    /// <summary>Ranked tiers, lowest first.</summary>
    public static readonly string[] TierOrder =
    {
        "IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "EMERALD", "DIAMOND",
        "MASTER", "GRANDMASTER", "CHALLENGER",
    };

    /// <summary>Bracket used when nothing is configured.</summary>
    public const string Default = "platinum_plus";

    private static string[] Plus(string tier)
        => TierOrder[Array.IndexOf(TierOrder, tier)..];

    private static string[] Minus(string tier)
        => TierOrder[..(Array.IndexOf(TierOrder, tier) + 1)];

    /// <summary>Every bracket, highest first — the order the client lists them in.</summary>
    public static readonly (string Slug, string[] Tiers)[] All =
    {
        ("all", TierOrder),
        ("challenger", Plus("CHALLENGER")),
        ("grandmaster_plus", Plus("GRANDMASTER")),
        ("master_plus", Plus("MASTER")),
        ("diamond_plus", Plus("DIAMOND")),
        ("emerald_plus", Plus("EMERALD")),
        ("platinum_plus", Plus("PLATINUM")),
        ("gold_plus", Plus("GOLD")),
        ("gold_minus", Minus("GOLD")),
        ("silver_minus", Minus("SILVER")),
        ("bronze_minus", Minus("BRONZE")),
        ("iron", Minus("IRON")),
    };

    /// <summary>Tiers a bracket covers; empty for an unknown slug.</summary>
    public static IReadOnlyList<string> TiersOf(string slug)
    {
        foreach (var (s, tiers) in All)
            if (string.Equals(s, slug, StringComparison.OrdinalIgnoreCase)) return tiers;
        return Array.Empty<string>();
    }

    /// <summary>True when <paramref name="slug"/> names a bracket this build knows.</summary>
    public static bool IsKnown(string slug) => TiersOf(slug).Count > 0;

    /// <summary>Brackets the given seed tiers can answer, highest first. A bracket whose tiers
    /// were never collected is left out entirely — the client offers only what exists, rather
    /// than an entry that would render as "no data for this patch".</summary>
    public static IReadOnlyList<string> Available(IEnumerable<string> stagedTiers)
    {
        var have = new HashSet<string>(stagedTiers, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var (slug, tiers) in All)
            if (Array.Exists(tiers, have.Contains)) result.Add(slug);
        return result;
    }
}
