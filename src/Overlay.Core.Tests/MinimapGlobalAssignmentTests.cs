using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// lolF04 t5e (TODO:425): <see cref="MinimapGlobalAssignment"/> and <see cref="AssignmentMode.Global"/> in
/// <see cref="MinimapDetector.Detect"/>. The icons are solid discs and the templates solid colours, so a score is
/// 1 − (RGB distance)/441.67. The colours are test values only.
/// </summary>
public class MinimapGlobalAssignmentTests
{
    private static readonly (byte R, byte G, byte B) Background = (30, 30, 30);
    // The "Seraphine" icon is closer to the Sett template than the real Sett icon is:
    // Seraphine icon: Sett 0.9547, Seraphine 0.8987; Sett icon: Sett 0.9094, Seraphine 0.7975.
    private static readonly (byte R, byte G, byte B) SeraphineIcon = (220, 20, 20);
    private static readonly (byte R, byte G, byte B) SettIcon = (220, 20, 80);
    private static readonly EnemyTemplate Sett = MakeSolidTemplate("Sett", (220, 20, 40));
    private static readonly EnemyTemplate Seraphine = MakeSolidTemplate("Seraphine", (220, 60, 0));
    private static readonly EnemyTemplate[] Roster = { Sett, Seraphine };

    private const int SeraphineX = 40, SettX = 120, IconY = 60;

    private static MinimapFrame MakeFrame(int width, int height)
    {
        int stride = width * 4;
        var bgra = new byte[stride * height];
        for (int i = 0; i < width * height; i++)
        {
            int off = i * 4;
            bgra[off] = Background.B;
            bgra[off + 1] = Background.G;
            bgra[off + 2] = Background.R;
            bgra[off + 3] = 255;
        }
        return new MinimapFrame(bgra, width, height, stride, 0, flipped: false);
    }

    private static void DrawFilledDisc(MinimapFrame frame, int cx, int cy, int radius, (byte R, byte G, byte B) color)
    {
        for (int y = 0; y < frame.Height; y++)
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

    private static EnemyTemplate MakeSolidTemplate(string championId, (byte R, byte G, byte B) color)
    {
        const int size = 16;
        var bgra = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            int off = i * 4;
            bgra[off] = color.B;
            bgra[off + 1] = color.G;
            bgra[off + 2] = color.R;
            bgra[off + 3] = 255;
        }
        return EnemyTemplate.FromSquareIcon(bgra, size, size, championId, isEnemy: true);
    }

    private static MinimapFrame Frame(bool withSett)
    {
        var frame = MakeFrame(160, 120);
        DrawFilledDisc(frame, SeraphineX, IconY, 11, SeraphineIcon);
        if (withSett) DrawFilledDisc(frame, SettX, IconY, 11, SettIcon);
        return frame;
    }

    private static IReadOnlyList<MinimapSighting> Detect(MinimapFrame frame, AssignmentMode mode)
        => new MinimapDetector().Detect(frame, Roster, DetectionOptions.Default with { Assignment = mode });

    private static string At(IReadOnlyList<MinimapSighting> sightings, int x)
        => Assert.Single(sightings, s => Math.Abs(s.MapPos01.X * 160 - x) < 2).ChampionId;

    [Fact]
    public void SettAndSeraphine_ArgmaxGivesSettToTheSeraphineIcon()
    {
        // Both icons' argmax is Sett; the Seraphine icon's claim is the stronger, so it keeps Sett.
        var s = Assert.Single(Detect(Frame(withSett: true), AssignmentMode.PerCandidate));
        Assert.Equal("Sett", s.ChampionId);
        Assert.Equal(SeraphineX / 160.0, s.MapPos01.X, precision: 2);
    }

    [Fact]
    public void SettAndSeraphine_GlobalGivesSettToTheSettIcon()
    {
        // Totals: Seraphine icon→Sett + Sett icon→Seraphine 1.752 < Seraphine icon→Seraphine + Sett icon→Sett 1.808.
        var sightings = Detect(Frame(withSett: true), AssignmentMode.Global);
        Assert.Equal(2, sightings.Count);
        Assert.Equal("Sett", At(sightings, SettX));
        Assert.Equal("Seraphine", At(sightings, SeraphineX));
    }

    /// <summary>The item's stated limit: with the real Sett absent, the Seraphine icon still takes Sett.</summary>
    [Fact]
    public void SettAbsent_TheSeraphineIconStillTakesSett()
    {
        var frame = Frame(withSett: false);
        Assert.Equal("Sett", Assert.Single(Detect(frame, AssignmentMode.Global)).ChampionId);
        Assert.Equal(Detect(frame, AssignmentMode.PerCandidate), Detect(frame, AssignmentMode.Global));
    }

