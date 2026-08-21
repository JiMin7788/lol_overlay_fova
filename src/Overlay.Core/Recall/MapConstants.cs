using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Core.Recall;

/// <summary>
/// M08 Agent Implementation Notes: the lane-distance presets used by the ETA arithmetic,
/// loaded from the bundled <c>data/map_constants.json</c> rather than hardcoded inline.
/// These are static Summoner's Rift map facts (approximate fountain→lane distances in game
/// units) — public map geometry (P1), NOT a live position source. Loaded once per session
/// and treated as immutable (data-algo SKILL "Static data" rule), mirroring
/// <see cref="ObjectiveTimerConfig"/>.
/// </summary>
public sealed class MapConstants
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    /// <summary>Approximate distance (game units) from a team's fountain to each lane,
    /// keyed by a lowercase lane id ("top", "mid", "bot", "jungle").</summary>
    [JsonPropertyName("laneDistances")]
    public Dictionary<string, double> LaneDistances { get; init; } = new();

    /// <summary>Preset distance for <paramref name="lane"/> (case-insensitive), or null if
    /// the lane id is unknown. Callers may supply their own distance instead.</summary>
    public double? GetLaneDistance(string lane)
    {
        foreach (var (key, value) in LaneDistances)
        {
            if (string.Equals(key, lane, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }
}

/// <summary>Loads <see cref="MapConstants"/> from the bundled JSON. Call once per session
/// and cache the result. Mirrors <see cref="ObjectiveTimerConfigLoader"/>.</summary>
public static class MapConstantsLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Default on-disk location relative to the app base directory.</summary>
    public static string DefaultPath
        => Path.Combine(AppContext.BaseDirectory, "Data", "map_constants.json");

    /// <summary>Load and deserialize from <paramref name="path"/> (defaults to the bundled
    /// file). Throws if missing or malformed — a session-startup operation, not the hot
    /// path, so failing loud is correct.</summary>
    public static MapConstants Load(string? path = null)
    {
        path ??= DefaultPath;
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<MapConstants>(stream, Options);
        return data ?? throw new InvalidDataException(
            $"map constants at '{path}' deserialized to null");
    }

    /// <summary>Deserialize from an already-read JSON string (testing/embedding).</summary>
    public static MapConstants LoadFromJson(string json)
    {
        var data = JsonSerializer.Deserialize<MapConstants>(json, Options);
        return data ?? throw new InvalidDataException(
            "map constants JSON deserialized to null");
    }
}
