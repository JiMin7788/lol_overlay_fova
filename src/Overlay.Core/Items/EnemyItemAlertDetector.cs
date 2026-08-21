using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;

namespace Overlay.Core.Items;

/// <summary>
/// Detects when an <b>enemy</b> champion completes a new legendary item and raises a HUD + TTS
/// alert carrying the champion + item so the overlay can show both icons.
///
/// <para><b>Structural premise (same as <see cref="EnemyJunglerSpottedDetector"/>):</b> the Live
/// Client API only refreshes an enemy row's data while that enemy is <i>visible</i>, so an observed
/// change in an enemy's item list already implies "the enemy was just in sight" — no coupling to the
/// image-based minimap pipeline is needed. This directly realizes the user request: "적이 시야에
/// 잡힐 때 아이템 변경 여부 확인 후 이전 대상 데이터에 없던 새로운 전설 아이템 감지 시 알림."</para>
///
/// <para><b>Enemy-only.</b> Ally item completions are already handled by <see cref="ItemTracker"/>
/// (<c>UI.ITEM_ALERT</c>). This detector resolves the active player's team and processes only rows on
/// the OTHER team; when the active team can't be resolved it stays silent (never guesses).</para>
///
/// <para><b>Legendary rule.</b> Reuses <see cref="ItemTracker"/>'s build-tree completion heuristic —
/// built from components AND nothing further builds from it — and additionally excludes boots
/// (<see cref="ItemData.IsBoots"/>), per the user's "완성템(보신 제외)" definition. No explicit
/// "legendary" tier exists in the Data Dragon data.</para>
///
/// <para><b>Baseline diff.</b> Keeps a per-enemy previous-item multiset. The FIRST time an enemy is
/// observed only establishes the baseline (emits nothing) — an alert requires a prior tick to diff
/// against, so an enemy already holding a legendary when first seen does not spuriously alert. Same
/// multiset-difference approach as <see cref="ItemTracker"/> (a pure slot re-sort yields nothing).</para>
/// </summary>
public sealed class EnemyItemAlertDetector : IDisposable
{
    private const string Source = "EnemyItemAlert";

    private readonly Func<GameSnapshot?> _currentSnapshot;
    private readonly IClock _clock;
    private readonly object _gate = new();

    // enemy championName -> last observed itemIds (baseline for the next diff).
    private readonly Dictionary<string, int[]> _previousItems = new(StringComparer.Ordinal);

    private string? _itemSubId;

