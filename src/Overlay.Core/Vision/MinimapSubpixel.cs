namespace Overlay.Core.Vision;

/// <summary>lolF04 (R19, TODO:595/590/592) sub-pixel alignment of the stage-2 crop. <see cref="Bilinear"/>: the crop
/// origin keeps the candidate centre's fraction and the region is downscaled to <see cref="EnemyTemplate.Size"/> by
/// bilinear sampling, instead of a <c>Math.Round</c> origin and the nearest-neighbour sampler. <see cref="OffsetStepPx"/>
/// &gt; 0: each template is scored on crops shifted by every offset in [-1, 1] px (x and y) at that step and keeps its
/// best, and the margin is taken between those per-template bests. Opt-in through <see cref="DetectionOptions.Subpixel"/>;
/// null = the detector behaves exactly as before.</summary>
public sealed class SubpixelSettings
{
    /// <summary>The offset steps the R19 grid allows: none, ½ px, ⅓ px.</summary>
    private static readonly double[] AllowedSteps = { 0, 0.5, 1.0 / 3 };

    public SubpixelSettings(bool bilinear, double offsetStepPx)
    {
        // AI-NOTE: exact comparison on purpose; callers pass 1.0 / 3 for the ⅓ px step. A NaN matches nothing.
        if (Array.IndexOf(AllowedSteps, offsetStepPx) < 0)
            throw new ArgumentException($"OffsetStepPx must be 0, 0.5 or 1/3 (got {offsetStepPx})", nameof(offsetStepPx));
        // §80 C-3: a sub-pixel offset fed to the nearest-neighbour sampler mostly selects the same source pixels.
        if (offsetStepPx > 0 && !bilinear)
            throw new ArgumentException("OffsetStepPx > 0 requires Bilinear", nameof(offsetStepPx));
        Bilinear = bilinear;
        OffsetStepPx = offsetStepPx;
    }

    /// <summary>Fractional crop origin and bilinear downscale.</summary>
    public bool Bilinear { get; }

    /// <summary>Step of the crop-offset search over [-1, 1] px, 0 = no search.</summary>
    public double OffsetStepPx { get; }

    /// <summary>The value chosen on the user's labels, null while none is adopted. lolF04 t5i built the variant and
    /// measured the bilinear-only part (docs/reports/minimap_lolF04_variants.md §TODO:595); t5j's measure 2 adopted
    /// neither the bilinear-only crop nor an offset step (§TODO:590/592/601), so production runs without it.</summary>
    public static SubpixelSettings? Measured { get; } = null;

    /// <summary>The crop offsets, in px, applied to x and to y: k × <see cref="OffsetStepPx"/> for k = -n..n with
    /// n × step = 1, or only 0 without a search.</summary>
    internal double[] Offsets()
    {
        if (OffsetStepPx == 0) return new[] { 0.0 };
        int n = (int)Math.Round(1 / OffsetStepPx);
        var offsets = new double[2 * n + 1];
        for (int k = -n; k <= n; k++) offsets[k + n] = k * OffsetStepPx;
        return offsets;
    }

    /// <summary>The variant's description: the replay prints it, and an adopted variant's start-up log line is
    /// "minimap: detector variant on — " + this (spec R14h).</summary>
    // AI-NOTE: invariant culture so the log text never follows the user's locale. Bilinear false (step 0) is
    // today's crop; it is valid but outside the R19 wording, so it reads "nearest".
    public string Describe() => !Bilinear
        ? "subpixel (nearest)"
        : OffsetStepPx == 0
            ? "subpixel (bilinear)"
            : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "subpixel (bilinear, offsets ±1 px step {0:F2})", OffsetStepPx);

    public override string ToString() => Describe();
}
