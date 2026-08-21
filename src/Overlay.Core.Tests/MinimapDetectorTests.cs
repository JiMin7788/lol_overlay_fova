using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// M31 P2 (docs/modules/M31_MINIMAP_VISION.md §7 "P2 round"): synthetic-fixture tests for
/// <see cref="MinimapDetector"/> — "synthetic ROI fixtures (rendered icons at known
/// positions/sizes, 1-frame flash fixture, ping/ward negative fixtures) → precision/recall
/// asserts; template generation from DDragon squares unit-tested."
///
/// <para><b>Fixture design note:</b> a real minimap icon is a red RING around a champion-specific
/// portrait, where only the ring needs to be red — the portrait art does not. These fixtures
/// simplify that to a single SOLID-COLOR disc per icon, using a distinct red-dominant shade per
/// champion (still passes the stage-1 red-dominance test, since only the R-vs-G/B margin is
/// checked — see <see cref="DetectionOptions.RedDominanceMargin"/>). This is enough to exercise
/// both stages (red-ring prefilter + identity color match) without needing a two-tone
/// ring+portrait rasterizer; the corner-masking behavior specifically is covered by its own test
/// using a two-tone square icon (real DDragon-portrait shape), independent of the detector.</para>
/// </summary>
public class MinimapDetectorTests
{
    private static readonly DetectionOptions Options = DetectionOptions.Default; // radius 10±4, confidence .75, margin 40

    private static readonly (byte R, byte G, byte B) Background = (30, 30, 30); // not red-dominant
    private static readonly (byte R, byte G, byte B) ChampA = (220, 20, 20);    // deep red
    private static readonly (byte R, byte G, byte B) ChampB = (255, 140, 0);    // red-orange

    private static MinimapFrame MakeFrame(int width, int height, (byte R, byte G, byte B) background, long timestampMs = 0)
    {
        int stride = width * 4;
        var bgra = new byte[stride * height];
        for (int i = 0; i < width * height; i++)
        {
            int off = i * 4;
            bgra[off] = background.B;
            bgra[off + 1] = background.G;
            bgra[off + 2] = background.R;
            bgra[off + 3] = 255;
        }
        return new MinimapFrame(bgra, width, height, stride, timestampMs, flipped: false);
    }

