namespace Overlay.Core.Vision;

/// <summary>Tunable thresholds for <see cref="MinimapDetector.Detect"/> (M31 P2 spec §3).
/// Defaults are documented approximations pending a live-fixture pass (P1's own numbers, e.g.
/// ROI size ~280px, come from the same spec section) — every value here is exercised by a
/// synthetic-fixture test, per §7's "no live tuning constants without a fixture" rule.</summary>
/// <param name="RedDominanceMargin">Stage 1: a pixel counts as red-dominant when its R channel
/// exceeds BOTH G and B by at least this many 0-255 units. League's enemy-icon ring is a
/// strongly saturated red, so a wide margin cheaply rejects skin-tone/orange/neutral pixels
/// without any HSV conversion (BGRA-native, per spec §3 "SIMD-friendly").</param>
/// <param name="ExpectedIconRadiusPx">Stage 1: the effective radius (see
/// <see cref="MinimapDetector"/> class doc for the area→radius formula) a genuine champion-icon
/// red ring is expected to have, in ROI pixels. Caller-supplied because it depends on the
/// calibrated minimap size (spec §3: "f(ROI size, known minimap icon scale)") — there is no
/// single correct constant across all users' HUD scales.</param>
/// <param name="ToleranceRadiusPx">Stage 1: candidates whose radius falls outside
/// <c>ExpectedIconRadiusPx ± ToleranceRadiusPx</c> are rejected — this is what filters out ping
/// markers (too small) and other red shapes (wrong size) before the expensive identity step.</param>
/// <param name="ConfidenceThreshold">Stage 2: minimum similarity score (0..1, see
/// <see cref="MinimapDetector"/> class doc) a candidate must reach against its best-matching
/// template to be emitted as a sighting. Below this, the candidate is dropped, never guessed
/// (spec §3: "no guessing" — this is what rejects wards / unknown red blobs that pass stage 1
/// but match no known enemy).</param>
/// <param name="MinMatchMargin">Stage 2: how far the best-matching template must beat the
/// SECOND-best before the match is trusted (0..1, in the same similarity units).
///
/// <para>(2026-07-20, live feedback) <see cref="ConfidenceThreshold"/> alone cannot carry this.
/// Similarity is <c>1 - avgDistance/441.67</c>, so a candidate whose colors are visibly different
/// from a template — mean per-channel distance 100 — still scores 0.774 and clears a 0.75 bar.
/// Nearly every candidate passes, and since the winner is simply the argmax, the detector always
/// names SOMEBODY. The margin is what lets it answer "I don't know": if two templates are within
/// this distance of each other, no identity is emitted.</para></param>
public readonly record struct DetectionOptions(
    int RedDominanceMargin,
    double ExpectedIconRadiusPx,
    double ToleranceRadiusPx,
    double ConfidenceThreshold,
    double MinMatchMargin = 0.03)   // = DefaultMinMatchMargin; a primary-ctor default cannot reference it
{
    /// <summary>MEASURED 2026-07-20 by replaying 120 real captured frames at several margins.
    ///
    /// <para>The first value shipped, 0.03, was a guess and it was rejecting <b>53.6% of all stage-1
    /// candidates</b> — more than every other rejection reason combined. That is what made detection
    /// intermittent: a champion plainly on the minimap was found in only ~42% of frames, and each
    /// gap past the lost-debounce fired a spurious "disappeared".</para>
    ///
    /// <para>Sweep over the real frames (frames with at least one detection / ambiguous spots):
    /// 0.030 → 56% / 2 · 0.015 → 71% / 2 · 0.010 → 72% / 2 · 0.005 → 76% / 3 · 0 → 79% / 3.</para>
    ///
    /// <para><b>REVERTED to 0.03 on the same day.</b> The sweep optimized COVERAGE, but coverage was
    /// the wrong objective: the user's actual complaint was enemies marked in places they could not
    /// be (an afterimage at our own inhibitor), and 0.015 lets more of those through. A missing
    /// marker is a gap in information; a marker in the wrong place is misinformation, and the second
    /// is worse. Re-measure with the sweep in CLAUDE_CODE_TODO §43-I if the metric or mask changes,
    /// but weigh false placements, not frame coverage.</para></summary>
    public const double DefaultMinMatchMargin = 0.03;

    /// <summary>Reasonable starting defaults for a ~280px ROI with a ~10px icon radius —
    /// tune per the acceptance fixtures in <c>MinimapDetectorTests</c>, not in production.</summary>
    public static DetectionOptions Default => new(
        RedDominanceMargin: 40,
        ExpectedIconRadiusPx: 10,
        ToleranceRadiusPx: 4,
        ConfidenceThreshold: 0.75,
        MinMatchMargin: DefaultMinMatchMargin);
}

