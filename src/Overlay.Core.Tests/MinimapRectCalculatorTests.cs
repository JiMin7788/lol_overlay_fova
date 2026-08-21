using Overlay.Core.Overlay;

namespace Overlay.Core.Tests;

/// <summary>
/// M02 pending-change #2 (minimap-anchored timers): <see cref="MinimapRectCalculator"/> is a
/// pure function of window size (+ optional HUD-scale multiplier), so its documented
/// calibration approximation is at least mechanically verifiable here — these tests pin down
/// the CURRENT constants' behavior (bottom-right anchored, square, sized off the shorter
/// dimension) rather than proving the constants are visually correct against a real League
/// client (which this sandbox cannot do — no dotnet SDK, no live game).
/// </summary>
public class MinimapRectCalculatorTests
{
    [Fact]
    public void Compute_16x9_1920x1080_IsSquare_AnchoredBottomRight()
    {
        var rect = MinimapRectCalculator.Compute(1920, 1080);

        Assert.Equal(rect.Width, rect.Height, precision: 6);
        // Bottom-right corner should sit within a small margin of the window edges.
        Assert.True(rect.X + rect.Width < 1920);
        Assert.True(rect.Y + rect.Height < 1080);
        Assert.True(rect.X + rect.Width > 1920 - 1920 * MinimapRectCalculator.SizeFraction - 40);
        Assert.True(rect.Y + rect.Height > 1080 - 40);
    }

    [Fact]
    public void Compute_SizesOffTheShorterDimension_NotTheWiderOne()
    {
        // An ultra-wide window: height is the shorter dimension, so the minimap size should
        // track height, not width — otherwise an ultra-wide window would get an absurdly large
        // minimap, which does not match how League's HUD actually behaves.
        var ultraWide = MinimapRectCalculator.Compute(3440, 1080);
        var standard = MinimapRectCalculator.Compute(1920, 1080);

        Assert.Equal(standard.Width, ultraWide.Width, precision: 6);
        Assert.Equal(standard.Height, ultraWide.Height, precision: 6);
    }

    [Fact]
    public void Compute_LargerWindow_YieldsLargerMinimap()
    {
        var small = MinimapRectCalculator.Compute(1280, 720);
        var large = MinimapRectCalculator.Compute(2560, 1440);

        Assert.True(large.Width > small.Width);
    }

    [Fact]
    public void Compute_HudScale_ScalesTheMinimapSizeLinearly()
    {
        var normal = MinimapRectCalculator.Compute(1920, 1080, hudScale: 1.0);
        var scaledUp = MinimapRectCalculator.Compute(1920, 1080, hudScale: 1.2);

        Assert.Equal(normal.Width * 1.2, scaledUp.Width, precision: 6);
    }

    [Fact]
    public void Compute_NonPositiveSize_ReturnsZeroRect()
    {
        var rect = MinimapRectCalculator.Compute(0, 1080);
        Assert.Equal(0, rect.Width);
        Assert.Equal(0, rect.Height);

        rect = MinimapRectCalculator.Compute(1920, -10);
        Assert.Equal(0, rect.Width);
        Assert.Equal(0, rect.Height);
    }

    [Fact]
    public void Compute_HudScale_IsClampedToASaneRange()
    {
        var extreme = MinimapRectCalculator.Compute(1920, 1080, hudScale: 100.0);
        var clampedAt2x = MinimapRectCalculator.Compute(1920, 1080, hudScale: 2.0);

        Assert.Equal(clampedAt2x.Width, extreme.Width, precision: 6);
    }
}