    /// <param name="currentSnapshot">Latest polled snapshot, e.g. <c>() => AppComposition.LatestSnapshot</c>
    /// (mirrors <see cref="EnemyJunglerSpottedDetector"/>).</param>
    /// <param name="clock">Time source for the TTS request timestamp (defaults to the system clock).</param>
    public EnemyItemAlertDetector(Func<GameSnapshot?> currentSnapshot, IClock? clock = null)
    {
        _currentSnapshot = currentSnapshot ?? throw new ArgumentNullException(nameof(currentSnapshot));
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Subscribe to the M01 item event this detector consumes. Idempotent.</summary>
    public void Start()
    {
        if (_itemSubId is not null) return;
        _itemSubId = EventBus.EventBus.Subscribe("GAME.ITEM_CHANGED", OnItemChanged);
    }

    private void OnItemChanged(Event evt)
    {
        if (evt.Payload is not ItemChangedPayload payload) return;
        if (string.IsNullOrEmpty(payload.ChampionName)) return;

        var snap = _currentSnapshot();
        if (snap is null || !snap.HasData) return;

        // Enemy-only: resolve the active player's team, then this row's team. Skip allies, and skip
        // when either can't be resolved (no guessing — P2).
        string activeTeam = FindActiveTeam(snap);
        if (activeTeam.Length == 0) return;
        string? rowTeam = TeamOf(snap, payload.ChampionName);
        if (rowTeam is null || string.Equals(rowTeam, activeTeam, StringComparison.Ordinal)) return;

        var currentIds = new int[payload.Items.Length];
        for (int i = 0; i < payload.Items.Length; i++) currentIds[i] = payload.Items[i].ItemId;

        List<int>? newlyAppeared = null;
        lock (_gate)
        {
            if (_previousItems.TryGetValue(payload.ChampionName, out var previous))
                newlyAppeared = MultisetDifference(previous, currentIds);
            // else: first observation for this enemy — establish a baseline, emit nothing.
            _previousItems[payload.ChampionName] = currentIds;
        }

        if (newlyAppeared is null) return;

        foreach (var id in newlyAppeared)
        {
            var item = ItemRepository.Get(id.ToString());
            if (item is null || !IsLegendary(item)) continue;
            Raise(payload.ChampionName, item);
        }
    }

    /// <summary>Legendary = completed (built from components AND nothing builds from it), excluding
    /// boots — the user's "완성템(보신 제외)" definition. Mirrors <see cref="ItemTracker"/>'s rule.</summary>
    private static bool IsLegendary(ItemData item)
        => item.BuildsFrom.Count > 0 && item.BuildsInto.Count == 0 && !item.IsBoots;

    private void Raise(string championName, ItemData item)
    {
        string itemName = string.IsNullOrEmpty(item.NameKo) ? item.Name : item.NameKo!;

        // Distinct stable id per (champion, item) so several enemy-item cards can stack, and a
        // re-publish of the SAME completion replaces its own card in place (OverlayCoordinator doc).
        var content = new EnemyItemAlert(championName, item.Id, itemName);
        var hud = new HUDPayload($"UI.ENEMY_ITEM_ALERT:{championName}:{item.Id}", HudType.EnemyItemAlert, content);
        EventBus.EventBus.Publish("UI.ENEMY_ITEM_ALERT", hud, Source);

        string message = $"적 {championName} {itemName} 완성";
        var speech = new SpeechRequest(Guid.NewGuid().ToString(), message, SpeechPriority.Normal,
            _clock.NowMs, CooldownKey: $"enemy-item:{championName}:{item.Id}");
        EventBus.EventBus.Publish("VOICE.SPEAK", speech, Source);
    }

    /// <summary>Item ids present in <paramref name="current"/> beyond what <paramref name="previous"/>
    /// already accounted for (multiset difference). A pure re-sort yields an empty result.</summary>
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

    /// <summary>Active player's team ("ORDER"/"CHAOS"), matched by the reliable riotId then the legacy
    /// summoner name. Empty string when unresolved. Mirrors <c>GoldEstimate.FindActiveTeam</c>.</summary>
    private static string FindActiveTeam(GameSnapshot snap)
    {
        if (snap.ActivePlayerRiotId.Length > 0)
        {
            for (int i = 0; i < snap.PlayerCount; i++)
            {
                ScoreboardEntry p = snap.Players[i];
                if (p.RiotId.Length > 0 && string.Equals(p.RiotId, snap.ActivePlayerRiotId, StringComparison.Ordinal))
                    return p.Team;
            }
        }
        if (snap.ActivePlayerSummonerName.Length > 0)
        {
            for (int i = 0; i < snap.PlayerCount; i++)
            {
                ScoreboardEntry p = snap.Players[i];
                if (p.SummonerName.Length > 0 && string.Equals(p.SummonerName, snap.ActivePlayerSummonerName, StringComparison.Ordinal))
                    return p.Team;
            }
        }
        return string.Empty;
    }

    /// <summary>The team of the scoreboard row whose champion matches <paramref name="championName"/>,
    /// or null when no such row exists this tick.</summary>
    private static string? TeamOf(GameSnapshot snap, string championName)
    {
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            ScoreboardEntry p = snap.Players[i];
            if (string.Equals(p.ChampionName, championName, StringComparison.Ordinal)) return p.Team;
        }
        return null;
    }

    public void Dispose()
    {
        if (_itemSubId is not null) { EventBus.EventBus.Unsubscribe(_itemSubId); _itemSubId = null; }
    }
}
