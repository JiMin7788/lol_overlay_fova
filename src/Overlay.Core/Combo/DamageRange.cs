namespace Overlay.Core.Combo;

/// <summary>
/// M24 P1 — a damage contribution expressed as a <c>[Min, Max]</c> UNCERTAINTY RANGE instead of a
/// single number. <see cref="Min"/> is the conservative floor (nothing favorable — no crit,
/// conditions unmet, minimum charge, one multi-hit, enemy defensive runes on); <see cref="Max"/> is
/// the best-case ceiling (everything favorable). A CERTAIN contribution has <c>Min == Max</c>; a
/// user KNOB, once set, collapses its axis to <c>Min == Max</c>. The overlay's "[하한 ~ 상한] 킬각"
/// is the whole combo's total range, and the range WIDTH is the calculation's confidence gap
/// (M24 "핵심 모델 — Uncertainty Range + Knobs").
///
/// <b>Composition</b> (M24 P1 contract): a combo's total range is the per-node ranges summed —
/// floors with floors, ceilings with ceilings (<see cref="op_Addition"/>) — so each independent
/// worst/best case combines. The axes a node's range folds in: crit (floor = non-crit, ceil = crit),
/// conditions (floor = unmet, ceil = met), knobs (collapsed when the user fixes them).
///
/// P1 introduces ONLY this primitive and surfaces the ONE axis already modeled — crit variance
/// (<see cref="DamageCalcResult.TotalDamageMin"/>/<c>TotalDamageMax</c>, aliased on
/// <see cref="ComboResult"/> as <c>CritMin</c>/<c>CritMax</c>) — as <see cref="ComboResult.RangeMin"/>/
/// <c>RangeMax</c>. Per the "통합 range" design that new range is the UNIFIED best/worst case; today,
/// with no condition/knob axis modeled yet, it simply equals the crit range, so no existing damage
/// number changes. P2/P3/P4 fold their axes into it and widen it.
/// </summary>
public readonly record struct DamageRange(double Min, double Max)
{
    /// <summary>A certain contribution — <c>Min == Max == v</c> (no uncertainty on this value).</summary>
    public static DamageRange Single(double v) => new(v, v);

    /// <summary>The empty range.</summary>
    public static readonly DamageRange Zero = new(0, 0);

    /// <summary>The range width (<c>Max - Min</c>) — the confidence gap; 0 for a certain value.</summary>
    public double Width => Max - Min;

    /// <summary>
    /// Builds the range from the crit floor/ceiling, falling back to a single certain
    /// <paramref name="fallback"/> value when the crit endpoints are unset/degenerate (defensive —
    /// a real combo always has <c>min ≤ total ≤ max</c>, but a synthetic or empty
    /// <see cref="DamageCalcResult"/> may carry zeros). Endpoints are ordered so <c>Min ≤ Max</c>.
    /// </summary>
    public static DamageRange FromCritRange(double min, double max, double fallback)
    {
        if (min <= 0 && max <= 0) return Single(fallback);
        double lo = min > 0 ? min : fallback;
        double hi = max > 0 ? max : fallback;
        return lo <= hi ? new(lo, hi) : new(hi, lo);
    }

    /// <summary>Sum of two ranges — floors add, ceilings add (M24 P1 combo-composition rule).</summary>
    public static DamageRange operator +(DamageRange a, DamageRange b) => new(a.Min + b.Min, a.Max + b.Max);

    /// <summary>Scales both endpoints by a certain scalar.</summary>
    public static DamageRange operator *(DamageRange r, double k) => new(r.Min * k, r.Max * k);

    /// <inheritdoc cref="op_Multiply(DamageRange,double)"/>
    public static DamageRange operator *(double k, DamageRange r) => r * k;

    /// <summary>A CERTAIN amplifier <c>×(1 + amp)</c> applied to both endpoints (an always-on
    /// multiplier). A conditional/uncertain amplifier — one that only MAY apply, widening the range
    /// by lifting only the ceiling — is a P4 concern and is intentionally not modeled here.</summary>
    public DamageRange Amplify(double amp) => new(Min * (1 + amp), Max * (1 + amp));
}
