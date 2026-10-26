using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>lolF03 R8 (TODO:63): restart the capture only on a settled mode/monitor/DPI change.</summary>
public class CaptureRestartPolicyTests
{
    private static readonly CaptureProbe Windowed = new("Windowed", 1, 96);
    private static readonly CaptureProbe Fullscreen = new("ExclusiveFullscreenLikely", 1, 96);
    private static readonly CaptureProbe OtherMonitor = new("ExclusiveFullscreenLikely", 2, 144);
    private static readonly CaptureProbe Gone = new("", 0, 0);

    private static CaptureRestartPolicy Started()
    {
        var policy = new CaptureRestartPolicy();
        policy.Begin(Windowed, 0);
        return policy;
    }

    [Fact]
    public void SameProbe_NoRestart()
    {
        var policy = Started();
        Assert.False(policy.Observe(Windowed, 1000));
        Assert.False(policy.Observe(Windowed, 2000));
    }

    [Fact]
    public void OneDifferingSample_NoRestart()
    {
        var policy = Started();
        Assert.False(policy.Observe(Fullscreen, 1000));
        Assert.False(policy.Observe(Windowed, 2000));
    }

    [Fact]
    public void TwoEqualDifferingSamples_Restart()
    {
        var policy = Started();
        Assert.False(policy.Observe(Fullscreen, 1000));
        Assert.True(policy.Observe(Fullscreen, 2000));
    }

    [Fact]
    public void WithinFiveSecondsOfLastRestart_NoRestart()
    {
        var policy = Started();
        policy.Observe(Fullscreen, 1000);
        Assert.True(policy.Observe(Fullscreen, 2000));
        policy.Begin(Fullscreen, 2100);

        Assert.False(policy.Observe(OtherMonitor, 3000));
        Assert.False(policy.Observe(OtherMonitor, 4000));
        Assert.False(policy.Observe(OtherMonitor, 6999));
        Assert.True(policy.Observe(OtherMonitor, 7000));
    }

    [Fact]
    public void AfterBeginWithNewBaseline_NoRestart()
    {
        var policy = Started();
        policy.Observe(Fullscreen, 1000);
        Assert.True(policy.Observe(Fullscreen, 2000));
        policy.Begin(Fullscreen, 2100);

        Assert.False(policy.Observe(Fullscreen, 8000));
        Assert.False(policy.Observe(Fullscreen, 9000));
    }

    [Fact]
    public void InvalidSampleBetweenTwoEqualDiffering_IsIgnored_PairStillRestarts()
    {
        var policy = Started();
        Assert.False(policy.Observe(Fullscreen, 1000));
        Assert.False(policy.Observe(Gone, 2000));
        Assert.True(policy.Observe(Fullscreen, 3000));
    }

    [Fact]
    public void InvalidSampleAlone_NoRestart()
    {
        var policy = Started();
        Assert.False(policy.Observe(Gone, 1000));
        Assert.False(policy.Observe(Gone, 2000));
        Assert.False(policy.Observe(new CaptureProbe("Windowed", 0, 96), 3000));
        Assert.False(policy.Observe(new CaptureProbe("Windowed", 1, 0), 4000));
    }

    [Fact]
    public void AfterRestartFailed_SameProbe_NoRestart()
    {
        var policy = Started();
        policy.Observe(Fullscreen, 1000);
        Assert.True(policy.Observe(Fullscreen, 2000));
        policy.RestartFailed(Fullscreen, 2050);

        Assert.Equal(Fullscreen, policy.Baseline);
        Assert.False(policy.Observe(Fullscreen, 8000));
        Assert.False(policy.Observe(Fullscreen, 9000));
        Assert.False(policy.Observe(Fullscreen, 20000));
    }
}
