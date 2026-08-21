using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Core;

/// <summary>
/// In-memory, deserialized view of the versioned objective-timer config JSON
/// (first-spawn times, respawn intervals, herald-despawn threshold, and the data-driven
/// objective list). Loaded ONCE per session by <see cref="ObjectiveTimerConfigLoader"/>
/// and treated as immutable thereafter (data-algo SKILL "Static data" rule).
///
/// Per Hard Rule #4, every time constant — first-spawn, respawn interval, herald
/// despawn, early-game guard — lives in this JSON, never inline in
/// <see cref="ObjectiveTimer"/>. Adding a new objective (e.g. a future seasonal boss)
/// is a JSON edit, not a code change.
/// </summary>
public sealed class ObjectiveTimerConfig
{
    /// <summary>Riot patch these constants were authored against (e.g. "14.12").</summary>
    [JsonPropertyName("patch")]
    public string Patch { get; init; } = "unknown";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    /// <summary>Data-driven list of all tracked neutral objectives. Each entry carries
    /// its own first-spawn time and respawn interval so new objectives are added via
    /// config, not code.</summary>
    [JsonPropertyName("objectives")]
    public ObjectiveDefinition[] Objectives { get; init; } = Array.Empty<ObjectiveDefinition>();

    /// <summary>Game time (seconds) at which Rift Herald permanently despawns, even if
    /// never killed (default: 1185s = 19:45). After this threshold the Herald timer is
    /// suppressed regardless of taken-state.</summary>
    [JsonPropertyName("heraldDespawnSeconds")]
    public double HeraldDespawnSeconds { get; init; } = 1185.0;

    /// <summary>Game times at or below this value are treated as "game not yet started /
    /// loading screen"; <see cref="ObjectiveTimer.GetTimers"/> returns all objectives as
    /// <see cref="ObjectiveState.NotYetSpawned"/> without computing countdowns.</summary>
    [JsonPropertyName("earlyGameThresholdSeconds")]
    public double EarlyGameThresholdSeconds { get; init; } = 10.0;
}

/// <summary>
/// Static definition of one neutral objective as read from the config JSON.
/// All time values are in seconds from game start. Immutable after deserialisation.
/// </summary>
public sealed class ObjectiveDefinition
{
    /// <summary>Stable, lowercase identifier used as the key in
    /// <see cref="ObjectiveTimer.NotifyObjectiveTaken"/> (e.g. "dragon", "baron",
    /// "rift_herald", "void_grubs", "atakhan").</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable label for UI/logging (e.g. "Dragon", "Baron Nashor").</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Game time (seconds) at which this objective first becomes available.
    /// Before this time the state is <see cref="ObjectiveState.NotYetSpawned"/>.</summary>
    [JsonPropertyName("firstSpawnSeconds")]
    public double FirstSpawnSeconds { get; init; }

    /// <summary>Seconds added to the taken-time to compute the next spawn after the
    /// objective is killed. Objectives that do not respawn (Void Grubs, Atakhan) use
    /// a very large sentinel value (e.g. 999999) from the config so the state stays
    /// <see cref="ObjectiveState.OnRespawn"/> without ever transitioning to
    /// <see cref="ObjectiveState.Up"/>.</summary>
    [JsonPropertyName("respawnSeconds")]
    public double RespawnSeconds { get; init; }
}

/// <summary>
/// Loads <see cref="ObjectiveTimerConfig"/> from the bundled/patch-versioned JSON file.
/// Call once per session and cache the result (data-algo SKILL "Static data").
/// Mirrors <see cref="RecallConfigLoader"/>.
/// </summary>
public static class ObjectiveTimerConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Default on-disk location relative to the app base directory.</summary>
    public static string DefaultConfigPath
        => Path.Combine(AppContext.BaseDirectory, "Data", "objective-config.json");

    /// <summary>Load and deserialize from <paramref name="path"/> (defaults to the
    /// bundled file). Throws if missing or malformed — a session-startup operation,
    /// not the hot path, so failing loud is correct.</summary>
    public static ObjectiveTimerConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath;
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<ObjectiveTimerConfig>(stream, Options);
        return data ?? throw new InvalidDataException(
            $"objective config at '{path}' deserialized to null");
    }

    /// <summary>Deserialize from an already-read JSON string (testing/embedding).</summary>
    public static ObjectiveTimerConfig LoadFromJson(string json)
    {
        var data = JsonSerializer.Deserialize<ObjectiveTimerConfig>(json, Options);
        return data ?? throw new InvalidDataException(
            "objective config JSON deserialized to null");
    }
}
