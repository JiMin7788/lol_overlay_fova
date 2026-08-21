using Overlay.Core.Jungle;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M31 P3 §4 zone naming — <see cref="MapZoneLookup"/>. Loads the real
/// shipped <c>data/map_regions.json</c> (same file <c>tools/validate_map_regions.py</c> validates
/// for full coverage/no-overlap) and checks a handful of known coordinates resolve to the
/// expected Korean zone names, plus the tolerant-of-missing-file fallback.
/// </summary>
public class MapZoneLookupTests
{
    private static string DataFilePath()
    {
        // Walk up from the test assembly's output dir to the repo-relative source file, mirroring
        // how MapZoneLookup.Default resolves the COPIED (bin-relative) file at runtime — this test
        // instead points LoadFile at the SOURCE file directly so it does not depend on the test
        // project's own output-copy wiring (see MapZoneLookup.LoadFile's doc comment).
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            string candidate = Path.Combine(dir, "src", "Overlay.Core", "Data", "map_regions.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new FileNotFoundException("map_regions.json not found by walking up from the test output directory.");
    }

    [Fact]
    public void KnownCoordinates_ResolveToExpectedZones()
    {
        var lookup = MapZoneLookup.LoadFile(DataFilePath());
        Assert.NotNull(lookup);

        Assert.Equal("탑", lookup!.ZoneName(0.05, 0.05));       // top edge band
        Assert.Equal("봇", lookup.ZoneName(0.95, 0.95));         // bottom edge band
        Assert.Equal("바론 피트", lookup.ZoneName(0.28, 0.28));  // Baron pit center
        Assert.Equal("용 피트", lookup.ZoneName(0.72, 0.72));    // Dragon pit center
        Assert.Equal("미드", lookup.ZoneName(0.50, 0.50));       // mid-lane diagonal crossing
    }

    [Fact]
    public void OutOfRangeCoordinates_AreClampedIntoTheGrid()
    {
        var lookup = MapZoneLookup.LoadFile(DataFilePath());
        Assert.NotNull(lookup);

        // Never throws for a slightly out-of-[0,1] point (e.g. an unclamped upstream transform).
        var zone = lookup!.ZoneName(-0.1, 1.2);
        Assert.False(string.IsNullOrEmpty(zone));
    }

    [Fact]
    public void MissingFile_ReturnsNull_FromLoadFile()
    {
        Assert.Null(MapZoneLookup.LoadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
    }

    [Fact]
    public void Default_NeverThrows_EvenIfDataFileMissingFromTestOutput()
    {
        // MapZoneLookup.Default resolves next to the test assembly; whether or not the file was
        // copied there, ZoneName must never throw (tolerant-of-missing-file convention).
        var zone = MapZoneLookup.Default.ZoneName(0.5, 0.5);
        Assert.False(string.IsNullOrEmpty(zone));
    }
}
