namespace Overlay.Core.Vision;

/// <summary>lolF04 (R18, TODO:442) strict ring: stage 1 accepts a seed's most complete ring of colour c at radius r
/// only when the share of c at r + <see cref="OuterOffsetPx"/> is at most <see cref="MaxOuterShare"/>. Without it any
/// circle inside a team-coloured disc passes as a ring; with it only an edge with no team colour just outside it does.
/// Opt-in through <see cref="DetectionOptions.StrictRing"/>; null = the ring finder behaves exactly as before.</summary>
public sealed class StrictRingSettings
{
    public StrictRingSettings(int outerOffsetPx, double maxOuterShare)
    {
        if (outerOffsetPx < 1)
            throw new ArgumentException($"OuterOffsetPx must be >= 1 (got {outerOffsetPx})", nameof(outerOffsetPx));
        // AI-NOTE: written as !(0 <= x < 1) so a NaN is rejected as well; a share of 1 would let every disc through.
        if (!(maxOuterShare >= 0 && maxOuterShare < 1))
            throw new ArgumentException($"MaxOuterShare must be in [0, 1) (got {maxOuterShare})", nameof(maxOuterShare));
        OuterOffsetPx = outerOffsetPx;
        MaxOuterShare = maxOuterShare;
    }

    /// <summary>How far outside the chosen ring the outer circle is sampled, in ROI pixels.</summary>
    public int OuterOffsetPx { get; }

    /// <summary>Largest share of the ring's own team colour the outer circle may carry.</summary>
    public double MaxOuterShare { get; }

    /// <summary>The value chosen on the user's labels, null while none is adopted. lolF04 t5h measured the grid and
    /// did not adopt it (docs/reports/minimap_lolF04_variants.md §TODO:442, "Result: measured, not adopted": every
    /// point lost TPs and removed 2 of the 4 FP), so production and the replay default run without the strict ring.</summary>
    public static StrictRingSettings? Measured { get; } = null;

    /// <summary>True when an outer-circle share of the ring's colour is too high for a ring edge.</summary>
    public bool Rejects(double outerShare) => outerShare > MaxOuterShare;

    /// <summary>The variant's description: the replay prints it, and an adopted variant's start-up log line is
    /// "minimap: detector variant on — " + this (spec R14h).</summary>
    // AI-NOTE: invariant culture so the log text never follows the user's locale.
    public string Describe() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "strict-ring (outer +{0} px share ≤ {1:F2})", OuterOffsetPx, MaxOuterShare);

    public override string ToString() => Describe();
}
