using System.Text.Json;

namespace Overlay.Core.Summoners;

/// <summary>
/// Loads the bundled, hand-authored summoner-spell damage map (<c>data/summoner_effects.json</c>)
/// and resolves a covered spell's level-scaled flat value. Mirrors
/// <see cref="Overlay.Core.Runes.RuneEffectDb"/>'s lazy-load/lookup shape and its linear
/// level-interpolation convention (baseAtLevel1..baseAtLevel18) — see summoner_effects.json's
/// top-level "_note" for why this is hand-authored (neither Data Dragon nor CommunityDragon exposes
/// a numeric value for Ignite; both give tooltip placeholders only).
///
/// Currently the only covered spell is Ignite (SummonerDot): 70-410 TRUE damage over 5s scaling with
/// the caster's CHAMPION level (= 50 + 20 x level). An uncovered/unknown name returns <c>null</c>.
/// Tolerant of a missing/corrupt file (returns empty) so a build with no summoner_effects.json is a no-op.
/// </summary>
public static class SummonerEffectDb
{
    private static Dictionary<string, SummonerEffectFormula>? _cache;
    private static readonly object Gate = new();

    /// <summary>Location of the bundled map next to the assembly (same convention as
    /// rune_effects.json — see Overlay.Core.csproj copy rules).</summary>
    private static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "summoner_effects.json");

    /// <summary>The formula for one summoner spell display name (e.g. "Ignite"), or <c>null</c>
    /// when not covered. Never throws.</summary>
    public static SummonerEffectFormula? Get(string name)
        => name is not null && Load().TryGetValue(name, out var f) ? f : null;

    /// <summary>All covered formulas (test/introspection helper).</summary>
    public static IReadOnlyCollection<SummonerEffectFormula> All() => Load().Values;

    /// <summary>The spell's flat value at <paramref name="level"/> (the caster's CHAMPION level),
    /// evaluated from its PIECEWISE-LINEAR segments: the first segment whose <c>MaxLevel &gt;= level</c>
    /// gives <c>flatBase + perLevelSlope * level</c>. This reproduces the user's exact in-game Ignite
    /// measurements (two segments: +20/level for L1-5, +25/level for L6+ — see summoner_effects.json's
    /// "_note"), which a single line or a baseAtLevel1..18 interp cannot. No level clamp (Arena/high
    /// modes keep the last segment's slope). Returns <c>null</c> when the name isn't covered.</summary>
    public static double? DamageAtLevel(string name, int level)
    {
        var f = Get(name);
        if (f is null || f.Segments.Count == 0) return null;
        int lv = Math.Max(1, level);
        foreach (var seg in f.Segments)
            if (lv <= seg.MaxLevel)
                return seg.FlatBase + seg.PerLevelSlope * lv;
        var last = f.Segments[^1]; // level beyond every segment's MaxLevel → extend the last slope
        return last.FlatBase + last.PerLevelSlope * lv;
    }

    /// <summary>Convenience: Ignite's TRUE damage at the given champion level
    /// (L1-5 = 50 + 20 x level, L6+ = 25 + 25 x level).</summary>
    public static double? IgniteDamage(int level) => DamageAtLevel("Ignite", level);

    private static Dictionary<string, SummonerEffectFormula> Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;

            var parsed = new Dictionary<string, SummonerEffectFormula>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(DefaultPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(DefaultPath));
                    if (doc.RootElement.TryGetProperty("summoners", out var summoners)
                        && summoners.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var s in summoners.EnumerateObject())
                            if (TryParseFormula(s.Name, s.Value, out var formula))
                                parsed[formula!.Name] = formula;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                parsed.Clear(); // treat any load/parse failure as "no summoner effects"
            }

            _cache = parsed;
            return _cache;
        }
    }

    private static bool TryParseFormula(string key, JsonElement obj, out SummonerEffectFormula? formula)
    {
        formula = null;
        try
        {
            string name = obj.TryGetProperty("name", out var n) ? (n.GetString() ?? key) : key;
            string damageType = obj.TryGetProperty("damageType", out var dt) && dt.ValueKind == JsonValueKind.String
                ? dt.GetString()! : "True";

            var segments = new List<SummonerSegment>();
            if (obj.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                foreach (var seg in segs.EnumerateArray())
                    segments.Add(new SummonerSegment(
                        MaxLevel: seg.GetProperty("maxLevel").GetInt32(),
                        FlatBase: seg.GetProperty("flatBase").GetDouble(),
                        PerLevelSlope: seg.GetProperty("perLevelSlope").GetDouble()));
            }
            if (segments.Count == 0) return false; // no usable curve

            formula = new SummonerEffectFormula(Name: name, DamageType: damageType, Segments: segments);
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Test-only reset so the lazy cache doesn't leak between cases.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _cache = null;
    }
}

/// <summary>One covered summoner spell's damage formula (see summoner_effects.json's "_note" for the
/// citation). <see cref="DamageType"/> is a string ("True" for Ignite) — resolved to the combo
/// damage-type when the node is built.</summary>
public sealed record SummonerEffectFormula(
    string Name,
    string DamageType,
    IReadOnlyList<SummonerSegment> Segments);

/// <summary>One piecewise-linear segment: applies to levels up to <see cref="MaxLevel"/> (inclusive),
/// value = <see cref="FlatBase"/> + <see cref="PerLevelSlope"/> × level. Segments are evaluated in
/// order; the last one's slope also extends to any level beyond its MaxLevel (Arena/high modes).</summary>
public sealed record SummonerSegment(int MaxLevel, double FlatBase, double PerLevelSlope);
