using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// §43-AO — which TEAM <see cref="MinimapRingFinder"/> assigns an icon whose interior and edge
/// carry different team colours.
///
/// <para><b>These fixtures encode a structural invariant, not a tuned threshold</b> (§43-S forbids
/// the latter): the icon's EDGE names the team and the interior is portrait art, whatever colour
/// that art happens to be. Both shapes below were taken from real captured frames, which is where
/// the two failures they guard were measured — an ally Gragas (red-heavy portrait, blue edge) read
/// as an enemy, and an enemy Kassadin (purple portrait, red edge) read as an ally once the blue
/// search was allowed to pick its own centre. The numbers in the assertions are team identities,
/// never coverage/score cutoffs.</para>
/// </summary>
public class MinimapRingFinderTeamTests
{
    private const int RadiusPx = 12;
    private const int RedMargin = 40;

    // The finder probes 0.80x-1.30x of RadiusPx, so the fixture's edge and interior have to straddle
    // that grid the way a real icon does: portrait out to PortraitPx, team colour from there to
    // EdgePx. Drawing them closer together than the grid spacing would mean no probe lands inside
    // the portrait at all, and the test would be measuring its own geometry rather than the finder.
    private const double PortraitPx = 11;
    private const double EdgePx = 13;

    private static readonly (byte R, byte G, byte B) Terrain = (60, 70, 45);   // neither red- nor blue-dominant
    private static readonly (byte R, byte G, byte B) RedRing = (220, 30, 30);
    private static readonly (byte R, byte G, byte B) BlueRing = (30, 90, 220);

    private static MinimapFrame MakeFrame(int size)
    {
        int stride = size * 4;
        var bgra = new byte[stride * size];
        for (int i = 0; i < size * size; i++)
        {
            int off = i * 4;
            bgra[off] = Terrain.B;
            bgra[off + 1] = Terrain.G;
            bgra[off + 2] = Terrain.R;
            bgra[off + 3] = 255;
        }
        return new MinimapFrame(bgra, size, size, stride, 0, flipped: false);
    }

    private static void DrawDisc(MinimapFrame frame, int cx, int cy, double radius, (byte R, byte G, byte B) c)
    {
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
            {
                double dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > radius * radius) continue;
                int off = frame.PixelOffset(x, y);
                frame.Bgra[off] = c.B;
                frame.Bgra[off + 1] = c.G;
                frame.Bgra[off + 2] = c.R;
                frame.Bgra[off + 3] = 255;
            }
    }

    /// <summary>Ally shape: blue edge, red-heavy portrait inside (Gragas). The red interior alone
    /// satisfies the red-ring test, so before §43-AO this icon was handed to the identity stage as
    /// an enemy — measured at 22% of margin-rejected and 51% of threshold-rejected candidates.</summary>
    [Fact]
    public void Find_RedPortraitInsideBlueEdge_IsAlly()
    {
        var frame = MakeFrame(64);
        DrawDisc(frame, 32, 32, EdgePx, BlueRing);       // icon edge = team colour
        DrawDisc(frame, 32, 32, PortraitPx, RedRing);    // portrait art, happens to be red

        var icons = MinimapRingFinder.Find(frame, RadiusPx, RedMargin);

        var icon = Assert.Single(icons);
        Assert.False(icon.IsEnemy);
    }

    /// <summary>Enemy shape: red edge, blue-heavy portrait inside (Kassadin). The mirror image of
    /// the case above, and the reason the outer-ring test must run on the SAME centre the red ring
    /// settled on — an independent centre search re-measured this interior as an outer ring and
    /// cost 54 real enemy sightings in replay.</summary>
    [Fact]
    public void Find_BluePortraitInsideRedEdge_IsEnemy()
    {
        var frame = MakeFrame(64);
        DrawDisc(frame, 32, 32, EdgePx, RedRing);
        DrawDisc(frame, 32, 32, PortraitPx, BlueRing);

        var icons = MinimapRingFinder.Find(frame, RadiusPx, RedMargin);

        var icon = Assert.Single(icons);
        Assert.True(icon.IsEnemy);
    }

    /// <summary>The ordinary enemy icon — red edge, non-team-coloured portrait — must be unaffected
    /// by the outer-ring test.</summary>
    [Fact]
    public void Find_RedEdgeWithNeutralPortrait_IsEnemy()
    {
        var frame = MakeFrame(64);
        DrawDisc(frame, 32, 32, EdgePx, RedRing);
        DrawDisc(frame, 32, 32, PortraitPx, (120, 110, 100));

        var icons = MinimapRingFinder.Find(frame, RadiusPx, RedMargin);

        var icon = Assert.Single(icons);
        Assert.True(icon.IsEnemy);
    }
}
