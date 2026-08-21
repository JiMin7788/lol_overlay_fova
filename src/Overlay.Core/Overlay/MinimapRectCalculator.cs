using Overlay.Core.Render;

namespace Overlay.Core.Overlay;

/// <summary>
/// M02 pending-change #2 (minimap-anchored timers): computes the League client's minimap
/// on-screen rectangle, in the SAME coordinate space <c>OverlayHost</c> already renders cards
/// in (overlay-window DIPs, origin at the game window's top-left — <c>MainWindow</c> already
/// aligns the overlay 1:1 to the tracked game client rect, so "window size" here IS "game
/// client size"). A pure function of window size (+ optional HUD-scale multiplier) — no game
/// state, no new inference — so it stays P1/P2-neutral: this only changes WHERE
/// already-known jungle/inhibitor timer text is drawn, never what is drawn or inferred.
///
/// <para><b>CALIBRATION APPROXIMATION — not derived from a live-measured signal.</b> The Live
/// Client Data API exposes no minimap rect, no HUD-scale value, and no way to read the user's
/// actual "인터페이스 → HUD 크기" client setting. This calculator instead encodes the
/// commonly-observed League HUD convention (also the approach comparable third-party overlay
/// tools use absent an official API): the minimap is a fixed-aspect SQUARE anchored to the
/// bottom-right corner of the game window, sized as a fraction of the window's SHORTER
/// dimension (the minimap tracks vertical resolution more than horizontal — it does not grow
/// on ultra-wide aspect ratios), with a small margin from the corner. <see cref="SizeFraction"/>
/// and <see cref="MarginFraction"/> are the two constants that encode this and are the most
/// likely values to need a user-reported correction (different client HUD-scale settings,
/// non-16:9 aspect ratios, etc.) — see <see cref="OverlayHost"/>'s reuse of the Feature #1
/// per-element drag offset (<c>overlay.positions.{key}</c>) as exactly that correction
/// mechanism: a user who finds the auto-anchor off by a few pixels drags the timer card once
/// while "이동 여부" is on, and that offset is added on top of this calculation from then on.</para>
/// </summary>
public static class MinimapRectCalculator
{
    /// <summary>Fraction of the window's shorter dimension the minimap's square edge occupies at
    /// default (100%) League HUD scale. Derived from League's own HUD layout (MinimapRight.ini
    /// <c>MinimapContent Rect: 832.64,576.0 - 1020.16,763.52 / 1024x768</c>, magic My=1080): the content
    /// height is (763.52-576.0)/768 = 0.2442 of the magic box, which at default HUD scale equals the
    /// window height. (Was 0.22 — a guess — which drew the minimap too small.)</summary>
    public const double SizeFraction = 0.2442;

    /// <summary>Margin from the bottom-right corner, as a fraction of the window's shorter dimension.
    /// From the same HUD layout: right inset (1024-1020.16)/1024·(1440/1080)=0.005, bottom inset
    /// (768-763.52)/768=0.0058 of height → ~0.0055 average. (Was 0.012 — ~2x too large.)</summary>
    public const double MarginFraction = 0.0055;

    /// <summary>Sane clamp range for the optional <paramref name="hudScale"/> multiplier,
    /// matching League's own in-client HUD-scale slider range (roughly 80%-120%, widened
    /// slightly here since this is an approximation, not a read of the real setting).</summary>
    private const double MinHudScale = 0.5;
    private const double MaxHudScale = 2.0;

    /// <summary>Computes the minimap's on-screen rect for a window of the given size.
    /// Returns a zero rect for a non-positive size (caller should skip rendering, mirroring
    /// how <c>OverlayHost.RenderFrame</c> already guards <c>w &gt; 0 &amp;&amp; h &gt; 0</c>
    /// before building any cards).</summary>
    /// <param name="windowWidth">Overlay window / tracked game-client width, DIPs.</param>
    /// <param name="windowHeight">Overlay window / tracked game-client height, DIPs.</param>
    /// <param name="hudScale">Optional multiplier for the user's League HUD-scale setting
    /// (not readable from the Live Client API — defaults to 1.0, i.e. "assume default HUD
    /// scale"). Clamped to <see cref="MinHudScale"/>/<see cref="MaxHudScale"/>.</param>
    public static RenderBounds Compute(double windowWidth, double windowHeight, double hudScale = 1.0)
    {
        if (windowWidth <= 0 || windowHeight <= 0) return default;

        double scale = Math.Clamp(hudScale, MinHudScale, MaxHudScale);
        double shortSide = Math.Min(windowWidth, windowHeight);
        double size = shortSide * SizeFraction * scale;
        double margin = shortSide * MarginFraction;

        double x = windowWidth - size - margin;
        double y = windowHeight - size - margin;
        return new RenderBounds(x, y, size, size);
    }
}
