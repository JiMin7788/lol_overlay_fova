namespace Overlay.Core.Vision;

/// <summary>
/// §43-Y — learns the champion-icon radius from the captured frames instead of assuming it.
///
/// <para>The radius was previously a constant fraction of the ROI width, on the assumption that icons
/// scale with the minimap. They do not reliably: minimap size depends on resolution, HUD scale and
/// the size slider, and the game carries a SEPARATE icon-scale setting. Measured against real
/// captures that constant was about 20% too small, and every size-based decision downstream — the
/// radius window, the overlap split, the blob-area floor — was built on it.</para>
///
/// <para><b>How it measures.</b> A champion icon is drawn with a team-coloured border, so the
/// red-minus-blue signal peaks sharply in a narrow band at the icon's edge and is much lower across
/// the portrait inside it. Sampling that profile by radius around red blobs and accumulating over
/// frames puts the peak at the true radius. This is the same procedure that exposed the 20% error,
/// so the technique is checked before being relied on.</para>
///
/// <para>Deliberately conservative: it reports nothing until it has seen enough samples, and the
/// caller keeps its previous value until then. A wrong radius is worse than a stale one.</para>
///
/// <para>Not thread-safe; the pipeline drives it from the single capture callback.</para>
/// </summary>
public sealed class MinimapIconRadiusCalibrator
{
    /// <summary>Radii probed, as a multiple of the caller's current estimate. Wide enough to contain
    /// a 20%-wrong starting point in either direction.</summary>
    public const double MinProbeScale = 0.55;
    public const double MaxProbeScale = 2.00;

    /// <summary>Profile samples needed before a radius is offered. Each sample is one blob in one
    /// frame; at 30fps with a few enemies visible this is a few seconds of play.</summary>
    public const int MinSamples = 120;

    /// <summary>How much the peak must stand above the profile's median before it counts as a real
    /// border rather than noise. Measured separation was roughly 71 against 32-36, so a peak barely
    /// above the middle means the icons were never actually seen.</summary>
    public const double MinPeakProminence = 8.0;

    /// <summary>How far red-minus-blue must fall on the OUTER side of a peak for it to be an icon
    /// EDGE rather than portrait interior. Just past a real ring is the map, so the drop is steep;
    /// inside a red portrait the value stays high outward. Measured drops: ~20+ per bin at the true
    /// edge, near zero across the interior bump.</summary>
    public const double PeakDropProminence = 8.0;

    private const int Bins = 30;

    private readonly double[] _sum = new double[Bins];
    private readonly int[] _count = new int[Bins];
    private int _samples;

    /// <summary>Samples so far. Exposed for diagnostics/logging.</summary>
    public int Samples => _samples;

    /// <summary>Feeds one blob centre. <paramref name="baseRadiusPx"/> is the caller's current
    /// estimate, used only to set the probe range.</summary>
    public void Observe(MinimapFrame frame, double centreX, double centreY, double baseRadiusPx)
    {
        if (baseRadiusPx <= 0) return;

        for (int b = 0; b < Bins; b++)
        {
            double scale = MinProbeScale + (MaxProbeScale - MinProbeScale) * b / (Bins - 1.0);
            double r = baseRadiusPx * scale;
            if (!SampleCircle(frame, centreX, centreY, r, out double redMinusBlue)) continue;
            _sum[b] += redMinusBlue;
            _count[b]++;
        }
        _samples++;
    }

    /// <summary>Mean red-minus-blue around a circle, or false when too much of it lies outside the
    /// frame to be trustworthy.</summary>
    private static bool SampleCircle(
        MinimapFrame frame, double cx, double cy, double radius, out double redMinusBlue)
    {
        redMinusBlue = 0;
        int steps = Math.Max(16, (int)(radius * 3));
        double sum = 0;
        int hits = 0;

        for (int i = 0; i < steps; i++)
        {
            double a = 2 * Math.PI * i / steps;
            int x = (int)Math.Round(cx + radius * Math.Cos(a));
            int y = (int)Math.Round(cy + radius * Math.Sin(a));
            if (x < 0 || x >= frame.Width || y < 0 || y >= frame.Height) continue;

            int p = y * frame.Stride + x * 4;
            sum += frame.Bgra[p + 2] - frame.Bgra[p];   // R - B
            hits++;
        }

        if (hits < steps * 0.75) return false;          // mostly off-frame: not a usable circle
        redMinusBlue = sum / hits;
        return true;
    }

