namespace Overlay.Core;

/// <summary>
/// Infers per-player recalls and estimates return-to-lane ETAs from a
/// <see cref="SnapshotDiff"/> + its companion <see cref="GameSnapshot"/> (DA-002).
///
/// VISIBILITY / POSITION PROXY (data-dep GAP — see task brief):
///   The Live Client Data API exposes NO visibility flag and NO champion XY position.
///   <see cref="GameSnapshot"/>/<see cref="ScoreboardEntry"/> have no such fields, and
///   <see cref="SnapshotDiff.PlayerChange"/> carries only item-slot-count deltas, not
///   individual item identities or removal signals.
///
///   Proxy used for "in-fog at fountain":
///     Items can ONLY be purchased at the fountain/shop, so <c>ItemsAdded &gt; 0</c>
///     implies the player was at base during that tick.  A player who was
///     simultaneously showing map activity (CS gained, a kill/assist secured) was
///     visibly acting on the map, making the fountain hypothesis weaker; when
///     <see cref="RecallDetectionConfig.TreatItemChangeWhileFarmingAsVisibleShop"/> is
///     true that combination lowers confidence.  Death / respawn also returns a player
///     to fountain and may coincide with buying; those ticks are excluded entirely
///     (death != recall).
///
///   Undo/sell: the diff carries no item-removal signal, so a net-zero or net-negative
///   item-count tick naturally produces <c>ItemsAdded == 0</c> and is not fired upon.
///   This is the correct no-alarm behaviour given the available diff shape.
///
///   Lane role: <see cref="GameSnapshot"/> has no role field.  Lane role is therefore
///   passed as <see langword="null"/> to <see cref="RecallConfig.GetLaneDistance"/>,
///   which falls back to the team's "default" entry (see <c>recall-config.json</c>).
///   Consumers that know the role out-of-band may override <see cref="RecallEvent.LaneRole"/>.
///
///   Enemy move speed: the Live Client Data API exposes live move speed ONLY for the
///   active player (<see cref="ActivePlayerStats.MoveSpeed"/>).  For all other players
///   the return ETA uses <see cref="RecallConfig.DefaultMoveSpeed"/>.  Consumers should
///   treat enemy ETAs as estimates, not precise routing values.
///
/// No UI, no polling, no I/O, no voice (data-algo SKILL "Boundary").
/// Not the 0.5 s hot path — allocation is acceptable off-path; LINQ is still avoided
/// per DA-001 style.  All distance / duration / threshold constants come from the
/// config JSON per Hard Rule #4 — no inline magic numbers.
/// </summary>
public sealed class RecallDetector
{
    private readonly RecallConfig _config;

    /// <param name="config">Loaded recall config (call <see cref="RecallConfigLoader.Load"/> once
    /// at session start and cache; do not reload per tick).</param>
    public RecallDetector(RecallConfig config)
        => _config = config ?? throw new ArgumentNullException(nameof(config));

