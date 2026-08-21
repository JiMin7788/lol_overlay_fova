namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for <see cref="ObjectiveTimer"/> (DA-004) — the game-time-driven
/// neutral-objective countdown. Verifies the early-game/loading-screen guard, the
/// static first-spawn schedule, the <see cref="ObjectiveTimer.NotifyObjectiveTaken"/>
/// respawn-schedule hook (both "still counting down" and "respawn has elapsed"), the
/// permanent Rift Herald despawn, and <see cref="ObjectiveTimer.Reset"/>.
/// </summary>
public class ObjectiveTimerTests
{
    private const string ConfigJson = """
    {
      "objectives": [
        { "id": "dragon", "displayName": "Dragon", "firstSpawnSeconds": 300, "respawnSeconds": 300 },
        { "id": "rift_herald", "displayName": "Rift Herald", "firstSpawnSeconds": 480, "respawnSeconds": 999999 }
      ],
      "heraldDespawnSeconds": 1185,
      "earlyGameThresholdSeconds": 10
    }
    """;

    private static ObjectiveTimer NewTimer() => new(ObjectiveTimerConfigLoader.LoadFromJson(ConfigJson));

    // ── Early-game / loading-screen guard ──────────────────────────────────────

    [Fact]
    public void GetTimers_AtOrBelowEarlyGameThreshold_AllObjectivesReportNotYetSpawned()
    {
        var timer = NewTimer();

        var results = timer.GetTimers(gameTime: 5.0); // below the 10s threshold

        Assert.All(results, r => Assert.Equal(ObjectiveState.NotYetSpawned, r.State));
    }

    // ── Static first-spawn schedule ─────────────────────────────────────────────

    [Fact]
    public void GetTimers_BeforeFirstSpawn_ReturnsNotYetSpawnedWithCorrectCountdown()
    {
        var timer = NewTimer();

        var results = timer.GetTimers(gameTime: 100.0);
        var dragon = results.Single(r => r.Id == "dragon");

        Assert.Equal(ObjectiveState.NotYetSpawned, dragon.State);
        Assert.Equal(200.0, dragon.SecondsUntilNextSpawn); // 300 - 100
    }

    [Fact]
    public void GetTimers_AtOrAfterFirstSpawn_NoTakenNotified_ReportsUp()
    {
        var timer = NewTimer();

        var results = timer.GetTimers(gameTime: 300.0); // exactly first spawn
        var dragon = results.Single(r => r.Id == "dragon");

        Assert.Equal(ObjectiveState.Up, dragon.State);
        Assert.Equal(0.0, dragon.SecondsUntilNextSpawn);
    }

    // ── NotifyObjectiveTaken -> respawn schedule ────────────────────────────────

    [Fact]
    public void NotifyObjectiveTaken_ThenQueryBeforeRespawn_ReportsOnRespawnWithRemainingCountdown()
    {
        var timer = NewTimer();

        timer.NotifyObjectiveTaken("dragon", gameTimeTaken: 400.0);
        var results = timer.GetTimers(gameTime: 600.0); // 100s into the 300s respawn
        var dragon = results.Single(r => r.Id == "dragon");

        Assert.Equal(ObjectiveState.OnRespawn, dragon.State);
        Assert.Equal(100.0, dragon.SecondsUntilNextSpawn); // (400+300) - 600
    }

    [Fact]
    public void NotifyObjectiveTaken_RespawnTimeElapsed_ReportsUp()
    {
        var timer = NewTimer();

        timer.NotifyObjectiveTaken("dragon", gameTimeTaken: 400.0);
        var results = timer.GetTimers(gameTime: 700.0); // == 400 + 300, exactly elapsed
        var dragon = results.Single(r => r.Id == "dragon");

        Assert.Equal(ObjectiveState.Up, dragon.State);
        Assert.Equal(0.0, dragon.SecondsUntilNextSpawn);
    }

    [Fact]
    public void NotifyObjectiveTaken_UnknownObjectiveId_IsNoOp()
    {
        var timer = NewTimer();

        timer.NotifyObjectiveTaken("baron", gameTimeTaken: 100.0); // "baron" not in this config
        var results = timer.GetTimers(gameTime: 300.0);

        // No taken-state was recorded for any known objective, so dragon still follows
        // the static first-spawn schedule (Up at t=300), unaffected by the bogus call.
        Assert.Equal(ObjectiveState.Up, results.Single(r => r.Id == "dragon").State);
    }

    // ── Rift Herald permanent despawn ───────────────────────────────────────────

    [Fact]
    public void RiftHerald_AfterDespawnTime_ReportsGone_EvenIfNeverTaken()
    {
        var timer = NewTimer();

        var results = timer.GetTimers(gameTime: 1200.0); // past heraldDespawnSeconds (1185)
        var herald = results.Single(r => r.Id == "rift_herald");

        Assert.Equal(ObjectiveState.Gone, herald.State);
        Assert.True(herald.IsHeraldGone);
        Assert.Equal(0.0, herald.SecondsUntilNextSpawn);
    }

    [Fact]
    public void RiftHerald_AfterDespawnTime_ReportsGone_EvenIfCurrentlyOnRespawn()
    {
        var timer = NewTimer();

        timer.NotifyObjectiveTaken("rift_herald", gameTimeTaken: 500.0);
        var results = timer.GetTimers(gameTime: 1200.0); // still "on respawn" by schedule, but despawned

        var herald = results.Single(r => r.Id == "rift_herald");
        Assert.Equal(ObjectiveState.Gone, herald.State);
        Assert.True(herald.IsHeraldGone);
    }

    // ── Reset ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsTakenState_ReturnsToStaticSchedule()
    {
        var timer = NewTimer();
        timer.NotifyObjectiveTaken("dragon", gameTimeTaken: 400.0);

        timer.Reset();
        var results = timer.GetTimers(gameTime: 600.0); // would be OnRespawn if not reset
        var dragon = results.Single(r => r.Id == "dragon");

        Assert.Equal(ObjectiveState.Up, dragon.State); // back to "past first-spawn, never taken"
    }

    // ── GameSnapshot convenience overload ───────────────────────────────────────

    [Fact]
    public void GetTimers_FromSnapshot_NoData_ReturnsEmpty()
    {
        var timer = NewTimer();
        var snapshot = new GameSnapshot { HasData = false };

        Assert.Empty(timer.GetTimers(snapshot));
    }

    [Fact]
    public void GetTimers_FromSnapshot_DelegatesToGameTime()
    {
        var timer = NewTimer();
        var snapshot = new GameSnapshot { HasData = true, GameTime = 300.0 };

        var results = timer.GetTimers(snapshot);

        Assert.Equal(ObjectiveState.Up, results.Single(r => r.Id == "dragon").State);
    }
}
