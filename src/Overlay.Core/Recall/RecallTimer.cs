using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;

namespace Overlay.Core.Recall;

/// <summary>
/// The testable heart of M08 Recall Timer (docs/modules/M08_RECALL_TIMER.md), with <b>no
/// display / audio / network dependency</b>. It turns M01's <c>GAME.CHAMPION_DIED</c> into a
/// Death-Timer countdown and computes the average-move-speed-based return ETA.
///
/// <para><b>Death Timer (exact).</b> On a death it builds a <see cref="DeathEvent"/> carrying
/// the payload's <see cref="ChampionDiedPayload.RespawnTimer"/> <i>verbatim</i> — no correction,
/// rounding, or added latency (Reviewer Checklist #2 / Acceptance "0.2s"). The countdown message
/// it publishes is the exact API value.</para>
///
/// <para><b>ETA (estimate).</b> <see cref="GetETA"/> is pure arithmetic on confirmed inputs
/// (fixed preset distance ÷ supplied average move speed, optionally + remaining respawn time).
/// It does NOT track an out-of-vision champion's live position; the result is explicitly labelled
/// an estimate (<see cref="ReturnETA.Message"/>) so it is never presented as confirmed info
/// (spec Policy Checklist — a UI-label requirement, not a P2 violation).</para>
///
/// <para>Alert delivery is decoupled: fan-out goes to registered callbacks AND to the M15 bus
/// (<c>UI.RECALL_TIMER</c> for the HUD, <c>VOICE.SPEAK</c> for an optional spoken countdown).
/// No concrete reference to M02/M09 beyond the shared <see cref="SpeechRequest"/> + bus.</para>
/// </summary>
public sealed class RecallTimer : IDisposable
{
    private const string Source = "M08.RecallTimer";

    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Action<DeathEvent>> _callbacks = new();

    private string? _deathSubId;

    /// <param name="clock">Time source for <see cref="DeathEvent.DeathTimestamp"/>
    /// (defaults to the system clock).</param>
    public RecallTimer(IClock? clock = null) => _clock = clock ?? new SystemClock();

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    /// <summary>Subscribe to M01's <c>GAME.CHAMPION_DIED</c>. Idempotent.</summary>
    public void Start()
    {
        if (_deathSubId is not null) return;
        _deathSubId = EventBus.EventBus.Subscribe("GAME.CHAMPION_DIED", OnChampionDied);
    }

    // ── Interfaces (spec) ────────────────────────────────────────────────────────

    /// <summary>Register a callback fired for every <see cref="DeathEvent"/> this timer raises.
    /// Returns a subscription id for <see cref="Unsubscribe"/>.</summary>
    public string OnDeath(Action<DeathEvent> callback)
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

    /// <summary>
    /// Estimated return-to-lane time. Travel time = <paramref name="distanceToLane"/> ÷
    /// effective move speed; when <paramref name="remainingRespawnSeconds"/> &gt; 0 it is added
    /// on top (the champion is still dead). Effective move speed is
    /// <paramref name="averageMoveSpeed"/> when positive, else the champion's base MS from M11
    /// (<see cref="ChampionRepository"/>) as a fallback default; if neither is available the
    /// speed guard throws <see cref="ArgumentException"/>.
    ///
    /// <para><see cref="ReturnETA.Basis"/> is <see cref="EtaBasis.RespawnPlusTravel"/> when a
    /// remaining respawn time is folded in, otherwise <see cref="EtaBasis.RespawnTimerOnly"/>.
    /// The result is an ESTIMATE (labelled via <see cref="ReturnETA.Message"/>), never a live
    /// position track.</para>
    /// </summary>
    public ReturnETA GetETA(
        string championId,
        double averageMoveSpeed,
        double distanceToLane,
        double remainingRespawnSeconds = 0)
    {
        double moveSpeed = averageMoveSpeed > 0
            ? averageMoveSpeed
            : ChampionRepository.Get(championId)?.BaseStats.Ms ?? 0;

        if (moveSpeed <= 0)
            throw new ArgumentException(
                "averageMoveSpeed must be positive and no M11 base move speed fallback was available.",
                nameof(averageMoveSpeed));

        double travelSeconds = distanceToLane > 0 ? distanceToLane / moveSpeed : 0;
        double eta = remainingRespawnSeconds + travelSeconds;
        var basis = remainingRespawnSeconds > 0 ? EtaBasis.RespawnPlusTravel : EtaBasis.RespawnTimerOnly;
        return new ReturnETA(championId, eta, basis);
    }

    // ── Death handling (spec Internal Logic #1) ──────────────────────────────────

    private void OnChampionDied(Event evt)
    {
        if (evt.Payload is not ChampionDiedPayload payload) return;

        // RAW respawnTimer — no correction/rounding (Reviewer Checklist #2).
        var death = new DeathEvent(payload.ChampionName, payload.RespawnTimer, _clock.NowMs);

        Action<DeathEvent>[] callbacks;
        lock (_gate) { callbacks = _callbacks.Values.ToArray(); }
        foreach (var cb in callbacks) cb(death);

        // Death Timer is the EXACT API value — presented as exact, no estimate marker.
        string message = $"{death.ChampionName} respawns in {death.RespawnTimer:0.#}s";
        EventBus.EventBus.Publish("UI.RECALL_TIMER", message, Source);

        // Optional spoken countdown. M09 applies its own cooldown against the key — no
        // tight coupling; only the shared SpeechRequest + bus are referenced.
        var speech = new SpeechRequest(
            Guid.NewGuid().ToString(), message, SpeechPriority.Normal, death.DeathTimestamp,
            CooldownKey: $"recall-death:{death.ChampionName}");
        EventBus.EventBus.Publish("VOICE.SPEAK", speech, Source);
    }

    public void Dispose()
    {
        if (_deathSubId is not null) { EventBus.EventBus.Unsubscribe(_deathSubId); _deathSubId = null; }
    }
}
