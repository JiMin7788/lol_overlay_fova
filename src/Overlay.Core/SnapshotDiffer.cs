namespace Overlay.Core;

/// <summary>
/// Compares the newly parsed snapshot against the previous tick's snapshot and
/// produces a typed <see cref="SnapshotDiff"/>. Runs on the background polling
/// worker, never on the UI thread.
///
/// Scoreboard matching is keyed by summoner name. Player count in LoL is fixed
/// for a match, so the small linear scan is cheap and avoids a per-tick
/// dictionary allocation.
/// </summary>
public static class SnapshotDiffer
{
    /// <summary>Shared no-change/no-data result. Returned on every steady-state
    /// tick where nothing changed (and on idle no-game ticks), so the hot path
    /// allocates no SnapshotDiff. Its <see cref="SnapshotDiff.HasChanges"/> is
    /// false, so the poller never publishes it. Immutable, safe to share.</summary>
    public static readonly SnapshotDiff NoChange = new() { GameIsActive = false };

    /// <summary>
    /// Build a diff of <paramref name="current"/> vs <paramref name="previous"/>.
    /// <paramref name="previousHadData"/> distinguishes "first snapshot of a game"
    /// from steady-state ticks. Returns the shared <see cref="NoChange"/> instance
    /// (no allocation) when nothing meaningful changed.
    /// </summary>
    public static SnapshotDiff Diff(GameSnapshot previous, GameSnapshot current, bool previousHadData)
    {
        bool availabilityChanged = previousHadData != current.HasData;
        bool initialSync = current.HasData && !previousHadData;

        // No active game and nothing changed — shared no-op, never published.
        if (!current.HasData && !availabilityChanged)
            return NoChange;

        if (!current.HasData)
        {
            return new SnapshotDiff
            {
                GameTime = current.GameTime,
                GameAvailabilityChanged = true,
                GameIsActive = false,
            };
        }

        double goldDelta = initialSync ? 0 : current.CurrentGold - previous.CurrentGold;
        int levelDelta = initialSync ? 0 : current.Level - previous.Level;
        bool statsChanged = initialSync || StatsChanged(previous.Stats, current.Stats);
        int newEvents = initialSync
            ? current.EventCount
            : Math.Max(0, current.EventCount - previous.EventCount);

        List<PlayerChange>? playerChanges = null;
        if (!initialSync)
            playerChanges = BuildPlayerChanges(previous, current);

        // Steady-state tick with nothing changed: return the shared no-op rather
        // than allocating a diff that HasChanges==false and is never published.
        if (!initialSync && !availabilityChanged && goldDelta == 0 && levelDelta == 0 &&
            !statsChanged && newEvents == 0 && (playerChanges is null || playerChanges.Count == 0))
            return NoChange;

        return new SnapshotDiff
        {
            GameTime = current.GameTime,
            IsInitialSync = initialSync,
            GameAvailabilityChanged = availabilityChanged,
            GameIsActive = true,
            GoldDelta = goldDelta,
            LevelDelta = levelDelta,
            ActivePlayerStatsChanged = statsChanged,
            NewEventCount = newEvents,
            PlayerChanges = (IReadOnlyList<PlayerChange>?)playerChanges ?? Array.Empty<PlayerChange>(),
        };
    }

    private static bool StatsChanged(in ActivePlayerStats a, in ActivePlayerStats b) =>
        a.CurrentHealth != b.CurrentHealth ||
        a.MaxHealth != b.MaxHealth ||
        a.ResourceValue != b.ResourceValue ||
        a.ResourceMax != b.ResourceMax ||
        a.AttackDamage != b.AttackDamage ||
        a.AbilityPower != b.AbilityPower ||
        a.Armor != b.Armor ||
        a.MagicResist != b.MagicResist ||
        a.MoveSpeed != b.MoveSpeed;

    private static List<PlayerChange>? BuildPlayerChanges(GameSnapshot prev, GameSnapshot cur)
    {
        List<PlayerChange>? changes = null;

        for (int i = 0; i < cur.PlayerCount; i++)
        {
            ScoreboardEntry c = cur.Players[i];
            ScoreboardEntry? p = FindByName(prev, c.SummonerName);
            if (p is null)
                continue; // new player mid-stream (rare); skip delta, will sync next tick

            int csDelta = c.CreepScore - p.CreepScore;
            int kDelta = c.Kills - p.Kills;
            int dDelta = c.Deaths - p.Deaths;
            int aDelta = c.Assists - p.Assists;
            int lvlDelta = c.Level - p.Level;
            int itemsAdded = Math.Max(0, c.ItemCount - p.ItemCount);
            bool deathChanged = c.IsDead != p.IsDead;

            if (csDelta == 0 && kDelta == 0 && dDelta == 0 && aDelta == 0 &&
                lvlDelta == 0 && itemsAdded == 0 && !deathChanged)
                continue;

            changes ??= new List<PlayerChange>();
            changes.Add(new PlayerChange
            {
                SummonerName = c.SummonerName,
                CreepScoreDelta = csDelta,
                KillsDelta = kDelta,
                DeathsDelta = dDelta,
                AssistsDelta = aDelta,
                LevelDelta = lvlDelta,
                ItemsAdded = itemsAdded,
                DeathStateChanged = deathChanged,
                IsDead = c.IsDead,
            });
        }

        return changes;
    }

    private static ScoreboardEntry? FindByName(GameSnapshot s, string name)
    {
        for (int i = 0; i < s.PlayerCount; i++)
            if (string.Equals(s.Players[i].SummonerName, name, StringComparison.Ordinal))
                return s.Players[i];
        return null;
    }
}
