using System.Diagnostics;
using Overlay.Core.EventBus;

namespace Overlay.Core.Tests;

/// <summary>
/// Not an Acceptance Criteria gate by itself -- throughput numbers here are reported
/// in M15_report.md per the spec's Agent Implementation Notes ("성능 벤치마크 결과를
/// Report에 첨부"). Kept as a normal xUnit fact (skipped from strict timing assertions)
/// so the numbers are regenerated on every test run rather than hand-typed once.
/// </summary>
public class EventBusBenchmarkTests
{
    public EventBusBenchmarkTests() => EventBus.EventBus.ResetForTests();

    [Fact]
    public void Benchmark_SyncPublishThroughput_10000Events()
    {
        const int count = 10_000;
        int received = 0;
        EventBus.EventBus.Subscribe("SYSTEM.BENCH_SYNC", _ => Interlocked.Increment(ref received));

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            EventBus.EventBus.Publish("SYSTEM.BENCH_SYNC", i, "BenchSource");
        }
        sw.Stop();

        Assert.Equal(count, received);
        double perEventUs = sw.Elapsed.TotalMilliseconds * 1000.0 / count;
        Console.WriteLine($"[Benchmark] Sync publish: {count} events in {sw.ElapsedMilliseconds} ms " +
                           $"({perEventUs:F2} us/event, {count / sw.Elapsed.TotalSeconds:F0} events/sec)");
    }

    [Fact]
    public void Benchmark_AsyncUiPublishThroughput_10000Events()
    {
        const int count = 10_000;
        int received = 0;
        var done = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.BENCH_ASYNC", _ =>
        {
            if (Interlocked.Increment(ref received) == count) done.Set();
        });

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            EventBus.EventBus.Publish("UI.BENCH_ASYNC", i, "BenchSource");
        }
        double publishOnlyMs = sw.Elapsed.TotalMilliseconds; // time to enqueue, not to drain
        bool drained = done.Wait(TimeSpan.FromSeconds(10));
        sw.Stop();

        Assert.True(drained);
        Assert.Equal(count, received);
        Console.WriteLine($"[Benchmark] Async(UI.*) publish: enqueue {count} events in {publishOnlyMs:F1} ms " +
                           $"({count / (publishOnlyMs / 1000.0):F0} events/sec enqueue); " +
                           $"full drain in {sw.ElapsedMilliseconds} ms");
    }
}
