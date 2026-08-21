using Overlay.Core.EventBus;

namespace Overlay.Core.NexusTurret;

/// <summary>
/// Patch 15.1 (25.S1.1) Nexus ("twin") Turret respawn timer. Turns M01's
/// <c>GAME.NEXUS_TURRET_DESTROYED</c> into a 3:00 respawn countdown per destroyed Nexus turret,
/// computed in the <b>in-game clock</b> (Live Client <c>GameTime</c> seconds), NOT wall-clock —
/// mirrors <see cref="Inhibitor.InhibitorTimer"/> exactly, only the duration differs (180s vs 300s).
///
/// <para>Unlike inhibitors, the Live Client exposes NO Nexus-turret respawn event, so a Nexus
/// timer ends the one available way: TIMEOUT — the game clock reaches <c>respawnAtGame</c>
/// (which coincides with the actual 3:00 respawn). The <b>NO-INTERFERENCE invariant</b> still
/// holds: a running timer is never reset/time-shifted by a further destroy event.</para>
///
/// <para>Real Live Client data (TurretKilled, P1) — never labelled an estimate.</para>
/// </summary>
public sealed class NexusTurretTimer : IDisposable
{
    private const string Source = "P15_1.NexusTurretTimer";

    /// <summary>Fixed Nexus-turret respawn duration (patch 15.1: 3-minute respawn delay).</summary>
    public const int RespawnSeconds = 180;

    private readonly object _gate = new();
    private readonly Dictionary<string, NexusTurretState> _tracked = new();

    private string? _destroyedSubId;
    private string? _disconnectedSubId;

    /// <summary>Parameterless — all time comes from the game-clock value on the event and the
    /// current-game-time argument to <see cref="GetActive"/> (no wall-clock injection).</summary>
    public NexusTurretTimer() { }

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    /// <summary>Subscribe to M01's <c>GAME.NEXUS_TURRET_DESTROYED</c>. Idempotent.</summary>
    public void Start()
    {
        if (_destroyedSubId is null)
            _destroyedSubId = EventBus.EventBus.Subscribe("GAME.NEXUS_TURRET_DESTROYED", OnNexusTurretDestroyed);
        // Clear on disconnect so a previous game's countdowns don't reappear over the new game's clock
        // as bogus timers (game-time resets to ~0 each match). Same rationale as InhibitorTimer.
        if (_disconnectedSubId is null)
            _disconnectedSubId = EventBus.EventBus.Subscribe("GAME.DISCONNECTED", OnGameDisconnected);
    }

    private void OnGameDisconnected(Event evt)
    {
        lock (_gate) _tracked.Clear();
    }

    // ── Query ────────────────────────────────────────────────────────────────────

    /// <summary>Every tracked Nexus turret whose respawn game-time has not yet been reached as of
    /// <paramref name="nowGameSeconds"/> (current Live Client <c>GameTime</c>), with seconds
    /// remaining. Pure — the caller supplies "now" from the latest snapshot.</summary>
    public IReadOnlyList<NexusTurretStatus> GetActive(double nowGameSeconds)
    {
        lock (_gate)
        {
            return _tracked.Values
                // DestroyedAtGame <= now guards against a stale previous-game entry (game-clock reset).
                .Where(s => s.RespawnAtGame > nowGameSeconds && s.DestroyedAtGame <= nowGameSeconds)
                .Select(s => new NexusTurretStatus(
                    s.TurretId, s.DestroyedAtGame, s.RespawnAtGame, s.RespawnAtGame - nowGameSeconds))
                .OrderBy(s => s.RespawnAtGame)
                .ToList();
        }
    }

    // ── Event handling ────────────────────────────────────────────────────────────

    private void OnNexusTurretDestroyed(Event evt)
    {
        if (evt.Payload is not NexusTurretDestroyedPayload payload) return;

        double destroyedAtGame = payload.GameTime;
        double respawnAtGame = destroyedAtGame + RespawnSeconds;

        lock (_gate)
        {
            // NO-INTERFERENCE (same as InhibitorTimer): a running 3:00 timer is never reset by a
            // further destroy event for the same turret until it expires.
            // Skip only for a valid same-timeline running timer; a stale previous-game entry
            // (DestroyedAtGame after this event's game-time = clock reset) is replaced, not honored.
            if (_tracked.TryGetValue(payload.TurretId, out var existing)
                && existing.RespawnAtGame > destroyedAtGame
                && existing.DestroyedAtGame <= destroyedAtGame)
                return;
            _tracked[payload.TurretId] = new NexusTurretState(payload.TurretId, destroyedAtGame, respawnAtGame);
        }
    }

    public void Dispose()
    {
        if (_destroyedSubId is not null)
        {
            EventBus.EventBus.Unsubscribe(_destroyedSubId);
            _destroyedSubId = null;
        }
        if (_disconnectedSubId is not null)
        {
            EventBus.EventBus.Unsubscribe(_disconnectedSubId);
            _disconnectedSubId = null;
        }
    }

    private readonly record struct NexusTurretState(string TurretId, double DestroyedAtGame, double RespawnAtGame);
}

/// <summary>Read-only snapshot of one tracked Nexus turret's respawn state, as of the
/// <c>nowGameSeconds</c> passed to <see cref="NexusTurretTimer.GetActive"/>. All times are Live
/// Client <c>GameTime</c> SECONDS.</summary>
public readonly record struct NexusTurretStatus(
    string TurretId, double DestroyedAtGame, double RespawnAtGame, double RemainingSeconds);
