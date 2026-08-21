using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;

namespace Overlay.Core.Items;

/// <summary>
/// The testable heart of M07 Item Tracker (docs/modules/M07_ITEM_TRACKER.md). It subscribes
/// to M01's <c>GAME.ITEM_CHANGED</c> / <c>GAME.GOLD_UPDATED</c> events, diffs the tracked
/// player's item slots to detect finished items, and computes exact missing gold toward a
/// user-selected target item — with <b>no display / audio / network dependency</b>.
///
/// <para><b>Policy.</b> It reads ONLY the item slots the Live Client API actually provides via
/// <see cref="ItemChangedPayload"/> (which M01 emits only for players the API exposes). It never
/// infers hidden or enemy item information (P1/P2). All item cost / build-tree data comes from
/// M11's <see cref="ItemRepository"/>, which is sourced exclusively from official Data Dragon —
/// no third-party stat crawling.</para>
///
/// <para><b>Item-completion rule.</b> An item counts as "completed/finished" when it is built
/// from components AND nothing further builds from it — i.e.
/// <see cref="ItemData.BuildsFrom"/> is non-empty and <see cref="ItemData.BuildsInto"/> is empty
/// (e.g. Rabadon's Deathcap). A raw component (Needlessly Large Rod: builds-from empty) is not a
/// completion.</para>
///
/// <para><b>Diffing.</b> New items are found by a <i>multiset</i> difference of current vs previous
/// itemIds (per Agent Implementation Notes: slot-index compare cross-checked by itemId set). A mere
/// slot re-sort yields the same multiset, so it produces zero "newly appeared" items and cannot
/// raise a false completion.</para>
/// </summary>
public sealed class ItemTracker : IDisposable
{
    private const string Source = "M07.ItemTracker";

    private readonly IClock _clock;
    private readonly object _gate = new();

    // championName -> last observed itemIds (baseline for the next diff).
    private readonly Dictionary<string, int[]> _previousItems = new();
    private readonly Dictionary<string, Action<ItemAlert>> _callbacks = new();

    private string? _targetItemId;
    private int _lastMilestoneBucket = int.MaxValue;

    private string? _itemSubId;
    private string? _goldSubId;

    /// <param name="clock">Time source for alert timestamps (defaults to the system clock).</param>
    public ItemTracker(IClock? clock = null) => _clock = clock ?? new SystemClock();

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    /// <summary>Subscribe to the M01 game events this tracker consumes. Idempotent.</summary>
    public void Start()
    {
        if (_itemSubId is not null) return;
        _itemSubId = EventBus.EventBus.Subscribe("GAME.ITEM_CHANGED", OnItemChangedEvent);
        _goldSubId = EventBus.EventBus.Subscribe("GAME.GOLD_UPDATED", OnGoldUpdatedEvent);
    }

    // ── Interfaces (spec) ────────────────────────────────────────────────────────

    /// <summary>Register a callback fired for every <see cref="ItemAlert"/> this tracker raises.
    /// Returns a subscription id for <see cref="Unsubscribe"/>.</summary>
    public string OnItemChanged(Action<ItemAlert> callback)
    {
        if (callback is null) throw new ArgumentNullException(nameof(callback));
        var id = Guid.NewGuid().ToString();
        lock (_gate) { _callbacks[id] = callback; }
        return id;
    }

    /// <summary>Remove a callback. No-op if the id is unknown.</summary>
    public void Unsubscribe(string subscriptionId)
    {
        if (string.IsNullOrEmpty(subscriptionId)) return;
        lock (_gate) { _callbacks.Remove(subscriptionId); }
    }

    /// <summary>Exact missing gold toward <paramref name="targetItemId"/> =
    /// <c>max(0, GoldTotal - currentGold)</c> using the Data Dragon price from M11.
    /// <paramref name="championId"/> is part of the spec signature (reserved for the V2 build
    /// advisor) and is not used by this exact-price calculation.
    /// Throws <see cref="ArgumentException"/> for an unknown item id (documented behavior).</summary>
    public int GetMissingGold(string championId, string targetItemId, int currentGold)
    {
        var item = ItemRepository.Get(targetItemId)
            ?? throw new ArgumentException($"Unknown item id '{targetItemId}'.", nameof(targetItemId));
        return Math.Max(0, item.GoldTotal - currentGold);
    }

    /// <summary>Set the user-selected target item to track gold progress toward. The next gold
    /// update re-arms the milestone announcements.</summary>
    public void SetTarget(string championId, string targetItemId)
    {
        lock (_gate)
        {
            _targetItemId = targetItemId;
            _lastMilestoneBucket = int.MaxValue; // re-arm: next gold update announces immediately
        }
    }

