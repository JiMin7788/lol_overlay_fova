using Overlay.Core.Flags;

namespace Overlay.Core.Tests;

/// <summary>
/// lolF05 <see cref="KillGate"/>: the capture start/stop seam. A beta_kill blocks a start, stops a
/// running capture once, never restarts it in-session, and a stop never runs inside a start.
/// Every wait has a timeout so a broken lock fails instead of hanging.
/// </summary>
public class KillGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly List<string> _log = new();
    private volatile bool _killed;

    private KillGate Minimap() => new(FeatureGate.MinimapVision, "minimap capture", () => _killed, _log.Add);
    private KillGate BuffStack() => new(FeatureGate.BuffStackVision, "buff-stack vision", () => _killed, _log.Add);

    [Fact]
    public void KilledBeforeStart_NeverCallsStart()
    {
        _killed = true;
        var gate = Minimap();
        int starts = 0;

        Assert.False(gate.TryStart(() => { starts++; return true; }));

        Assert.Equal(0, starts);
        Assert.False(gate.IsRunning);
        Assert.Equal(new[] { "beta_kill lists minimapVision: minimap capture not started" }, _log);
    }

    [Fact]
    public void StartThenKill_StopsOnce()
    {
        var gate = BuffStack();
        int stops = 0;
        Assert.True(gate.TryStart(() => true));
        Assert.True(gate.IsRunning);

        _killed = true;
        Assert.True(gate.StopIfKilled(() => stops++));
        Assert.False(gate.StopIfKilled(() => stops++)); // second call is a no-op

        Assert.Equal(1, stops);
        Assert.False(gate.IsRunning);
        Assert.Equal(new[] { "beta_kill lists buffStackVision: buff-stack vision stopped" }, _log);
    }

    [Fact]
    public async Task StopRacingABlockedStart_RunsAfterTheStart()
    {
        var gate = Minimap();
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var startEntered = new ManualResetEventSlim();
        using var stopperReady = new ManualResetEventSlim();
        using var releaseStart = new ManualResetEventSlim();

        var starter = Task.Run(() => gate.TryStart(() =>
        {
            order.Enqueue("start-begin");
            startEntered.Set();
            releaseStart.Wait(Timeout);
            order.Enqueue("start-end");
            return true;
        }));
        Assert.True(startEntered.Wait(Timeout));

        _killed = true;
        var stopper = Task.Run(() =>
        {
            stopperReady.Set();
            return gate.StopIfKilled(() => order.Enqueue("stop"));
        });
        Assert.True(stopperReady.Wait(Timeout));
        // AI-NOTE: the stopper is now at (or inside) StopIfKilled while start is blocked. With one
        // lock it must still be waiting; a broken lock lets it return (false, start not finished).
        await Task.Delay(200);
        Assert.False(stopper.IsCompleted);
        releaseStart.Set();

        Assert.True(await starter.WaitAsync(Timeout));
        Assert.True(await stopper.WaitAsync(Timeout));
        Assert.Equal(new[] { "start-begin", "start-end", "stop" }, order);
        Assert.False(gate.IsRunning);
    }

    [Fact]
    public void AfterAStop_NothingRestarts_EvenWhenTheKillIsLifted()
    {
        var gate = Minimap();
        Assert.True(gate.TryStart(() => true));
        _killed = true;
        Assert.True(gate.StopIfKilled(() => { }));

        _killed = false;
        int starts = 0;
        Assert.False(gate.TryStart(() => { starts++; return true; }));

        Assert.Equal(0, starts);
        Assert.False(gate.IsRunning);
    }

    [Fact]
    public void NotKilled_StopIfKilled_DoesNothing()
    {
        var gate = Minimap();
        Assert.True(gate.TryStart(() => true));
        int stops = 0;

        Assert.False(gate.StopIfKilled(() => stops++));

        Assert.Equal(0, stops);
        Assert.True(gate.IsRunning);
        Assert.Empty(_log);
    }

    [Fact]
    public void StartReturningFalse_IsNotRunning_AndIsNeverStopped()
    {
        var gate = BuffStack();
        Assert.False(gate.TryStart(() => false));
        Assert.False(gate.IsRunning);

        _killed = true;
        int stops = 0;
        Assert.False(gate.StopIfKilled(() => stops++));
        Assert.Equal(0, stops);
    }

    [Fact]
    public void StartThrowing_Propagates_AndIsNotRunning()
    {
        var gate = Minimap();

        Assert.Throws<InvalidOperationException>(() => gate.TryStart(() => throw new InvalidOperationException("boom")));

        Assert.False(gate.IsRunning);
    }

    [Fact]
    public void SecondTryStart_WhileRunning_DoesNotStartAgain()
    {
        var gate = Minimap();
        int starts = 0;
        Assert.True(gate.TryStart(() => { starts++; return true; }));

        Assert.False(gate.TryStart(() => { starts++; return true; }));

        Assert.Equal(1, starts);
        Assert.True(gate.IsRunning);
    }
}
