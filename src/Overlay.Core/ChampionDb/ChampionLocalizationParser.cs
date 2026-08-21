using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Pure parsing for CommunityDragon's champion-summary.json (a per-locale bulk file listing
/// every champion's id/name/alias — distinct from the per-champion BIN/gameplay files parsed
/// elsewhere in this namespace). No network/file I/O — mirrors <see cref="DDragonParser"/>'s
/// separation of fetch (<see cref="CommunityDragonClient"/>) from parse. Feeds
/// <see cref="ChampionLocalizationRepository"/>, which backs
/// <c>Overlay.Client.Localization.ChampionName</c>'s display names.
/// </summary>
public static class ChampionLocalizationParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class SummaryEntry
    {
        [JsonPropertyName("alias")] public string Alias { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    }

    /// <summary>Parses the champion-summary JSON array into an alias (this project's canonical
    /// championId, e.g. "Aatrox") -&gt; localized display name map. Entries with an empty
    /// alias (e.g. id 0's placeholder "None" row) or empty name are skipped.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string championSummaryJson)
    {
        var entries = JsonSerializer.Deserialize<List<SummaryEntry>>(championSummaryJson, Options)
            ?? throw new InvalidDataException("champion-summary.json deserialized to null");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Alias) || string.IsNullOrEmpty(entry.Name)) continue;
            map[entry.Alias] = entry.Name;
        }
        return map;
    }
}
