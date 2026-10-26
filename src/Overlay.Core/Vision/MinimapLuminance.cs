namespace Overlay.Core.Vision;

/// <summary>lolF04 (R17, TODO:346) luminance discriminator: when the candidate crop is brighter than its best
/// template by more than <see cref="MaxExcess"/> — the item's premise is that false positives sit on bright
/// backgrounds (brackets, fountain) — no identity is emitted. Opt-in through
/// <see cref="DetectionOptions.Luminance"/>; null = the detector behaves exactly as before.</summary>
public sealed class LuminanceSettings
{
    public LuminanceSettings(double maxExcess)
    {
        // AI-NOTE: written as !(x > 0) so a NaN is rejected as well; the unit is 0-255 luma.
        if (!(maxExcess > 0))
            throw new ArgumentException($"MaxExcess must be > 0 (got {maxExcess})", nameof(maxExcess));
        MaxExcess = maxExcess;
    }

    public double MaxExcess { get; }

    /// <summary>The value chosen on the user's labels, null while none is adopted. lolF04 t5f measured the grid and
    /// did not adopt it (docs/reports/minimap_lolF04_variants.md §TODO:346, "Result: measured, not adopted": every
    /// point lost TPs and removed no FP), so production and the replay default run without the luminance rule.</summary>
    public static LuminanceSettings? Measured { get; } = null;

    /// <summary>True when <paramref name="excess"/> (see <see cref="Excess"/>) is above <see cref="MaxExcess"/>.</summary>
    public bool Rejects(double excess) => excess > MaxExcess;

    /// <summary>Mean Rec.601 luma (0.299R + 0.587G + 0.114B, 0-255) of the downscaled candidate crop over
    /// <paramref name="best"/>'s circle mask, minus the same mean of <paramref name="best"/> itself.
    /// Positive = the crop is brighter than the template.</summary>
    public static double Excess(byte[] cropBgra, EnemyTemplate best)
    {
        ArgumentNullException.ThrowIfNull(cropBgra);
        ArgumentNullException.ThrowIfNull(best);
        double sum = 0;
        int count = 0;
        for (int i = 0; i < best.CircleMask.Length; i++)
        {
            if (!best.CircleMask[i]) continue;
            int off = i * 4;
            sum += Luma(cropBgra, off) - Luma(best.Bgra, off);
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    private static double Luma(byte[] bgra, int off) => 0.299 * bgra[off + 2] + 0.587 * bgra[off + 1] + 0.114 * bgra[off];

    /// <summary>The variant's description: the replay prints it, and an adopted variant's start-up log line is
    /// "minimap: detector variant on — " + this (spec R14h).</summary>
    // AI-NOTE: invariant culture so the log text never follows the user's locale.
    public string Describe() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "luminance (excess ≤ {0:F0})", MaxExcess);

    public override string ToString() => Describe();
}
