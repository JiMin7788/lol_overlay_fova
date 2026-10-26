using Overlay.Core.Overlay;
using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// lolF04 (R2a) — the structure table: the 10 <see cref="StructureMinimapLayout"/> anchors, the
/// points measured from the dumps (docs/reports/minimap_lolF04_gates.md §Structures) and their point
/// mirrors, merged within <see cref="MinimapStructurePositions.MergeRadius01"/>.
/// </summary>
public class MinimapStructurePositionsTests
{
    private static readonly string[] AnchorIds =
    {
        "Order_L2", "Order_L1", "Order_L0", "Chaos_L2", "Chaos_L1", "Chaos_L0",
        "Order_NexusTop", "Order_NexusBot", "Chaos_NexusTop", "Chaos_NexusBot",
    };

    private static (double X, double Y) Anchor(string id)
        => (id.Contains("Nexus") ? StructureMinimapLayout.NexusTurret(id) : StructureMinimapLayout.Inhibitor(id))!.Value;

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    [Fact]
    public void Count_Is10AnchorsPlus1MeasuredPlusItsMirror()
    {
        // The anchor layout is point-symmetric, so every anchor mirror merges into the opposite anchor.
        Assert.Equal(12, MinimapStructurePositions.All.Count);
        Assert.Equal(10, MinimapStructurePositions.All.Count(e => e.Source.StartsWith("StructureMinimapLayout ")));
        Assert.Equal(1, MinimapStructurePositions.All.Count(e => e.Source.StartsWith("measured ")));
        Assert.Equal(1, MinimapStructurePositions.All.Count(e => e.Source.StartsWith("mirror of ")));
    }

    [Fact]
    public void EveryAnchor_IsPresentWithItsSource()
    {
        foreach (string id in AnchorIds)
        {
            var (x, y) = Anchor(id);
            var e = Assert.Single(MinimapStructurePositions.All, e => e.Source == "StructureMinimapLayout " + id);
            Assert.Equal(x, e.X01);
            Assert.Equal(y, e.Y01);
        }
    }

    [Fact]
    public void MeasuredPoint_IsTheKeptCluster()
    {
        var e = Assert.Single(MinimapStructurePositions.All, e => e.Source.StartsWith("measured "));
        Assert.Equal(new MinimapStructurePositions.Entry(0.8651, 0.1438, "measured 6 dumps / 3 days"), e);
    }

    [Fact]
    public void EveryMirror_IsPresentOrMergedIntoAnEntryWithin0015()
    {
        foreach (var e in MinimapStructurePositions.All.Where(e => !e.Source.StartsWith("mirror of ")))
        {
            double mx = 1.0 - e.X01, my = 1.0 - e.Y01;
            Assert.Contains(MinimapStructurePositions.All, o => Dist(o.X01, o.Y01, mx, my) < MinimapStructurePositions.MergeRadius01);
        }
        var mirror = Assert.Single(MinimapStructurePositions.All, e => e.Source == "mirror of measured 6 dumps / 3 days");
        Assert.Equal(1.0 - 0.8651, mirror.X01, 12);
        Assert.Equal(1.0 - 0.1438, mirror.Y01, 12);
    }

    [Fact]
    public void NoTwoEntries_AreCloserThan0015()
    {
        Assert.Equal(0.015, MinimapStructurePositions.MergeRadius01);
        var all = MinimapStructurePositions.All;
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
                Assert.True(Dist(all[i].X01, all[i].Y01, all[j].X01, all[j].Y01) >= 0.015, $"{all[i]} vs {all[j]}");
    }

    [Fact]
    public void AllCoordinates_AreIn01_AndEverySourceIsSet()
    {
        foreach (var e in MinimapStructurePositions.All)
        {
            Assert.InRange(e.X01, 0.0, 1.0);
            Assert.InRange(e.Y01, 0.0, 1.0);
            Assert.False(string.IsNullOrWhiteSpace(e.Source));
        }
    }
}
