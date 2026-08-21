using Overlay.Core.Render;

namespace Overlay.Core.Vision;

/// <summary>Predicate deciding whether one BGRA pixel belongs to the minimap's border frame.
/// Injected rather than hardcoded: the exact border color is a LIVE-observed value this sandbox
/// cannot know, so tests supply a known-color predicate over synthetic frames and the production
/// predicate is pinned from a real captured frame (M31 §7 "NO live tuning constants without a
/// fixture"; see the M31 P1 entry in <c>CLAUDE_CODE_TODO.md</c>).</summary>
public delegate bool MinimapBorderPredicate(byte b, byte g, byte r, byte a);

/// <summary>Outcome of a layer-2 refinement pass.</summary>
/// <param name="Refined">True when a clear rectangular border was found; false means keep the
/// input prior rect (M31 §6: never emit a wrong-rect guess).</param>
/// <param name="Rect">The refined minimap rect (frame-pixel space) when <paramref name="Refined"/>
/// is true; otherwise the unchanged search region.</param>
public readonly record struct MinimapAutoCalibrationResult(bool Refined, RenderBounds Rect);

/// <summary>
/// M31 §2 calibration LAYER 2: refine the prior rect to pixel accuracy by finding the minimap's
/// distinct rectangular border frame inside a captured neighborhood. Pure geometry over a pixel
/// buffer — the native capture layer feeds it one frame slightly larger than the prior rect, off
/// the UI thread, as a one-shot on window resize/DPI change (M31 §2 layer 2, &lt;50 ms).
///
/// <para>Detection is a projection scan: count border-predicate hits per row and per column
/// within the search region; a real border edge covers most of its side, so the outermost rows
/// and columns whose hit count clears <paramref name="edgeCoverage"/> of the region span are the
/// frame's four edges. This rejects stray matching pixels (a few specks never fill a whole row/
/// column) without any color-specific tuning here — the color lives entirely in the injected
/// <see cref="MinimapBorderPredicate"/>. Acceptance (M31 §2): ≤ 2 px error; on a clean frame this
/// returns the exact edges (0 px).</para>
/// </summary>
public static class MinimapAutoCalibrator
{
    /// <summary>Fraction of a region side a row/column must cover to count as a border edge.
    /// A full square border spans ~100% of each side; 0.5 tolerates partial occlusion/AA.</summary>
    public const double DefaultEdgeCoverage = 0.5;

    /// <summary>Refine within <paramref name="searchRegion"/> of <paramref name="frame"/>. A
    /// non-positive-size region means "scan the whole frame". Returns
    /// <see cref="MinimapAutoCalibrationResult.Refined"/> = false when no clear border is found.</summary>
    public static MinimapAutoCalibrationResult Refine(
        MinimapFrame frame,
        RenderBounds searchRegion,
        MinimapBorderPredicate isBorder,
        double edgeCoverage = DefaultEdgeCoverage)
    {
        ArgumentNullException.ThrowIfNull(isBorder);

        // Resolve the integer scan window, clamped to the frame; default → whole frame.
        int sx = 0, sy = 0, sw = frame.Width, sh = frame.Height;
        if (searchRegion.Width > 0 && searchRegion.Height > 0)
        {
            sx = Math.Clamp((int)Math.Floor(searchRegion.X), 0, frame.Width);
            sy = Math.Clamp((int)Math.Floor(searchRegion.Y), 0, frame.Height);
            int ex = Math.Clamp((int)Math.Ceiling(searchRegion.X + searchRegion.Width), 0, frame.Width);
            int ey = Math.Clamp((int)Math.Ceiling(searchRegion.Y + searchRegion.Height), 0, frame.Height);
            sw = ex - sx;
            sh = ey - sy;
        }
        if (sw <= 0 || sh <= 0) return new MinimapAutoCalibrationResult(false, searchRegion);

        var rowHits = new int[sh];
        var colHits = new int[sw];
        var buf = frame.Bgra;

        for (int y = 0; y < sh; y++)
        {
            int rowBase = frame.PixelOffset(sx, sy + y);
            int off = rowBase;
            for (int x = 0; x < sw; x++, off += 4)
            {
                if (isBorder(buf[off], buf[off + 1], buf[off + 2], buf[off + 3]))
                {
                    rowHits[y]++;
                    colHits[x]++;
                }
            }
        }

        double rowThreshold = edgeCoverage * sw;
        double colThreshold = edgeCoverage * sh;

        int top = FirstIndexAtLeast(rowHits, rowThreshold, fromStart: true);
        int bottom = FirstIndexAtLeast(rowHits, rowThreshold, fromStart: false);
        int left = FirstIndexAtLeast(colHits, colThreshold, fromStart: true);
        int right = FirstIndexAtLeast(colHits, colThreshold, fromStart: false);

        if (top < 0 || bottom < 0 || left < 0 || right < 0 || bottom < top || right < left)
            return new MinimapAutoCalibrationResult(false, searchRegion);

        var rect = new RenderBounds(
            sx + left,
            sy + top,
            right - left + 1,
            bottom - top + 1);
        return new MinimapAutoCalibrationResult(true, rect);
    }

    /// <summary>First (or last, when <paramref name="fromStart"/> is false) index whose value
    /// meets <paramref name="threshold"/>, or -1 if none do.</summary>
    private static int FirstIndexAtLeast(int[] hits, double threshold, bool fromStart)
    {
        if (fromStart)
        {
            for (int i = 0; i < hits.Length; i++)
                if (hits[i] >= threshold) return i;
        }
        else
        {
            for (int i = hits.Length - 1; i >= 0; i--)
                if (hits[i] >= threshold) return i;
        }
        return -1;
    }
}
