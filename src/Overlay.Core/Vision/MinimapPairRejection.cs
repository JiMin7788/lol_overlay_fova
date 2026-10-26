namespace Overlay.Core.Vision;

/// <summary>lolF04 (R15, TODO:411) risky-pair mutual rejection: when a candidate's best and runner-up
/// enemy templates are a risky pair — <see cref="MinimapDetector.MaxAchievableMargin"/> below
/// <see cref="MaxPairBound"/> — and the best beats the runner-up by less than <see cref="MinPairMargin"/>,
/// no identity is emitted ("모르겠다": silence is better than a wrong name). Opt-in through
/// <see cref="DetectionOptions.PairRejection"/>; null = the detector behaves exactly as before.</summary>
public sealed class PairRejectionSettings
{
    public PairRejectionSettings(double maxPairBound, double minPairMargin)
    {
        // AI-NOTE: written as !(0 < x && x <= 1) so a NaN is rejected as well; both are in similarity units (0..1).
        if (!(maxPairBound > 0 && maxPairBound <= 1))
            throw new ArgumentException($"MaxPairBound must be in (0, 1] (got {maxPairBound})", nameof(maxPairBound));
        if (!(minPairMargin > 0 && minPairMargin <= 1))
            throw new ArgumentException($"MinPairMargin must be in (0, 1] (got {minPairMargin})", nameof(minPairMargin));
        MaxPairBound = maxPairBound;
        MinPairMargin = minPairMargin;
    }

    public double MaxPairBound { get; }
    public double MinPairMargin { get; }

    /// <summary>The values chosen on the user's labels, null while none is adopted. lolF04 t5d measured the
    /// grid and did not adopt it (docs/reports/minimap_lolF04_variants.md §TODO:411, "Result: measured, not
    /// adopted"), so production and the replay default run without pair rejection.</summary>
    public static PairRejectionSettings? Measured { get; } = null;

    /// <summary>True when <paramref name="best"/> and <paramref name="runnerUp"/> are a risky pair and
    /// <paramref name="margin"/> (best − runner-up score) is too small to tell them apart.</summary>
    public bool Rejects(EnemyTemplate best, EnemyTemplate runnerUp, double margin)
    {
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(runnerUp);
        // AI-NOTE: margin first — the bound costs one template comparison and is needed only for a close call.
        return margin < MinPairMargin && MinimapDetector.MaxAchievableMargin(best, runnerUp) < MaxPairBound;
    }

    /// <summary>The variant's description: the replay prints it, and an adopted variant's start-up log line is
    /// "minimap: detector variant on — " + this (spec R14h).</summary>
    // AI-NOTE: invariant culture so the decimal separator in minimap-vision.log never follows the user's locale.
    public string Describe() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "pair-rejection (bound < {0:F2}, margin ≥ {1:F2})", MaxPairBound, MinPairMargin);

    public override string ToString() => Describe();
}
