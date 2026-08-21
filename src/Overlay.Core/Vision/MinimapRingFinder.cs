namespace Overlay.Core.Vision;

/// <summary>
/// §43-AI — finds champion icons by their RING rather than by clumps of red pixels.
///
/// <para>A minimap champion icon is a circle of fixed radius outlined in the team's colour. The old
/// stage 1 ignored that and grouped any red-dominant pixels into blobs, which conflated three
/// different things: the ring, red content inside the portrait, and unrelated red map objects. It
/// made blob SIZE depend on how red a champion's art happens to be — one champion's icon came back
/// as a 236-pixel ring while another's arrived as a 748-pixel filled disc — so every size threshold
/// downstream was really measuring portrait colour, not icon size.</para>
///
/// <para>Measured on a real minimap screenshot, testing the circumference instead separates cleanly:
/// all four champion icons return 1.00 ring coverage while turrets and other red structures return
/// 0.08-0.28. That is a decisive gap, against the 0.001 margins the colour matcher was fighting.</para>
///
/// <para>It also settles TEAM at stage 1: the ring is red for enemies and blue for allies, so ally
/// decoy templates, the blob-area floor and the overlap split all become unnecessary — those existed
/// only to recover team information this test reads directly.</para>
/// </summary>
public static class MinimapRingFinder
{
    /// <summary>Share of the circumference that must carry the team colour. The observed split is
    /// 1.00 for real icons against at most 0.28 for anything else, so this sits far from both.</summary>
    public const double MinRingCoverage = 0.55;

    /// <summary>How far the centre may be nudged while looking for the most complete ring. A blob
    /// centroid is a rough seed — for a filled icon it lands near the centre, for a partial ring it
    /// can sit several pixels off, and without this refinement a real icon measured only 0.32
    /// coverage and was missed.</summary>
    public const int CentreSearchRadiusPx = 6;

    /// <summary>Minimum red pixels for a component to be worth testing as a seed.</summary>
    public const int MinSeedPixels = 40;

    /// <summary>Radii probed around the caller's estimate, as multipliers.
    ///
    /// <para>(2026-07-20) Searching the radius is not an optimisation, it breaks a FEEDBACK LOOP.
    /// The finder located icons using the assumed radius, and the radius calibrator then measured
    /// around those same centres — so a wrong radius produced off-centre hits that measured as
    /// confirming the wrong radius. It reported "+0%" and locked in, the centres jittered, and
    /// nothing ever satisfied the tracker's confirmed-position requirement, which silenced markers,
    /// toasts and voice together. Testing several radii and keeping the best ring makes the finder
    /// answer from the pixels rather than from what it was told.</para></summary>
    private static readonly double[] RadiusScales = { 0.80, 0.90, 1.00, 1.10, 1.20, 1.30 };

    /// <summary>A located icon: centre, which team its ring says it belongs to, and how complete
    /// that ring was.</summary>
    /// <param name="RadiusPx">Radius at which this icon's ring was actually found. Reported so the
    /// caller can learn the true icon size from the pixels rather than assuming it.</param>
    public readonly record struct Icon(
        double CentreX, double CentreY, bool IsEnemy, double Coverage, double RadiusPx);

    private static readonly int[] Dx = { 1, -1, 0, 0, 1, -1, 1, -1 };
    private static readonly int[] Dy = { 0, 0, 1, -1, 1, -1, -1, 1 };

