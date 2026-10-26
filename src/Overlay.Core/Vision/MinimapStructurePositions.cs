using Overlay.Core.Overlay;

namespace Overlay.Core.Vision;

/// <summary>
/// lolF04 (R2a) — fixed red map structures (turrets, inhibitors, nexus) in canonical map space
/// (0..1, x right / y down, as if FlipMiniMap were off — see <see cref="Jungle.MinimapCoordinateTransform"/>).
/// A stage-1 enemy ring sitting on one of these points can be a structure icon rather than a champion.
///
/// <para>Built once, in this order: (1) the 10 datamined <see cref="StructureMinimapLayout"/> positions,
/// (2) points measured from the debug dumps as fixed red rings (MinimapReplay --static-icons, report
/// docs/reports/minimap_lolF04_gates.md §Structures), (3) the point mirror (1-x, 1-y) of every entry of
/// (1) and (2). An entry closer than <see cref="MergeRadius01"/> to an earlier entry is dropped.</para>
///
/// <para>AI-NOTE: label-free on purpose. No label file is read here and the measured points were not
/// chosen with labels: the table is later judged against the labelled set, so it must not come from it.
/// The mirror works because Summoner's Rift is point-symmetric (Order top inhibitor (0.086, 0.756) vs
/// Chaos bot (0.916, 0.243)), so a structure measured on one side gives its twin on the other.</para>
/// </summary>
public static class MinimapStructurePositions
{
    /// <summary>One structure point and where it came from.</summary>
    public readonly record struct Entry(double X01, double Y01, string Source);

    /// <summary>Entries closer than this to an earlier entry are the same structure and are dropped.</summary>
    public const double MergeRadius01 = 0.015;

    private static readonly string[] InhibitorIds =
        { "Order_L2", "Order_L1", "Order_L0", "Chaos_L2", "Chaos_L1", "Chaos_L0" };

    private static readonly string[] NexusTurretIds =
        { "Order_NexusTop", "Order_NexusBot", "Chaos_NexusTop", "Chaos_NexusBot" };

    // Kept clusters of fixed red rings measured from the dumps (report §Structures, "Final table").
    private static readonly Entry[] Measured =
    {
        // docs/reports/minimap_lolF04_gates.md §Structures cluster CL1: the Chaos base between the two
        // nexus turrets, 6 static runs on 20260722/20260726/20260821; 0.0173 from the nearest anchor.
        new(0.8651, 0.1438, "measured 6 dumps / 3 days"),
    };

    /// <summary>Every structure point, anchors first, then measured points, then mirrors.</summary>
    public static IReadOnlyList<Entry> All { get; } = Build();

    private static IReadOnlyList<Entry> Build()
    {
        var primary = new List<Entry>();
        foreach (string id in InhibitorIds)
            if (StructureMinimapLayout.Inhibitor(id) is { } p) primary.Add(new Entry(p.nx, p.ny, "StructureMinimapLayout " + id));
        foreach (string id in NexusTurretIds)
            if (StructureMinimapLayout.NexusTurret(id) is { } p) primary.Add(new Entry(p.nx, p.ny, "StructureMinimapLayout " + id));
        primary.AddRange(Measured);

        var candidates = new List<Entry>(primary);
        foreach (var e in primary) candidates.Add(new Entry(1.0 - e.X01, 1.0 - e.Y01, "mirror of " + e.Source));

        var all = new List<Entry>();
        foreach (var c in candidates)
            if (!all.Any(e => Distance(e, c) < MergeRadius01)) all.Add(c);
        return all;
    }

    private static double Distance(Entry a, Entry b)
    {
        double dx = a.X01 - b.X01, dy = a.Y01 - b.Y01;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
