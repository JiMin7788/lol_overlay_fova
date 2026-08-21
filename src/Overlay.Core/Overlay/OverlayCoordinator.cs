using Overlay.Core.Config;
using Overlay.Core.EventBus;

namespace Overlay.Core.Overlay;

/// <summary>
/// The testable heart of M02 Overlay Engine. It owns all HUD lifecycle state — event
/// conversion, auto-hide scheduling, and Z-Order priority — with <b>no WPF dependency</b>
/// and <b>no rendering code</b>. The actual window show/hide is delegated to
/// <see cref="IOverlayView"/> (implemented by the WPF host over the existing click-through
/// <c>MainWindow</c> + M16 <c>RenderSurface</c>).
///
/// <para><b>Spec Internal Logic:</b>
/// <list type="number">
///   <item>The overlay window is a topmost, click-through, <em>independent</em> window
///   (owned by the WPF host — this Core class never touches a window or the game process).</item>
///   <item><c>UI.*</c> Event Bus events are subscribed (see <see cref="Start"/>) and
///   converted into <see cref="HUDPayload"/>s via a small data-driven map
///   (<see cref="TypeMap"/>).</item>
///   <item>Each shown HUD auto-hides after <c>durationMs</c> — computed as a pure expiry
///   timestamp (<see cref="ComputeExpiry"/>) so the "within 100ms of the configured
///   duration" criterion is testable with a virtual clock. Default duration comes from
///   M14 <c>overlay.hudDisplayDuration</c> (seconds, default 4).</item>
///   <item>Concurrent HUDs are ordered by Z-Order priority (combo result &gt; notification
///   &gt; jungle track by default, overridable) via <see cref="GetRenderList"/>, mapped to
///   M16 zOrder integers.</item>
/// </list></para>
///
/// <para><b>Thread-safety.</b> M15 dispatches <c>UI.*</c> events on a background thread,
/// while the WPF host drains <see cref="GetRenderList"/>/<see cref="PurgeExpired"/> on the
/// UI thread. All access to the active-HUD map is therefore guarded by a lock; view
/// callbacks are invoked outside the lock to avoid reentrancy deadlocks.</para>
/// </summary>
public sealed class OverlayCoordinator : IDisposable
{
    /// <summary>Data-driven <c>UI.*</c> event type → <see cref="HudType"/> mapping
    /// (spec Internal Logic step 2). Events outside this map are ignored.</summary>
    public static readonly IReadOnlyDictionary<string, HudType> TypeMap = new Dictionary<string, HudType>
    {
        ["UI.COMBO_RESULT"] = HudType.ComboResult,
        ["UI.ITEM_ALERT"]   = HudType.ItemAlert,
        ["UI.RECALL_TIMER"] = HudType.RecallTimer,
        ["UI.NOTIFICATION"] = HudType.Notification,
        ["UI.INHIBITOR_TIMER"] = HudType.InhibitorTimer,
        ["UI.GLOBAL_GOLD"] = HudType.GlobalGold,
        ["UI.ENEMY_JUNGLER_DEBUG"] = HudType.EnemyJunglerDebug, // M30 TEMPORARY debug panel
        ["UI.ENEMY_JUNGLER_SPOTTED_ALERT"] = HudType.EnemyJunglerSpottedAlert,
        ["UI.ENEMY_ITEM_ALERT"] = HudType.EnemyItemAlert,
    };

    /// <summary>M30: the real "적 정글 발견" alert shows for exactly 3s (user-specified), not the
    /// configurable <c>overlay.hudDisplayDuration</c> default every other toast uses — a fixed,
    /// short duration is part of this alert's own spec, not a user preference.</summary>
    private const int EnemyJunglerSpottedAlertDurationMs = 3000;

    /// <summary>Spec default HUD duration when neither the caller nor Config supplies one.</summary>
    private const int FallbackDurationMs = 4000;

    /// <summary>Sentinel duration marking a HUD as <em>persistent</em>: it never auto-expires
    /// and stays on screen until replaced (by a new HUD with the same id) or explicitly cleared
    /// (<see cref="ClearHud"/>/<see cref="ClearHudType"/>). The combo-result card uses this so a
    /// combo overlay behaves as a toggle rather than a timed toast. Any non-positive duration is
    /// treated as persistent by <see cref="ComputeExpiry"/>.</summary>
    public const int PersistentDurationMs = -1;

