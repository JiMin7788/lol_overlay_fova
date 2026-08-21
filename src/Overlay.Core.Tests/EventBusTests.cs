using Overlay.Core.EventBus;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M15 Event Bus Acceptance Criteria:
///  1. Publish-order == callback-order for >=100 same-type events.
///  2. Cycle guard on A-triggers-B-triggers-A style recursive publish.
///  3. Wildcard subscription ("GAME.*") receives every GAME.X sub-event.
/// Plus: namespace enforcement, missing-source warning, UI.* forced-async dispatch
/// (verified via a different managed thread id, not just "didn't throw"), and the
/// optional batching mode.
///
/// EventBus is a static, process-wide bus, so every test calls
/// <see cref="EventBus.ResetForTests"/> first to avoid cross-test bleed (xUnit does not
/// guarantee test method ordering/isolation across a shared static within one
/// collection otherwise). Tests in this class run in the same collection sequentially
/// by default for xUnit (parallelism is per-class), which is sufficient here.
/// </summary>
public class EventBusTests
{
    public EventBusTests() => EventBus.EventBus.ResetForTests();

    [Fact]
    public void Publish_PreservesOrder_ForManySameTypeEvents()
    {
        const int count = 500; // "100개 이상" — used 500 for a stronger guarantee
        var received = new List<int>();
        EventBus.EventBus.Subscribe("GAME.CS_UPDATE", evt =>
        {
            var n = (int)evt.Payload!;
            lock (received) received.Add(n);
        });

        for (int i = 0; i < count; i++)
        {
            EventBus.EventBus.Publish("GAME.CS_UPDATE", i, "TestSource");
        }

        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, received[i]);
        }
    }

    [Fact]
    public void Wildcard_Subscription_ReceivesAllSubEvents()
    {
        var receivedTypes = new List<string>();
        EventBus.EventBus.Subscribe("GAME.*", evt =>
        {
            lock (receivedTypes) receivedTypes.Add(evt.Type);
        });

        EventBus.EventBus.Publish("GAME.CS_UPDATE", null, "TestSource");
        EventBus.EventBus.Publish("GAME.LEVEL_UP", null, "TestSource");
        EventBus.EventBus.Publish("GAME.RECALL", null, "TestSource");
        // A different namespace must NOT be picked up by the GAME.* subscriber.
        EventBus.EventBus.Publish("COMBO.READY", null, "TestSource");

        Assert.Equal(3, receivedTypes.Count);
        Assert.Contains("GAME.CS_UPDATE", receivedTypes);
        Assert.Contains("GAME.LEVEL_UP", receivedTypes);
        Assert.Contains("GAME.RECALL", receivedTypes);
    }

    [Fact]
    public void ExactSubscription_DoesNotReceive_OtherTypesInSameNamespace()
    {
        int hits = 0;
        EventBus.EventBus.Subscribe("GAME.CS_UPDATE", _ => Interlocked.Increment(ref hits));

        EventBus.EventBus.Publish("GAME.CS_UPDATE", null, "TestSource");
        EventBus.EventBus.Publish("GAME.LEVEL_UP", null, "TestSource");

        Assert.Equal(1, hits);
    }

    [Fact]
    public void CycleGuard_StopsInfiniteRecursion_WhenATriggersBTriggersA()
    {
        // A triggers B, B triggers A -> unbounded mutual recursion without the guard.
        // We expect an InvalidOperationException once the chain depth exceeds
        // EventBus.MaxChainDepth (10), instead of a StackOverflowException (which
        // would crash the test process outright and could not be caught at all --
        // the fact that we CAN catch a clean exception here is itself proof the guard
        // engaged before the stack blew).
        string subA = string.Empty, subB = string.Empty;
        int depthReached = 0;

        subA = EventBus.EventBus.Subscribe("SYSTEM.EVENT_A", _ =>
        {
            depthReached++;
            EventBus.EventBus.Publish("SYSTEM.EVENT_B", null, "TestSource");
        });
        subB = EventBus.EventBus.Subscribe("SYSTEM.EVENT_B", _ =>
        {
            depthReached++;
            EventBus.EventBus.Publish("SYSTEM.EVENT_A", null, "TestSource");
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            EventBus.EventBus.Publish("SYSTEM.EVENT_A", null, "TestSource"));

        Assert.Contains("cycle guard", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Guard threshold is 10 -- the chain must have actually recursed close to that
        // many times (not been rejected on the very first call) to prove it is a real
        // depth counter, not an immediate no-op.
        Assert.True(depthReached >= EventBus.EventBus.MaxChainDepth,
            $"Expected the chain to recurse at least {EventBus.EventBus.MaxChainDepth} times before the guard tripped, got {depthReached}.");

        EventBus.EventBus.Unsubscribe(subA);
        EventBus.EventBus.Unsubscribe(subB);
    }

    [Fact]
    public void Publish_OutsideAllowedNamespace_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            EventBus.EventBus.Publish("NOT_A_NAMESPACE.FOO", null, "TestSource"));
    }

    [Theory]
    [InlineData("GAME.X")]
    [InlineData("COMBO.X")]
    [InlineData("UI.X")]
    [InlineData("VOICE.X")]
    [InlineData("SYSTEM.X")]
    public void Publish_InsideAllowedNamespaces_DoesNotThrow(string type)
    {
        EventBus.EventBus.Publish(type, null, "TestSource");
    }

    [Fact]
    public void Publish_MissingSource_LogsWarning_ButStillDelivers()
    {
        var warnings = new List<string>();
        var originalLogger = EventBus.EventBus.Logger;
        EventBus.EventBus.Logger = new RecordingLogger(warnings);
        try
        {
            bool delivered = false;
            EventBus.EventBus.Subscribe("SYSTEM.NO_SOURCE_TEST", _ => delivered = true);

            EventBus.EventBus.Publish("SYSTEM.NO_SOURCE_TEST", null); // source omitted

            Assert.True(delivered);
            Assert.Single(warnings);
            Assert.Contains("SYSTEM.NO_SOURCE_TEST", warnings[0]);
        }
        finally
        {
            EventBus.EventBus.Logger = originalLogger;
        }
    }

    [Fact]
    public void UiNamespace_IsDispatched_OnADifferentThread_NotSynchronously()
    {
        // Proves UI.* is genuinely asynchronous: the handler observes a different
        // ManagedThreadId than the publisher, and Publish() returns before the handler
        // has necessarily run (we wait on a signal afterward rather than asserting
        // "already ran", since that would be a race either way -- the meaningful,
        // deterministic assertion is the thread identity).
        int publisherThreadId = Environment.CurrentManagedThreadId;
        int handlerThreadId = -1;
        var done = new ManualResetEventSlim(false);

        EventBus.EventBus.Subscribe("UI.SHOW_TOAST", _ =>
        {
            handlerThreadId = Environment.CurrentManagedThreadId;
            done.Set();
        });

        EventBus.EventBus.Publish("UI.SHOW_TOAST", null, "TestSource");

        bool signaled = done.Wait(TimeSpan.FromSeconds(2));

        Assert.True(signaled, "UI.* handler never ran.");
        Assert.NotEqual(publisherThreadId, handlerThreadId);
    }

    [Fact]
    public void Batching_CoalescesFloodOfSameTypeEvents_IntoFewerDeliveries()
    {
        var deliveries = new List<int>();
        var done = new ManualResetEventSlim(false);

        EventBus.EventBus.Subscribe("GAME.SPAM", evt =>
        {
            lock (deliveries)
            {
                deliveries.Add((int)evt.Payload!);
                done.Set();
            }
        }, new SubscribeOptions { Batch = new BatchOptions { WindowMilliseconds = 150 } });

        // Flood: 50 publishes of the same type in a tight loop (simulates a same-frame
        // spam burst, e.g. rapid API polling deltas).
        for (int i = 0; i < 50; i++)
        {
            EventBus.EventBus.Publish("GAME.SPAM", i, "TestSource");
        }

        // Wait past the batch window for the coalesced delivery to fire.
        Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(50); // small settle margin in case more land in the same window

        lock (deliveries)
        {
            Assert.True(deliveries.Count < 50, $"Expected batching to coalesce 50 publishes into far fewer deliveries, got {deliveries.Count}.");
            Assert.Equal(49, deliveries[^1]); // latest payload wins
        }
    }

    private sealed class RecordingLogger : IEventBusLogger
    {
        private readonly List<string> _sink;
        public RecordingLogger(List<string> sink) => _sink = sink;
        public void Warn(string message)
        {
            lock (_sink) _sink.Add(message);
        }
    }
}
