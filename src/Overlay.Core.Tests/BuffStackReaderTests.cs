using System.IO;
using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 540) <see cref="BuffStackReader"/> against REAL captured buff-bar bands (1080p windowed,
/// PrintWindow, practice tool — the user farmed Nasus 112→2230 stacks live for this corpus). Each
/// fixture is a raw <c>[int32 w][int32 h][BGRA]</c> band whose expected value was verified by the
/// corpus-wide monotonic-stacks check (672/720 frames readable, sequence strictly consistent with
/// +3/+12 Siphoning gains). The template fixture is the Data Dragon NasusQ spell icon downscaled
/// to the measured on-screen buff-icon size (25px), exactly what the client wiring builds.
/// </summary>
public class BuffStackReaderTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "buffstacks");

    private static (byte[] Bgra, int W, int H) LoadBgra(string name)
    {
        var raw = File.ReadAllBytes(Path.Combine(FixtureDir, name));
        int w = BitConverter.ToInt32(raw, 0);
        int h = BitConverter.ToInt32(raw, 4);
        var px = new byte[w * h * 4];
        Array.Copy(raw, 8, px, 0, px.Length);
        return (px, w, h);
    }

    private static BuffStackReader Reader()
    {
        var (tpl, w, _) = LoadBgra("tmpl_NasusQ25.bgra");
        return new BuffStackReader(tpl, w);
    }

    [Theory]
    [InlineData("band_112.bgra", 112)]    // two digits, second buff slot
    [InlineData("band_500.bgra", 500)]    // trailing zeros
    [InlineData("band_792.bgra", 792)]    // contains 9, adjacent buff to the right
    [InlineData("band_1162.bgra", 1162)]  // four digits
    [InlineData("band_2230.bgra", 2230)]  // four digits, late-game corpus end
    public void ReadsTheRealStackCount(string fixture, int expected)
    {
        var (px, w, h) = LoadBgra(fixture);
        Assert.Equal(expected, Reader().ReadStacks(px, w, h, w * 4));
    }

    [Fact]
    public void NoIcon_ReadsNothing()
    {
        var (px, w, h) = LoadBgra("band_noicon.bgra");
        Assert.Null(Reader().ReadStacks(px, w, h, w * 4));
    }

    [Fact]
    public void IconMatch_ClearsThresholdOnRealBands_AndNotOnBackground()
    {
        var reader = Reader();
        var (px, w, h) = LoadBgra("band_792.bgra");
        var hit = reader.FindIcon(px, w, h, w * 4);
        Assert.True(hit.Score >= 0.75, $"real band scored only {hit.Score:0.###}");

        var (bg, bw, bh) = LoadBgra("band_noicon.bgra");
        var miss = reader.FindIcon(bg, bw, bh, bw * 4);
        Assert.True(miss.Score < 0.70, $"background scored {miss.Score:0.###} — false icon");
    }
}
