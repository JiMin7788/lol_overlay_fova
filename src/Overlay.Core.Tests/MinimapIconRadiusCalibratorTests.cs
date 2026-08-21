using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// §43-Y — the calibrator must recover a radius it was never told, because that radius differs per
/// PC: minimap size follows resolution, HUD scale and the size slider, and the game has a separate
/// icon-scale setting. The constant it replaces measured 20% wrong against real captures.
///
/// <para>Synthetic frames are legitimate HERE in a way they were not for tuning the match mask: the
/// test plants a border at a KNOWN radius and asks whether the calibrator finds that same number.
/// It is checking a measurement procedure against ground truth, not choosing a threshold from
/// invented pixels.</para>
/// </summary>
public class MinimapIconRadiusCalibratorTests
{
    private const int Size = 200;

    /// <summary>A frame with team-coloured rings of <paramref name="ringRadius"/> around each centre,
    /// over a neutral field, with a duller portrait fill inside — the arrangement the calibrator is
    /// meant to read.</summary>
    private static MinimapFrame FrameWithRings(double ringRadius, params (int X, int Y)[] centres)
    {
        int stride = Size * 4;
        var bgra = new byte[stride * Size];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 70; bgra[i + 1] = 70; bgra[i + 2] = 70; bgra[i + 3] = 255;
        }

        void Put(int x, int y, byte b, byte g, byte r)
        {
            if (x < 0 || x >= Size || y < 0 || y >= Size) return;
            int p = y * stride + x * 4;
            bgra[p] = b; bgra[p + 1] = g; bgra[p + 2] = r; bgra[p + 3] = 255;
        }

