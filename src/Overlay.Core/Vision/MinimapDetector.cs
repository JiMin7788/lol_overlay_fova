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
/// <param name="Gates">lolF04: the size band and fixed-structure rule (<see cref="MinimapCandidateGate"/>),
/// applied to every matched candidate before <c>KeepBestPerChampion</c>. Null (the default) = no gate,
/// the detector behaves exactly as before.</param>
/// <param name="PairRejection">lolF04 (TODO:411): risky-pair mutual rejection (<see cref="PairRejectionSettings"/>),
/// applied in stage 2 after the margin rule. Null (the default) = off, the detector behaves exactly as before.</param>
/// <param name="Assignment">lolF04 (TODO:425): how champions are given to the frame's candidates
/// (<see cref="AssignmentMode"/>). PerCandidate (the default) = the detector behaves exactly as before; Global =
/// <see cref="MinimapGlobalAssignment"/>, then the margin rule, pair rejection and the gates.</param>
/// <param name="Luminance">lolF04 (TODO:346): the crop-vs-template luminance excess rule (<see cref="LuminanceSettings"/>),
/// applied in stage 2 after the identity rules and before the gates. Null (the default) = off, the detector behaves
/// exactly as before.</param>
/// <param name="StrictRing">lolF04 (TODO:442): the strict-ring stage-1 rule (<see cref="StrictRingSettings"/>), passed to
/// <see cref="MinimapRingFinder.Find"/>. Null (the default) = off, the detector behaves exactly as before.</param>
/// <param name="Subpixel">lolF04 (R19, TODO:595/590): the sub-pixel stage-2 crop (<see cref="SubpixelSettings"/>):
/// fractional origin + bilinear downscale, optionally the best score per template over crop offsets. Null (the
/// default) = off, the detector behaves exactly as before.</param>
public readonly record struct DetectionOptions(
    int RedDominanceMargin,
    double ExpectedIconRadiusPx,
    double ToleranceRadiusPx,
    double ConfidenceThreshold,
    double MinMatchMargin = 0.03,   // = DefaultMinMatchMargin; a primary-ctor default cannot reference it
    CandidateGateSettings? Gates = null,
    PairRejectionSettings? PairRejection = null,
    AssignmentMode Assignment = AssignmentMode.PerCandidate,
    LuminanceSettings? Luminance = null,
    StrictRingSettings? StrictRing = null,
    SubpixelSettings? Subpixel = null)
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
/// <para><b>Stage 1 — ring finder (every frame):</b> <see cref="MinimapRingFinder.Find"/> locates
/// icons by their team-coloured ring around <c>ExpectedIconRadiusPx</c>, with
/// <c>RedDominanceMargin</c> as the red test. Ally (blue-ringed) icons are dropped; each enemy icon
/// becomes a candidate centred on its ring. No enemy icon is the common case and returns
/// immediately — no stage 2 work at all.</para>
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
        var icons = MinimapRingFinder.Find(frame, options.ExpectedIconRadiusPx, options.RedDominanceMargin, options.StrictRing);
        if (icons.Count == 0)
            return Array.Empty<MinimapSighting>();

        if (options.Assignment == AssignmentMode.Global)
            return DetectGlobal(frame, icons, templates, options);

        var sightings = new List<MinimapSighting>(icons.Count);
        foreach (var icon in icons)
        {
            if (!icon.IsEnemy) continue;   // an ally's ring is blue — nothing to identify
            var candidate = new Candidate(icon.CentreX, icon.CentreY, options.ExpectedIconRadiusPx);
            var (championId, confidence, margin) = MatchBestTemplate(frame, candidate, options, templates);
            if (championId is null)
                continue; // below confidence threshold on every template — drop, no guessing

            // AI-NOTE: gated BEFORE KeepBestPerChampion, so a champion whose strongest claim is a
            // rejected look-alike keeps its next valid claim instead of losing the frame.
            if (options.Gates is { } gates
                && MinimapCandidateGate.Evaluate(gates, icon.RadiusPx, frame.Width,
                       candidate.CentroidX / frame.Width, candidate.CentroidY / frame.Height, frame.Flipped,
                       confidence, margin, MinimapStructurePositions.All) != GateResult.Pass)
                continue;

            var mapPos = new MapPosition01(candidate.CentroidX / frame.Width, candidate.CentroidY / frame.Height);
            sightings.Add(new MinimapSighting(championId, mapPos, confidence, frame.TimestampMs));
        }

        return KeepBestPerChampion(sightings);
    }

    /// <summary>lolF04 (R16, TODO:425) stage 2 with <see cref="AssignmentMode.Global"/>: every enemy-ring candidate
    /// is scored against every enemy template, <see cref="MinimapGlobalAssignment.Assign"/> gives each champion to
    /// at most one candidate, then the margin rule (over champions no other candidate holds), pair rejection, the
    /// luminance rule and the gates apply to each assigned candidate.</summary>
    private static IReadOnlyList<MinimapSighting> DetectGlobal(
        MinimapFrame frame, IReadOnlyList<MinimapRingFinder.Icon> icons, IReadOnlyList<EnemyTemplate> templates,
        DetectionOptions options)
    {
        // Ally templates take no part in matching, as in MatchBestTemplate.
        var enemies = templates.Where(t => t.IsEnemy).ToList();
        if (enemies.Count == 0)
            return Array.Empty<MinimapSighting>();

        var candidates = new List<(MinimapRingFinder.Icon Icon, Candidate Candidate, byte[][] Crops)>();
        var scores = new List<double[]>();
        foreach (var icon in icons)
        {
            if (!icon.IsEnemy) continue;
            var candidate = new Candidate(icon.CentreX, icon.CentreY, options.ExpectedIconRadiusPx);
            var scored = ScoreCandidate(frame, icon.CentreX, icon.CentreY, options, enemies);
            candidates.Add((icon, candidate, scored.Select(s => s.Crop).ToArray()));
            scores.Add(scored.Select(s => s.Score).ToArray());
        }

        var (champion, margin, runnerUp) = MinimapGlobalAssignment.Assign(scores, options.ConfidenceThreshold);
        var sightings = new List<MinimapSighting>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (champion[i] < 0) continue;   // nothing left for it at or above the threshold
            if (!double.IsNaN(margin[i]) && margin[i] < options.MinMatchMargin) continue;
            if (options.PairRejection is { } pairRejection && runnerUp[i] >= 0
                && pairRejection.Rejects(enemies[champion[i]], enemies[runnerUp[i]], margin[i]))
                continue;

            var (icon, candidate, crops) = candidates[i];
            if (options.Luminance is { } luminance
                && luminance.Rejects(LuminanceSettings.Excess(crops[champion[i]], enemies[champion[i]])))
                continue;
            double confidence = scores[i][champion[i]];
            if (options.Gates is { } gates
                && MinimapCandidateGate.Evaluate(gates, icon.RadiusPx, frame.Width,
                       candidate.CentroidX / frame.Width, candidate.CentroidY / frame.Height, frame.Flipped,
                       confidence, margin[i], MinimapStructurePositions.All) != GateResult.Pass)
                continue;

            var mapPos = new MapPosition01(candidate.CentroidX / frame.Width, candidate.CentroidY / frame.Height);
            sightings.Add(new MinimapSighting(enemies[champion[i]].ChampionId, mapPos, confidence, frame.TimestampMs));
        }

        // AI-NOTE: no KeepBestPerChampion — the assignment is one-to-one, so no champion is claimed twice.
        return sightings;
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

    /// <summary>Stage 2: crop+downscale the candidate region, compare against every template,
    /// return the best match if it clears the confidence threshold (else a null championId), with
    /// its margin over the runner-up (NaN when there is no runner-up).</summary>
    private static (string? ChampionId, double Confidence, double Margin) MatchBestTemplate(
        MinimapFrame frame, Candidate candidate, DetectionOptions options, IReadOnlyList<EnemyTemplate> templates)
    {
        byte[][] crops = CandidateCrops(frame, candidate, options);

        string? bestId = null;
        bool bestIsEnemy = false;
        double bestSimilarity = -1, secondSimilarity = -1;
        EnemyTemplate? bestTemplate = null, secondTemplate = null;
        byte[]? bestCrop = null;

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

            // lolF04 (R19): with the offset search each template keeps its own best crop, so the margin below is
            // between per-template bests; with one crop this is ColorSimilarity on it.
            var (similarity, crop) = BestOver(crops, template);
            if (similarity > bestSimilarity)
            {
                secondSimilarity = bestSimilarity;
                secondTemplate = bestTemplate;
                bestSimilarity = similarity;
                bestId = template.ChampionId;
                bestIsEnemy = template.IsEnemy;
                bestTemplate = template;
                bestCrop = crop;
            }
            else if (similarity > secondSimilarity)
            {
                secondSimilarity = similarity;
                secondTemplate = template;
            }
        }

        double margin = secondSimilarity >= 0 ? bestSimilarity - secondSimilarity : double.NaN;
        if (bestSimilarity < options.ConfidenceThreshold)
            return (null, bestSimilarity, margin);

        // Ambiguous: the runner-up is too close to call. Emitting the argmax here is what produced
        // confident-sounding wrong identities in the live pass, so say nothing instead.
        //
        if (secondSimilarity >= 0 && bestSimilarity - secondSimilarity < options.MinMatchMargin)
            return (null, bestSimilarity, margin);

        // lolF04 (TODO:411): a risky pair (templates close by the MaxAchievableMargin bound) needs a
        // wider margin than the rule above; computed from this call's templates only, so Detect stays pure.
        if (options.PairRejection is { } pairRejection && secondTemplate is not null
            && pairRejection.Rejects(bestTemplate!, secondTemplate, margin))
            return (null, bestSimilarity, margin);

        // lolF04 (TODO:346): a crop much brighter than the template it matched sits on a bright background.
        if (options.Luminance is { } luminance && luminance.Rejects(LuminanceSettings.Excess(bestCrop!, bestTemplate!)))
            return (null, bestSimilarity, margin);

        return (bestId, bestSimilarity, margin);
    }

    /// <summary>lolF04 (R19): every enemy template's stage-2 score at a candidate centre and the crop it was scored on
    /// (its best crop under the offset search), in <paramref name="enemies"/> order. Internal so the replay scores
    /// through the same code.</summary>
    internal static (double Score, byte[] Crop)[] ScoreCandidate(
        MinimapFrame frame, double centreX, double centreY, DetectionOptions options, IReadOnlyList<EnemyTemplate> enemies)
    {
        byte[][] crops = CandidateCrops(frame, new Candidate(centreX, centreY, options.ExpectedIconRadiusPx), options);
        var scored = new (double Score, byte[] Crop)[enemies.Count];
        for (int i = 0; i < enemies.Count; i++) scored[i] = BestOver(crops, enemies[i]);
        return scored;
    }

    /// <summary>The highest <see cref="ColorSimilarity"/> of <paramref name="template"/> over <paramref name="crops"/>,
    /// with that crop; the first one on a tie.</summary>
    private static (double Score, byte[] Crop) BestOver(byte[][] crops, EnemyTemplate template)
    {
        double best = ColorSimilarity(crops[0], template);
        byte[] bestCrop = crops[0];
        for (int i = 1; i < crops.Length; i++)
        {
            double s = ColorSimilarity(crops[i], template);
            if (s > best) { best = s; bestCrop = crops[i]; }
        }
        return (best, bestCrop);
    }

    /// <summary>The crops stage 2 scores a candidate on: today's one crop, or with <see cref="SubpixelSettings.Bilinear"/>
    /// the bilinear crop at the fractional origin, shifted by every (dx, dy) of <see cref="SubpixelSettings.Offsets"/>
    /// (dy outer, dx inner, both ascending).</summary>
    private static byte[][] CandidateCrops(MinimapFrame frame, Candidate candidate, DetectionOptions options)
    {
        if (options.Subpixel is not { Bilinear: true } subpixel)
            return new[] { CropCandidate(frame, candidate, options) };

        // Same region side and origin formula as CropCandidate, minus the Math.Round of the origin.
        // AI-NOTE: the origin keeps today's centre - side/2 (not + 0.5), so the crop sits where today's crop sits on
        // average and only the rounding is removed — measure 1b (TODO:595) isolates exactly that contribution.
        int regionSize = Math.Max(1, (int)Math.Round(options.ExpectedIconRadiusPx * 2));
        regionSize = Math.Min(regionSize, Math.Min(frame.Width, frame.Height));
        double[] offsets = subpixel.Offsets();
        var crops = new byte[offsets.Length * offsets.Length][];
        int k = 0;
        foreach (double dy in offsets)
            foreach (double dx in offsets)
            {
                double srcX = Math.Clamp(candidate.CentroidX + dx - regionSize / 2.0, 0, frame.Width - regionSize);
                double srcY = Math.Clamp(candidate.CentroidY + dy - regionSize / 2.0, 0, frame.Height - regionSize);
                crops[k++] = MinimapPixelSampling.BilinearDownscale(
                    frame.Bgra, frame.Stride, frame.Width, frame.Height, srcX, srcY, regionSize, EnemyTemplate.Size);
            }
        return crops;
    }

    /// <summary>The candidate's source region downscaled to <see cref="EnemyTemplate.Size"/>.</summary>
    private static byte[] CropCandidate(MinimapFrame frame, Candidate candidate, DetectionOptions options)
    {
        // Region side = 2x the expected icon radius (the icon's approximate diameter), clamped
        // so a tiny test-fixture ROI never asks for a crop larger than the frame itself.
        int regionSize = Math.Max(1, (int)Math.Round(options.ExpectedIconRadiusPx * 2));
        regionSize = Math.Min(regionSize, Math.Min(frame.Width, frame.Height));
        int srcX = Clamp((int)Math.Round(candidate.CentroidX - regionSize / 2.0), 0, frame.Width - regionSize);
        int srcY = Clamp((int)Math.Round(candidate.CentroidY - regionSize / 2.0), 0, frame.Height - regionSize);

        return MinimapPixelSampling.NearestNeighborDownscale(
            frame.Bgra, frame.Stride, srcX, srcY, regionSize, EnemyTemplate.Size);
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