/// <summary>
/// M31 P2 (docs/modules/M31_MINIMAP_VISION.md §3): two-stage enemy-icon detection over a single
/// captured minimap ROI. Pure, synchronous, no I/O, no networking, no threading, and NO hidden
/// state between calls — <see cref="Detect"/> is a pure function of its three arguments, safe to
/// call repeatedly on the same instance with unrelated frames (there is nothing to reset).
///
/// <para><b>Stage 1 — red-ring prefilter (every frame, cheap):</b> scan every pixel of
/// <see cref="MinimapFrame.Bgra"/> for "red-dominant" (see <see cref="DetectionOptions"/>), then
/// group hits into candidate blobs via 4-connectivity flood fill (connected-component labeling).
/// For each blob: centroid = mean (x, y) of its member pixels; effective radius =
/// <c>sqrt(area / π)</c> (the radius of a circle with the same pixel area — robust to
/// jagged/anti-aliased blob edges, unlike a bounding-box radius). Blobs whose radius falls
/// outside <c>ExpectedIconRadiusPx ± ToleranceRadiusPx</c> are dropped. Zero surviving blobs is
/// the common case and returns immediately — no stage 2 work at all.</para>
///
/// <para><b>Stage 2 — identity match (candidates only, ≤5 templates):</b> each surviving
/// candidate's source region (a square of side <c>2 × ExpectedIconRadiusPx</c> centered on its
/// centroid, clamped to the frame) is downscaled to <see cref="EnemyTemplate.Size"/> with the
/// same nearest-neighbor sampler <see cref="EnemyTemplate.FromSquareIcon"/> uses (see
/// <see cref="MinimapPixelSampling"/>), then compared against every supplied template using MEAN
/// PER-PIXEL RGB EUCLIDEAN DISTANCE, restricted to each template's <see cref="EnemyTemplate.CircleMask"/>
/// pixels (so the candidate's own corners, which were never part of the red-ring blob anyway,
/// never enter the comparison either). Distance is normalized to a 0..1 similarity:
/// <c>similarity = 1 - avgDistance / maxPossibleDistance</c>, where
/// <c>maxPossibleDistance = sqrt(3 * 255^2)</c> (the largest possible per-pixel RGB distance).
/// The highest-similarity template wins; if its similarity is below
/// <see cref="DetectionOptions.ConfidenceThreshold"/> the candidate is dropped entirely (no
/// sighting emitted — never assign a "best guess" identity).</para>
///
/// <para><b>Why mean color distance instead of normalized cross-correlation (NCC):</b> the spec
/// (§3) allows either. Plain mean-RGB-distance was chosen for this round because it is exactly
/// as effective as NCC for this problem (both are pure color-similarity metrics — 16×16
/// solid-color-dominated champion portraits carry almost no exploitable local contrast pattern
/// for NCC's normalization step to add value over) while being simpler to implement and reason
/// about (no per-template mean/variance precomputation, no near-zero-variance edge case for a
/// template that happens to be near-solid-color). If a future round finds portraits that are
/// hard to tell apart by mean color alone, NCC (or a color histogram) is the documented
/// escalation path (spec §3 mentions both).</para>
///
/// <para><b>Explicitly out of scope:</b> no flip-awareness or map-space transform of
/// <see cref="MinimapSighting.MapPos01"/> — see that type's doc comment. No temporal
/// state/tracking across frames (that is P3's presence state machine). No P1 capture, no
/// DDragon fetch (see <see cref="EnemyTemplate.FromSquareIcon"/> doc).</para>
/// </summary>
public sealed class MinimapDetector
{
    /// <summary>Detects known-enemy minimap icons in a single frame. Pure function — no state
    /// is read or written on this instance; calling this repeatedly with different frames never
    /// cross-contaminates results.</summary>
    /// <param name="frame">The captured minimap ROI (see <see cref="MinimapFrame"/>).</param>
    /// <param name="templates">Known-enemy templates to match against (spec: ≤5, the current
    /// game's enemy roster). Empty returns an empty result (nothing to identify against).</param>
    /// <param name="options">Detection thresholds (see <see cref="DetectionOptions"/>).</param>
    public IReadOnlyList<MinimapSighting> Detect(
        MinimapFrame frame, IReadOnlyList<EnemyTemplate> templates, DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(templates);

        if (templates.Count == 0)
            return Array.Empty<MinimapSighting>();

        // §43-AI: icons are located by their RING, which also settles which team they belong to.
        // Ally-ringed icons are dropped here, so ally decoy templates are no longer needed.
        var icons = MinimapRingFinder.Find(frame, options.ExpectedIconRadiusPx, options.RedDominanceMargin);
        if (icons.Count == 0)
            return Array.Empty<MinimapSighting>();

        var sightings = new List<MinimapSighting>(icons.Count);
        foreach (var icon in icons)
        {
            if (!icon.IsEnemy) continue;   // an ally's ring is blue — nothing to identify
            var candidate = new Candidate(icon.CentreX, icon.CentreY, options.ExpectedIconRadiusPx);
            var (championId, confidence) = MatchBestTemplate(frame, candidate, options, templates);
            if (championId is null)
                continue; // below confidence threshold on every template — drop, no guessing

            var mapPos = new MapPosition01(candidate.CentroidX / frame.Width, candidate.CentroidY / frame.Height);
            sightings.Add(new MinimapSighting(championId, mapPos, confidence, frame.TimestampMs));
        }

        return KeepBestPerChampion(sightings);
    }