    private static void DrawFilledDisc(MinimapFrame frame, int cx, int cy, int radius, (byte R, byte G, byte B) color)
    {
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > radius * radius) continue;
                int off = frame.PixelOffset(x, y);
                frame.Bgra[off] = color.B;
                frame.Bgra[off + 1] = color.G;
                frame.Bgra[off + 2] = color.R;
                frame.Bgra[off + 3] = 255;
            }
        }
    }

    private static EnemyTemplate MakeSolidTemplate(string championId, (byte R, byte G, byte B) color, int size = 16)
    {
        var bgra = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            int off = i * 4;
            bgra[off] = color.B;
            bgra[off + 1] = color.G;
            bgra[off + 2] = color.R;
            bgra[off + 3] = 255;
        }
        return EnemyTemplate.FromSquareIcon(bgra, size, size, championId);
    }

    // --- Zero-candidate fast path ---

    [Fact]
    public void Detect_AllBackgroundFrame_ReturnsEmpty()
    {
        var frame = MakeFrame(100, 100, Background);
        var templates = new[] { MakeSolidTemplate("ChampA", ChampA) };

        var result = new MinimapDetector().Detect(frame, templates, Options);

        Assert.Empty(result);
    }

    // --- Single known-position icon ---

    [Fact]
    public void Detect_OneRedRingedIcon_ReturnsOneSightingAtKnownPosition()
    {
        var frame = MakeFrame(100, 100, Background);
        DrawFilledDisc(frame, cx: 30, cy: 40, radius: 11, ChampA);
        var templates = new[] { MakeSolidTemplate("ChampA", ChampA) };

        var result = new MinimapDetector().Detect(frame, templates, Options);

        var sighting = Assert.Single(result);
        Assert.Equal("ChampA", sighting.ChampionId);
        Assert.True(sighting.Confidence >= Options.ConfidenceThreshold);
        Assert.Equal(30.0 / 100, sighting.MapPos01.X, precision: 1);
        Assert.Equal(40.0 / 100, sighting.MapPos01.Y, precision: 1);
        Assert.Equal(frame.TimestampMs, sighting.TimestampMs);
    }

    // --- 1-frame flash: Detect is stateless, no cross-contamination between calls ---

    [Fact]
    public void Detect_CalledTwiceWithDifferentFrames_DoesNotCarryStateBetweenCalls()
    {
        var detector = new MinimapDetector();
        var templates = new[] { MakeSolidTemplate("ChampA", ChampA) };

        var withIcon = MakeFrame(100, 100, Background);
        DrawFilledDisc(withIcon, cx: 30, cy: 40, radius: 11, ChampA);
        var emptyFrame = MakeFrame(100, 100, Background);

        var first = detector.Detect(withIcon, templates, Options);
        var second = detector.Detect(emptyFrame, templates, Options);
        var third = detector.Detect(withIcon, templates, Options);

        Assert.Single(first);
        Assert.Empty(second); // a prior sighting must not leak into a frame with nothing in it
        Assert.Single(third); // and the detector must still find the icon again afterward
        Assert.Equal(first[0].ChampionId, third[0].ChampionId);
    }

    // --- Ping/ward negative fixtures ---

    [Fact]
    public void Detect_TooSmallRedBlob_LikeAPingDot_FilteredAtStage1()
    {
        var frame = MakeFrame(100, 100, Background);
        DrawFilledDisc(frame, cx: 30, cy: 40, radius: 2, ChampA); // way below ExpectedIconRadiusPx-Tolerance
        var templates = new[] { MakeSolidTemplate("ChampA", ChampA) };

        var result = new MinimapDetector().Detect(frame, templates, Options);

        Assert.Empty(result);
    }

    [Fact]
    public void Detect_RightSizedBlob_ButUnknownColor_FilteredAtStage2Confidence()
    {
        var frame = MakeFrame(100, 100, Background);
        // Muted red-gray: still clears the RedDominanceMargin (passes stage 1) but is far in
        // color-space from the one known template (fails stage 2 confidence) — the "ward icon
        // shape is right but it isn't a known enemy" case.
        DrawFilledDisc(frame, cx: 30, cy: 40, radius: 11, (180, 140, 140));
        var templates = new[] { MakeSolidTemplate("ChampA", ChampA) };

        var result = new MinimapDetector().Detect(frame, templates, Options);

        Assert.Empty(result);
    }

    // --- Multi-candidate discrimination ---

    [Fact]
    public void Detect_TwoIconsTwoTemplates_EachMatchedToTheCorrectTemplate_NotSwapped()
    {
        var frame = MakeFrame(120, 120, Background);
        DrawFilledDisc(frame, cx: 25, cy: 25, radius: 11, ChampA);
        DrawFilledDisc(frame, cx: 90, cy: 90, radius: 11, ChampB);
        var templates = new[]
        {
            MakeSolidTemplate("ChampA", ChampA),
            MakeSolidTemplate("ChampB", ChampB),
        };

        var result = new MinimapDetector().Detect(frame, templates, Options);

        Assert.Equal(2, result.Count);
        var atA = Assert.Single(result, s => Math.Abs(s.MapPos01.X - 25.0 / 120) < 0.05);
        var atB = Assert.Single(result, s => Math.Abs(s.MapPos01.X - 90.0 / 120) < 0.05);
        Assert.Equal("ChampA", atA.ChampionId);
        Assert.Equal("ChampB", atB.ChampionId);
    }

    // --- EnemyTemplate.FromSquareIcon: circular mask + fixed output size ---

    [Fact]
    public void FromSquareIcon_OutputIsTheDocumentedFixedSize()
    {
        var template = MakeSolidTemplate("ChampA", ChampA);

        Assert.Equal(EnemyTemplate.Size, 16);
        Assert.Equal(EnemyTemplate.Size * EnemyTemplate.Size * 4, template.Bgra.Length);
        Assert.Equal(EnemyTemplate.Size * EnemyTemplate.Size, template.CircleMask.Length);
    }

    [Fact]
    public void FromSquareIcon_CircularMask_ExcludesCornerPixelColorFromTheMaskedTemplate()
    {
        // 32x32 source: corners filled with a color that never appears near the center, then a
        // disc (radius 17 — a small margin past the exact half-side of 16, so nearest-neighbor
        // downscale rounding never accidentally samples a corner pixel for a mask-included
        // target pixel, while staying well short of the true corners at distance ~22.6) drawn
        // over the center in a completely different color. Only the disc color should survive
        // into the masked pixels.
        const int sourceSize = 32;
        var cornerColor = (R: (byte)10, G: (byte)220, B: (byte)10); // green — nothing else uses green
        var centerColor = (R: (byte)200, G: (byte)50, B: (byte)50);

        var bgra = new byte[sourceSize * sourceSize * 4];
        for (int y = 0; y < sourceSize; y++)
        {
            for (int x = 0; x < sourceSize; x++)
            {
                int off = (y * sourceSize + x) * 4;
                double dx = x - (sourceSize - 1) / 2.0, dy = y - (sourceSize - 1) / 2.0;
                bool inDisc = dx * dx + dy * dy <= 17 * 17;
                var c = inDisc ? centerColor : cornerColor;
                bgra[off] = c.B;
                bgra[off + 1] = c.G;
                bgra[off + 2] = c.R;
                bgra[off + 3] = 255;
            }
        }

        var template = EnemyTemplate.FromSquareIcon(bgra, sourceSize, sourceSize, "ChampCorner");

        int maskedCount = 0;
        for (int i = 0; i < template.CircleMask.Length; i++)
        {
            if (!template.CircleMask[i]) continue;
            maskedCount++;
            int off = i * 4;
            // The corner color's giveaway channel (G) must never appear in a masked pixel —
            // proving corner pixels never reached the stored/matched template data.
            Assert.NotEqual(cornerColor.G, template.Bgra[off + 1]);
        }

        Assert.True(maskedCount > 0, "expected at least some pixels inside the circular mask");
    }
}
