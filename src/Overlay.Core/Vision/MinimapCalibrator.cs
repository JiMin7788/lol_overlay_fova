using Overlay.Core.Overlay;
using Overlay.Core.Render;

namespace Overlay.Core.Vision;

/// <summary>Which M31 §2 calibration layer produced a <see cref="MinimapCalibration"/>.</summary>
public enum MinimapCalibrationSource
{
    /// <summary>Layer 1: pure geometry (<see cref="MinimapRectCalculator"/>), no game.cfg.</summary>
    GeometricPrior,

    /// <summary>Layer 0: refined by at least one game.cfg value (scale and/or flip).</summary>
    GameCfg,
}

/// <summary>Result of composing calibration layers 0+1 into an on-screen minimap rect.</summary>
/// <param name="Rect">Minimap rect in game-client / overlay-window pixels (same space as
/// <see cref="MinimapRectCalculator"/>). The ROI to crop on the GPU.</param>
/// <param name="Flipped">Calibrated flip (game.cfg <c>FlipMiniMap</c>), carried into
/// <see cref="MinimapFrame.Flipped"/> for P2's map-coordinate transform.</param>
/// <param name="Source">Which layer produced <paramref name="Rect"/>.</param>
public readonly record struct MinimapCalibration(
    RenderBounds Rect, bool Flipped, MinimapCalibrationSource Source);

/// <summary>
/// M31 §2 calibration layers 0 + 1: refine the geometric prior
/// (<see cref="MinimapRectCalculator"/>) with whatever League's <c>game.cfg</c> gives us
/// (<see cref="GameCfgReader"/>). Two independent refinements:
///
/// <list type="bullet">
/// <item><b>Flip</b> (game.cfg <c>FlipMiniMap</c>) is a clean boolean, applied deterministically:
/// it mirrors the anchor from bottom-RIGHT (prior) to bottom-LEFT. This AUTO-DETECTS the flip
/// (M31 §9-Q3) — a real, verifiable win.</item>
/// <item><b>Scale</b> (game.cfg <c>MinimapScale</c>) is the user's slider value; converting it to
/// a pixel size needs a coefficient this sandbox cannot observe (no live client). We apply it as
/// a bounded multiplier around <see cref="ApproxDefaultMinimapScale"/> — the MECHANISM is exact
/// and unit-tested, but the reference constant is an APPROXIMATION flagged for a one-shot live
/// layer-2 pin (see the M31 P1 entry in <c>CLAUDE_CODE_TODO.md</c>). If the reference is off the
/// worst case is a scalar size error the user corrects with the existing move-mode drag offset —
/// the same escape hatch <see cref="MinimapRectCalculator"/> already documents.</item>
/// </list>
///
/// When game.cfg supplies neither value, this returns the prior unchanged
/// (<see cref="MinimapCalibrationSource.GeometricPrior"/>). Auto-calibration (layer 2,
/// <see cref="MinimapAutoCalibrator"/>) refines the result further from a captured frame when
/// one is available; manual drag (layer 3) is the final override.
/// </summary>
public static class MinimapCalibrator
{
    /// <summary>The game.cfg <c>MinimapScale</c> value taken to mean League's DEFAULT (100%)
    /// minimap size — the point at which this layer reproduces the geometric prior exactly
    /// (<c>hudScale = 1.0</c>). <b>APPROXIMATION — UNVERIFIED:</b> Cowork cannot read a live
    /// client to observe the real slider→pixel relationship, so this reference must be pinned by
    /// one live layer-2 measurement (M31 P1 TODO). An ABSENT scale also yields the prior, so a
    /// user on default settings is unaffected regardless of this constant's accuracy.</summary>
    public const double ApproxDefaultMinimapScale = 1.0;

    /// <summary>Compose layers 0+1 for a game client of the given pixel size.</summary>
    /// <param name="windowWidth">Tracked game-client / overlay-window width, pixels.</param>
    /// <param name="windowHeight">Tracked game-client / overlay-window height, pixels.</param>
    /// <param name="cfg">Parsed game.cfg HUD settings, or null/empty to use the prior only.</param>
    public static MinimapCalibration Compute(
        double windowWidth, double windowHeight, GameCfgHudSettings? cfg = null)
    {
        cfg ??= GameCfgHudSettings.Empty;
        bool usedCfg = false;

        double hudScale = 1.0;
        if (cfg.MinimapScale is double s && s > 0.0 && ApproxDefaultMinimapScale > 0.0)
        {
            hudScale = s / ApproxDefaultMinimapScale;
            usedCfg = true;
        }

        // Prior clamps hudScale internally and returns a zero rect for a non-positive window.
        var baseRect = MinimapRectCalculator.Compute(windowWidth, windowHeight, hudScale);

        bool flipped = false;
        if (cfg.FlipMiniMap is bool f)
        {
            flipped = f;
            usedCfg = true;
        }

        // Flip mirrors the anchor horizontally: bottom-RIGHT → bottom-LEFT. Reflecting the prior's
        // X across the window span gives newX = W - X - width (= the same margin on the left).
        // Guard against the prior's degenerate zero rect so a non-positive window stays at origin.
        var rect = (flipped && baseRect.Width > 0.0)
            ? baseRect with { X = windowWidth - baseRect.X - baseRect.Width }
            : baseRect;

        var source = usedCfg ? MinimapCalibrationSource.GameCfg : MinimapCalibrationSource.GeometricPrior;
        return new MinimapCalibration(rect, flipped, source);
    }
}
