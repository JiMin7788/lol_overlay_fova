using System.Collections.Concurrent;

namespace Overlay.Core.EventBus;

/// <summary>
/// M15 Event Bus: central pub/sub used by every other module to communicate without a
/// direct reference to one another. Pure infrastructure — no dependency on any other
/// module (spec Dependencies: "없음").
///
/// ─── NAMESPACE ENFORCEMENT ───────────────────────────────────────────────────────
/// Every published <see cref="Event.Type"/> must start with one of
/// <see cref="AllowedNamespaces"/> (GAME./COMBO./UI./VOICE./SYSTEM.). Publishing
/// outside these namespaces throws <see cref="ArgumentException"/> — rejected, not
/// silently dropped, so a misbehaving caller fails fast during development instead of
/// losing events at runtime (spec Scope: "이벤트 네임스페이스 규칙 강제").
///
/// ─── SYNC VS ASYNC DISPATCH ──────────────────────────────────────────────────────
/// - Sync subscribers are invoked directly on the publisher's calling thread/stack, in
///   the order they were registered.
/// - Async subscribers (any subscription created with async:true, and ALL `UI.*`
///   subscriptions regardless of what the subscriber asked for — spec Internal Logic
///   #2: "UI.* 네임스페이스 이벤트는 ... 기본적으로 비동기 처리를 강제한다") are queued onto a
///   single dedicated background dispatch thread (see <see cref="_asyncQueue"/>) and
///   invoked there. This is a real OS thread, not "async void" (which can still run
///   synchronously to its first await on the calling thread) — so it is verifiably off
///   the calling/render thread; see M15_report.md Test Results for a demonstration
///   that Thread.CurrentThread.ManagedThreadId differs between publisher and handler.
/// - Because there is exactly one async dispatch thread draining one FIFO queue,
///   async deliveries preserve publish order across the whole bus, and (combined with
///   sync handlers running synchronously during Publish) per-type ordering holds:
///   two Publish calls for the same type are enqueued to the async queue in the same
///   order they were called, and the single consumer drains strictly FIFO.
///
/// ─── CYCLE GUARD ─────────────────────────────────────────────────────────────────
/// A handler that calls Publish again (directly, or transitively through another
/// handler) increments an <see cref="AsyncLocal{T}"/> chain-depth counter for the
/// duration of that nested Publish. If the depth would exceed
/// <see cref="MaxChainDepth"/> (10, per spec Agent Implementation Notes), Publish
/// throws <see cref="InvalidOperationException"/> instead of recursing further,
/// preventing a stack overflow from an A-triggers-B-triggers-A style loop. The depth
/// counter only tracks the SYNC call stack (AsyncLocal flows with the synchronous
/// call, which is exactly the recursive path that can blow the stack); async-queued
/// handlers start a fresh depth of 0 because they run on a new stack, not nested
/// inside the original Publish call.
/// </summary>
public static class EventBus
{
    public const int MaxChainDepth = 10;

    public static readonly string[] AllowedNamespaces =
        { "GAME.", "COMBO.", "UI.", "VOICE.", "SYSTEM." };

    /// <summary>Injectable warning sink for spec Internal Logic #3 (missing source).
    /// Placeholder pending the real M18 Logging module (see IEventBusLogger.cs).
    /// Swappable for tests.</summary>
    public static IEventBusLogger Logger { get; set; } = new DebugEventBusLogger();

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Subscription> _subscriptions = new();

    // Single background dispatcher thread + FIFO queue: guarantees async deliveries
    // happen off the calling thread (not "async void") and in strict publish order.
    private static readonly BlockingCollection<Action> _asyncQueue = new();
    private static readonly Thread _asyncWorker;

    // Cycle guard: depth of nested synchronous Publish calls on the current logical
    // call stack (flows through sync handler invocation only — see class doc).
    private static readonly AsyncLocal<int> _chainDepth = new();

    static EventBus()
    {
        _asyncWorker = new Thread(AsyncDispatchLoop)
        {
            IsBackground = true,
            Name = "EventBus.AsyncDispatch",
        };
        _asyncWorker.Start();
    }

    // ── Public API (spec Interfaces) ────────────────────────────────────────────

