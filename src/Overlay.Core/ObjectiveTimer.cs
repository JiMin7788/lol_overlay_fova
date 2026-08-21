namespace Overlay.Core;

/// <summary>
/// Deterministic, stateful component that reports neutral-objective (Dragon, Rift
/// Herald, Baron Nashor, Void Grubs, Atakhan) spawn / respawn countdowns driven
/// by game time and a versioned config (DA-004).
///
/// ─── EVENT-TYPE PROXY (data-dependency GAP) ──────────────────────────────────
/// The Live Client Data API exposes NO event-type or event-name fields for scoreboard
/// events. <see cref="GameSnapshot"/> carries only <c>MaxEventId</c> and
/// <c>EventCount</c> — the event payload is opaque; there is NO way to detect
/// "Dragon was killed" or "Baron was slain" from the snapshot directly.
///
/// Proxy used by this component:
///   Objective state is driven PRIMARILY by <c>GameTime</c> + the static spawn
///   schedule from <see cref="ObjectiveTimerConfig"/>. When a future caller gains
///   access to event names (e.g. via a future API endpoint or a websocket extension),
///   it should call <see cref="NotifyObjectiveTaken"/> with the objective id and the
///   game time of the kill event. Until then, the timers can only transition through
///   the static schedule; they cannot auto-detect a mid-game kill without an external
///   notification.
///
/// This is stated plainly and intentionally — this component does NOT pretend to
/// detect kills from EventCount changes.
///
/// ─── STATEFULNESS ────────────────────────────────────────────────────────────
/// The component is stateful across calls: it maintains a per-objective
/// "last taken time" dictionary that persists between <see cref="GetTimers"/> calls.
/// The component remains pure with respect to its inputs — the same
/// (gameTime, taken-notification history) sequence always produces the same output.
/// Call <see cref="Reset"/> when a new game begins.
///
/// ─── DESIGN NOTES ────────────────────────────────────────────────────────────
/// - NOT the 0.5s hot path. Off-hot-path: allocation (small Dictionary, output list)
///   is acceptable (per task brief and RecallDetector precedent). No LINQ.
/// - No UI, no polling, no I/O beyond the one-time config load (data-algo SKILL
///   "Boundary").
/// - All time constants — first spawn, respawn interval, herald despawn, early-game
///   guard — come from <see cref="ObjectiveTimerConfig"/> (Hard Rule #4). No inline
///   magic numbers.
/// - The objective list is data-driven: each <see cref="ObjectiveDefinition"/> in the
///   config JSON adds one tracked objective; adding Atakhan or a new seasonal boss is
///   a config edit, not a code change.
/// - Negative countdowns are clamped to 0.
/// </summary>
public sealed class ObjectiveTimer
{
    private readonly ObjectiveTimerConfig _config;

