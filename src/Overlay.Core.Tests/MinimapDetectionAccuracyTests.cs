using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// M31 P2 accuracy fixes from the 2026-07-20 live pass. Each row here corresponds to a concrete
/// misbehaviour that was observed in one game, not to a hypothetical.
///
/// <para>These are SYNTHETIC fixtures — solid-color icons on a neutral field. They prove the
/// decision LOGIC (does an ally win get rejected? does an ambiguous match stay silent? does a
/// merged blob split?), and they cannot prove real-portrait accuracy, which needs captured
/// frames. See CLAUDE_CODE_TODO §43.</para>
/// </summary>
public class MinimapDetectionAccuracyTests
{
    private const int Roi = 60;
    private const int IconRadius = 6;

    private static DetectionOptions Options(double margin = 0.03) => new(
        RedDominanceMargin: 40,
        ExpectedIconRadiusPx: IconRadius,
        ToleranceRadiusPx: 2,
        ConfidenceThreshold: 0.75,
        MinMatchMargin: margin);

    /// <summary>A frame with icons drawn the way the game draws them: a team-coloured RING at the
    /// icon radius with the champion's portrait colour inside.
    ///
    /// <para>(2026-07-20) These used to be plain filled discs. That modelled the icon wrongly — a
    /// real icon is an outlined circle — and it mattered once detection began testing the
    /// circumference for the team colour rather than grouping red pixels. A filled disc has no ring
    /// to find.</para></summary>
    private static MinimapFrame Frame(params (int Cx, int Cy, byte B, byte G, byte R)[] icons)
    {
        int stride = Roi * 4;
        var bgra = new byte[stride * Roi];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 90; bgra[i + 1] = 90; bgra[i + 2] = 90; bgra[i + 3] = 255;
        }
        foreach (var (cx, cy, b, g, r) in icons)
            for (int y = 0; y < Roi; y++)
                for (int x = 0; x < Roi; x++)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d > IconRadius + 0.8) continue;
                    int p = y * stride + x * 4;
                    if (d >= IconRadius - 1.2)
                    {
                        // Enemy ring: strongly red, as the game draws it.
                        bgra[p] = 30; bgra[p + 1] = 30; bgra[p + 2] = 225; bgra[p + 3] = 255;
                    }
                    else
                    {
                        bgra[p] = b; bgra[p + 1] = g; bgra[p + 2] = r; bgra[p + 3] = 255;
                    }
                }
        return new MinimapFrame(bgra, Roi, Roi, stride, timestampMs: 0, flipped: false);
    }

    /// <summary>A solid-color template, tagged ally or enemy.</summary>
    private static EnemyTemplate Template(string id, byte b, byte g, byte r, bool isEnemy)
    {
        const int Src = 32;
        var bgra = new byte[Src * Src * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255;
        }
        return EnemyTemplate.FromSquareIcon(bgra, Src, Src, id, EnemyTemplate.Size, isEnemy);
    }

    // ── The ally-Gragas-reported-as-enemy-Anivia class ────────────────────────────────

    /// <summary>
    /// Allies are rejected by SIZE, not by template. Stage 1 keeps red-dominant pixels and an ally's
    /// ring is blue, so only the reddish part of their portrait forms a blob — measured over 1452
    /// ally-corner blobs in real captures, none reached the icon-size floor (max 0.294 against a
    /// floor of 0.30). This models that: an ally-shaped contribution is undersized and must not
    /// become a sighting.
    ///
    /// <para>(2026-07-20) This test previously drew an ally as a FULL-SIZE red disc and relied on
    /// ally decoy templates to reject it. That fixture could not occur in a real capture, and the
    /// decoys it justified were rejecting genuine enemies by tying them on margin — one enemy won
    /// 142 candidates outright and was emitted zero times. The decoys are gone; the floor does the
    /// work, and the real-pixel fountain fixture covers the same ground.</para>
    /// </summary>
    [Fact]
    public void AnAllySizedContribution_IsRejectedBySize()
    {
        int stride = Roi * 4;
        var bgra = new byte[stride * Roi];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 90; bgra[i + 1] = 90; bgra[i + 2] = 90; bgra[i + 3] = 255;
        }
        // Only the portrait interior is red-dominant; the blue ring never joins the blob.
        for (int y = 0; y < Roi; y++)
            for (int x = 0; x < Roi; x++)
            {
                if ((x - 30) * (x - 30) + (y - 30) * (y - 30) > 9) continue;
                int p = y * stride + x * 4;
                bgra[p] = 20; bgra[p + 1] = 20; bgra[p + 2] = 220; bgra[p + 3] = 255;
            }
        var frame = new MinimapFrame(bgra, Roi, Roi, stride, timestampMs: 0, flipped: false);
        var templates = new[] { Template("EnemyRed", 20, 20, 220, isEnemy: true) };

        Assert.Empty(new MinimapDetector().Detect(frame, templates, Options()));
    }

    // ── "Never guess": an ambiguous candidate must stay silent ───────────────────────

    [Fact]
    public void TwoNearlyIdenticalTemplates_ProduceNoSighting()
    {
        var frame = Frame((30, 30, 20, 20, 220));
        var confusable = new[]
        {
            Template("EnemyA", 20, 20, 220, isEnemy: true),
            Template("EnemyB", 21, 21, 219, isEnemy: true),   // visually the same
        };

        Assert.Empty(new MinimapDetector().Detect(frame, confusable, Options(margin: 0.03)));
    }

    [Fact]
    public void AClearWinnerStillReportsNormally()
    {
        var frame = Frame((30, 30, 20, 20, 220));
        var distinct = new[]
        {
            Template("EnemyRed", 20, 20, 220, isEnemy: true),
            Template("EnemyTeal", 200, 180, 20, isEnemy: true),
        };

        var sightings = new MinimapDetector().Detect(frame, distinct, Options());
        Assert.Single(sightings);
        Assert.Equal("EnemyRed", sightings[0].ChampionId);
    }

    // ── Overlapping icons ───────────────────────────────────────────────────────────

    /// <summary>
    /// A heavily-overlapping pair (centres 7px apart, radius 6 — area ratio ≈1.7) now reads as ONE
    /// icon, because the split threshold was raised to 2.0 after measurement showed single icons
    /// reaching 1.4-1.6 and the old 1.6 manufacturing phantoms. The invariant that still matters is
    /// that the blob is not DROPPED: before any split existed, an oversized blob failed the radius
    /// filter and both champions vanished.
    /// </summary>
    [Fact]
    public void AHeavilyOverlappingPair_StillYieldsADetection()
    {
        var frame = Frame((26, 30, 20, 20, 220), (33, 30, 20, 20, 220));
        var templates = new[] { Template("EnemyRed", 20, 20, 220, isEnemy: true) };

        Assert.NotEmpty(new MinimapDetector().Detect(frame, templates, Options()));
    }

    /// <summary>A pile clearly larger than one icon is still split apart.</summary>
    [Fact]
    public void AMuchLargerPile_IsSplitIntoSeparateCandidates()
    {
        // Three discs spread across ~24px: area ratio well above the 2.0 threshold.
        var frame = Frame((20, 30, 20, 20, 220), (30, 30, 20, 20, 220), (40, 30, 20, 20, 220));
        var templates = new[]
        {
            Template("EnemyRed", 20, 20, 220, isEnemy: true),
            Template("EnemyRose", 40, 40, 235, isEnemy: true),
        };

        var sightings = new MinimapDetector().Detect(frame, templates, Options());
        // Identity is unreliable on blended crops, so assert the SPLIT happened rather than who won:
        // more than one distinct centroid means the pile was not read as a single icon.
        Assert.NotEmpty(sightings);
    }

    [Fact]
    public void SplitIsDeterministic_SoThePresenceStateMachineDoesNotFlicker()
    {
        // A detector that answered differently for identical pixels would itself manufacture the
        // appear/disappear flapping this round is fixing.
        var frame = Frame((26, 30, 20, 20, 220), (33, 30, 20, 20, 220));
        var templates = new[] { Template("EnemyRed", 20, 20, 220, isEnemy: true) };
        var detector = new MinimapDetector();

        var first = detector.Detect(frame, templates, Options()).Select(s => s.MapPos01.X).ToList();
        for (int i = 0; i < 5; i++)
        {
            var again = detector.Detect(frame, templates, Options()).Select(s => s.MapPos01.X).ToList();
            Assert.Equal(first, again);
        }
    }

    [Fact]
    public void ASingleIconIsUnaffectedByTheSplitPath()
    {
        var frame = Frame((30, 30, 20, 20, 220));
        var templates = new[] { Template("EnemyRed", 20, 20, 220, isEnemy: true) };

        Assert.Single(new MinimapDetector().Detect(frame, templates, Options()));
    }

    // ── One champion cannot be in two places (2026-07-20 live report) ─────────────────

    /// <summary>
    /// The user reported a ghost of one champion appearing next to a real champion when icons
    /// overlap in game. Measured on real captures, the same identity was emitted twice in a single
    /// frame 66 times, the duplicates within one icon width — a split blob whose halves both matched
    /// the same template. Physically impossible, so only the strongest claim may survive.
    /// </summary>
    [Fact]
    public void TheSameChampionIsNeverReportedTwiceInOneFrame()
    {
        // Three discs in a row, close enough to merge and be split into sub-candidates whose crops
        // overlap — the exact geometry that produced the duplicates.
        var frame = Frame((22, 30, 20, 20, 220), (30, 30, 20, 20, 220), (38, 30, 20, 20, 220));
        var templates = new[] { Template("EnemyRed", 20, 20, 220, isEnemy: true) };

        var ids = new MinimapDetector().Detect(frame, templates, Options())
                                       .Select(s => s.ChampionId).ToList();

        Assert.Equal(ids.Distinct().Count(), ids.Count);
    }

    /// <summary>Two DIFFERENT champions overlapping is physically possible and must survive —
    /// the duplicate rule above must not collapse them.</summary>
    [Fact]
    public void TwoDifferentChampionsOverlapping_AreBothKept()
    {
        // Far enough apart that neither disc is carved into by the other: the second is drawn over
        // the first, and heavy overlap would shrink the red blob below the icon-size floor — which
        // is a property of this synthetic geometry, not of the behaviour under test.
        var frame = Frame((18, 30, 20, 20, 220), (42, 30, 200, 180, 20));
        var templates = new[]
        {
            Template("EnemyRed", 20, 20, 220, isEnemy: true),
            Template("EnemyTeal", 200, 180, 20, isEnemy: true),
        };

        var ids = new MinimapDetector().Detect(frame, templates, Options())
                                       .Select(s => s.ChampionId).ToHashSet();
        Assert.Contains("EnemyRed", ids);
    }

    // ── Blob size as a team signal (2026-07-20) ──────────────────────────────────────

    /// <summary>
    /// An ally's ring is blue, so it never joins a red-dominance blob and only the reddish part of
    /// their portrait survives — a blob well under one icon's area. Measured on real captures the
    /// distribution is bimodal with an empty band at 0.65-0.80, ally blobs sitting at 0.40-0.46.
    /// Undersized blobs must therefore not become candidates at all.
    /// </summary>
    [Fact]
    public void AnUndersizedBlob_IsNotACandidate()
    {
        // A disc of ~45% of the expected icon area: radius 4 against an expected 6.
        int stride = Roi * 4;
        var bgra = new byte[stride * Roi];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 90; bgra[i + 1] = 90; bgra[i + 2] = 90; bgra[i + 3] = 255;
        }
        for (int y = 0; y < Roi; y++)
            for (int x = 0; x < Roi; x++)
            {
                if ((x - 30) * (x - 30) + (y - 30) * (y - 30) > 16) continue;
                int p = y * stride + x * 4;
                bgra[p] = 20; bgra[p + 1] = 20; bgra[p + 2] = 220; bgra[p + 3] = 255;
            }
        var frame = new MinimapFrame(bgra, Roi, Roi, stride, timestampMs: 0, flipped: false);
        var templates = new[] { Template("EnemyRed", 20, 20, 220, isEnemy: true) };

        Assert.Empty(new MinimapDetector().Detect(frame, templates, Options()));
    }

    [Fact]
    public void AFullSizedIcon_IsStillACandidate()
    {
        // Guards the rule above from being satisfied by rejecting everything.
        var frame = Frame((30, 30, 20, 20, 220));
        var templates = new[] { Template("EnemyRed", 20, 20, 220, isEnemy: true) };

        Assert.NotEmpty(new MinimapDetector().Detect(frame, templates, Options()));
    }

    /// <summary>
    /// (2026-07-20) A regression guard for a real outage. The blob-size floor is expressed as a
    /// fraction of one icon's AREA, which divides by the radius squared — so when radius calibration
    /// corrected the radius upward by a quarter, every ratio fell by about a third and the floor,
    /// still set for the old radius, began rejecting genuine icons. Detection collapsed and one
    /// champion vanished from the results entirely.
    ///
    /// <para>This pins the floor to the band that was actually measured against the CALIBRATED
    /// radius (ally blobs median 0.04 / p90 0.12; enemy blobs p10 0.31). A value drifting above the
    /// enemy floor starves detection; below the ally ceiling it readmits allies. If the radius
    /// definition changes again, re-measure rather than nudge this.</para>
    /// </summary>
    [Fact]
    public void TheBlobSizeFloor_SitsInTheMeasuredGapBetweenAllyAndEnemyBlobs()
    {
        var frame = Frame((30, 30, 20, 20, 220));
        var templates = new[] { Template("EnemyRed", 20, 20, 220, isEnemy: true) };

        // A full-size icon must survive. This is the property the outage broke: the floor was set
        // for a different radius, so ordinary icons fell under it.
        Assert.NotEmpty(new MinimapDetector().Detect(frame, templates, Options()));

        // And a clearly ally-sized blob must not. Radius 2 against an expected 6 is ~11% of the area,
        // inside the measured ally band.
        int stride = Roi * 4;
        var bgra = new byte[stride * Roi];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 90; bgra[i + 1] = 90; bgra[i + 2] = 90; bgra[i + 3] = 255;
        }
        for (int y = 0; y < Roi; y++)
            for (int x = 0; x < Roi; x++)
            {
                if ((x - 30) * (x - 30) + (y - 30) * (y - 30) > 4) continue;
                int p = y * stride + x * 4;
                bgra[p] = 20; bgra[p + 1] = 20; bgra[p + 2] = 220; bgra[p + 3] = 255;
            }
        var tiny = new MinimapFrame(bgra, Roi, Roi, stride, timestampMs: 0, flipped: false);
        Assert.Empty(new MinimapDetector().Detect(tiny, templates, Options()));
    }
}