    /// <summary>
    /// Publish an event. <paramref name="type"/> must be namespaced under one of
    /// <see cref="AllowedNamespaces"/> or this throws <see cref="ArgumentException"/>.
    /// A missing/empty <paramref name="source"/> logs a warning (traceability) but does
    /// not block delivery. <paramref name="options"/> lets a caller force async delivery
    /// for a non-UI event; UI.* is always async regardless of <paramref name="options"/>.
    /// </summary>
    public static void Publish(string type, object? payload, string source = "", PublishOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Event type must not be empty.", nameof(type));

        if (!IsAllowedNamespace(type))
        {
            throw new ArgumentException(
                $"Event type '{type}' is outside the allowed namespaces " +
                $"({string.Join(", ", AllowedNamespaces)}).", nameof(type));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            Logger.Warn($"Publish('{type}') is missing a source module name — traceability degraded.");
        }

        var evt = new Event
        {
            Id = Guid.NewGuid().ToString(),
            Type = type,
            Source = source,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload,
        };

        bool forceAsync = type.StartsWith("UI.", StringComparison.Ordinal) || (options?.Async ?? false);

        // Cycle guard applies to the synchronous delivery path: a handler invoked
        // synchronously below may itself call Publish, which re-enters here on the
        // same logical stack (AsyncLocal flows with it). Async-queued deliveries run
        // on the dispatch thread with a fresh depth (no ambient AsyncLocal captured
        // from the queueing call), so they cannot use this guard to detect a cycle
        // that only manifests asynchronously — acceptable per spec, which frames the
        // guard around the recursive/stack-overflow scenario.
        int depth = _chainDepth.Value;
        if (depth >= MaxChainDepth)
        {
            throw new InvalidOperationException(
                $"EventBus cycle guard: chain depth exceeded {MaxChainDepth} while publishing '{type}'. " +
                "A handler is very likely re-publishing an event that loops back on itself.");
        }

        foreach (var sub in MatchingSubscriptions(type))
        {
            if (BatchIfConfigured(sub, evt))
                continue;

            Deliver(sub, evt, forceAsync, depth);
        }
    }

    /// <summary>Subscribe to an exact type ("GAME.CS_UPDATE") or a wildcard pattern
    /// ("GAME.*"). Returns a subscription id for <see cref="Unsubscribe"/>.</summary>
    public static string Subscribe(string typeOrPattern, Action<Event> handler, SubscribeOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(typeOrPattern))
            throw new ArgumentException("Pattern must not be empty.", nameof(typeOrPattern));
        if (handler is null)
            throw new ArgumentNullException(nameof(handler));

        var id = Guid.NewGuid().ToString();
        var sub = new Subscription(id, typeOrPattern, handler, options?.Async ?? false, options?.Batch);

        lock (_lock)
        {
            _subscriptions[id] = sub;
        }

