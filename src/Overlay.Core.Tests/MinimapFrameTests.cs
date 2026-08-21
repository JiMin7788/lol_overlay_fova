using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>M31 P1 output contract invariants for <see cref="MinimapFrame"/>.</summary>
public class MinimapFrameTests
{
    private static byte[] Buffer(int stride, int height) => new byte[stride * height];

    [Fact]
    public void PixelOffset_UsesStride_NotWidthTimesFour()
    {
        // Padded stride: a row is wider than width*4, so offset must step by stride.
        int w = 10, h = 4, stride = w * 4 + 12;
        var frame = new MinimapFrame(Buffer(stride, h), w, h, stride, timestampMs: 0, flipped: false);

        Assert.Equal(0, frame.PixelOffset(0, 0));
        Assert.Equal(stride, frame.PixelOffset(0, 1));       // next row = +stride
        Assert.Equal(stride + 4 * 4, frame.PixelOffset(4, 1)); // +4 px within the row
    }

    [Fact]
    public void FlippedFlag_RoundTrips()
    {
        int w = 8, h = 8, stride = w * 4;
        Assert.True(new MinimapFrame(Buffer(stride, h), w, h, stride, 0, flipped: true).Flipped);
        Assert.False(new MinimapFrame(Buffer(stride, h), w, h, stride, 0, flipped: false).Flipped);
    }

    [Fact]
    public void Ctor_NullBuffer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MinimapFrame(null!, 4, 4, 16, 0, false));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    public void Ctor_NonPositiveDimensions_Throw(int w, int h)
    {
        int stride = Math.Max(1, w) * 4;
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MinimapFrame(new byte[stride * Math.Max(1, h)], w, h, stride, 0, false));
    }

    [Fact]
    public void Ctor_StrideSmallerThanRow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MinimapFrame(new byte[64], 8, 2, stride: 8 * 4 - 1, 0, false));
    }

    [Fact]
    public void Ctor_BufferTooSmallForStrideTimesHeight_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new MinimapFrame(new byte[16], 4, 4, stride: 16, 0, false));
    }
}
