using Overlay.Core.Overlay;
using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// M31 P1 calibration layers 0+1: <see cref="MinimapCalibrator"/> composes game.cfg over the
/// <see cref="MinimapRectCalculator"/> geometric prior. These pin the MECHANICS (flip mirrors the
/// anchor, scale multiplies linearly around <see cref="MinimapCalibrator.ApproxDefaultMinimapScale"/>,
/// absent config = prior) — NOT the visual correctness of the approximate scale reference, which
/// needs a live layer-2 pin (see CLAUDE_CODE_TODO.md).
/// </summary>
public class MinimapCalibratorTests
{
    [Fact]
    public void Compute_NoCfg_EqualsGeometricPrior()
    {
        var prior = MinimapRectCalculator.Compute(1920, 1080);
        var cal = MinimapCalibrator.Compute(1920, 1080, cfg: null);

        Assert.Equal(MinimapCalibrationSource.GeometricPrior, cal.Source);
        Assert.False(cal.Flipped);
        Assert.Equal(prior.X, cal.Rect.X, precision: 6);
        Assert.Equal(prior.Y, cal.Rect.Y, precision: 6);
        Assert.Equal(prior.Width, cal.Rect.Width, precision: 6);
        Assert.Equal(prior.Height, cal.Rect.Height, precision: 6);
    }

    [Fact]
    public void Compute_EmptyCfg_IsAlsoTheGeometricPrior()
    {
        var cal = MinimapCalibrator.Compute(1920, 1080, GameCfgHudSettings.Empty);
        Assert.Equal(MinimapCalibrationSource.GeometricPrior, cal.Source);
    }

    [Fact]
    public void Compute_Flip_MirrorsAnchorToBottomLeft_SourceGameCfg()
    {
        var prior = MinimapRectCalculator.Compute(1920, 1080);
        var cal = MinimapCalibrator.Compute(1920, 1080,
            new GameCfgHudSettings { FlipMiniMap = true });

        Assert.True(cal.Flipped);
        Assert.Equal(MinimapCalibrationSource.GameCfg, cal.Source);
        // Same size and vertical position, X reflected across the window span.
        Assert.Equal(prior.Width, cal.Rect.Width, precision: 6);
        Assert.Equal(prior.Height, cal.Rect.Height, precision: 6);
        Assert.Equal(prior.Y, cal.Rect.Y, precision: 6);
        Assert.Equal(1920 - prior.X - prior.Width, cal.Rect.X, precision: 6);
        // Flipped minimap sits on the LEFT (smaller X than the right-anchored prior).
        Assert.True(cal.Rect.X < prior.X);
    }

    [Fact]
    public void Compute_FlipFalse_KeepsBottomRightAnchor_ButStillGameCfgSourced()
    {
        var prior = MinimapRectCalculator.Compute(1920, 1080);
        var cal = MinimapCalibrator.Compute(1920, 1080,
            new GameCfgHudSettings { FlipMiniMap = false });

        Assert.False(cal.Flipped);
        Assert.Equal(MinimapCalibrationSource.GameCfg, cal.Source); // a present value counts
        Assert.Equal(prior.X, cal.Rect.X, precision: 6);
    }

    [Fact]
    public void Compute_ScaleAtReference_ReproducesThePrior()
    {
        var prior = MinimapRectCalculator.Compute(1920, 1080);
        var cal = MinimapCalibrator.Compute(1920, 1080,
            new GameCfgHudSettings { MinimapScale = MinimapCalibrator.ApproxDefaultMinimapScale });

        Assert.Equal(MinimapCalibrationSource.GameCfg, cal.Source);
        Assert.Equal(prior.Width, cal.Rect.Width, precision: 6);
    }

    [Fact]
    public void Compute_ScaleScalesSizeLinearly_AroundTheReference()
    {
        var prior = MinimapRectCalculator.Compute(1920, 1080);

        // 2× / 0.5× the reference → 2× / 0.5× size (both land on the prior's clamp bounds).
        var doubled = MinimapCalibrator.Compute(1920, 1080,
            new GameCfgHudSettings { MinimapScale = 2.0 * MinimapCalibrator.ApproxDefaultMinimapScale });
        var halved = MinimapCalibrator.Compute(1920, 1080,
            new GameCfgHudSettings { MinimapScale = 0.5 * MinimapCalibrator.ApproxDefaultMinimapScale });

        Assert.Equal(prior.Width * 2.0, doubled.Rect.Width, precision: 6);
        Assert.Equal(prior.Width * 0.5, halved.Rect.Width, precision: 6);
    }

    [Fact]
    public void Compute_ScaleAndFlipTogether_BothApply()
    {
        var atRef = MinimapRectCalculator.Compute(1920, 1080); // hudScale 1.0 == reference
        var cal = MinimapCalibrator.Compute(1920, 1080, new GameCfgHudSettings
        {
            MinimapScale = MinimapCalibrator.ApproxDefaultMinimapScale,
            FlipMiniMap = true,
        });

        Assert.True(cal.Flipped);
        Assert.Equal(atRef.Width, cal.Rect.Width, precision: 6);
        Assert.Equal(1920 - atRef.X - atRef.Width, cal.Rect.X, precision: 6);
    }

    [Fact]
    public void Compute_NonPositiveWindow_ReturnsZeroRect_NoFlipBlowup()
    {
        var cal = MinimapCalibrator.Compute(0, 1080,
            new GameCfgHudSettings { FlipMiniMap = true });

        Assert.Equal(0, cal.Rect.Width, precision: 6);
        Assert.Equal(0, cal.Rect.Height, precision: 6);
    }
}