        return id;
    }

    /// <summary>Remove a subscription. No-op if the id is unknown/already removed.</summary>
    public static void Unsubscribe(string subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;
        lock (_lock)
        {
            _subscriptions.Remove(subscriptionId);
        }
    }

    /// <summary>Test/shutdown hook: removes every subscription. Not part of the spec's
    /// public Interfaces, but needed so isolated tests don't leak state into each
    /// other given EventBus is a static/process-wide bus.</summary>
    internal static void ResetForTests()
    {
        lock (_lock)
        {
            _subscriptions.Clear();
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────────

    private static bool IsAllowedNamespace(string type)
    {
        foreach (var ns in AllowedNamespaces)
        {
            if (type.StartsWith(ns, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Snapshot of subscriptions whose pattern matches <paramref name="type"/>,
    /// in registration order. Snapshotting under the lock avoids holding the lock while
    /// invoking arbitrary handler code (which could itself call Subscribe/Unsubscribe).</summary>
    private static List<Subscription> MatchingSubscriptions(string type)
    {
        lock (_lock)
        {
            var result = new List<Subscription>();
            foreach (var sub in _subscriptions.Values)
            {
                if (Matches(sub.Pattern, type)) result.Add(sub);
            }
            return result;
        }
    }

    private static bool Matches(string pattern, string type)
    {
        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^1]; // keep trailing '.', drop '*'
            return type.StartsWith(prefix, StringComparison.Ordinal);
        }
        return string.Equals(pattern, type, StringComparison.Ordinal);
    }

    private static void Deliver(Subscription sub, Event evt, bool forceAsync, int depth)
    {
        bool isAsync = forceAsync || sub.Async;
        if (isAsync)
        {
            _asyncQueue.Add(() => InvokeHandler(sub, evt, 0));
        }
        else
        {
            InvokeHandler(sub, evt, depth + 1);
        }
    }

    private static void InvokeHandler(Subscription sub, Event evt, int newDepth)
    {
        int previous = _chainDepth.Value;
        _chainDepth.Value = newDepth;
        try
        {
            sub.Handler(evt);
        }
        finally
        {
            _chainDepth.Value = previous;
        }
    }

    private static void AsyncDispatchLoop()
    {
        foreach (var action in _asyncQueue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch
            {
                // A misbehaving async subscriber must not kill the single shared
                // dispatch thread for every other UI.*/async subscriber.
            }
        }
    }

    /// <summary>Spec Internal Logic #4: optional batching for same-type floods.
    /// When a subscription opted into batching, rapid same-type events within the
    /// configured window are coalesced — the handler fires once per window with the
    /// LATEST payload rather than once per publish. Returns true if the event was
    /// absorbed into a pending batch (caller should not deliver it immediately).</summary>
    private static bool BatchIfConfigured(Subscription sub, Event evt)
    {
        var batch = sub.Batch;
        if (batch is null) return false;

        lock (batch)
        {
            batch.Latest = evt;
            if (batch.Timer is not null) return true; // window already pending

            batch.Timer = new Timer(_ =>
            {
                Event toDeliver;
                lock (batch)
                {
                    toDeliver = batch.Latest!;
                    batch.Timer!.Dispose();
                    batch.Timer = null;
                }
                Deliver(sub, toDeliver, forceAsync: sub.Pattern.StartsWith("UI.", StringComparison.Ordinal), depth: 0);
            }, null, batch.WindowMilliseconds, Timeout.Infinite);

            return true;
        }
    }

    private sealed class Subscription
    {
        public string Id { get; }
        public string Pattern { get; }
        public Action<Event> Handler { get; }
        public bool Async { get; }
        public BatchState? Batch { get; }

        public Subscription(string id, string pattern, Action<Event> handler, bool async, BatchOptions? batchOptions)
        {
            Id = id;
            Pattern = pattern;
            Handler = handler;
            Async = async;
            Batch = batchOptions is null ? null : new BatchState(batchOptions.WindowMilliseconds);
        }
    }

    private sealed class BatchState
    {
        public int WindowMilliseconds { get; }
        public Event? Latest;
        public Timer? Timer;

        public BatchState(int windowMilliseconds) => WindowMilliseconds = windowMilliseconds;
    }
}

/// <summary>Options for <see cref="EventBus.Publish"/>. Matches spec Interfaces
/// (<c>options?: { async: boolean }</c>).</summary>
public sealed class PublishOptions
{
    /// <summary>Force async delivery for this publish even for non-UI types.
    /// UI.* is always async regardless of this flag.</summary>
    public bool Async { get; init; }
}

/// <summary>Options for <see cref="EventBus.Subscribe"/>.</summary>
public sealed class SubscribeOptions
{
    /// <summary>Request async delivery for this subscription (handler runs on the
    /// EventBus dispatch thread, not the publisher's thread). UI.* subscriptions are
    /// always dispatched async regardless of this flag.</summary>
    public bool Async { get; init; }

    /// <summary>Opt into batching for same-type floods (spec Internal Logic #4). When
    /// set, rapid publishes of the subscribed type are coalesced within the window and
    /// the handler receives only the latest payload once per window.</summary>
    public BatchOptions? Batch { get; init; }
}

/// <summary>Batching window configuration for a single subscription.</summary>
public sealed class BatchOptions
{
    /// <summary>Coalescing window in milliseconds. Default 100ms — short enough to
    /// stay imperceptible for a UI update, long enough to collapse a same-frame flood
    /// (e.g. rapid Live Client polling deltas for the same stat).</summary>
    public int WindowMilliseconds { get; init; } = 100;
}
