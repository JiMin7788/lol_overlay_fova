using Overlay.Core;
using Overlay.Core.EventBus;
using Overlay.Core.Inhibitor;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M19 §3.2 Inhibitor Timers — <see cref="InhibitorTimer"/>, now on the
/// IN-GAME clock (Live Client GameTime seconds). Verifies respawnAtGame = destroyedAtGame + 300,
/// the countdown/expiry query driven by the current game time, multiple concurrently-tracked
/// inhibitors, the NO-INTERFERENCE invariant (a running timer is never reset), and removal via a
/// real InhibRespawned event. Times are game-SECONDS, not wall-clock ms.
/// </summary>
public class InhibitorTimerTests : IDisposable
{
    public InhibitorTimerTests() => EventBus.EventBus.ResetForTests();

    public void Dispose() => EventBus.EventBus.ResetForTests();

    private static void PublishDestroyed(string inhibitorId, double gameTime)
        => EventBus.EventBus.Publish(
            "GAME.INHIBITOR_DESTROYED", new InhibitorDestroyedPayload(inhibitorId, gameTime), "TestSource");

    private static void PublishRespawned(string inhibitorId, double gameTime)
        => EventBus.EventBus.Publish(
            "GAME.INHIBITOR_RESPAWNED", new InhibitorRespawnedPayload(inhibitorId, gameTime), "TestSource");

    [Fact]
    public void OnDestroyed_TracksRespawnAt_ExactlyDestroyedGameTimePlus300Seconds()
    {
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Barracks_T1_L1", 500.0);

        var status = Assert.Single(timer.GetActive(500.0));
        Assert.Equal("Barracks_T1_L1", status.InhibitorId);
        Assert.Equal(500.0, status.DestroyedAtGame);
        Assert.Equal(800.0, status.RespawnAtGame);        // exactly +300s, no drift
        Assert.Equal(300.0, status.RemainingSeconds, precision: 3);
    }

    [Fact]
    public void GetActive_JustBeforeRespawn_StillReportsRemainingSeconds()
    {
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Barracks_T1_L1", 100.0); // respawns at game-time 400

        var status = Assert.Single(timer.GetActive(399.0)); // 1s before respawn
        Assert.Equal(1.0, status.RemainingSeconds, precision: 3);
    }

    [Fact]
    public void GetActive_AtOrAfterRespawnGameTime_NoLongerReturnsTheInhibitor()
    {
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Barracks_T1_L1", 100.0); // respawns at 400

        Assert.Empty(timer.GetActive(400.0)); // exactly at respawn
        Assert.Empty(timer.GetActive(500.0)); // well after
    }

    [Fact]
    public void MultipleDestroyedInhibitors_AreTrackedIndependently()
    {
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Barracks_T1_L1", 100.0);
        PublishDestroyed("Barracks_T1_C1", 160.0);

        var active = timer.GetActive(160.0);
        Assert.Equal(2, active.Count);
        Assert.Contains(active, s => s.InhibitorId == "Barracks_T1_L1" && s.RespawnAtGame == 400.0);
        Assert.Contains(active, s => s.InhibitorId == "Barracks_T1_C1" && s.RespawnAtGame == 460.0);
    }

    [Fact]
    public void TwoInhibitorsDestroyedAtTheSameGameTime_AreBothTracked_Count2()
    {
        // §3-B-2 regression: two inhibitors destroyed at the SAME game time (simultaneous, one poll
        // tick) must BOTH be tracked — the full-InhibId key means no collision even when the destroy
        // time is identical (the prior multi-timer test used DIFFERENT times, so it didn't cover this).
        // Proves the "second timer doesn't appear" symptom is NOT in the core tracker.
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Barracks_T1_L1", 300.0);
        PublishDestroyed("Barracks_T2_C1", 300.0); // same game time, distinct inhibitor id

        var active = timer.GetActive(300.0);
        Assert.Equal(2, active.Count);
        Assert.Contains(active, s => s.InhibitorId == "Barracks_T1_L1" && s.RespawnAtGame == 600.0);
        Assert.Contains(active, s => s.InhibitorId == "Barracks_T2_C1" && s.RespawnAtGame == 600.0);
    }

