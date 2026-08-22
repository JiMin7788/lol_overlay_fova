using System.Text.Json;
using System.Text.Json.Serialization;
using Overlay.Core.ChampSelect;

namespace Overlay.Core.Stats;

/// <summary>One lane opponent for the counter columns: the opponent's champion (by Match-V5
/// <c>championName</c>, the same key the portrait loader and <see cref="Localization"/> use) and
/// the champion's measured win rate against it.</summary>
public readonly record struct Matchup(string Name, int Games, double WinRate);

/// <summary>A champion's most favourable and least favourable lane opponents, most extreme first.
/// Either list can be empty when the sample held too few qualifying matchups.</summary>
public sealed record MatchupSet(IReadOnlyList<Matchup> Best, IReadOnlyList<Matchup> Worst);

/// <summary>
/// The statistics view's counter / anti-counter data (유리한 / 불리한 챔피언), read from
/// <c>rec/{patch}/stats/matchups.json</c> (<c>tools/aggregate_matchups.py</c>). Same patch
/// resolution as the tier table (<see cref="FileRecommendationSource.ResolveLatestPatchDir"/>) and
/// the same failure posture (missing/corrupt → no counters, never an error), so the two tabs always
/// describe the same patch.
///
/// <para>Unlike the tier table this file is NOT split by seed tier: a single champion-vs-champion
/// pairing is sparse, so the aggregation pools the whole collected ladder to clear a sample floor
/// at all. The counters therefore do not respond to the view's bracket dropdown — they are the
/// ladder-wide matchup, and the column header says so. The file already holds only the top/bottom
/// few opponents per champion-lane, so this class just parses and serves them.</para>
/// </summary>
public sealed class FileMatchupSource
{
    private readonly string _recRoot;
    private readonly object _gate = new();
    // lane -> championKey -> set
    private Dictionary<string, Dictionary<int, MatchupSet>> _byLane = new(StringComparer.OrdinalIgnoreCase);
    private string _patch = "";
    private bool _loaded;

    public FileMatchupSource(string recRoot) => _recRoot = recRoot ?? "";

    /// <summary>The patch the counters describe ("" when nothing loaded).</summary>
    public string Patch { get { Load(); return _patch; } }

    /// <summary>Best/worst opponents for a champion in one lane, or null when the file lists none
    /// (thin sample, or a lane the champion was never recorded in).</summary>
    public MatchupSet? Get(string lane, int championKey)
    {
        Load();
        return _byLane.TryGetValue(lane, out var byChamp)
               && byChamp.TryGetValue(championKey, out var set)
            ? set : null;
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
                if (FileRecommendationSource.ResolveLatestPatchDir(_recRoot) is not { } patchDir) return;
                string path = Path.Combine(patchDir, "stats", "matchups.json");
                if (!File.Exists(path)) return;

                var root = JsonSerializer.Deserialize<RootDto>(File.ReadAllText(path));
                if (root?.Matchups is null) return;
                _patch = root.Patch ?? "";

                var byLane = new Dictionary<string, Dictionary<int, MatchupSet>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (lane, champs) in root.Matchups)
                {
                    if (champs is null) continue;
                    var byChamp = new Dictionary<int, MatchupSet>();
                    foreach (var (keyText, dto) in champs)
                    {
                        if (dto is null || !int.TryParse(keyText, out int key)) continue;
                        byChamp[key] = new MatchupSet(Convert(dto.Best), Convert(dto.Worst));
                    }
                    if (byChamp.Count > 0) byLane[lane] = byChamp;
                }
                _byLane = byLane;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // failure posture: no counters
            }
        }
    }

    private static IReadOnlyList<Matchup> Convert(List<OppDto>? opps)
    {
        if (opps is null || opps.Count == 0) return Array.Empty<Matchup>();
        var list = new List<Matchup>(opps.Count);
        foreach (var o in opps)
            if (o.Games > 0 && !string.IsNullOrEmpty(o.Name))
                list.Add(new Matchup(o.Name, o.Games, (double)o.Wins / o.Games));
        return list;
    }

    // ── file model ──────────────────────────────────────────────────────────────

    private sealed class OppDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("games")] public int Games { get; set; }
        [JsonPropertyName("wins")] public int Wins { get; set; }
    }

    private sealed class SetDto
    {
        [JsonPropertyName("best")] public List<OppDto>? Best { get; set; }
        [JsonPropertyName("worst")] public List<OppDto>? Worst { get; set; }
    }

    private sealed class RootDto
    {
        [JsonPropertyName("patch")] public string? Patch { get; set; }
        [JsonPropertyName("matchups")] public Dictionary<string, Dictionary<string, SetDto>?>? Matchups { get; set; }
    }
}