    /// <summary>Locates champion icons in a frame. <paramref name="radiusPx"/> is the calibrated
    /// icon radius (see <see cref="MinimapIconRadiusCalibrator"/>).</summary>
    public static IReadOnlyList<Icon> Find(MinimapFrame frame, double radiusPx, int redMargin)
    {
        if (radiusPx <= 2) return Array.Empty<Icon>();

        var seeds = Seeds(frame, redMargin);
        var found = new List<Icon>();

        foreach (var (sx, sy) in seeds)
        {
            // Take the LARGEST radius whose ring is complete, not the best-scoring one. A champion
            // whose portrait is red fills the interior, so every smaller circle also reads as fully
            // red and a plain "best coverage" search collapses onto the smallest radius tried — the
            // measurement returned 11.6px against a true ~18px for exactly this reason. The outer
            // boundary is the icon edge; anything inside it is portrait.
            // Red and blue are searched INDEPENDENTLY, each keeping its own best centre and radius.
            //
            // (§43-AO) They used to share one search that maximised RED, reading blue only at
            // whichever radius red had already chosen. That cannot see the case it most needed to:
            // an ally whose portrait is red-heavy (Gragas) satisfies the red-ring test on a circle
            // INSIDE the icon, so red wins at a small radius and the complete BLUE ring further out
            // — the icon's actual edge — is never looked at. Measured over 400 sampled candidates
            // from a real game, 22% of margin-rejected and 51% of threshold-rejected "enemy" icons
            // were allies arriving this way.
            // (§43-AS) The icon's edge is wherever SOME team ring is most complete, and that ring's
            // colour names the team. Red and blue are measured at every radius, each free to centre
            // itself, and the single most complete ring anywhere in that search decides both.
            //
            // Rules that look right and are not, each ruled out by a real captured icon:
            //   - "red ring => enemy" reads an ally Gragas as an enemy, because his red-heavy
            //     portrait forms a complete red circle INSIDE the icon (0.97 at 0.80x).
            //   - "complete blue ring further out => ally" misses that same Gragas, whose blue edge
            //     sits at the SAME radius the red matched, not beyond it (blue 0.99 at 1.00x).
            //   - "largest complete ring wins" flips a real Kassadin to ally: his purple portrait
            //     keeps blue above the bar out to 1.10x, past his red edge at 1.00x.
            // Taking the most complete ring separates all three with room to spare — Gragas blue
            // 0.99 vs red 0.43, Sett red 1.00 vs blue 0.07, Kassadin red 1.00 vs blue 0.62.
            // Radii are walked from the OUTSIDE IN, and a later radius only wins by strictly beating
            // what is held. Equally complete rings therefore resolve to the outer one, which is the
            // icon's edge — a portrait that happens to be team-coloured fills the interior, so every
            // circle inside the edge reads just as complete as the edge itself.
            double bestCoverage = -1, bx = sx, by = sy, bestR = radiusPx;
            bool enemy = false;
            for (int si = RadiusScales.Length - 1; si >= 0; si--)
            {
                double scale = RadiusScales[si];
                double r = radiusPx * scale;
                if (r <= 2) continue;
                for (int dy = -CentreSearchRadiusPx; dy <= CentreSearchRadiusPx; dy++)
                    for (int dx = -CentreSearchRadiusPx; dx <= CentreSearchRadiusPx; dx++)
                    {
                        if (!RingCoverage(frame, sx + dx, sy + dy, r, redMargin,
                                          out double red, out double blue)) continue;
                        if (red > bestCoverage)
                        { bestCoverage = red; enemy = true; bx = sx + dx; by = sy + dy; bestR = r; }
                        if (blue > bestCoverage)
                        { bestCoverage = blue; enemy = false; bx = sx + dx; by = sy + dy; bestR = r; }
                    }
            }

            if (bestCoverage < MinRingCoverage) continue;

            // One icon can seed several components (ring and interior arrive separately).
            bool dup = false;
            foreach (var f in found)
                if (Math.Sqrt((f.CentreX - bx) * (f.CentreX - bx) + (f.CentreY - by) * (f.CentreY - by)) < bestR)
                { dup = true; break; }
            if (dup) continue;

            found.Add(new Icon(bx, by, enemy, bestCoverage, bestR));
        }

        return found;
    }

    /// <summary>Share of the circumference at <paramref name="radius"/> carrying each team colour.
    /// False when too much of the circle lies outside the frame to judge.</summary>
    private static bool RingCoverage(
        MinimapFrame frame, double cx, double cy, double radius, int redMargin,
        out double redShare, out double blueShare)
    {
        redShare = blueShare = 0;
        int steps = Math.Max(24, (int)(2 * Math.PI * radius / 1.5));
        int red = 0, blue = 0, hits = 0;

        for (int i = 0; i < steps; i++)
        {
            double a = 2 * Math.PI * i / steps;
            int x = (int)Math.Round(cx + radius * Math.Cos(a));
            int y = (int)Math.Round(cy + radius * Math.Sin(a));
            if (x < 0 || x >= frame.Width || y < 0 || y >= frame.Height) continue;

            int p = y * frame.Stride + x * 4;
            int b = frame.Bgra[p], g = frame.Bgra[p + 1], r = frame.Bgra[p + 2];
            hits++;
            if (r - g >= redMargin && r - b >= redMargin) red++;
            else if (b - r >= 25) blue++;
        }

        if (hits < steps * 0.8) return false;
        redShare = red / (double)hits;
        blueShare = blue / (double)hits;
        return true;
    }

    /// <summary>Centroids of red-dominant components, used only as starting points for the ring
    /// search. Their size and shape carry no decision — that is the point of the redesign.</summary>
    private static List<(double X, double Y)> Seeds(MinimapFrame frame, int redMargin)
    {
        int w = frame.Width, h = frame.Height;
        var isRed = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * frame.Stride;
            for (int x = 0; x < w; x++)
            {
                int p = row + x * 4;
                int b = frame.Bgra[p], g = frame.Bgra[p + 1], r = frame.Bgra[p + 2];
                isRed[y * w + x] = r - g >= redMargin && r - b >= redMargin;
            }
        }

        var seen = new bool[w * h];
        var seeds = new List<(double, double)>();
        var stack = new Stack<int>();

        for (int i = 0; i < w * h; i++)
        {
            if (!isRed[i] || seen[i]) continue;
            stack.Push(i); seen[i] = true;
            long sx = 0, sy = 0; int n = 0;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                int kx = k % w, ky = k / w;
                sx += kx; sy += ky; n++;
                for (int d = 0; d < 8; d++)
                {
                    int nx = kx + Dx[d], ny = ky + Dy[d];
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int j = ny * w + nx;
                    if (!isRed[j] || seen[j]) continue;
                    seen[j] = true; stack.Push(j);
                }
            }
            if (n >= MinSeedPixels) seeds.Add((sx / (double)n, sy / (double)n));
        }
        return seeds;
    }
}
