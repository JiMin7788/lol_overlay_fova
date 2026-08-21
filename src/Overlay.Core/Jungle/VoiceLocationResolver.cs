using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Core.Jungle;

/// <summary>
/// M31 §B — turns a sighting position into the voice clip key that names where it is.
///
/// <para>Two modes, because the useful granularity depends on what the user is doing:
/// <b>simple</b> collapses the <c>map_regions.json</c> zone grid onto seven broad locations
/// ("적 캠프", "탑라인"), while <b>detail</b> names the nearest camp or objective from
/// <c>jungle_camps.json</c> ("적 레드", "윗 바위게"). Simple mode is a region lookup; detail
/// mode is a nearest-neighbour search over points — different data, hence two files.</para>
///
/// <para>Pure and synchronous: it takes coordinates and returns a clip key, with no Event Bus,
/// no I/O past the one-time data load, and no audio knowledge. That keeps the mode logic unit
/// testable without a sound device — <c>EnemyVoicePlayer</c> owns playback.</para>
///
/// <para>Both data files are v1 straight-line approximations (see their notes), so a wrong
/// answer here is a data-tuning problem, not a code one.</para>
/// </summary>
public sealed class VoiceLocationResolver
{
    /// <summary>Path relative to the app base directory, matching <c>MapZoneLookup</c>'s convention.</summary>
    public const string DefaultCampsPath = "data/jungle_camps.json";

    private readonly IReadOnlyDictionary<string, string> _simple;
    private readonly IReadOnlyList<Camp> _camps;
    private readonly IReadOnlyList<LaneCorridor> _lanes;
    private readonly HashSet<string> _keepZoneInDetail;