    /// <summary>
    /// Inspect every <see cref="PlayerChange"/> in <paramref name="diff"/> and emit a
    /// <see cref="RecallEvent"/> for each player whose item-array change passes the
    /// fountain-proxy heuristics.  Returns an empty list when no qualifying events exist.
    ///
    /// The returned list is a fresh allocation (off-path, per task brief — no pooling
    /// required here).  An empty <see cref="List{T}"/> is only created when there is
    /// at least one qualifying player change; the static empty-array sentinel is returned
    /// when no iteration is needed.
    /// </summary>
    /// <param name="diff">Typed diff produced by the snapshot differ (BE-001).</param>
    /// <param name="snapshot">The CURRENT snapshot that the diff was built from.
    /// Used to resolve each player's team (for lane-distance lookup) and to obtain the
    /// active player's live move-speed.</param>
    public IReadOnlyList<RecallEvent> Detect(SnapshotDiff diff, GameSnapshot snapshot)
    {
        if (diff is null) throw new ArgumentNullException(nameof(diff));
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        // Fast-exit: no game, initial sync (treat as a full baseline, not a change
        // event), or no player changes at all.
        if (!snapshot.HasData || diff.IsInitialSync || diff.PlayerChanges.Count == 0)
            return Array.Empty<RecallEvent>();

        List<RecallEvent>? results = null;

        for (int i = 0; i < diff.PlayerChanges.Count; i++)
        {
            var change = diff.PlayerChanges[i];

            // ---- GATE 1: must have acquired at least one item slot this tick ----
            // ItemsAdded == 0 means no purchase (or net-zero via undo/sell) -> skip.
            if (change.ItemsAdded <= 0)
                continue;

            // ---- GATE 2: death != recall ----
            // If the player just died (DeathStateChanged && IsDead) OR is currently dead
            // (IsDead without a state change — they respawned earlier this tick and bought),
            // exclude.  DeathsDelta > 0 in the same tick also indicates death.
            if (change.IsDead || change.DeathStateChanged || change.DeathsDelta > 0)
                continue;

            // ---- GATE 3: activity-while-visible proxy ----
            // CS/kill/assist gain in the same tick the items changed means the player
            // was likely visible on the map (they could have backed already and then
            // scored on the way back, but combined with item-buy it is ambiguous).
            // When the config knob is enabled, treat this as a visible-shop case and
            // lower confidence rather than suppress entirely.
            bool hasVisibleActivity =
                change.CreepScoreDelta > 0 ||
                change.KillsDelta > 0 ||
                change.AssistsDelta > 0;

            // ---- Confidence model ----
            // Base confidence: 1.0 (item buy with no counter-signal).
            // Visible-activity penalty: config-driven factor (visibleActivityConfidencePenalty → "possible but ambiguous").
            double confidence;
            if (hasVisibleActivity && _config.Detection.TreatItemChangeWhileFarmingAsVisibleShop)
            {
                // Visible activity lowers confidence substantially; still reportable
                // if above minConfidence so downstream UI can decide.
                confidence = _config.Detection.VisibleActivityConfidencePenalty;
            }
            else
            {
                confidence = 1.0;
            }

            // Drop events below the configured floor.
            if (confidence < _config.Detection.MinConfidence)
                continue;

            // ---- Resolve team for lane-distance lookup ----
            // Find the scoreboard entry for this player to obtain team.  Linear scan;
            // PlayerCount is at most 10 (GameSnapshot.MaxPlayers).
            string? team = null;
            for (int p = 0; p < snapshot.PlayerCount; p++)
            {
                var entry = snapshot.Players[p];
                if (string.Equals(entry.SummonerName, change.SummonerName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    team = entry.Team; // "ORDER" or "CHAOS"
                    break;
                }
            }

            // ---- Lane role ----
            // GameSnapshot / ScoreboardEntry have no role field.  Pass null so
            // GetLaneDistance falls back to the team "default" entry.  Callers that
            // know the role out-of-band should build their own ETA using the same
            // config knobs (this is documented in RecallEvent.LaneRole below).
            const string? laneRole = null;

            double laneDistance = _config.GetLaneDistance(team, laneRole);

            // ---- Move speed ----
            // Live move speed is available only for the active player.  For any
            // other player use DefaultMoveSpeed (ETA is an estimate — noted in class
            // doc above and in RecallEvent).
            double moveSpeed;
            bool usedDefaultMoveSpeed;
            if (string.Equals(snapshot.ActivePlayerSummonerName, change.SummonerName,
                    StringComparison.OrdinalIgnoreCase) &&
                snapshot.Stats.MoveSpeed > 0)
            {
                moveSpeed = snapshot.Stats.MoveSpeed;
                usedDefaultMoveSpeed = false;
            }
            else
            {
                moveSpeed = _config.DefaultMoveSpeed;
                usedDefaultMoveSpeed = true;
            }

            // ---- ETA formula (SKILL: etaSeconds = distance / moveSpeed) ----
            // Absolute game-clock time when the player is expected back in lane:
            //   GameTimeWhenDetected + RecallChannelSeconds + (laneDistance / moveSpeed)
            // GameTimeWhenDetected = game clock at the tick the item-change was detected.
            // RecallChannelSeconds = time spent channelling (player has not yet left fountain).
            // travelSeconds = time to walk from fountain to the default lane entry point.
            double travelSeconds = laneDistance / moveSpeed;
            double estimatedReturnSeconds = diff.GameTime + _config.RecallChannelSeconds + travelSeconds;

            var ev = new RecallEvent
            {
                SummonerName = change.SummonerName,
                Confidence = confidence,
                EstimatedReturnSeconds = estimatedReturnSeconds,
                GameTimeWhenDetected = diff.GameTime,
                LaneRole = laneRole,
                Team = team,
                UsedDefaultMoveSpeed = usedDefaultMoveSpeed,
                UsedDefaultLaneRole = true, // always true: no role field on snapshot
                ItemsAdded = change.ItemsAdded,
                HasVisibleActivity = hasVisibleActivity,
            };

            results ??= new List<RecallEvent>();
            results.Add(ev);
        }

        return results is null
            ? (IReadOnlyList<RecallEvent>)Array.Empty<RecallEvent>()
            : results;
    }

}

/// <summary>
/// A single inferred recall event for one player.  Value type — no heap allocation
/// beyond the list that holds it.
/// </summary>
public readonly struct RecallEvent
{
    /// <summary>Summoner name of the recalling player.</summary>
    public string SummonerName { get; init; }

    /// <summary>
    /// Confidence in the recall inference, [0, 1].  1.0 = item-added with no
    /// concurrent visible activity.  <see cref="RecallDetectionConfig.VisibleActivityConfidencePenalty"/>
    /// = item-added while also gaining CS/kill/assist
    /// (ambiguous — could be a back after a fight).  Only events at or above
    /// <see cref="RecallDetectionConfig.MinConfidence"/> are emitted.
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Estimated game-clock time (seconds from game start) at which the player will
    /// reach lane.  Computed as:
    /// <c>GameTimeWhenDetected + RecallChannelSeconds + (laneDistance / moveSpeed)</c>.
    ///
    /// NOTE: for non-active players, <see cref="UsedDefaultMoveSpeed"/> is true and
    /// this is a coarse estimate (default 335 units/sec; boots not modelled for enemies).
    /// NOTE: lane role is unknown (<see cref="UsedDefaultLaneRole"/> is always true);
    /// distance uses the team "default" entry from <c>recall-config.json</c>.
    /// </summary>
    public double EstimatedReturnSeconds { get; init; }

    /// <summary>Game clock (seconds) at the tick the item-change was first detected.</summary>
    public double GameTimeWhenDetected { get; init; }

    /// <summary>Player's team ("ORDER"/"CHAOS"), or <see langword="null"/> if the player
    /// was not found in the snapshot's scoreboard rows.</summary>
    public string? Team { get; init; }

    /// <summary>Always <see langword="null"/> in this implementation — <see cref="GameSnapshot"/>
    /// carries no role field.  Callers that know the role should feed it to
    /// <see cref="RecallConfig.GetLaneDistance"/> themselves to compute a more precise ETA.
    /// </summary>
    public string? LaneRole { get; init; }

    /// <summary>True if <see cref="EstimatedReturnSeconds"/> used the config fallback move
    /// speed rather than a live stat (always true for non-active players).</summary>
    public bool UsedDefaultMoveSpeed { get; init; }

    /// <summary>Always true — lane role cannot be inferred from the current snapshot
    /// shape; the distance estimate uses the team "default" lane entry.</summary>
    public bool UsedDefaultLaneRole { get; init; }

    /// <summary>Number of item slots added in the triggering tick.</summary>
    public int ItemsAdded { get; init; }

    /// <summary>True when CS/kill/assist gain coincided with the item-add (the event has
    /// reduced confidence because the player may have been visible on the map).</summary>
    public bool HasVisibleActivity { get; init; }
}