    // Per-objective mutable state: the game time at which this objective was last
    // taken (killed). Null means the objective has never been notified as taken in
    // this session (either it hasn't spawned yet, or it is currently up).
    // Keyed by ObjectiveDefinition.Id (case-insensitive for safety).
    private readonly Dictionary<string, double> _lastTakenTimes
        = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="config">Loaded objective config (call
    /// <see cref="ObjectiveTimerConfigLoader.Load"/> once at session start and cache;
    /// do not reload per tick).</param>
    public ObjectiveTimer(ObjectiveTimerConfig config)
        => _config = config ?? throw new ArgumentNullException(nameof(config));

    // ── Public API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute the current state of every objective in the config at the given
    /// <paramref name="gameTime"/> (seconds from game start), incorporating any
    /// previously notified taken-events.
    ///
    /// <para>Returns one <see cref="ObjectiveTimerResult"/> per objective defined in
    /// <see cref="ObjectiveTimerConfig.Objectives"/>. Results are in config-list order.
    /// </para>
    ///
    /// <para>If <paramref name="gameTime"/> is at or below
    /// <see cref="ObjectiveTimerConfig.EarlyGameThresholdSeconds"/>, all objectives are
    /// returned as <see cref="ObjectiveState.NotYetSpawned"/> — guards against bogus
    /// zero/near-zero ticks during the loading screen.</para>
    ///
    /// <para>Negative countdown values are clamped to 0.</para>
    /// </summary>
    /// <param name="gameTime">Current game clock in seconds (from
    /// <see cref="GameSnapshot.GameTime"/>). Must be non-negative.</param>
    public IReadOnlyList<ObjectiveTimerResult> GetTimers(double gameTime)
    {
        var objectives = _config.Objectives;
        if (objectives is null || objectives.Length == 0)
            return Array.Empty<ObjectiveTimerResult>();

        // Early game / loading-screen guard: return all NotYetSpawned.
        if (gameTime <= _config.EarlyGameThresholdSeconds)
        {
            var earlyResults = new ObjectiveTimerResult[objectives.Length];
            for (int i = 0; i < objectives.Length; i++)
            {
                var def = objectives[i];
                earlyResults[i] = new ObjectiveTimerResult
                {
                    Id = def.Id,
                    DisplayName = def.DisplayName,
                    State = ObjectiveState.NotYetSpawned,
                    SecondsUntilNextSpawn = def.FirstSpawnSeconds - gameTime < 0
                        ? 0.0
                        : def.FirstSpawnSeconds - gameTime,
                };
            }
            return earlyResults;
        }

        var results = new ObjectiveTimerResult[objectives.Length];

        for (int i = 0; i < objectives.Length; i++)
        {
            results[i] = ComputeResult(objectives[i], gameTime);
        }

        return results;
    }

    /// <summary>
    /// Convenience overload: extracts <see cref="GameSnapshot.GameTime"/> and
    /// delegates to <see cref="GetTimers(double)"/>.
    ///
    /// <para>Returns <see cref="Array.Empty{T}"/> if <paramref name="snapshot"/> has
    /// no data (<see cref="GameSnapshot.HasData"/> is false).</para>
    /// </summary>
    /// <param name="snapshot">Current snapshot from the polling loop. Must not be null.</param>
    public IReadOnlyList<ObjectiveTimerResult> GetTimers(GameSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (!snapshot.HasData) return Array.Empty<ObjectiveTimerResult>();
        return GetTimers(snapshot.GameTime);
    }

    /// <summary>
    /// Notify the timer that a specific objective was taken (killed) at
    /// <paramref name="gameTimeTaken"/>. The next spawn will be computed as
    /// <c>gameTimeTaken + respawnSeconds</c> (from config).
    ///
    /// <para>This is the external hook for event-driven kill detection. When the Live
    /// Client Data API exposes event names (or when the caller obtains kill data via
    /// another means), call this method with the objective's id and the game time of
    /// the kill event. Until that caller exists, timers run on the static schedule
    /// only (see class-level proxy comment).</para>
    ///
    /// <para>Calling this method with an unknown <paramref name="objectiveId"/> is a
    /// no-op (the id is simply not found in the config's objective list).</para>
    /// </summary>
    /// <param name="objectiveId">The objective identifier matching
    /// <see cref="ObjectiveDefinition.Id"/> (e.g. "dragon", "baron", "rift_herald").
    /// Case-insensitive.</param>
    /// <param name="gameTimeTaken">Game clock (seconds) at the moment the objective
    /// was killed. Must be non-negative.</param>
    public void NotifyObjectiveTaken(string objectiveId, double gameTimeTaken)
    {
        if (string.IsNullOrEmpty(objectiveId)) return;
        if (gameTimeTaken < 0) gameTimeTaken = 0;

        // Only record if the id exists in the config objective list (guards typos /
        // stale callers when the config is updated).
        var objectives = _config.Objectives;
        for (int i = 0; i < objectives.Length; i++)
        {
            if (string.Equals(objectives[i].Id, objectiveId,
                    StringComparison.OrdinalIgnoreCase))
            {
                _lastTakenTimes[objectiveId] = gameTimeTaken;
                return;
            }
        }
        // Unknown id — no-op (caller used an id not present in the config).
    }

    /// <summary>
    /// Reset all per-objective taken-time state. Call when a new game begins (e.g.
    /// when <see cref="SnapshotDiff.GameAvailabilityChanged"/> transitions to false then
    /// back to true, or when the session poller signals a new match).
    /// </summary>
    public void Reset() => _lastTakenTimes.Clear();

    // ── Private helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Compute the <see cref="ObjectiveTimerResult"/> for one objective at the given
    /// game time, using the static schedule and any previously notified taken-time.
    /// </summary>
    private ObjectiveTimerResult ComputeResult(ObjectiveDefinition def, double gameTime)
    {
        // ---- Special case: Rift Herald permanently despawns at heraldDespawnSeconds ----
        // After that timestamp it is gone regardless of whether it was killed or not.
        bool isHerald = string.Equals(def.Id, "rift_herald",
            StringComparison.OrdinalIgnoreCase);
        if (isHerald && gameTime >= _config.HeraldDespawnSeconds)
        {
            // Herald window has closed — report as gone (use OnRespawn with a very large
            // countdown as a "no longer available" sentinel; the caller can check the
            // HeraldGone flag on the result to distinguish from a normal respawn).
            return new ObjectiveTimerResult
            {
                Id = def.Id,
                DisplayName = def.DisplayName,
                State = ObjectiveState.Gone,
                SecondsUntilNextSpawn = 0.0,
                IsHeraldGone = true,
            };
        }

        // ---- Has the objective been notified as taken? ----
        if (_lastTakenTimes.TryGetValue(def.Id, out double takenTime))
        {
            // Respawn schedule: next spawn = takenTime + respawnSeconds.
            double nextSpawnTime = takenTime + def.RespawnSeconds;
            double remaining = nextSpawnTime - gameTime;

            if (remaining <= 0)
            {
                // Respawn time has elapsed — objective is back up.
                return new ObjectiveTimerResult
                {
                    Id = def.Id,
                    DisplayName = def.DisplayName,
                    State = ObjectiveState.Up,
                    SecondsUntilNextSpawn = 0.0,
                };
            }
            else
            {
                // Still on respawn countdown.
                return new ObjectiveTimerResult
                {
                    Id = def.Id,
                    DisplayName = def.DisplayName,
                    State = ObjectiveState.OnRespawn,
                    SecondsUntilNextSpawn = remaining,
                };
            }
        }

        // ---- No taken notification recorded — use the static first-spawn schedule ----
        if (gameTime < def.FirstSpawnSeconds)
        {
            double countdown = def.FirstSpawnSeconds - gameTime;
            return new ObjectiveTimerResult
            {
                Id = def.Id,
                DisplayName = def.DisplayName,
                State = ObjectiveState.NotYetSpawned,
                SecondsUntilNextSpawn = countdown < 0 ? 0.0 : countdown,
            };
        }
        else
        {
            // Past first spawn time and no taken event recorded — objective is up.
            return new ObjectiveTimerResult
            {
                Id = def.Id,
                DisplayName = def.DisplayName,
                State = ObjectiveState.Up,
                SecondsUntilNextSpawn = 0.0,
            };
        }
    }
}

// ── Output types ──────────────────────────────────────────────────────────────

/// <summary>
/// The current spawn/respawn state of one neutral objective, returned by
/// <see cref="ObjectiveTimer.GetTimers(double)"/> per objective in the config.
/// Value-type struct — no heap allocation beyond the array that holds it.
/// </summary>
public readonly struct ObjectiveTimerResult
{
    /// <summary>Objective identifier matching <see cref="ObjectiveDefinition.Id"/>
    /// (e.g. "dragon", "baron", "rift_herald").</summary>
    public string Id { get; init; }

    /// <summary>Human-readable label from the config (e.g. "Baron Nashor").</summary>
    public string DisplayName { get; init; }

    /// <summary>Current lifecycle state of the objective.</summary>
    public ObjectiveState State { get; init; }

    /// <summary>
    /// Seconds until the objective next becomes available (spawns or respawns).
    /// <list type="bullet">
    ///   <item><description><see cref="ObjectiveState.NotYetSpawned"/>: seconds
    ///   until <see cref="ObjectiveDefinition.FirstSpawnSeconds"/>.</description></item>
    ///   <item><description><see cref="ObjectiveState.OnRespawn"/>: seconds until
    ///   <c>takenTime + respawnSeconds</c>.</description></item>
    ///   <item><description><see cref="ObjectiveState.Up"/>: 0 — already available.
    ///   </description></item>
    ///   <item><description><see cref="ObjectiveState.Gone"/>: 0 — no longer in the
    ///   game (Rift Herald after despawn time).</description></item>
    /// </list>
    /// Always clamped to 0 — never negative.
    /// </summary>
    public double SecondsUntilNextSpawn { get; init; }

    /// <summary>
    /// True when Rift Herald has permanently despawned (game time exceeded
    /// <see cref="ObjectiveTimerConfig.HeraldDespawnSeconds"/>). Only meaningful
    /// when <see cref="State"/> is <see cref="ObjectiveState.Gone"/>; always false
    /// for all other objectives.
    /// </summary>
    public bool IsHeraldGone { get; init; }
}

/// <summary>
/// Lifecycle state of a neutral objective at the current game time.
/// </summary>
public enum ObjectiveState
{
    /// <summary>
    /// The objective has not yet reached its first spawn time
    /// (<see cref="ObjectiveDefinition.FirstSpawnSeconds"/>). The countdown reflects
    /// the time remaining until first spawn.
    /// </summary>
    NotYetSpawned = 0,

    /// <summary>
    /// The objective is alive and available to contest on the map. No active countdown.
    /// </summary>
    Up = 1,

    /// <summary>
    /// The objective was killed and is counting down to respawn. The countdown reflects
    /// <c>takenTime + respawnSeconds - currentGameTime</c>.
    /// For objectives that do not respawn (Void Grubs, Atakhan) the countdown will
    /// effectively never reach 0 (large sentinel respawn interval from config).
    /// </summary>
    OnRespawn = 2,

    /// <summary>
    /// The objective is permanently gone from the game — currently only Rift Herald
    /// after <see cref="ObjectiveTimerConfig.HeraldDespawnSeconds"/>. No countdown.
    /// </summary>
    Gone = 3,
}
