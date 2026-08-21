using System.Text.Json;
using System.Text.Json.Serialization;
using Overlay.Core.ChampSelect;

namespace Overlay.Core.Stats;

/// <summary>One champion+role Diamond distribution from the benchmark aggregation
/// (<c>tools/aggregate_benchmarks.py</c>). Percentile arrays are [p10, p25, p50, p75, p90]
/// of FULL-GAME rates (final totals / game minutes) in the collected Diamond-I KR sample.</summary>
public sealed record BenchmarkEntry(
    string Role,
    int Games,
    IReadOnlyList<double> CsPerMin,
    IReadOnlyList<double> GoldPerMin,
    double KdaMedian)
{
    /// <summary>Approximate percentile rank (0–100) of <paramref name="value"/> within this
    /// entry's distribution given its five stored percentiles, by piecewise-linear interpolation.
    /// Clamped to [5, 95]: outside the stored p10–p90 span the tails are unknown, so the estimate
    /// never claims better than "top 5%" or worse than "bottom 5%" (P2 — no precision the data
    /// does not carry).</summary>
    public static double EstimatePercentile(IReadOnlyList<double> percentiles, double value)
    {
        if (percentiles.Count != 5) return 50;
        ReadOnlySpan<double> ranks = stackalloc double[] { 10, 25, 50, 75, 90 };
        if (value <= percentiles[0]) return 5;
        if (value >= percentiles[4]) return 95;
        for (int i = 1; i < 5; i++)
        {
            if (value > percentiles[i]) continue;
            double lo = percentiles[i - 1], hi = percentiles[i];
            double frac = hi > lo ? (value - lo) / (hi - lo) : 1.0;
            return ranks[i - 1] + frac * (ranks[i] - ranks[i - 1]);
        }
        return 95;
    }

    /// <summary>Percentile rank of a live CS/min value in this distribution (0–100, clamped
    /// 5–95; higher = better).</summary>
    public double CsPercentile(double csPerMin) => EstimatePercentile(CsPerMin, csPerMin);
}

/// <summary>
/// Per-champion Diamond benchmark distributions read from
/// <c>rec/{patch}/{bracket}/stats/benchmarks.json</c>. Third consumer of the aggregation pipeline, sharing
/// <see cref="FileRecommendationSource"/>'s patch resolution (all three panels describe the same
/// patch; the stats/ subdir is invisible to the resolver's coverage count) and its failure
/// posture — anything missing or corrupt yields null, never an error.
///
/// <para>Lookup accepts the champion's numeric key OR a name. Names are normalized to bare
/// lowercase alphanumerics so the Live Client scoreboard's display form ("Dr. Mundo") matches
/// Match-V5's internal form ("DrMundo"); callers with a localized name should first resolve it
/// via <c>ChampionSummary.ResolveKoreanName</c> and may pass both forms.</para>
/// </summary>
public sealed class FileBenchmarkSource
{
    private readonly string _recRoot;
    private readonly object _gate = new();
    private Dictionary<int, (string Name, string MainRole, Dictionary<string, BenchmarkEntry> Roles)>? _byKey;
    private Dictionary<string, int>? _keyByName;
    private bool _loaded;

    private readonly string _bracket;

    /// <param name="bracket">Tier bracket slug (<see cref="RecBrackets"/>) — the distribution the
    /// live comparison is made against, so the HUD label can name it.</param>
    public FileBenchmarkSource(string recRoot, string bracket = "")
    {
        _recRoot = recRoot ?? "";
        _bracket = bracket ?? "";
    }

    /// <summary>The main-role benchmark for a champion by numeric key, or null.</summary>
    public BenchmarkEntry? GetMainRole(int championKey)
    {
        Load();
        if (_byKey is null || !_byKey.TryGetValue(championKey, out var c)) return null;
        return c.Roles.TryGetValue(c.MainRole, out var entry) ? entry : null;
    }

    /// <summary>The main-role benchmark for a champion by (any known form of) name, or null.
    /// Tries each candidate in order; null/empty candidates are skipped.</summary>
    public BenchmarkEntry? GetMainRole(params string?[] nameCandidates)
    {
        Load();
        if (_keyByName is null) return null;
        foreach (var name in nameCandidates)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (_keyByName.TryGetValue(Normalize(name), out int key))
                return GetMainRole(key);
        }
        return null;
    }

    private static string Normalize(string name)
    {
        Span<char> buf = stackalloc char[name.Length];
        int n = 0;
        foreach (char c in name)
            if (char.IsLetterOrDigit(c))
                buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    private sealed class RoleDto
    {
        [JsonPropertyName("games")] public int Games { get; set; }
        [JsonPropertyName("csPerMin")] public List<double>? CsPerMin { get; set; }
        [JsonPropertyName("goldPerMin")] public List<double>? GoldPerMin { get; set; }
        [JsonPropertyName("kdaMedian")] public double KdaMedian { get; set; }
    }

    private sealed class ChampDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("mainRole")] public string? MainRole { get; set; }
        [JsonPropertyName("roles")] public Dictionary<string, RoleDto>? Roles { get; set; }
    }

    private sealed class RootDto
    {
        [JsonPropertyName("champions")] public Dictionary<string, ChampDto>? Champions { get; set; }
    }

    private void Load()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (_recRoot.Length == 0) return;
                if (FileRecommendationSource.ResolveLatestPatchDir(_recRoot) is not { } patch) return;
                string patchDir = FileRecommendationSource.ContentDir(patch, _bracket);
                string path = Path.Combine(patchDir, "stats", "benchmarks.json");
                if (!File.Exists(path)) return;

                var root = JsonSerializer.Deserialize<RootDto>(File.ReadAllText(path));
                if (root?.Champions is null) return;

                var byKey = new Dictionary<int, (string, string, Dictionary<string, BenchmarkEntry>)>();
                var keyByName = new Dictionary<string, int>();
                foreach (var (keyText, champ) in root.Champions)
                {
                    if (!int.TryParse(keyText, out int key) || champ.Roles is null) continue;
                    var roles = new Dictionary<string, BenchmarkEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (role, dto) in champ.Roles)
                    {
                        if (dto.CsPerMin is not { Count: 5 }) continue;
                        roles[role] = new BenchmarkEntry(
                            role, dto.Games, dto.CsPerMin,
                            (IReadOnlyList<double>?)dto.GoldPerMin ?? Array.Empty<double>(),
                            dto.KdaMedian);
                    }
                    if (roles.Count == 0) continue;
                    byKey[key] = (champ.Name ?? "", champ.MainRole ?? "", roles);
                    if (!string.IsNullOrEmpty(champ.Name))
                        keyByName[Normalize(champ.Name)] = key;
                }
                _byKey = byKey;
                _keyByName = keyByName;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // failure posture: benchmarks simply absent
            }
        }
    }
}
