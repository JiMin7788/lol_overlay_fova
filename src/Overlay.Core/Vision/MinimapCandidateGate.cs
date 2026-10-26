using Overlay.Core.Jungle;

namespace Overlay.Core.Vision;

/// <summary>lolF04 (R1) size band: E = <see cref="K"/> x ROI width is the expected icon radius, and a
/// candidate whose ring radius r lies outside [<see cref="Lo"/> x E, <see cref="Hi"/> x E] is rejected.
/// Both edges are inclusive.</summary>
public readonly record struct SizeGate(double K, double Lo, double Hi);

/// <summary>lolF04 (R2c) fixed-structure rule: a candidate whose map-space centre lies within
/// <see cref="Radius01"/> of a <see cref="MinimapStructurePositions"/> entry is kept only when it is
/// clear on every count — score &gt;= <see cref="MinScore"/>, margin &gt;= <see cref="MinMargin"/> (or no
/// runner-up), and r &gt;= <see cref="MinSizeOfE"/> x E.</summary>
public readonly record struct StructureGate(double Radius01, double MinScore, double MinMargin, double MinSizeOfE);

/// <summary>Which rule rejected a candidate, or <see cref="Pass"/>.</summary>
public enum GateResult { Pass, Size, Structure }

/// <summary>Settings for <see cref="MinimapCandidateGate"/>. <see cref="Structure"/> null = size band only.</summary>
public sealed class CandidateGateSettings
{
    public CandidateGateSettings(SizeGate size, StructureGate? structure)
    {
        // AI-NOTE: written as !(x > 0) so a NaN is rejected as well.
        if (!(size.K > 0)) throw new ArgumentException($"K must be > 0 (got {size.K})", nameof(size));
        if (!(size.Lo > 0) || !(size.Lo < size.Hi))
            throw new ArgumentException($"need 0 < Lo < Hi (got Lo {size.Lo}, Hi {size.Hi})", nameof(size));
        if (structure is { } s)
        {
            if (!(s.Radius01 > 0)) throw new ArgumentException($"Radius01 must be > 0 (got {s.Radius01})", nameof(structure));
            if (!(s.MinSizeOfE > 0)) throw new ArgumentException($"MinSizeOfE must be > 0 (got {s.MinSizeOfE})", nameof(structure));
        }
        Size = size;
        Structure = structure;
    }

    public SizeGate Size { get; }
    public StructureGate? Structure { get; }

    /// <summary>The settings lolF04 t2b chose on the user's labels (docs/reports/minimap_lolF04_gates.md §Result):
    /// K 0.0446, size band 0.75-1.50, structure rule D 0.04 / S 0.80 / M 0.10 / LoS 0.75. The report's verdict is
    /// "adopted": the live pipeline (MinimapVisionPipeline.OptionsFor) and the replay's default <c>--gates on</c>
    /// both run it (lolF04 t3).</summary>
    // AI-NOTE: copied from tools/minimap_gate_eval.py's "chosen: --gate-values" line, never tuned by hand —
    // its gates-on check fails when these differ from the values the grid chose.
    public static CandidateGateSettings? Measured { get; } = new(
        new SizeGate(0.0446, 0.75, 1.50),
        new StructureGate(0.04, 0.80, 0.10, 0.75));

    /// <summary>The line the live pipeline logs once at start when these gates are on (lolF04 R4).
    /// <paramref name="structureCount"/> = the number of structure points the rule checks
    /// (<see cref="MinimapStructurePositions.All"/>).</summary>
    // AI-NOTE: invariant culture so the decimal separator in minimap-vision.log never follows the user's locale.
    public string Describe(int structureCount)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        string size = string.Format(ic, "minimap: candidate gates on — size {0:F2}-{1:F2} × E (E = {2:F4} × ROI width); ",
            Size.Lo, Size.Hi, Size.K);
        return Structure is { } s
            ? size + string.Format(ic,
                "structure within {0:F3} of {1} points needs score ≥ {2:F2}, margin ≥ {3:F2}, size ≥ {4:F2} × E",
                s.Radius01, structureCount, s.MinScore, s.MinMargin, s.MinSizeOfE)
            : size + "structure rule off";
    }

    public override string ToString() => Structure is { } s
        ? $"K {Size.K} Lo {Size.Lo} Hi {Size.Hi} D {s.Radius01} S {s.MinScore} M {s.MinMargin} LoS {s.MinSizeOfE}"
        : $"K {Size.K} Lo {Size.Lo} Hi {Size.Hi} (no structure rule)";
}

/// <summary>
/// lolF04 — the two false-positive rules as one pure stage after identity matching (see
/// <see cref="DetectionOptions.Gates"/>). Opt-in: with no settings the detector does not call it.
/// </summary>
public static class MinimapCandidateGate
{
    /// <param name="radiusPx">The ring radius the finder reported (<see cref="MinimapRingFinder.Icon.RadiusPx"/>).</param>
    /// <param name="x01">Candidate centre / ROI width (ROI space, as in <see cref="MinimapSighting.MapPos01"/>).</param>
    /// <param name="y01">Candidate centre / ROI height.</param>
    /// <param name="flipped">The frame's FlipMiniMap state; <paramref name="structures"/> are in map space.</param>
    /// <param name="margin">Best minus runner-up score; NaN when there is no runner-up.</param>
    public static GateResult Evaluate(
        CandidateGateSettings settings, double radiusPx, int frameWidth, double x01, double y01, bool flipped,
        double bestScore, double margin, IReadOnlyList<MinimapStructurePositions.Entry> structures)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(structures);

        // AI-NOTE: E scales with the ROI width, not a fixed px: icon radius / ROI width is the same
        // across widths (r 12.01 at W 269 is the same icon size as r 18.17 at W 407).
        double e = settings.Size.K * frameWidth;
        if (radiusPx < settings.Size.Lo * e || radiusPx > settings.Size.Hi * e) return GateResult.Size;

        if (settings.Structure is not { } s) return GateResult.Pass;

        var (mx, my) = MinimapCoordinateTransform.ToMapSpace(new MapPosition01(x01, y01), flipped);
        bool atStructure = false;
        foreach (var entry in structures)
        {
            double dx = mx - entry.X01, dy = my - entry.Y01;
            if (Math.Sqrt(dx * dx + dy * dy) <= s.Radius01) { atStructure = true; break; }
        }
        if (!atStructure) return GateResult.Pass;

        // AI-NOTE: a structure zone is never blanked — a candidate clear on score, margin and size
        // there (a tower dive) is still a champion. Only the weak look-alike is dropped.
        bool clear = bestScore >= s.MinScore
                     && (double.IsNaN(margin) || margin >= s.MinMargin)
                     && radiusPx >= s.MinSizeOfE * e;
        return clear ? GateResult.Pass : GateResult.Structure;
    }
}
