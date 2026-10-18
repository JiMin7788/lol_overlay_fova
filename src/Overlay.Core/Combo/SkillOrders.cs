using System.Text.Json;

namespace Overlay.Core.Combo;

/// <summary>
/// (M40) The most common skill max order per champion (<c>data/skill_orders.json</c>, e.g. "QEW" =
/// Q first, then E, then W), from aggregate stats sites (each entry lists its sources). Other
/// players' ability ranks are not in the Live Client API, so an enemy's ranks are assumed from its
/// level and this order (<see cref="SyntheticAttacker.AssumedRanks"/>). Play data, not game data;
/// reviewed like any curated file. Never throws: a missing or malformed file means no orders.
/// </summary>
public static class SkillOrders
{
    private static IReadOnlyDictionary<string, string>? _all;
    private static IReadOnlyDictionary<string, string>? _roles;
    private static readonly object Gate = new();

    private static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "skill_orders.json");

    /// <summary>The max order for <paramref name="championId"/> (Data Dragon id, case-insensitive), or null.</summary>
    public static string? For(string championId)
        => All.TryGetValue(championId, out var order) ? order : null;

    /// <summary>(M41) The champion's most-played role as recorded with its order ("top", "jungle",
    /// "mid", "adc", "support"), or null.</summary>
    public static string? RoleFor(string championId)
    {
        _ = All;
        return _roles!.TryGetValue(championId, out var role) ? role : null;
    }

    public static IReadOnlyDictionary<string, string> All
    {
        get
        {
            lock (Gate)
            {
                if (_all is not null) return _all;
                try
                {
                    var json = File.ReadAllText(DefaultPath);
                    _all = Parse(json);
                    _roles = ParseRoles(json);
                }
                catch
                {
                    _all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                return _all;
            }
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseRoles(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (var champ in doc.RootElement.EnumerateObject())
            if (!champ.Name.StartsWith('_') && champ.Value.ValueKind == JsonValueKind.Object
                && champ.Value.TryGetProperty("role", out var r) && r.GetString() is { Length: > 0 } role)
                result[champ.Name] = role.ToLowerInvariant();
        return result;
    }

    /// <summary>Parses <c>{ "Zed": { "order": "QEW", ... } }</c>; keys starting with "_" are notes.
    /// An order that is not a permutation of Q, W, E is skipped.</summary>
    internal static IReadOnlyDictionary<string, string> Parse(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (var champ in doc.RootElement.EnumerateObject())
        {
            if (champ.Name.StartsWith('_') || champ.Value.ValueKind != JsonValueKind.Object) continue;
            if (champ.Value.TryGetProperty("order", out var o) && o.GetString() is { } order && IsPermutation(order))
                result[champ.Name] = order.ToUpperInvariant();
        }
        return result;
    }

    internal static bool IsPermutation(string order)
        => order.Length == 3 && order.ToUpperInvariant().OrderBy(c => c).SequenceEqual("EQW");
}
