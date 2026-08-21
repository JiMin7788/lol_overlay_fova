using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Core;

/// <summary>
/// In-memory, deserialized view of the versioned recall-detection config JSON
/// (recall channel duration, fountain-to-lane distances, fallback move speed,
/// and detection heuristic knobs). Loaded ONCE per session by
/// <see cref="RecallConfigLoader"/> and treated as immutable thereafter
/// (data-algo SKILL "Static data" rule).
///
/// Per Hard Rule #4, every distance / duration / threshold the recall estimator
/// uses lives in this JSON, never inline in <see cref="RecallDetector"/>. This is
/// reference data only; it is never on the 0.5s hot path.
/// </summary>
public sealed class RecallConfig
{
    /// <summary>Riot patch these constants were authored against (e.g. "14.12").</summary>
    [JsonPropertyName("patch")]
    public string Patch { get; init; } = "unknown";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    /// <summary>Base recall channel duration in seconds (the fixed cast time before
    /// the player teleports to fountain).</summary>
    [JsonPropertyName("recallChannelSeconds")]
    public double RecallChannelSeconds { get; init; } = 8.0;

    /// <summary>Fallback move speed (map units/sec) when no live or static base speed
    /// is available for the target.</summary>
    [JsonPropertyName("defaultMoveSpeed")]
    public double DefaultMoveSpeed { get; init; } = 335.0;

    /// <summary>Fountain-to-lane reference distances keyed by team ("ORDER"/"CHAOS"),
    /// then by lane role ("TOP"/"MID"/"BOT"/"JUNGLE"/"default").</summary>
    [JsonPropertyName("lanes")]
    public Dictionary<string, Dictionary<string, double>> Lanes { get; init; } = new();

    [JsonPropertyName("detection")]
    public RecallDetectionConfig Detection { get; init; } = new();

    /// <summary>Default lane-role key used when a target's role cannot be inferred.</summary>
    public const string DefaultLaneKey = "default";

    /// <summary>Resolve the fountain-to-lane distance for a team + lane role, falling
    /// back to the team's "default" entry, then to <see cref="DefaultMoveSpeed"/>-based
    /// neutral distance of 6000 only if nothing is configured.</summary>
    public double GetLaneDistance(string? team, string? laneRole)
    {
        if (!string.IsNullOrEmpty(team) && Lanes.TryGetValue(team, out var byLane))
        {
            if (!string.IsNullOrEmpty(laneRole) && byLane.TryGetValue(laneRole, out var d))
                return d;
            if (byLane.TryGetValue(DefaultLaneKey, out var def))
                return def;
        }
        // Last-resort neutral distance if config is empty for this team.
        return 6000.0;
    }
}

/// <summary>Heuristic knobs governing how an item-array change is classified as a
/// fog recall vs a normal visible shop.</summary>
public sealed class RecallDetectionConfig
{
    /// <summary>When true, an item change in the same tick the player also gained
    /// CS/kills/assists is treated as visible activity (not a fog recall).</summary>
    [JsonPropertyName("treatItemChangeWhileFarmingAsVisibleShop")]
    public bool TreatItemChangeWhileFarmingAsVisibleShop { get; init; } = true;

    /// <summary>Recall events below this confidence are dropped by the detector.</summary>
    [JsonPropertyName("minConfidence")]
    public double MinConfidence { get; init; }

    /// <summary>
    /// Confidence assigned when an item change coincides with visible activity
    /// (CS/kill/assist in the same tick) and
    /// <see cref="TreatItemChangeWhileFarmingAsVisibleShop"/> is true.
    /// Governs whether ambiguous "farming-tick" purchases are surfaced above
    /// <see cref="MinConfidence"/>; must be paired with <see cref="MinConfidence"/>
    /// when tuning the detection threshold.
    /// Default: 0.4.
    /// </summary>
    [JsonPropertyName("visibleActivityConfidencePenalty")]
    public double VisibleActivityConfidencePenalty { get; init; } = 0.4;
}

/// <summary>
/// Loads <see cref="RecallConfig"/> from the bundled/patch-versioned JSON file.
/// Call once per session and cache the result (data-algo SKILL "Static data").
/// Mirrors <see cref="StaticGameDataLoader"/> (the DA-001 loader pattern).
/// </summary>
public static class RecallConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Default on-disk location relative to the app base directory.</summary>
    public static string DefaultConfigPath
        => Path.Combine(AppContext.BaseDirectory, "Data", "recall-config.json");

    /// <summary>Load and deserialize from <paramref name="path"/> (defaults to the
    /// bundled file). Throws if missing/malformed — a session-startup operation, not
    /// the hot path, so failing loud is correct.</summary>
    public static RecallConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath;
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<RecallConfig>(stream, Options);
        return data ?? throw new InvalidDataException($"recall config at '{path}' deserialized to null");
    }

    /// <summary>Deserialize from an already-read JSON string (testing/embedding).</summary>
    public static RecallConfig LoadFromJson(string json)
    {
        var data = JsonSerializer.Deserialize<RecallConfig>(json, Options);
        return data ?? throw new InvalidDataException("recall config JSON deserialized to null");
    }
}