    private VoiceLocationResolver(
        IReadOnlyDictionary<string, string> simple,
        IReadOnlyList<Camp> camps,
        IEnumerable<string>? keepZoneInDetail = null,
        IReadOnlyList<LaneCorridor>? lanes = null)
    {
        _simple = simple;
        _camps = camps;
        _lanes = lanes ?? Array.Empty<LaneCorridor>();
        _keepZoneInDetail = new HashSet<string>(
            keepZoneInDetail ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A camp/objective anchor: normalized minimap position plus the clip that names it.</summary>
    public readonly record struct Camp(string Key, string Voice, double X01, double Y01);

    /// <summary>A lane as a corridor: the clip that names it plus the polyline it runs along.
    /// Lanes are lines, not points, so detail mode measures distance to the path.</summary>
    public sealed record LaneCorridor(string Key, string Voice, IReadOnlyList<double[]> Points);

    /// <summary>Lane corridors in load order.</summary>
    public IReadOnlyList<LaneCorridor> Lanes => _lanes;

    /// <summary>Camps in load order — exposed so §C and diagnostics can reuse the anchors.</summary>
    public IReadOnlyList<Camp> Camps => _camps;

    /// <summary>
    /// Loads the camp table. Returns <c>null</c> rather than throwing when the file is missing or
    /// malformed: a missing voice-data file must degrade to "no location callout", never take down
    /// the caller (same posture as the rest of the alert path, which swallows and logs).
    /// </summary>
    public static VoiceLocationResolver? TryLoad(string? path = null)
    {
        try
        {
            var full = path ?? Path.Combine(AppContext.BaseDirectory, DefaultCampsPath);
            if (!File.Exists(full)) return null;

            var doc = JsonSerializer.Deserialize<CampsDoc>(File.ReadAllText(full));
            if (doc?.Camps is null || doc.Camps.Count == 0) return null;

            var camps = doc.Camps
                .Where(c => !string.IsNullOrEmpty(c.Key) && !string.IsNullOrEmpty(c.Voice))
                .Select(c => new Camp(c.Key!, c.Voice!, c.X01, c.Y01))
                .ToList();
            if (camps.Count == 0) return null;

            var lanes = (doc.LaneCorridors ?? new List<LaneDto>())
                .Where(l => !string.IsNullOrEmpty(l.Key) && !string.IsNullOrEmpty(l.Voice)
                            && l.Points is { Count: >= 2 })
                .Select(l => new LaneCorridor(l.Key!, l.Voice!, l.Points!))
                .ToList();

            var simple = doc.SimpleMode ?? new Dictionary<string, string>();
            return new VoiceLocationResolver(simple, camps, doc.DetailModeKeepsZone, lanes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Builds directly from data — for tests, so they need no file on disk.</summary>
    public static VoiceLocationResolver FromData(
        IReadOnlyDictionary<string, string> simpleMode,
        IReadOnlyList<Camp> camps,
        IEnumerable<string>? keepZoneInDetail = null) =>
        new(simpleMode, camps, keepZoneInDetail);

    /// <summary>
    /// Simple mode: maps a <c>map_regions.json</c> zone key onto its broad location clip.
    /// Returns <c>null</c> for an unmapped or empty zone, which the caller voices as a
    /// location-less alert rather than guessing.
    /// </summary>
    public string? ResolveSimple(string zoneKey) =>
        !string.IsNullOrEmpty(zoneKey) && _simple.TryGetValue(zoneKey, out var v) ? v : null;

    /// <summary>
    /// Detail mode: names the nearest camp/objective to <paramref name="x01"/>/<paramref name="y01"/>.
    /// Plain squared-distance scan — 16 anchors, so an index would cost more than it saves.
    /// </summary>
    public string? ResolveDetail(double x01, double y01)
    {
        var best = double.MaxValue;
        string? key = null;

        foreach (var c in _camps)
        {
            var dx = c.X01 - x01;
            var dy = c.Y01 - y01;
            var d = dx * dx + dy * dy;
            if (d < best)
            {
                best = d;
                key = c.Voice;
            }
        }

        // (2026-07-20) Lanes compete on the same nearest-anchor basis as camps. Searching camps
        // alone meant a bot-lane sighting could only ever be named after a CAMP — it was announced
        // as 두꺼비 — because the lane itself was not a candidate. A lane is a corridor rather than
        // a point, so distance is measured to its polyline.
        foreach (var lane in _lanes)
        {
            for (int i = 0; i + 1 < lane.Points.Count; i++)
            {
                double d = DistanceSquaredToSegment(
                    x01, y01,
                    lane.Points[i][0], lane.Points[i][1],
                    lane.Points[i + 1][0], lane.Points[i + 1][1]);
                if (d < best)
                {
                    best = d;
                    key = lane.Voice;
                }
            }
        }

        return key;
    }

    /// <summary>Squared distance from a point to a line segment (clamped projection).</summary>
    private static double DistanceSquaredToSegment(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double vx = bx - ax, vy = by - ay;
        double len2 = vx * vx + vy * vy;
        double t = len2 <= 1e-12 ? 0.0 : Math.Clamp(((px - ax) * vx + (py - ay) * vy) / len2, 0.0, 1.0);
        double dx = px - (ax + t * vx), dy = py - (ay + t * vy);
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Resolves per the configured mode, falling back to simple when detail yields nothing.
    /// <paramref name="detail"/> is the raw config string so an unrecognised value behaves like
    /// "simple" instead of throwing on a hand-edited config.
    ///
    /// <para>Zones listed in <c>detailModeKeepsZone</c> stay coarse even in detail mode. Detail
    /// mode exists to break up zones that were too vague to act on ("적 캠프" spans half the
    /// jungle); lanes were already precise and hold no camps, so nearest-camp on a bot-lane
    /// sighting answers "드래곤" — worse than the name it replaced.</para>
    /// </summary>
    public string? Resolve(string zoneKey, double x01, double y01, string? detail)
    {
        if (!string.Equals(detail, "detail", StringComparison.OrdinalIgnoreCase))
            return ResolveSimple(zoneKey);
        if (_keepZoneInDetail.Contains(zoneKey ?? string.Empty))
            return ResolveSimple(zoneKey!);
        return ResolveDetail(x01, y01) ?? ResolveSimple(zoneKey!);
    }

    private sealed class CampsDoc
    {
        [JsonPropertyName("simpleMode")] public Dictionary<string, string>? SimpleMode { get; set; }
        [JsonPropertyName("detailModeKeepsZone")] public List<string>? DetailModeKeepsZone { get; set; }
        [JsonPropertyName("camps")] public List<CampDto>? Camps { get; set; }
        [JsonPropertyName("laneCorridors")] public List<LaneDto>? LaneCorridors { get; set; }
    }

    private sealed class LaneDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("voice")] public string? Voice { get; set; }
        [JsonPropertyName("points")] public List<double[]>? Points { get; set; }
    }

    private sealed class CampDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("voice")] public string? Voice { get; set; }
        [JsonPropertyName("x01")] public double X01 { get; set; }
        [JsonPropertyName("y01")] public double Y01 { get; set; }
    }
}