    [Fact]
    public void PerCandidate_IsTheDefault_AndUnchanged()
    {
        Assert.Equal(AssignmentMode.PerCandidate, DetectionOptions.Default.Assignment);
        var frame = Frame(withSett: true);
        Assert.Equal(new MinimapDetector().Detect(frame, Roster, DetectionOptions.Default),
                     Detect(frame, AssignmentMode.PerCandidate));
    }

    [Fact]
    public void Assign_EqualTotals_LowerCandidateFirst()
    {
        var (champion, _, _) = MinimapGlobalAssignment.Assign(new[] { new[] { 0.9 }, new[] { 0.9 } }, 0.75);
        Assert.Equal(new[] { 0, -1 }, champion);
    }

    [Fact]
    public void Assign_EqualTotals_ThenChampionOrder()
    {
        var (one, _, _) = MinimapGlobalAssignment.Assign(new[] { new[] { 0.8, 0.8 } }, 0.75);
        Assert.Equal(new[] { 0 }, one);
        // {0→0, 1→1} and {0→1, 1→0} both total 1.6: candidate 0 gets the earlier champion.
        var (two, _, _) = MinimapGlobalAssignment.Assign(new[] { new[] { 0.8, 0.8 }, new[] { 0.8, 0.8 } }, 0.75);
        Assert.Equal(new[] { 0, 1 }, two);
    }

    [Fact]
    public void Assign_MaximisesTheTotal_AndSkipsScoresBelowTheThreshold()
    {
        var (champion, _, _) = MinimapGlobalAssignment.Assign(
            new[] { new[] { 0.95, 0.90 }, new[] { 0.90, 0.70 } }, 0.75);
        Assert.Equal(new[] { 1, 0 }, champion);   // 1.80 beats 0.95 alone (0.70 is below the threshold)
        var (none, margin, runnerUp) = MinimapGlobalAssignment.Assign(new[] { new[] { 0.74, 0.70 } }, 0.75);
        Assert.Equal(new[] { -1 }, none);
        Assert.True(double.IsNaN(margin[0]));
        Assert.Equal(-1, runnerUp[0]);
    }

    /// <summary>Candidate 0's argmax margin is 0.01 (champion 1), but champion 1 is candidate 1's, so its margin
    /// is over champion 2.</summary>
    [Fact]
    public void Assign_MarginIsOverChampionsNoOtherCandidateHolds()
    {
        var (champion, margin, runnerUp) = MinimapGlobalAssignment.Assign(
            new[] { new[] { 0.95, 0.94, 0.85 }, new[] { 0.80, 0.93, 0.80 } }, 0.75);
        Assert.Equal(new[] { 0, 1 }, champion);
        Assert.Equal(new[] { 2, 2 }, runnerUp);
        Assert.Equal(0.10, margin[0], precision: 10);
        Assert.Equal(0.13, margin[1], precision: 10);
    }

    [Fact]
    public void Assign_NoFreeChampion_MarginIsNaN()
    {
        var (champion, margin, runnerUp) = MinimapGlobalAssignment.Assign(
            new[] { new[] { 0.95, 0.90 }, new[] { 0.80, 0.93 } }, 0.75);
        Assert.Equal(new[] { 0, 1 }, champion);
        Assert.True(double.IsNaN(margin[0]) && double.IsNaN(margin[1]));
        Assert.Equal(new[] { -1, -1 }, runnerUp);
    }

    /// <summary>A margin below MinMatchMargin over a free champion drops the candidate, as the argmax rule does.</summary>
    [Fact]
    public void Global_SmallMarginOverAFreeChampion_NoIdentity()
    {
        var near = MakeSolidTemplate("Near", (220, 22, 22));   // scores 0.994 at the icon: margin 0.006 < 0.03
        var frame = Frame(withSett: false);
        var options = DetectionOptions.Default with { Assignment = AssignmentMode.Global };
        Assert.Empty(new MinimapDetector().Detect(frame, new[] { MakeSolidTemplate("Exact", SeraphineIcon), near }, options));
    }

    /// <summary>docs/reports/minimap_lolF04_variants.md §TODO:425 ends "Result: measured, not adopted".</summary>
    [Fact]
    public void Measured_IsPerCandidate_NotAdopted()
        => Assert.Equal(AssignmentMode.PerCandidate, MinimapGlobalAssignment.Measured);

    // AI-NOTE: pinned to the R16 wording; an adopted variant is logged at start as "minimap: detector variant on — " + this.
    [Fact]
    public void Describe_IsGlobalAssignment()
        => Assert.Equal("global-assignment", MinimapGlobalAssignment.Describe());
}
