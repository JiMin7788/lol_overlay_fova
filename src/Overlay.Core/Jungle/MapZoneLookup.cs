using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Core.Jungle;

/// <summary>
/// M31 P3 §4 zone naming: loads the grid-based zone table (<c>data/map_regions.json</c>, see that
/// file's own doc note for the coordinate convention and how it was generated/validated —
/// <c>tools/validate_map_regions.py</c>) and answers "which named region is this canonical
/// map-space point in" for the appear/disappear alert text.
///
/// A GRID (not hand-authored polygons) was chosen for this v1: every cell holds exactly one zone
/// key, which guarantees full coverage of the 0..1 square and zero overlap BY CONSTRUCTION — the
/// spec (§7) explicitly allows "polygon/grid zones" as alternatives.
///
/// Tolerant of a missing/corrupt data file (same convention as <c>SkillDamageDb</c>): falls back
/// to a lookup that always returns a generic "알 수 없는 구역" label rather than throwing, so a
/// packaging mistake degrades alert text instead of crashing the tracker.
/// </summary>
public sealed class MapZoneLookup
{
    private const string UnknownZone = "알 수 없는 구역";

    private readonly string[][] _grid;
    private readonly IReadOnlyDictionary<string, string> _zoneNames;
    private readonly int _gridSize;

    private MapZoneLookup(string[][] grid, IReadOnlyDictionary<string, string> zoneNames, int gridSize)
    {
        _grid = grid;
        _zoneNames = zoneNames;
        _gridSize = gridSize;
    }

    /// <summary>Empty lookup — always returns <see cref="UnknownZone"/>. Used when the data file
    /// is missing/corrupt so callers never need a null check.</summary>
    private static readonly MapZoneLookup EmptyLookup = new(Array.Empty<string[]>(), new Dictionary<string, string>(), 0);

    private static readonly Lazy<MapZoneLookup> LazyDefault = new(() => LoadFile(DefaultPath) ?? EmptyLookup);

    /// <summary>Lazily loaded, process-wide singleton resolved from <see cref="DefaultPath"/> —
    /// same lazy-cache-once convention as <c>SkillDamageDb</c>.</summary>
    public static MapZoneLookup Default => LazyDefault.Value;

    /// <summary>Resolved next to the assembly, same convention as every other bundled data file
    /// in this project (see Overlay.Core.csproj's <c>Data\map_regions.json</c> copy rule, linked
    /// to lowercase <c>data\map_regions.json</c>).</summary>
    private static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "map_regions.json");

    /// <summary>Loads a specific file (used by tests to point at the real source file without
    /// depending on the test project's output-copy wiring, and by <see cref="Default"/>).
    /// Returns null on any I/O/parse failure — never throws.</summary>
    public static MapZoneLookup? LoadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var parsed = JsonSerializer.Deserialize<MapRegionsFile>(stream, JsonOptions);
            if (parsed?.Grid is null || parsed.Zones is null || parsed.GridSize <= 0) return null;
            if (parsed.Grid.Length != parsed.GridSize) return null;
            return new MapZoneLookup(parsed.Grid, parsed.Zones, parsed.GridSize);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Zone display name (Korean) for a canonical map-space point — see
    /// <see cref="MinimapCoordinateTransform"/> for what "canonical" means and how a raw P2
    /// sighting gets here. Never throws; an out-of-range or unloaded lookup returns
    /// <see cref="UnknownZone"/>.</summary>
    public string ZoneName(double x01, double y01) => ZoneKeyLabel(x01, y01).Label;

    /// <summary>Both the English zone KEY (the <c>data/map_regions.json</c> grid cell value, e.g.
    /// "top_lane") and the Korean display <c>Label</c> for a canonical map-space point. M31 §B needs
    /// the English key to pick a pre-recorded voice file (<c>disappear_{role}_{zoneKey}.wav</c>),
    /// which the label-only <see cref="ZoneName"/> can't supply. An unloaded/out-of-range lookup
    /// returns <c>("", <see cref="UnknownZone"/>)</c>; never throws.</summary>
    public (string Key, string Label) ZoneKeyLabel(double x01, double y01)
    {
        if (_gridSize <= 0) return ("", UnknownZone);

        int col = Math.Clamp((int)(x01 * _gridSize), 0, _gridSize - 1);
        int row = Math.Clamp((int)(y01 * _gridSize), 0, _gridSize - 1);
        string key = _grid[row][col];
        return (key, _zoneNames.TryGetValue(key, out var name) ? name : UnknownZone);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class MapRegionsFile
    {
        [JsonPropertyName("gridSize")]
        public int GridSize { get; set; }

        [JsonPropertyName("zones")]
        public Dictionary<string, string>? Zones { get; set; }

        [JsonPropertyName("grid")]
        public string[][]? Grid { get; set; }
    }
}