    [Fact]
    public void SameInhibitorDestroyedAgain_AfterExpiry_ReplacesThePriorEntry_NotDuplicated()
    {
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Barracks_T1_L1", 100.0); // respawns at 400
        PublishDestroyed("Barracks_T1_L1", 600.0); // destroyed again after the prior timer elapsed

        var status = Assert.Single(timer.GetActive(600.0));
        Assert.Equal(900.0, status.RespawnAtGame); // a re-destruction AFTER expiry starts a fresh timer
    }

    [Fact]
    public void SameInhibitorDestroyedAgain_WhileActive_DoesNotResetTheRunningTimer()
    {
        // NO-INTERFERENCE invariant: "한번 작동한 타이머는 종료될 때까지 간섭받지 않음".
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Barracks_T1_L1", 100.0); // respawns at 400
        PublishDestroyed("Barracks_T1_L1", 160.0); // duplicate/re-fire mid-countdown must be ignored

        var status = Assert.Single(timer.GetActive(160.0));
        Assert.Equal(400.0, status.RespawnAtGame);   // unchanged — NOT 460
        Assert.Equal(100.0, status.DestroyedAtGame); // original destruction time preserved
    }

    [Fact]
    public void InhibRespawnedEvent_RemovesTheTimer_EvenBeforeTimeout()
    {
        // The one non-timeout removal path: the game reports the inhibitor actually respawned.
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Barracks_T1_L1", 100.0); // respawns at 400
        Assert.Single(timer.GetActive(200.0));     // running at t=200

        PublishRespawned("Barracks_T1_L1", 250.0); // game says it's back early
        Assert.Empty(timer.GetActive(260.0));      // gone, even though 400 not yet reached
    }

    [Fact]
    public void Destroyed_DoesNotPublishUiInhibitorTimerToast_OverlayLiveQueriesInstead()
    {
        // (loop 142) The one-shot UI.INHIBITOR_TIMER toast was removed — the overlay LIVE-queries
        // GetActive each frame and draws the countdown on the minimap at the inhibitor's location.
        string? hud = null;
        using var delivered = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.INHIBITOR_TIMER", e => { hud = e.Payload as string; delivered.Set(); });

        using var timer = new InhibitorTimer();
        timer.Start();
        PublishDestroyed("Barracks_T1_L1", 100.0);

        Assert.False(delivered.Wait(TimeSpan.FromMilliseconds(200)), "UI.INHIBITOR_TIMER must not be published");
        Assert.Null(hud);

        var status = Assert.Single(timer.GetActive(100.0));
        Assert.Equal(300.0, status.RemainingSeconds, precision: 3);
    }

    [Fact]
    public void NewGameClockReset_StalePreviousGameTimer_IsNotShown()
    {
        // Previous game: inhibitor destroyed late (game-time 900 → respawn 1200). Next game's clock
        // resets to ~0; the old entry must NOT render as a bogus ~20-minute countdown.
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Chaos_L0", 900.0);      // prev game, respawns at game-time 1200
        Assert.Empty(timer.GetActive(5.0));       // new game clock = 5s → stale entry filtered out
    }

    [Fact]
    public void GameDisconnected_ClearsAllTimers()
    {
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Chaos_L0", 100.0);
        PublishDestroyed("Chaos_L1", 100.0);
        Assert.Equal(2, timer.GetActive(150.0).Count);

        EventBus.EventBus.Publish("GAME.DISCONNECTED", null, "TestSource"); // game ended
        Assert.Empty(timer.GetActive(150.0));
    }

    [Fact]
    public void AfterClockReset_SameInhibitorDestroyed_StartsFreshTimer_NotBlockedByStaleEntry()
    {
        // A stale entry must never block the new game's timer via NO-INTERFERENCE (the stale respawn
        // is "in the future" but its destruction time is after the new game's clock → it's replaced).
        using var timer = new InhibitorTimer();
        timer.Start();

        PublishDestroyed("Chaos_L0", 900.0);      // stale: respawn 1200
        PublishDestroyed("Chaos_L0", 100.0);      // new game: destroyed at 100

        var status = Assert.Single(timer.GetActive(100.0));
        Assert.Equal(400.0, status.RespawnAtGame); // fresh 100+300, NOT the stale 1200
    }

    [Fact]
    public void Dispose_UnsubscribesFromEventBus_NoFurtherTracking()
    {
        var timer = new InhibitorTimer();
        timer.Start();
        timer.Dispose();

        PublishDestroyed("Barracks_T1_L1", 100.0);

        Assert.Empty(timer.GetActive(100.0));
    }
}