        foreach (var (cx, cy) in centres)
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d <= ringRadius - 1.5) Put(x, y, 110, 95, 130);        // portrait: mildly red
                    else if (d <= ringRadius + 0.6) Put(x, y, 30, 30, 225);    // border: strongly red
                }

        return new MinimapFrame(bgra, Size, Size, stride, timestampMs: 0, flipped: false);
    }

    private static MinimapIconRadiusCalibrator Feed(double trueRadius, double baseGuess, int frames = 40)
    {
        var cal = new MinimapIconRadiusCalibrator();
        var centres = new[] { (60, 60), (140, 70), (100, 140) };
        var frame = FrameWithRings(trueRadius, centres);
        for (int f = 0; f < frames; f++)
            foreach (var (x, y) in centres)
                cal.Observe(frame, x, y, baseGuess);
        return cal;
    }

    [Theory]
    // The starting guess is deliberately wrong in both directions, by more than the 20% seen live.
    [InlineData(18.0, 14.5)]
    [InlineData(18.0, 24.0)]
    [InlineData(11.0, 14.5)]
    public void ItRecoversTheTrueRadius_FromAWrongStartingGuess(double trueRadius, double guess)
    {
        double? found = Feed(trueRadius, guess).Resolve(guess);

        Assert.NotNull(found);
        Assert.InRange(found!.Value, trueRadius * 0.88, trueRadius * 1.12);
    }

    [Fact]
    public void ItReportsNothingUntilItHasSeenEnough()
    {
        var cal = new MinimapIconRadiusCalibrator();
        var frame = FrameWithRings(18.0, (60, 60));
        for (int i = 0; i < MinimapIconRadiusCalibrator.MinSamples / 2; i++)
            cal.Observe(frame, 60, 60, 14.5);

        // A stale radius beats a wrong one, so the caller keeps its own value meanwhile.
        Assert.Null(cal.Resolve(14.5));
    }

    [Fact]
    public void AFieldWithNoBorders_YieldsNothing()
    {
        // Flat noise has no prominent peak; offering a radius here would be inventing one.
        int stride = Size * 4;
        var bgra = new byte[stride * Size];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 90; bgra[i + 1] = 88; bgra[i + 2] = 95; bgra[i + 3] = 255;
        }
        var frame = new MinimapFrame(bgra, Size, Size, stride, timestampMs: 0, flipped: false);

        var cal = new MinimapIconRadiusCalibrator();
        for (int i = 0; i < MinimapIconRadiusCalibrator.MinSamples * 2; i++)
            cal.Observe(frame, 100, 100, 14.5);

        Assert.Null(cal.Resolve(14.5));
    }

    /// <summary>A frame whose red comes from soft blobs that FADE outward rather than ending at a
    /// hard edge — a red-heavy portrait sitting in reddish minimap clutter, not a clean ring. Red is
    /// strongest at the centre and declines gradually with distance, so red-minus-blue has no sharp
    /// outer drop anywhere. This is the real artifact that collapsed a game: it raises the small-radius
    /// bins with no edge for the calibrator to lock onto. A hard disc on a neutral field would instead
    /// forge a genuine small ring, which is not what the real data shows.</summary>
    private static MinimapFrame FrameWithSoftRedBlobs(double coreRadius, params (int X, int Y)[] centres)
    {
        int stride = Size * 4;
        var bgra = new byte[stride * Size];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 70; bgra[i + 1] = 70; bgra[i + 2] = 70; bgra[i + 3] = 255;
        }
        foreach (var (cx, cy) in centres)
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    // Redness fades linearly to nothing over ~2x the core, so the metric declines
                    // gently across many radius bins instead of stepping down at one.
                    double t = Math.Max(0, 1.0 - d / (coreRadius * 2.0));
                    if (t <= 0) continue;
                    int p = y * stride + x * 4;
                    byte r = (byte)(70 + t * 155);
                    if (r <= bgra[p + 2]) continue;
                    bgra[p] = (byte)(70 - t * 40); bgra[p + 1] = (byte)(70 - t * 40); bgra[p + 2] = r;
                }
        return new MinimapFrame(bgra, Size, Size, stride, timestampMs: 0, flipped: false);
    }

    /// <summary>§43-AT — the collapse fixed by the outermost-peak rule. Real 18px ringed icons and
    /// small soft red blobs contribute to the SAME accumulated profile: the blobs raise red-minus-blue
    /// at small radii with no outward drop, and taking the tallest bump read that instead of the ring.
    /// The true edge — the outermost peak with a sharp outer drop, where the signal falls away to the
    /// map — must win even when the small-radius bump is taller.</summary>
    [Fact]
    public void ItReadsTheRing_NotTheRedPortraitBlobsAlongside()
    {
        var cal = new MinimapIconRadiusCalibrator();
        var ringed = FrameWithRings(18.0, (60, 60), (140, 70));
        var blobs = FrameWithSoftRedBlobs(8.0, (40, 150), (100, 150), (160, 150), (70, 100), (130, 100));

        // More blob centres than ring centres, so the small-radius bump is the taller one — the exact
        // condition that collapsed a real game to 10px under the tallest-peak rule.
        for (int f = 0; f < 40; f++)
        {
            foreach (var (x, y) in new[] { (60, 60), (140, 70) }) cal.Observe(ringed, x, y, 14.5);
            foreach (var (x, y) in new[] { (40, 150), (100, 150), (160, 150), (70, 100), (130, 100) })
                cal.Observe(blobs, x, y, 14.5);
        }

        double? found = cal.Resolve(14.5);
        Assert.True(found is >= 18.0 * 0.88 and <= 18.0 * 1.12,
            $"expected ~18px, got {found?.ToString() ?? "null"}\n" + cal.DumpProfile(14.5));
    }

    /// <summary>§43-AV — the opening-collapse correction the never-freeze wiring depends on. A roster
    /// slow to detect (blue portraits) shows almost nothing but small red map objects in the first
    /// seconds, so an early Resolve reads a too-small radius; froze, that wrecked a whole game (two
    /// enemies never detected). Because the profile only accumulates, its outermost ring peak moves
    /// OUT to the true size as champions appear, so a later Resolve on the same instance corrects — no
    /// reset, no re-seeding. This asserts exactly that: small first, then the real ring, and the
    /// answer follows.</summary>
    [Fact]
    public void ItCorrectsAnEarlySmallRadius_AsRealIconsAppear()
    {
        var cal = new MinimapIconRadiusCalibrator();

        // Opening: only small red objects (a turret-sized 9px ring) are on the map.
        var small = FrameWithRings(9.0, (60, 60), (140, 70), (100, 140));
        for (int f = 0; f < 50; f++)
            foreach (var (x, y) in new[] { (60, 60), (140, 70), (100, 140) })
                cal.Observe(small, x, y, 14.5);
        double? early = cal.Resolve(14.5);
        Assert.NotNull(early);
        Assert.InRange(early!.Value, 9.0 * 0.82, 9.0 * 1.18);   // reads the small radius early

        // The champions finally show, at the true 18px, and keep being seen.
        var real = FrameWithRings(18.0, (60, 60), (140, 70), (100, 140));
        for (int f = 0; f < 120; f++)
            foreach (var (x, y) in new[] { (60, 60), (140, 70), (100, 140) })
                cal.Observe(real, x, y, 14.5);
        double? corrected = cal.Resolve(14.5);
        Assert.NotNull(corrected);
        Assert.InRange(corrected!.Value, 18.0 * 0.88, 18.0 * 1.12);   // later Resolve has moved out
    }

    [Fact]
    public void Reset_DiscardsWhatItLearned()
    {
        var cal = Feed(18.0, 14.5);
        Assert.NotNull(cal.Resolve(14.5));

        cal.Reset();
        Assert.Null(cal.Resolve(14.5));
        Assert.Equal(0, cal.Samples);
    }
}