    /// <summary>Enforces the obvious physical constraint: one champion occupies one place. Where the
    /// same identity is claimed more than once in a frame, only the strongest claim survives.
    ///
    /// <para>(2026-07-20) Measured on real captures, the same champion was being reported twice in a
    /// single frame 66 times, and the duplicates sat within one icon width of each other — a blob
    /// split into sub-candidates whose crops each contain part of the neighbour, so both blends
    /// matched the same low-contrast template. The user saw this as a ghost appearing beside a real
    /// champion. Removing the impossible duplicates costs 1.2% of detections.</para>
    ///
    /// <para>Note this does NOT resolve two DIFFERENT champions overlapping — that is physically
    /// possible and must stay.</para></summary>
    private static IReadOnlyList<MinimapSighting> KeepBestPerChampion(List<MinimapSighting> sightings)
    {
        if (sightings.Count < 2) return sightings;

        var best = new Dictionary<string, MinimapSighting>(StringComparer.Ordinal);
        foreach (var s in sightings)
        {
            if (!best.TryGetValue(s.ChampionId, out var cur) || s.Confidence > cur.Confidence)
                best[s.ChampionId] = s;
        }
        return best.Count == sightings.Count ? sightings : best.Values.ToList();
    }

    private readonly record struct Candidate(double CentroidX, double CentroidY, double Radius);

    private static readonly int[] NeighborDx = { 1, -1, 0, 0 };
    private static readonly int[] NeighborDy = { 0, 0, 1, -1 };

