using Overlay.Core.Threading;

namespace Overlay.Core.Tests;

/// <summary>(lolf01-t3) The 1s timer's tick is skipped, not queued, while the previous one runs.</summary>
public class TickGateTests
{
    [Fact]
    public async Task ASecondTick_WhileTheFirstRuns_IsSkipped_AndRunsAfterIt()
    {
        var gate = new TickGate();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => gate.TryRun(() => { entered.Set(); release.Wait(); }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        bool secondRan = false;
        Assert.False(gate.TryRun(() => secondRan = true));
        Assert.False(secondRan);

        release.Set();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));

        bool thirdRan = false;
        Assert.True(gate.TryRun(() => thirdRan = true));
        Assert.True(thirdRan);
    }

    [Fact]
    public void AnExceptionPropagates_AndTheGateIsFreeAfterwards()
    {
        var gate = new TickGate();
        var ex = Assert.Throws<InvalidOperationException>(() => gate.TryRun(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", ex.Message);

        bool ran = false;
        Assert.True(gate.TryRun(() => ran = true));
        Assert.True(ran);
    }
}