    /// <summary>The learned radius in pixels, or null while the evidence is insufficient.</summary>
    public double? Resolve(double baseRadiusPx)
    {
        if (_samples < MinSamples || baseRadiusPx <= 0) return null;

        var mean = new double[Bins];
        var present = new List<double>(Bins);
        for (int b = 0; b < Bins; b++)
        {
            if (_count[b] == 0) { mean[b] = double.NaN; continue; }
            mean[b] = _sum[b] / _count[b];
            present.Add(mean[b]);
        }
        if (present.Count < Bins / 2) return null;

        present.Sort();
        double median = present[present.Count / 2];

        // Take the OUTERMOST edge, not the tallest peak.
        //
        // (§43-AT) A red-heavy portrait — an ally Gragas's face, a red minion clump, a turret — is
        // red both inside and at its own small edge, so it raises red-minus-blue at SMALL radii and
        // declines GENTLY on the way out, with no sharp step; a real icon's team ring raises it in a
        // band whose OUTER side falls away steeply, because just past the ring is the map. Both appear
        // in the same accumulated profile. Measured over three games the small-radius bump reached 53
        // while the true 18px ring reached 42-65, so which is taller depends only on how many real
        // champions were visible — the tallest-peak rule read 18px on two games and collapsed to 10px
        // on the third. The icon's real size is the OUTER edge of the ring regardless of height.
        //
        // The icon radius is the OUTERMOST prominent PEAK — a local maximum that then falls away — not
        // the tallest point of the profile.
        //
        // (§43-AT) A team ring is dull inside (portrait), bright at the ring, dark outside (map): it
        // RISES to a peak and then drops. A red-heavy portrait, a turret or a minion clump is instead
        // brightest at its centre and DECLINES monotonically outward — no rise, so no peak, only a
        // slope crossing whatever threshold it happens to cross. Selecting the tallest point, or the
        // outermost prominent point, reads that slope as an edge; measured, it put a real game at 10px
        // (the portrait bump was taller than the ring) and another at 14.5px (the slope crossed the
        // threshold there). Requiring an actual peak rejects both, and in the opening seconds — when
        // few champions are visible and only the portrait bump exists — it correctly returns nothing,
        // leaving the caller on the prior until a real ring establishes itself.
        //
        // The drop is checked within TWO bins, not one: a ring can span two bins, so its peak sits one
        // bin inside the fall-off. Demanding the drop at the peak itself missed those.
        int peak = -1;
        for (int b = Bins - 2; b >= 1; b--)
        {
            if (double.IsNaN(mean[b]) || double.IsNaN(mean[b - 1]) || double.IsNaN(mean[b + 1])) continue;
            bool isPeak = mean[b] >= mean[b - 1] && mean[b] >= mean[b + 1];
            if (!isPeak || mean[b] - median < MinPeakProminence) continue;

            double outer = double.NaN;
            for (int o = b + 1; o <= b + 2 && o < Bins; o++)
                if (!double.IsNaN(mean[o])) outer = double.IsNaN(outer) ? mean[o] : Math.Min(outer, mean[o]);
            if (!double.IsNaN(outer) && mean[b] - outer >= PeakDropProminence) { peak = b; break; }
        }
        if (peak < 0) return null;

        double scale = MinProbeScale + (MaxProbeScale - MinProbeScale) * peak / (Bins - 1.0);
        return baseRadiusPx * scale;
    }

    /// <summary>Diagnostic: the accumulated red-minus-blue profile by radius, for offline analysis
    /// of where the peak sits and why. Not used in production.</summary>
    public string DumpProfile(double baseRadiusPx)
    {
        var sb = new System.Text.StringBuilder("profile (radiusPx : meanRedMinusBlue : samples)\n");
        for (int b = 0; b < Bins; b++)
        {
            double scale = MinProbeScale + (MaxProbeScale - MinProbeScale) * b / (Bins - 1.0);
            double r = baseRadiusPx * scale;
            double mean = _count[b] == 0 ? double.NaN : _sum[b] / _count[b];
            string bar = double.IsNaN(mean) ? "" : new string('#', Math.Max(0, (int)(mean / 3)));
            sb.Append($"  {r,5:F1} : {mean,7:F1} : {_count[b],5}  {bar}\n");
        }
        return sb.ToString();
    }

    /// <summary>Discards everything learned. Call when the capture geometry changes — a new game,
    /// a resolution change — since a radius learned under one layout does not carry to another.</summary>
    public void Reset()
    {
        Array.Clear(_sum);
        Array.Clear(_count);
        _samples = 0;
    }
}