    private readonly IOverlayView _view;
    private readonly ConfigManager _config;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<HudType, int> _zOrder;

    private readonly object _gate = new();
    private readonly Dictionary<string, ActiveHud> _active = new();
    private long _seq;

    private string? _subscriptionId;
    private bool _dashboardOpen;

    private readonly struct ActiveHud
    {
        public readonly HUDPayload Payload;
        public readonly long ExpiryMs;
        public readonly int ZOrder;
        public readonly long Seq;

        public ActiveHud(HUDPayload payload, long expiryMs, int zOrder, long seq)
        {
            Payload = payload;
            ExpiryMs = expiryMs;
            ZOrder = zOrder;
            Seq = seq;
        }
    }

    public OverlayCoordinator(
        IOverlayView view,
        ConfigManager config,
        IClock? clock = null,
        IReadOnlyDictionary<HudType, int>? zOrderOverrides = null)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? new SystemClock();
        _zOrder = zOrderOverrides ?? DefaultZOrders;
    }

    // ── Event subscription (spec Internal Logic step 2) ──────────────────────────

    /// <summary>Subscribe to <c>UI.*</c> events, converting each into a shown HUD. Safe to
    /// call once; a second call while already started is a no-op.</summary>
    public void Start()
    {
        if (_subscriptionId is not null) return;
        _subscriptionId = EventBus.EventBus.Subscribe("UI.*", OnUiEvent);
    }

    private void OnUiEvent(Event evt)
    {
        if (!TryConvert(evt, out var payload)) return;

        // Combo-result cards are persistent (toggle-style): they stay until replaced by the next
        // combo trigger (same stable id) or explicitly cleared. Every other UI.* HUD keeps its
        // configured auto-hide.
        int? duration = payload.Type switch
        {
            HudType.ComboResult => PersistentDurationMs,
            HudType.EnemyJunglerSpottedAlert => EnemyJunglerSpottedAlertDurationMs,
            _ => null,
        };
        ShowHUD(payload, duration);
    }

    /// <summary>Convert a <c>UI.*</c> event into a <see cref="HUDPayload"/>. If the event's
    /// payload already IS a <see cref="HUDPayload"/> it is used verbatim; otherwise the
    /// event type is mapped to a <see cref="HudType"/> and the raw payload becomes the HUD
    /// content. Returns false for unmapped event types.</summary>
    public static bool TryConvert(Event evt, out HUDPayload payload)
    {
        if (evt.Payload is HUDPayload direct)
        {
            payload = direct;
            return true;
        }

        if (TypeMap.TryGetValue(evt.Type, out var type))
        {
            // Stable id keyed by event type (not a fresh Guid): a stream of the SAME kind of
            // event — e.g. a respawn countdown re-published every poll tick — REPLACES the one
            // active card in place and keeps its auto-hide fresh, instead of stacking dozens of
            // near-duplicate toasts. One live-updating card per UI.* type. (A producer that
            // needs several concurrent cards of one type publishes its own HUDPayload with a
            // distinct stable id — that path is handled above.)
            payload = new HUDPayload(evt.Type, type, evt.Payload, Position: null);
            return true;
        }

        payload = null!;
        return false;
    }

    // ── Interfaces (spec) ────────────────────────────────────────────────────────

    /// <summary>Show (or replace, by id) a HUD element. <paramref name="durationMs"/>
    /// defaults to M14 <c>overlay.hudDisplayDuration</c> when null. A null
    /// <see cref="HUDPayload.Position"/> is resolved to the Config default position.</summary>
    public void ShowHUD(HUDPayload payload, int? durationMs = null)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));

        int dur = durationMs ?? DefaultDurationMs();
        var resolved = payload.Position is null
            ? payload with { Position = DefaultPosition() }
            : payload;

        long now = _clock.NowMs;
        var entry = new ActiveHud(resolved, ComputeExpiry(now, dur), ZOrderFor(resolved.Type), NextSeq());

        lock (_gate)
        {
            _active[resolved.Id] = entry;
        }

        _view.ShowHud(resolved);
    }

    /// <summary>Remove a HUD element by id (before its auto-hide expiry). No-op if unknown.</summary>
    public void HideHUD(string hudId)
    {
        if (string.IsNullOrEmpty(hudId)) return;

        bool removed;
        lock (_gate)
        {
            removed = _active.Remove(hudId);
        }

        if (removed) _view.HideHud(hudId);
    }

    /// <summary>Explicitly remove an active HUD by id and notify the view — the clear path for a
    /// persistent (combo) card, e.g. a future toggle-off or game-disconnect. Removes under the
    /// lock, then fires the view hide callback outside the lock (same reentrancy pattern as
    /// <see cref="PurgeExpired"/>). No-op if the id is unknown.</summary>
    public void ClearHud(string id)
    {
        if (string.IsNullOrEmpty(id)) return;

        bool removed;
        lock (_gate)
        {
            removed = _active.Remove(id);
        }

        if (removed) _view.HideHud(id);
    }

    /// <summary>Clear every active HUD of a given type (e.g. all combo cards on disconnect) and
    /// notify the view. Removes under the lock, then fires the view hide callbacks outside the
    /// lock. Returns the removed ids (empty if none matched).</summary>
    public IReadOnlyList<string> ClearHudType(HudType type)
    {
        List<string> removed;
        lock (_gate)
        {
            removed = _active.Values
                .Where(h => h.Payload.Type == type)
                .Select(h => h.Payload.Id)
                .ToList();

            foreach (var id in removed) _active.Remove(id);
        }

        foreach (var id in removed) _view.HideHud(id);
        return removed;
    }

    /// <summary>Open the dashboard (settings/combo manager). Idempotent.</summary>
    public void OpenDashboard()
    {
        if (_dashboardOpen) return;
        _dashboardOpen = true;
        _view.OpenDashboard();
    }

    /// <summary>Close the dashboard. Idempotent.</summary>
    public void CloseDashboard()
    {
        if (!_dashboardOpen) return;
        _dashboardOpen = false;
        _view.CloseDashboard();
    }

    public bool IsDashboardOpen => _dashboardOpen;

    public int ActiveCount
    {
        get { lock (_gate) { return _active.Count; } }
    }

    /// <summary>True if a HUD with the given stable id is currently active (shown and not yet
    /// expired/cleared). For a <c>UI.*</c>-event-derived card the id is the event type itself
    /// (spec v1.3 changelog; e.g. <c>"UI.COMBO_RESULT"</c>), so a caller who knows the event
    /// type it publishes can check whether its own card is still on screen — the toggle-off
    /// wiring point for M13/M04's "press the same combo's hotkey again to hide it" request.</summary>
    public bool IsActive(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        lock (_gate) { return _active.ContainsKey(id); }
    }

    // ── Auto-hide scheduling (spec Internal Logic step 3) ────────────────────────

    /// <summary>Pure expiry computation: the wall-clock ms at which a HUD shown at
    /// <paramref name="shownAtMs"/> for <paramref name="durationMs"/> should be removed. A
    /// non-positive duration (e.g. <see cref="PersistentDurationMs"/>) yields
    /// <see cref="long.MaxValue"/> — the HUD never expires and is never returned by
    /// <see cref="GetExpired"/>/<see cref="PurgeExpired"/>.</summary>
    public static long ComputeExpiry(long shownAtMs, int durationMs)
        => durationMs <= 0 ? long.MaxValue : shownAtMs + durationMs;

    /// <summary>Ids of every HUD whose expiry is at or before <paramref name="nowMs"/>,
    /// without mutating state. Deterministic (ordered by expiry then enqueue sequence) so
    /// the auto-hide boundary is unit-testable with a virtual clock.</summary>
    public IReadOnlyList<string> GetExpired(long nowMs)
    {
        lock (_gate)
        {
            return _active.Values
                .Where(h => h.ExpiryMs <= nowMs)
                .OrderBy(h => h.ExpiryMs).ThenBy(h => h.Seq)
                .Select(h => h.Payload.Id)
                .ToList();
        }
    }

    /// <summary>Remove every HUD expired as of <paramref name="nowMs"/> and notify the view.
    /// Called each frame by the WPF host with the real clock; called with a fake clock in
    /// tests. Returns the removed ids.</summary>
    public IReadOnlyList<string> PurgeExpired(long nowMs)
    {
        List<string> expired;
        lock (_gate)
        {
            expired = _active.Values
                .Where(h => h.ExpiryMs <= nowMs)
                .OrderBy(h => h.ExpiryMs).ThenBy(h => h.Seq)
                .Select(h => h.Payload.Id)
                .ToList();

            foreach (var id in expired) _active.Remove(id);
        }

        foreach (var id in expired) _view.HideHud(id);
        return expired;
    }

    // ── Z-Order priority (spec Internal Logic step 4) ────────────────────────────

    /// <summary>Default priorities: combo result &gt; notification &gt; jungle track (spec).
    /// Item alerts, recall timers, the M19 inhibitor timer, and the M19 global-gold estimate
    /// — unranked by the spec — sit between combo and notification. Higher value draws on
    /// top (larger M16 zOrder).</summary>
    public static int DefaultZOrder(HudType type) => type switch
    {
        HudType.ComboResult     => 100,
        HudType.ItemAlert       => 80,
        HudType.RecallTimer     => 60,
        HudType.InhibitorTimer  => 50,
        HudType.GlobalGold      => 45,
        HudType.Notification    => 40,
        HudType.EnemyJunglerDebug => 35, // M30 TEMPORARY debug panel
        HudType.EnemyJunglerSpottedAlert => 90, // urgent — draws over nearly everything
        HudType.EnemyItemAlert  => 82, // just above the ally ItemAlert (80), below the spotted alert
        HudType.EnemyPresence   => 42, // the enemy appear/disappear toast (replaces the plain notification)
        _ => 0,
    };

    private static readonly IReadOnlyDictionary<HudType, int> DefaultZOrders =
        Enum.GetValues<HudType>().ToDictionary(t => t, DefaultZOrder);

    private int ZOrderFor(HudType type) => _zOrder.TryGetValue(type, out var z) ? z : DefaultZOrder(type);

    /// <summary>Active HUDs ordered for rendering: ascending zOrder (higher priority last =
    /// drawn on top under M16's painter's-algorithm), ties broken by show order. This is the
    /// list the WPF host enqueues into M16's <c>RenderQueue</c>.</summary>
    public IReadOnlyList<HudRenderItem> GetRenderList()
    {
        lock (_gate)
        {
            return _active.Values
                .OrderBy(h => h.ZOrder).ThenBy(h => h.Seq)
                .Select(h => new HudRenderItem(h.Payload, h.ZOrder, h.ExpiryMs))
                .ToList();
        }
    }

    // ── Config-sourced defaults (M14) ────────────────────────────────────────────

    private int DefaultDurationMs()
    {
        double seconds = GetDouble("overlay.hudDisplayDuration", double.NaN);
        if (double.IsNaN(seconds) || seconds <= 0) return FallbackDurationMs;
        return (int)Math.Round(seconds * 1000);
    }

    private HudPosition DefaultPosition()
        => new(GetDouble("overlay.position.x", 0), GetDouble("overlay.position.y", 0));

    private double GetDouble(string key, double fallback)
    {
        try
        {
            return _config.Get(key) is { } v ? Convert.ToDouble(v) : fallback;
        }
        catch (Exception e) when (e is InvalidCastException or FormatException)
        {
            return fallback;
        }
    }

    private long NextSeq() => Interlocked.Increment(ref _seq);

    public void Dispose()
    {
        if (_subscriptionId is not null)
        {
            EventBus.EventBus.Unsubscribe(_subscriptionId);
            _subscriptionId = null;
        }
    }
}

/// <summary>One active HUD paired with its resolved M16 zOrder — the render-ready item
/// the WPF host enqueues each frame.</summary>
/// <summary><see cref="ExpiryMs"/> (Unix-epoch ms; <see cref="long.MaxValue"/> for a persistent
/// HUD) lets a renderer compute a fade-out as a HUD nears its auto-hide time (M30's "3초 후
/// 페이드아웃" alert) — defaults to persistent so pre-existing 2-arg construction stays
/// source-compatible.</summary>
public readonly record struct HudRenderItem(HUDPayload Payload, int ZOrder, long ExpiryMs = long.MaxValue);
