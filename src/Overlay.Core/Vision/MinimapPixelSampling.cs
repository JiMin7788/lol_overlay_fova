namespace Overlay.Core.Vision;

/// <summary>
/// M31 P2 shared pixel-math helpers used by both <see cref="EnemyTemplate"/> (template
/// generation) and <see cref="MinimapDetector"/> (candidate cropping) — kept in one place so the
/// two downscale to the exact same sampling rule and stay directly comparable.
/// </summary>
internal static class MinimapPixelSampling
{
    /// <summary>
    /// Downscales a square <paramref name="srcSize"/>×<paramref name="srcSize"/> region of a
    /// BGRA buffer to <paramref name="targetSize"/>×<paramref name="targetSize"/> using
    /// nearest-neighbor sampling: for each output pixel, pick the single closest source pixel
    /// rather than averaging a box of source pixels. Chosen over box-averaging for simplicity
    /// and determinism — this is a coarse color-identity match (see
    /// <see cref="MinimapDetector"/> class doc), not a high-fidelity resample, so the extra
    /// accuracy box-averaging would buy is not worth the added complexity.
    /// </summary>
    /// <param name="src">Source BGRA8 buffer, row-major.</param>
    /// <param name="stride">Bytes per source row (may exceed <c>width*4</c> — see
    /// <see cref="MinimapFrame"/>).</param>
    /// <param name="srcX">Left edge of the square region to sample, in source pixels.</param>
    /// <param name="srcY">Top edge of the square region to sample, in source pixels.</param>
    /// <param name="srcSize">Side length of the square region to sample, in source pixels.</param>
    /// <param name="targetSize">Output side length.</param>
    /// <returns>Tightly packed BGRA8 buffer, <paramref name="targetSize"/> x
    /// <paramref name="targetSize"/> x 4 bytes, no stride padding.</returns>
    public static byte[] NearestNeighborDownscale(
        byte[] src, int stride, int srcX, int srcY, int srcSize, int targetSize)
    {
        var outPixels = new byte[targetSize * targetSize * 4];
        double ratio = (double)srcSize / targetSize;

        for (int ty = 0; ty < targetSize; ty++)
        {
            int sy = srcY + Math.Min(srcSize - 1, (int)((ty + 0.5) * ratio));
            for (int tx = 0; tx < targetSize; tx++)
            {
                int sx = srcX + Math.Min(srcSize - 1, (int)((tx + 0.5) * ratio));
                int srcOffset = sy * stride + sx * 4;
                int dstOffset = (ty * targetSize + tx) * 4;
                outPixels[dstOffset] = src[srcOffset];
                outPixels[dstOffset + 1] = src[srcOffset + 1];
                outPixels[dstOffset + 2] = src[srcOffset + 2];
                outPixels[dstOffset + 3] = src[srcOffset + 3];
            }
        }

        return outPixels;
    }

    /// <summary>
    /// Builds a row-major inclusion mask for the circle inscribed in a <paramref name="size"/>×
    /// <paramref name="size"/> square (center at the square's center, radius = size/2). Used to
    /// exclude a square icon's corner pixels from color matching — see
    /// <see cref="EnemyTemplate.CircleMask"/>.
    /// </summary>
    public static bool[] BuildCircleMask(int size) => BuildCircleMask(size, 1.0);

    /// <summary>Circular mask keeping pixels within <paramref name="radiusFraction"/> of the
    /// inscribed radius. See <see cref="EnemyTemplate.MatchRadiusFraction"/> for why matching uses
    /// less than the full circle.</summary>
    public static bool[] BuildCircleMask(int size, double radiusFraction)
    {
        var mask = new bool[size * size];
        double center = (size - 1) / 2.0;
        double radius = size / 2.0 * radiusFraction;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - center;
                double dy = y - center;
                mask[y * size + x] = (dx * dx + dy * dy) <= radius * radius;
            }
        }

        return mask;
    }
}
