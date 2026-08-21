using Overlay.Core.EventBus;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;

namespace Overlay.Core;

/// <summary>
/// M30 Internal Logic #2-5 (docs/modules/M30_ENEMY_JUNGLER_SPOTTED.md): subscribes to M01's
/// <c>GAME.ITEM_CHANGED</c> / <c>GAME.CS_CHANGED</c> / <c>GAME.PLAYER_LEVEL_UP</c>, and — for the
/// row <see cref="EnemyJunglerLocator"/> currently identifies as the enemy jungler — raises a
/// single "적 정글 발견" alert (HUD + TTS) per real change. No coordinates/direction are ever
/// included (that inference was already rejected for <c>JungleTracker</c>, P2).
///
/// <para><b>Structural premise (user-confirmed, not this class's own inference):</b> a CS/item/level
/// change on that row means the Live Client's data for it just refreshed, which only happens while
/// the player is visible — so "change detected" is treated as "spotted", not an estimate. (2026-07:
/// user verified via the raw API that <c>GAME.CS_CHANGED</c> specifically lags behind real play —
/// Riot appears to intentionally throttle/batch the enemy team's creepScore, likely an anti-scripting
/// measure against exactly this kind of vision-inference. Kept as a trigger since it is still a real,
/// eventual field-level change — just a weaker/slower signal than item or level changes.)</para>
///
/// <para><b>Dedup.</b> CS/item/level can all change in the SAME polling tick (same
/// <see cref="GameSnapshot.GameTime"/>) — e.g. a canceled recall right after a camp clear. Only one
/// alert is raised per distinct game-time tick (Acceptance Criteria: "동일 틱에 CS와 아이템이 동시에
/// 바뀌어도 알림은 정확히 1개만 발생"). Separate ticks (even seconds apart) each raise their own alert;
/// long-window spam suppression is left to M09's `voice.ttsCooldownSeconds` on the HUD/TTS channel,
/// same as every other alert in this codebase (see <see cref="Items.ItemTracker"/>).</para>
/// </summary>
public sealed class EnemyJunglerSpottedDetector : IDisposable
{
    private const string Source = "M30.EnemyJunglerSpotted";
    private const string CooldownKey = "enemy-jungler-spotted";

    private readonly Func<GameSnapshot?> _currentSnapshot;
    private readonly IClock _clock;
    private readonly object _gate = new();

    private string? _itemSubId;
    private string? _csSubId;
    private string? _levelSubId;
    private double _lastAlertGameTime = double.NegativeInfinity;

    /// <param name="currentSnapshot">Latest polled snapshot, e.g. <c>() => AppComposition.LatestSnapshot</c>
    /// (mirrors <see cref="Combo.ComboRunner"/>'s constructor convention).</param>
    /// <param name="clock">Time source for the TTS request timestamp (defaults to the system clock).</param>
    public EnemyJunglerSpottedDetector(Func<GameSnapshot?> currentSnapshot, IClock? clock = null)
    {
        _currentSnapshot = currentSnapshot ?? throw new ArgumentNullException(nameof(currentSnapshot));
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Subscribe to the M01 events this detector consumes. Idempotent.</summary>
    public void Start()
    {
        if (_itemSubId is not null) return;
        _itemSubId = EventBus.EventBus.Subscribe("GAME.ITEM_CHANGED", evt => OnChanged(ChampionNameOf<ItemChangedPayload>(evt, p => p.ChampionName)));
        _csSubId = EventBus.EventBus.Subscribe("GAME.CS_CHANGED", evt => OnChanged(ChampionNameOf<CreepScoreChangedPayload>(evt, p => p.ChampionName)));
        _levelSubId = EventBus.EventBus.Subscribe("GAME.PLAYER_LEVEL_UP", evt => OnChanged(ChampionNameOf<PlayerLevelUpPayload>(evt, p => p.ChampionName)));
    }

    private static string? ChampionNameOf<TPayload>(Event evt, Func<TPayload, string> selector)
        => evt.Payload is TPayload payload ? selector(payload) : null;

    private void OnChanged(string? championName)
    {
        if (string.IsNullOrEmpty(championName)) return;

        var snap = _currentSnapshot();
        if (snap is null || !snap.HasData) return;

        var enemyJungler = EnemyJunglerLocator.Find(snap);
        if (enemyJungler is null) return; // no position data this game mode — no guess (P2)
        if (!string.Equals(enemyJungler.ChampionName, championName, StringComparison.Ordinal)) return;

        lock (_gate)
        {
            if (snap.GameTime == _lastAlertGameTime) return; // same tick already alerted (dedup)
            _lastAlertGameTime = snap.GameTime;
        }

        Raise(championName);
    }

    private void Raise(string championName)
    {
        string message = $"적 정글이 감지되었습니다 ({championName})";

        EventBus.EventBus.Publish("UI.ENEMY_JUNGLER_SPOTTED_ALERT", message, Source);

        var speech = new SpeechRequest(Guid.NewGuid().ToString(), message, SpeechPriority.Normal, _clock.NowMs, CooldownKey);
        EventBus.EventBus.Publish("VOICE.SPEAK", speech, Source);
    }

    public void Dispose()
    {
        if (_itemSubId is not null) { EventBus.EventBus.Unsubscribe(_itemSubId); _itemSubId = null; }
        if (_csSubId is not null) { EventBus.EventBus.Unsubscribe(_csSubId); _csSubId = null; }
        if (_levelSubId is not null) { EventBus.EventBus.Unsubscribe(_levelSubId); _levelSubId = null; }
    }
}