    /// <summary>Stage 1: red-dominant pixel mask → connected components → radius-filtered
    /// candidate list.</summary>
    private static List<Candidate> FindRedRingCandidates(MinimapFrame frame, DetectionOptions options)
    {
        int w = frame.Width, h = frame.Height, stride = frame.Stride;
        var isRed = new bool[w * h];

        for (int y = 0; y < h; y++)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < w; x++)
            {
                int px = rowOffset + x * 4;
                byte b = frame.Bgra[px];
                byte g = frame.Bgra[px + 1];
                byte r = frame.Bgra[px + 2];
                isRed[y * w + x] = r - g >= options.RedDominanceMargin && r - b >= options.RedDominanceMargin;
            }
        }

        var visited = new bool[w * h];
        var candidates = new List<Candidate>();
        var stack = new Stack<(int x, int y)>();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                if (!isRed[idx] || visited[idx]) continue;

                // Flood fill (4-connectivity) this blob. Member pixels are kept because an
                // oversized blob has to be SPLIT rather than dropped (see below), and that needs
                // the pixels, not just their centroid.
                long sumX = 0, sumY = 0;
                var blob = new List<(int X, int Y)>();
                stack.Push((x, y));
                visited[idx] = true;

                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    sumX += cx;
                    sumY += cy;
                    blob.Add((cx, cy));

                    for (int n = 0; n < 4; n++)
                    {
                        int nx = cx + NeighborDx[n], ny = cy + NeighborDy[n];
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int nIdx = ny * w + nx;
                        if (!isRed[nIdx] || visited[nIdx]) continue;
                        visited[nIdx] = true;
                        stack.Push((nx, ny));
                    }
                }

                int area = blob.Count;
                double radius = Math.Sqrt(area / Math.PI);
                double minR = options.ExpectedIconRadiusPx - options.ToleranceRadiusPx;
                double maxR = options.ExpectedIconRadiusPx + options.ToleranceRadiusPx;

                // (2026-07-20, live feedback) Overlap is decided on AREA, not radius. Two champions
                // standing on each other merge into one red component, and because radius grows only
                // as sqrt(area) the merged blob usually still passes the radius window — with the
                // production tolerance (40% of the radius) a clean pair lands around 7.8px against an
                // 8.4px ceiling. So it was not being dropped; it was being read as ONE icon, and the
                // second champion silently went missing, which the presence machine then reported as
                // a disappearance. Area is linear in icon count and says plainly what happened.
                double expectedArea = Math.PI * options.ExpectedIconRadiusPx * options.ExpectedIconRadiusPx;
                double areaRatio = area / Math.Max(1.0, expectedArea);

                if (areaRatio >= OverlapAreaRatio)
                {
                    int k = Math.Clamp((int)Math.Round(areaRatio), 2, MaxOverlapSplit);
                    foreach (var c in SplitBlob(blob, k, options))
                        candidates.Add(c);
                }
                else if (areaRatio >= MinIconAreaRatio && radius >= minR && radius <= maxR)
                {
                    candidates.Add(new Candidate(sumX / (double)area, sumY / (double)area, radius));
                }
            }
        }

        return candidates;
    }

    /// <summary>How many icons' worth of area a component must cover before it is treated as a
    /// merged pile rather than one icon.
    ///
    /// <para>Rescaled with <see cref="MinIconAreaRatio"/> when the calibrated radius corrected: at
    /// the true radius a single icon's blob tops out near 1.10 in the same captures, so 1.35 sits
    /// just above the observed single-icon ceiling. PROVISIONAL — the sample contained no blob above
    /// 1.10, so the lower edge of the genuine two-icon population has not been observed directly and
    /// this should be re-measured once frames with the corrected radius exist.</para></summary>
    private const double OverlapAreaRatio = 1.35;

    /// <summary>Smallest blob, as a fraction of one icon's area, that may be treated as a champion.
    ///
    /// <para>Stage 1 keeps RED-dominant pixels. An enemy icon is ringed red, so its ring joins the
    /// blob and the blob comes out icon-sized. An ALLY is ringed BLUE, so the ring never qualifies
    /// and only the reddish part of their portrait survives, giving a much smaller blob. Blob size
    /// is therefore a team signal owing nothing to portrait similarity — which matters because
    /// portrait similarity is exactly what fails when an enemy ties an ally to within 0.001.</para>
    ///
    /// <para><b>THIS NUMBER IS TIED TO THE ICON RADIUS.</b> Area ratio divides by the radius SQUARED,
    /// so any change in the radius rescales it. It was first measured at the old 14.5px assumption
    /// and shipped as 0.72; when the calibrator corrected the radius to 18.2px, every ratio fell by
    /// about a third and 0.72 began rejecting real icons — detection collapsed and one champion
    /// stopped being seen entirely. Re-derive this whenever the radius definition changes.</para>
    ///
    /// <para>Re-measured at the calibrated radius over 552 ally-corner and 405 enemy blobs: ally
    /// blobs sit at a median 0.04 (p90 0.12) and enemy blobs at a median 0.67 (p10 0.31), a far wider
    /// gap than before. 0.30 rejects 100% of ally blobs while keeping 97% of enemy ones.</para></summary>
    private const double MinIconAreaRatio = 0.30;


    /// <summary>Upper bound on how many icons one merged red component is allowed to represent.
    /// Five is the whole enemy team — beyond that the blob is some other red shape, not a pile of
    /// champions, and inventing more sub-candidates would only manufacture false positives.</summary>
    private const int MaxOverlapSplit = 5;

    /// <summary>Splits a merged red component into <paramref name="k"/> sub-candidates by Lloyd's
    /// algorithm (k-means) over the member pixel coordinates.
    ///
    /// <para>Seeds are spread along the blob's longer axis rather than chosen at random, so the
    /// result is DETERMINISTIC — this runs every frame and a detector that returns different
    /// answers for identical pixels would make the presence state machine flicker, which is the
    /// very symptom being fixed. Sub-candidates whose own pixel count is nowhere near an icon are
    /// discarded: a split that produces a sliver means the blob was not really two icons.</para></summary>
    private static List<Candidate> SplitBlob(
        List<(int X, int Y)> blob, int k, DetectionOptions options)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var (px, py) in blob)
        {
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
        }

        bool horizontal = (maxX - minX) >= (maxY - minY);
        var cx = new double[k];
        var cy = new double[k];
        for (int i = 0; i < k; i++)
        {
            double t = (i + 0.5) / k;
            cx[i] = horizontal ? minX + t * (maxX - minX) : (minX + maxX) / 2.0;
            cy[i] = horizontal ? (minY + maxY) / 2.0 : minY + t * (maxY - minY);
        }

        var assign = new int[blob.Count];
        for (int iter = 0; iter < 8; iter++)
        {
            for (int p = 0; p < blob.Count; p++)
            {
                double best = double.MaxValue;
                int bestI = 0;
                for (int i = 0; i < k; i++)
                {
                    double dx = blob[p].X - cx[i], dy = blob[p].Y - cy[i];
                    double d = dx * dx + dy * dy;
                    if (d < best) { best = d; bestI = i; }
                }
                assign[p] = bestI;
            }

            var sx = new double[k];
            var sy = new double[k];
            var n = new int[k];
            for (int p = 0; p < blob.Count; p++)
            {
                sx[assign[p]] += blob[p].X;
                sy[assign[p]] += blob[p].Y;
                n[assign[p]]++;
            }
            for (int i = 0; i < k; i++)
                if (n[i] > 0) { cx[i] = sx[i] / n[i]; cy[i] = sy[i] / n[i]; }
        }

        var counts = new int[k];
        foreach (var a in assign) counts[a]++;

        double expectedArea = Math.PI * options.ExpectedIconRadiusPx * options.ExpectedIconRadiusPx;
        var result = new List<Candidate>(k);
        for (int i = 0; i < k; i++)
        {
            if (counts[i] < expectedArea * 0.55) continue;  // sliver — not a real icon
            result.Add(new Candidate(cx[i], cy[i], Math.Sqrt(counts[i] / Math.PI)));
        }
        return result;
    }

    /// <summary>Stage 2: crop+downscale the candidate region, compare against every template,
    /// return the best match if it clears the confidence threshold (else a null championId).</summary>
    private static (string? ChampionId, double Confidence) MatchBestTemplate(
        MinimapFrame frame, Candidate candidate, DetectionOptions options, IReadOnlyList<EnemyTemplate> templates)
    {
        // Region side = 2x the expected icon radius (the icon's approximate diameter), clamped
        // so a tiny test-fixture ROI never asks for a crop larger than the frame itself.
        int regionSize = Math.Max(1, (int)Math.Round(options.ExpectedIconRadiusPx * 2));
        regionSize = Math.Min(regionSize, Math.Min(frame.Width, frame.Height));
        int srcX = Clamp((int)Math.Round(candidate.CentroidX - regionSize / 2.0), 0, frame.Width - regionSize);
        int srcY = Clamp((int)Math.Round(candidate.CentroidY - regionSize / 2.0), 0, frame.Height - regionSize);

        byte[] downscaled = MinimapPixelSampling.NearestNeighborDownscale(
            frame.Bgra, frame.Stride, srcX, srcY, regionSize, EnemyTemplate.Size);

        string? bestId = null;
        bool bestIsEnemy = false;
        double bestSimilarity = -1, secondSimilarity = -1;

        foreach (var template in templates)
        {
            // (2026-07-20) ALLY templates take no part in matching. Stage 1 keeps red-dominant
            // pixels, and an ally's ring is BLUE, so only the reddish part of their portrait forms a
            // blob — measured over 1452 ally-corner blobs in real captures, NONE reached the icon-size
            // floor (max 0.294 against a floor of 0.30). Anything arriving here is therefore an enemy,
            // and an ally template can only do harm: it cannot be the right answer, but it can tie a
            // real enemy and get them rejected on margin. That was happening constantly — one enemy
            // won 142 candidates outright and was emitted zero times, every rejection naming an ally
            // as runner-up. Skipping them also halves the comparisons.
            if (!template.IsEnemy) continue;

            double similarity = ColorSimilarity(downscaled, template);
            if (similarity > bestSimilarity)
            {
                secondSimilarity = bestSimilarity;
                bestSimilarity = similarity;
                bestId = template.ChampionId;
                bestIsEnemy = template.IsEnemy;
            }
            else if (similarity > secondSimilarity)
            {
                secondSimilarity = similarity;
            }
        }

        if (bestSimilarity < options.ConfidenceThreshold)
            return (null, bestSimilarity);

        // Ambiguous: the runner-up is too close to call. Emitting the argmax here is what produced
        // confident-sounding wrong identities in the live pass, so say nothing instead.
        //
        if (secondSimilarity >= 0 && bestSimilarity - secondSimilarity < options.MinMatchMargin)
            return (null, bestSimilarity);

        return (bestId, bestSimilarity);
    }

    /// <summary>Scores one template against another with the SAME metric the matcher uses, so the
    /// number means what a match score means. Symmetric in practice: every template shares
    /// <see cref="EnemyTemplate.Size"/> and <see cref="EnemyTemplate.MatchRadiusFraction"/>, so the
    /// two circle masks are identical.</summary>
    public static double TemplateSimilarity(EnemyTemplate a, EnemyTemplate b)
        => ColorSimilarity(a.Bgra, b);

    /// <summary>The largest score gap any candidate crop can EVER produce between these two
    /// templates.
    ///
    /// <para>This is a bound, not an estimate. <see cref="ColorSimilarity"/> is
    /// <c>1 - avgDist/MaxPerPixelRgbDistance</c> where avgDist is a mean of per-pixel Euclidean
    /// distances over a fixed mask — an average of metrics is a metric, so the triangle inequality
    /// holds: <c>|avgDist(c,A) - avgDist(c,B)| &lt;= avgDist(A,B)</c> for every crop c. Dividing
    /// through gives <c>|sim(c,A) - sim(c,B)| &lt;= 1 - sim(A,B)</c>, which is this value.</para>
    ///
    /// <para>In principle a value below <see cref="DetectionOptions.MinMatchMargin"/> would mean the
    /// pair can never satisfy the margin rule at all. MEASURED, that never happens: across all 1891
    /// pairs of the 62 cached minimap icons the tightest bound is 0.157, more than five times the
    /// 0.03 margin (p1 0.194, p5 0.220, median 0.309, max 0.515 — Anivia vs Malphite). So this is a
    /// LOOSE bound, and treating it as a proof of inseparability would be false comfort. It was
    /// written as such first; the measurement is what corrected it.</para>
    ///
    /// <para>What it is good for is RANKING. Within one roster it says which two champions sit
    /// closest together, and against the distribution above it says how close that is by the
    /// standards of the whole champion pool. Neither direction is a promise: a tight pair need not
    /// be misread, and a loose one can still be missed, because a real crop resembles neither
    /// template perfectly.</para></summary>
    public static double MaxAchievableMargin(EnemyTemplate a, EnemyTemplate b)
        => 1.0 - TemplateSimilarity(a, b);

    private const double MaxPerPixelRgbDistance = 441.6729559300637; // sqrt(3 * 255^2)

    /// <summary>Mean per-pixel RGB Euclidean distance between a downscaled candidate crop and a
    /// template, restricted to the template's circular mask, normalized to a 0..1 similarity
    /// (1 = identical, 0 = maximally different). See <see cref="MinimapDetector"/> class doc.</summary>
    private static double ColorSimilarity(byte[] candidateBgra, EnemyTemplate template)
    {
        double sumDist = 0;
        int count = 0;

        for (int i = 0; i < template.CircleMask.Length; i++)
        {
            if (!template.CircleMask[i]) continue;

            int off = i * 4;
            double db = candidateBgra[off] - template.Bgra[off];
            double dg = candidateBgra[off + 1] - template.Bgra[off + 1];
            double dr = candidateBgra[off + 2] - template.Bgra[off + 2];
            sumDist += Math.Sqrt(dr * dr + dg * dg + db * db);
            count++;
        }

        if (count == 0) return 0; // degenerate mask (targetSize too small) — no signal, no match
        double avgDist = sumDist / count;
        return Math.Clamp(1.0 - avgDist / MaxPerPixelRgbDistance, 0.0, 1.0);
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
