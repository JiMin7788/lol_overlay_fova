namespace Overlay.Core;

/// <summary>
/// Typed change set produced by comparing the new tick's snapshot against the
/// previous one. This is the core/algo boundary object handed to downstream
/// consumers (data-algo, UI) — they never see raw JSON.
///
/// The diff is built only when something actually changed, and crosses out of
/// the hot polling loop, so modest allocation here is acceptable.
/// </summary>
public sealed class SnapshotDiff
{
    /// <summary>Game clock at the time of this diff (seconds).</summary>
    public double GameTime { get; init; }

    /// <summary>True on the first snapshot that has data (game just started /
    /// poller just connected). Consumers may treat this as a full sync rather
    /// than an incremental update.</summary>
    public bool IsInitialSync { get; init; }

    /// <summary>Transition between "no game" and "game active".</summary>
    public bool GameAvailabilityChanged { get; init; }
    public bool GameIsActive { get; init; }

    // ---- active player deltas ----
    public double GoldDelta { get; init; }
    public bool ActivePlayerStatsChanged { get; init; }
    public int LevelDelta { get; init; }

    // ---- events ----
    /// <summary>Number of newly appended game events since the previous tick.</summary>
    public int NewEventCount { get; init; }

    // ---- scoreboard ----
    public IReadOnlyList<PlayerChange> PlayerChanges { get; init; } = Array.Empty<PlayerChange>();

    /// <summary>True when at least one meaningful field changed.</summary>
    public bool HasChanges =>
        IsInitialSync || GameAvailabilityChanged || GoldDelta != 0 ||
        ActivePlayerStatsChanged || LevelDelta != 0 || NewEventCount > 0 ||
        PlayerChanges.Count > 0;
}

/// <summary>Per-player change record within a diff.</summary>
public readonly struct PlayerChange
{
    public string SummonerName { get; init; }
    public int CreepScoreDelta { get; init; }
    public int KillsDelta { get; init; }
    public int DeathsDelta { get; init; }
    public int AssistsDelta { get; init; }
    public int LevelDelta { get; init; }
    /// <summary>Number of item slots added since previous tick (item purchased/built).</summary>
    public int ItemsAdded { get; init; }
    /// <summary>Death/respawn state toggled this tick.</summary>
    public bool DeathStateChanged { get; init; }
    public bool IsDead { get; init; }
}
