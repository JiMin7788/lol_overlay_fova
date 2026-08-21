using Overlay.Core.EventBus;

namespace Overlay.Core.Inhibitor;

/// <summary>
/// M19 §3.2 Inhibitor Timers: turns M01's <c>GAME.INHIBITOR_DESTROYED</c> into a 5:00
/// respawn countdown per destroyed inhibitor, computed entirely in the <b>in-game clock</b>
/// (Live Client <c>GameTime</c> seconds), NOT wall-clock time — so the countdown tracks the
/// real match clock and is immune to overlay-process pauses/hitches (user rule: "타이머 시간은
/// 인게임타이머를 기준으로 계산").
///
/// <para>The inhibitor's destruction game-time is carried by the event itself
/// (<c>InhibKilled</c>'s <c>EventTime</c>), so <c>respawnAtGame = destroyedAtGame + 300s</c>
/// is fixed at detection and never recomputed. <see cref="GetActive"/> is a pure function of
/// the CURRENT game time the caller passes (from the latest snapshot's <c>GameTime</c>).</para>
///
/// <para><b>NO-INTERFERENCE invariant</b> (user rule: "한번 작동한 타이머는 종료될 때까지 어떠한
/// 간섭도 받지 않음"): once a timer is running for an inhibitor it is NEVER reset or time-shifted.
/// It leaves <see cref="GetActive"/> in exactly two ways: (1) TIMEOUT — the game clock reaches
/// <c>respawnAtGame</c>; or (2) a real <c>GAME.INHIBITOR_RESPAWNED</c> event for that inhibitor.
/// Nothing else (duplicate/re-fired destroy events, re-destruction while still down) may touch it.</para>
///
/// <para>This is real Live Client data (P1) — never labelled an estimate.</para>
/// </summary>
public sealed class InhibitorTimer : IDisposable
{
    private const string Source = "M19.InhibitorTimer";

    /// <summary>Fixed inhibitor respawn duration (M19 §3.2: "5:00 respawn countdown").</summary>
    public const int RespawnSeconds = 300;

    private readonly object _gate = new();
    private readonly Dictionary<string, InhibitorState> _tracked = new();

    private string? _destroyedSubId;
    private string? _respawnedSubId;
    private string? _disconnectedSubId;

    /// <summary>Parameterless — the timer needs no clock injection anymore: all time comes from
    /// the game-clock values on the events and the current-game-time argument to
    /// <see cref="GetActive"/>. (Kept as a ctor rather than a static for the Start/Dispose seam.)</summary>
    public InhibitorTimer() { }

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    /// <summary>Subscribe to M01's <c>GAME.INHIBITOR_DESTROYED</c> (start a timer) and
    /// <c>GAME.INHIBITOR_RESPAWNED</c> (end one early on the real respawn event). Idempotent.</summary>
    public void Start()
    {
        if (_destroyedSubId is null)
            _destroyedSubId = EventBus.EventBus.Subscribe("GAME.INHIBITOR_DESTROYED", OnInhibitorDestroyed);
        if (_respawnedSubId is null)
            _respawnedSubId = EventBus.EventBus.Subscribe("GAME.INHIBITOR_RESPAWNED", OnInhibitorRespawned);
        // Game-time is only meaningful WITHIN one match; it resets to ~0 each new game. Clearing on
        // disconnect stops a previous game's countdowns (whose respawnAtGame is a large game-time like
        // 1281s) from reappearing over the new game's clock as bogus ~20-minute timers.
        if (_disconnectedSubId is null)
            _disconnectedSubId = EventBus.EventBus.Subscribe("GAME.DISCONNECTED", OnGameDisconnected);
    }

    private void OnGameDisconnected(Event evt)
    {
        lock (_gate) _tracked.Clear();
    }

    // ── Query ────────────────────────────────────────────────────────────────────

    /// <summary>Every tracked inhibitor whose respawn game-time has not yet been reached as of
    /// <paramref name="nowGameSeconds"/> (the current Live Client <c>GameTime</c>), with seconds
    /// remaining until respawn. Pure — no internal ticking; the caller supplies "now" from the
    /// latest snapshot so the countdown is driven by the in-game clock.</summary>
    public IReadOnlyList<InhibitorStatus> GetActive(double nowGameSeconds)
    {
        lock (_gate)
        {
            return _tracked.Values
                // RespawnAtGame in the future = still counting down; DestroyedAtGame <= now guards against
                // a stale previous-game entry (destroyed "in the future" relative to this game's clock).
                .Where(s => s.RespawnAtGame > nowGameSeconds && s.DestroyedAtGame <= nowGameSeconds)
                .Select(s => new InhibitorStatus(
                    s.InhibitorId, s.DestroyedAtGame, s.RespawnAtGame, s.RespawnAtGame - nowGameSeconds))
                .OrderBy(s => s.RespawnAtGame)
                .ToList();
        }
    }

    // ── Event handling ────────────────────────────────────────────────────────────

    private void OnInhibitorDestroyed(Event evt)
    {
        if (evt.Payload is not InhibitorDestroyedPayload payload) return;

        double destroyedAtGame = payload.GameTime;
        double respawnAtGame = destroyedAtGame + RespawnSeconds;

        lock (_gate)
        {
            // NO-INTERFERENCE: if a timer for this inhibitor is still running (its respawn game-time
            // is still in the future relative to THIS event's game-time), the new event is ignored —
            // the running countdown is never reset. Only a never-seen inhibitor, or a genuine
            // re-destruction AFTER the prior 5:00 elapsed, starts/replaces a timer.
            // Skip ONLY when a valid, same-timeline timer is still running for this inhibitor. An existing
            // entry whose DestroyedAtGame is AFTER this event's game-time is stale from a previous game
            // (clock reset) — replace it rather than let it block the new game's timer.
            if (_tracked.TryGetValue(payload.InhibitorId, out var existing)
                && existing.RespawnAtGame > destroyedAtGame
                && existing.DestroyedAtGame <= destroyedAtGame)
                return;
            _tracked[payload.InhibitorId] = new InhibitorState(payload.InhibitorId, destroyedAtGame, respawnAtGame);
        }
    }

    /// <summary>The game reported this inhibitor has actually respawned — the one non-timeout way a
    /// timer may end. Remove it so it stops rendering immediately (authoritative over the countdown).</summary>
    private void OnInhibitorRespawned(Event evt)
    {
        if (evt.Payload is not InhibitorRespawnedPayload payload) return;
        lock (_gate)
        {
            _tracked.Remove(payload.InhibitorId);
        }
    }

    public void Dispose()
    {
        if (_destroyedSubId is not null)
        {
            EventBus.EventBus.Unsubscribe(_destroyedSubId);
            _destroyedSubId = null;
        }
        if (_respawnedSubId is not null)
        {
            EventBus.EventBus.Unsubscribe(_respawnedSubId);
            _respawnedSubId = null;
        }
        if (_disconnectedSubId is not null)
        {
            EventBus.EventBus.Unsubscribe(_disconnectedSubId);
            _disconnectedSubId = null;
        }
    }

    private readonly record struct InhibitorState(string InhibitorId, double DestroyedAtGame, double RespawnAtGame);
}

/// <summary>Read-only snapshot of one tracked inhibitor's respawn state, as of the
/// <c>nowGameSeconds</c> passed to <see cref="InhibitorTimer.GetActive"/>. All times are Live
/// Client <c>GameTime</c> SECONDS (not wall-clock ms).</summary>
public readonly record struct InhibitorStatus(
    string InhibitorId, double DestroyedAtGame, double RespawnAtGame, double RemainingSeconds);
