using Overlay.Core.EventBus;

namespace Overlay.Core.Tests;

/// <summary>
/// <see cref="LiveSnapshotHolder"/>: keeps a stable copy of the poller's latest snapshot and clears
/// it on disconnect, before any GAME.DISCONNECTED subscriber runs (lolf01-t1: a stale last-game
/// snapshot kept the overlay preview off after the first game).
///
/// Drives a real <see cref="LiveClientPoller"/> against
/// <see cref="LiveClientPollerTests.FakeHttpMessageHandler"/>; ticks run on the real 100ms cadence,
/// so the tests wait for a condition with a deadline rather than a fixed delay.
/// </summary>
public class LiveSnapshotHolderTests
{
    // Mirrors LiveClientPoller.DisconnectFailureThreshold (private const).
    private const int DisconnectFailureThreshold = 3;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Latest_IsACopy_UnchangedAfterThePollerReparsesItsBuffer()
    {
        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n =>
            LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: n == 1 ? 10 : 20)));
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);
        var holder = new LiveSnapshotHolder(poller);

        GameSnapshot? firstBuffer = null;
        int ticks = 0;
        poller.SnapshotAvailable += (prev, cur, initial) =>
        {
            if (Interlocked.Increment(ref ticks) == 1) firstBuffer = cur;
        };

        poller.Start();
        Assert.True(await WaitUntil(() => holder.Latest is not null));
        GameSnapshot first = holder.Latest!;
        Assert.Equal(10, first.GameTime);

        // The poller ping-pongs two buffers: tick 3 re-parses tick 1's buffer in place.
        Assert.True(await WaitUntil(() => Volatile.Read(ref ticks) >= 3));
        await poller.StopAsync();

        Assert.Equal(20, firstBuffer!.GameTime); // the buffer itself was overwritten...
        Assert.NotSame(firstBuffer, first);
        Assert.Equal(10, first.GameTime);        // ...but the holder's copy was not.
        Assert.True(first.HasData);
    }

    [Fact]
    public async Task Latest_IsNull_AfterThresholdConsecutiveFailures_FollowingAConnect()
    {
        int calls = 0;
        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n =>
        {
            Volatile.Write(ref calls, n);
            return n == 1
                ? LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 10))
                : throw new HttpRequestException("simulated failure");
        });
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);
        var holder = new LiveSnapshotHolder(poller);

        poller.Start();
        Assert.True(await WaitUntil(() => holder.Latest is { HasData: true }));

        // Tick 1 connected; ticks 2..1+threshold fail -> disconnect.
        Assert.True(await WaitUntil(() => Volatile.Read(ref calls) >= 1 + DisconnectFailureThreshold && !poller.GameConnected));
        await poller.StopAsync();

        Assert.Null(holder.Latest);
    }

    [Fact]
    public async Task Latest_HasDataAgain_AfterAGoodPollFollowingADisconnect()
    {
        bool fail = false;
        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(_ => Volatile.Read(ref fail)
            ? throw new HttpRequestException("simulated failure")
            : LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 10)));
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);
        var holder = new LiveSnapshotHolder(poller);

        poller.Start();
        Assert.True(await WaitUntil(() => holder.Latest is { HasData: true }));

        Volatile.Write(ref fail, true);
        Assert.True(await WaitUntil(() => holder.Latest is null));

        Volatile.Write(ref fail, false);
        Assert.True(await WaitUntil(() => holder.Latest is { HasData: true }));
        await poller.StopAsync();

        Assert.Equal(10, holder.Latest!.GameTime);
    }

    [Fact]
    public async Task HolderBuiltBeforePublisher_GameDisconnectedHandler_SeesNullLatest_ExactlyOnce()
    {
        EventBus.EventBus.ResetForTests();

        int calls = 0;
        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n =>
        {
            Volatile.Write(ref calls, n);
            return n == 1
                ? LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 10))
                : throw new HttpRequestException("simulated failure");
        });
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);

        // Same order AppComposition uses: poller -> holder -> publisher.
        var holder = new LiveSnapshotHolder(poller);
        var publisher = new LiveClientEventPublisher(poller);

        var recorded = new List<GameSnapshot?>();
        bool sawData = false;
        poller.SnapshotAvailable += (prev, cur, initial) => Volatile.Write(ref sawData, true);
        // GAME.* is delivered synchronously on the publishing (poller) thread.
        string subId = EventBus.EventBus.Subscribe("GAME.DISCONNECTED", _ =>
        {
            lock (recorded) recorded.Add(holder.Latest);
        });
        try
        {
            poller.Start();
            Assert.True(await WaitUntil(() => { lock (recorded) return recorded.Count >= 1; }));
            // A few more failing ticks: the poller must not publish a second disconnect.
            int callsAtDisconnect = Volatile.Read(ref calls);
            Assert.True(await WaitUntil(() => Volatile.Read(ref calls) >= callsAtDisconnect + 3));
            await poller.StopAsync();

            Assert.True(Volatile.Read(ref sawData));
            lock (recorded)
            {
                Assert.Single(recorded);
                Assert.Null(recorded[0]);
            }
        }
        finally
        {
            EventBus.EventBus.Unsubscribe(subId);
            publisher.Dispose();
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > Deadline) return false;
            await Task.Delay(10);
        }
        return true;
    }
}
