using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>lolF03 R9 (TODO:73 part 3): WGC with no frame for 3 s of visible time falls back to DXGI.</summary>
public class WgcNoFramePolicyTests
{
    [Fact]
    public void FrameBeforeThreeSeconds_NoFallback()
    {
        var policy = new WgcNoFramePolicy();
        policy.Start(0);
        policy.OnFrame(1000);
        Assert.False(policy.ShouldFallback(3000));
        Assert.False(policy.ShouldFallback(10000));
    }

    [Fact]
    public void NoFrameAtThreeSeconds_Fallback()
    {
        var policy = new WgcNoFramePolicy();
        policy.Start(0);
        Assert.False(policy.ShouldFallback(2999));
        Assert.True(policy.ShouldFallback(3000));
    }

    [Fact]
    public void TwoSecondsVisible_Minimize_Restore_TwoSecondsVisible_NoFallback_ThenThreeSecondsAfterRestore_Fallback()
    {
        var policy = new WgcNoFramePolicy();
        policy.Start(0);
        policy.SetMinimized(false, 1000);
        policy.SetMinimized(true, 2000);
        policy.SetMinimized(true, 8000);
        policy.SetMinimized(false, 10000);

        Assert.False(policy.ShouldFallback(12000));
        Assert.True(policy.ShouldFallback(13000));
    }

    [Fact]
    public void MinimizedAtThreeSecondMark_NoFallback()
    {
        var policy = new WgcNoFramePolicy();
        policy.Start(0);
        policy.SetMinimized(true, 2500);
        Assert.False(policy.ShouldFallback(3000));
    }

    [Fact]
    public void NeverStarted_DxgiSession_NoFallback()
    {
        var policy = new WgcNoFramePolicy();
        Assert.False(policy.ShouldFallback(60000));
    }
}
