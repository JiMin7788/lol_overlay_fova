using Overlay.Core.EventBus;

namespace Overlay.Core.Gold;

/// <summary>
/// M19 §3.3 Global Gold Compare: turns the poller's <see cref="GameSnapshot"/> into a
/// <see cref="GoldEstimate.TryCompute"/> team-gold comparison and publishes it as a
/// <c>UI.GLOBAL_GOLD</c> toast. Mirrors <see cref="Inhibitor.InhibitorTimer"/>'s
/// Start()/Dispose() subscribe-unsubscribe shape.
///
/// <para><b>Item-event-driven recompute (M07 "Pending User-Reported Changes"):</b> the estimate
/// only meaningfully changes when a player buys/sells an item, so this no longer recomputes on
/// every poll tick. It subscribes to M07's own <c>GAME.ITEM_CHANGED</c> (fired for ANY visible
/// scoreboard player's item slots, not just the active player — see
/// <see cref="LiveClientEventPublisher.PublishPerPlayerChanges"/>) and only ARMS a recompute
/// flag there. It still subscribes to <see cref="LiveClientPoller.SnapshotAvailable"/> too —
/// but only to consume that flag against the tick's fresh, FULL scoreboard snapshot (no single
/// GAME.* payload carries the whole scoreboard <see cref="GoldEstimate.TryCompute"/> needs), not
/// to recompute unconditionally on every tick. Since <see cref="LiveClientEventPublisher"/> is
/// wired to the poller before this panel (see <c>AppComposition</c>) and GAME.* Event Bus
/// delivery to a synchronous subscriber happens on the publisher's own call stack, the ARM
/// (via GAME.ITEM_CHANGED, published from inside the publisher's own SnapshotAvailable handler)
/// always happens before this panel's own SnapshotAvailable handler runs in the same tick — so
/// the consumed snapshot is always the same tick's fresh data, never stale.</para>
///
/// <para>"점수판 켰을 때만" (M19 §3.3) cannot be detected without a keyboard hook (P3 forbids
/// a global keylogger for the game's Tab state), so — per the spec's own documented
/// alternative — this always publishes while the item is enabled (gated by
/// <c>overlay.items.globalGold.enabled</c> in <c>AppComposition</c>), not just while Tab is
/// held.</para>
/// </summary>
public sealed class GlobalGoldPanel : IDisposable
{
    private const string Source = "M19.GlobalGoldPanel";

    private readonly LiveClientPoller _poller;
    private bool _subscribed;
    private string? _itemSubId;

    /// <summary>Set by an item-change event, consumed (and cleared) by the next
    /// SnapshotAvailable tick. Recompute only happens when this is true — see class doc.</summary>
    private bool _pendingRecompute;

    public GlobalGoldPanel(LiveClientPoller poller)
        => _poller = poller ?? throw new ArgumentNullException(nameof(poller));

    public void Start()
    {
        if (_subscribed) return;
        _itemSubId = EventBus.EventBus.Subscribe("GAME.ITEM_CHANGED", OnItemChanged);
        _poller.SnapshotAvailable += OnSnapshot;
        _subscribed = true;
    }

    /// <summary>Arms the recompute flag. Does not itself compute or publish anything — the
    /// payload only carries ONE player's item slots, not the full scoreboard.</summary>
    private void OnItemChanged(Event evt)
    {
        if (evt.Payload is not ItemChangedPayload) return;
        _pendingRecompute = true;
    }

    private void OnSnapshot(GameSnapshot previous, GameSnapshot current, bool isInitialSync)
    {
        if (!_pendingRecompute) return;
        _pendingRecompute = false;

        if (!current.HasData) return;
        if (!GoldEstimate.TryCompute(current, out var result)) return;

        EventBus.EventBus.Publish("UI.GLOBAL_GOLD", BuildMessage(result), Source);
    }

    /// <summary>"(추정)" is on every line — P2: never let this read as confirmed gold.</summary>
    internal static string BuildMessage(TeamGoldEstimate r)
    {
        string sign = r.Diff >= 0 ? "+" : "";
        return string.Join(Environment.NewLine,
            "글로벌 골드 비교 (추정)",
            $"우리 팀: {r.AllyGold:0}g",
            $"상대 팀: {r.EnemyGold:0}g",
            $"차이: {sign}{r.Diff:0}g");
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        _poller.SnapshotAvailable -= OnSnapshot;
        if (_itemSubId is not null) { EventBus.EventBus.Unsubscribe(_itemSubId); _itemSubId = null; }
        _subscribed = false;
    }
}