    // ── Item-completion detection (spec Internal Logic #1-2) ─────────────────────

    private void OnItemChangedEvent(Event evt)
    {
        if (evt.Payload is not ItemChangedPayload payload) return;

        var currentIds = new int[payload.Items.Length];
        for (int i = 0; i < payload.Items.Length; i++) currentIds[i] = payload.Items[i].ItemId;

        List<int>? newlyAppeared = null;
        lock (_gate)
        {
            if (_previousItems.TryGetValue(payload.ChampionName, out var previous))
                newlyAppeared = MultisetDifference(previous, currentIds);
            // else: first observation for this player — establish a baseline, emit nothing
            // (we cannot know what is "newly appeared" without a prior tick to diff against).
            _previousItems[payload.ChampionName] = currentIds;
        }

        if (newlyAppeared is null) return;

        foreach (var id in newlyAppeared)
        {
            var item = ItemRepository.Get(id.ToString());
            if (item is null || !IsCompleted(item)) continue;
            RaiseAlert(new ItemAlert(ItemAlertType.ItemCompleted, $"{item.Name} completed", _clock.NowMs));
        }
    }

    /// <summary>Completion rule: built from components AND nothing further builds from it.</summary>
    private static bool IsCompleted(ItemData item)
        => item.BuildsFrom.Count > 0 && item.BuildsInto.Count == 0;

    /// <summary>Item ids present in <paramref name="current"/> beyond what
    /// <paramref name="previous"/> already accounted for (multiset difference). A pure re-sort
    /// of the same ids yields an empty result.</summary>
    private static List<int> MultisetDifference(int[] previous, int[] current)
    {
        var prevCounts = new Dictionary<int, int>();
        foreach (var id in previous)
            prevCounts[id] = prevCounts.GetValueOrDefault(id) + 1;

        var appeared = new List<int>();
        foreach (var id in current)
        {
            if (prevCounts.TryGetValue(id, out var c) && c > 0) prevCounts[id] = c - 1;
            else appeared.Add(id);
        }
        return appeared;
    }

    // ── Gold milestones (spec Internal Logic #3) ─────────────────────────────────

    private void OnGoldUpdatedEvent(Event evt)
    {
        if (evt.Payload is not GoldUpdatedPayload payload) return;

        string targetItemId;
        lock (_gate)
        {
            if (_targetItemId is null) return;
            targetItemId = _targetItemId;
        }

        var target = ItemRepository.Get(targetItemId);
        if (target is null) return;

        int missing = Math.Max(0, target.GoldTotal - (int)payload.CurrentGold);
        if (missing == 0) return; // target already affordable — nothing to announce toward it

        int bucket = missing / 100;
        lock (_gate)
        {
            // Announce only when a NEW (closer) 100-gold milestone is reached. The stable
            // cooldownKey below lets M09 additionally suppress any residual repeats.
            if (bucket >= _lastMilestoneBucket) return;
            _lastMilestoneBucket = bucket;
        }

        var alert = new ItemAlert(
            ItemAlertType.GoldMilestone, $"Need {missing} gold for {target.Name}", _clock.NowMs);
        RaiseAlert(alert);

        // Route the spoken milestone through M09 with a stable cooldownKey so repeats don't spam
        // (Acceptance #3). M09's TtsScheduler.OnVoiceEvent applies the cooldown — no tight coupling.
        var speech = new SpeechRequest(
            Guid.NewGuid().ToString(), alert.Message, SpeechPriority.Normal, alert.Timestamp,
            CooldownKey: $"gold-milestone:{targetItemId}");
        EventBus.EventBus.Publish("VOICE.SPEAK", speech, Source);
    }

    // ── Alert fan-out ────────────────────────────────────────────────────────────

    /// <summary>Deliver an alert to every registered callback AND publish it to the HUD via
    /// <c>UI.ITEM_ALERT</c> (a type already mapped by M02's overlay coordinator). Callbacks are
    /// invoked outside the lock to avoid reentrancy.</summary>
    private void RaiseAlert(ItemAlert alert)
    {
        Action<ItemAlert>[] callbacks;
        lock (_gate) { callbacks = _callbacks.Values.ToArray(); }

        foreach (var cb in callbacks) cb(alert);

        EventBus.EventBus.Publish("UI.ITEM_ALERT", alert.Message, Source);
    }

    public void Dispose()
    {
        if (_itemSubId is not null) { EventBus.EventBus.Unsubscribe(_itemSubId); _itemSubId = null; }
        if (_goldSubId is not null) { EventBus.EventBus.Unsubscribe(_goldSubId); _goldSubId = null; }
    }
}
