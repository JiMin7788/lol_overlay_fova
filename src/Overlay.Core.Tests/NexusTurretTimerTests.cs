using Overlay.Core;
using Overlay.Core.EventBus;
using Overlay.Core.NexusTurret;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for the patch-15.1 Nexus ("twin") Turret respawn timer —
/// <see cref="NexusTurretTimer"/>, on the IN-GAME clock (Live Client GameTime seconds).
/// Verifies respawnAtGame = destroyedAtGame + 180 (3:00), the countdown/expiry query driven by
/// the current game time, both twin turrets tracked independently, the NO-INTERFERENCE invariant,
/// and the best-effort <see cref="LiveClientEventPublisher.IsNexusTurretId"/> filter. Times are
/// game-SECONDS. (No Nexus-turret respawn event exists → timeout is the only removal path.)
/// </summary>
public class NexusTurretTimerTests : IDisposable
{
    public NexusTurretTimerTests() => EventBus.EventBus.ResetForTests();

    public void Dispose() => EventBus.EventBus.ResetForTests();

    private static void PublishDestroyed(string turretId, double gameTime)
        => EventBus.EventBus.Publish(
            "GAME.NEXUS_TURRET_DESTROYED", new NexusTurretDestroyedPayload(turretId, gameTime), "TestSource");

    [Fact]
    public void OnDestroyed_TracksRespawnAt_ExactlyDestroyedGameTimePlus180Seconds()
    {
        using var timer = new NexusTurretTimer();
        timer.Start();

        PublishDestroyed("Turret_T1_C_02_A", 500.0);

        var status = Assert.Single(timer.GetActive(500.0));
        Assert.Equal("Turret_T1_C_02_A", status.TurretId);
        Assert.Equal(500.0, status.DestroyedAtGame);
        Assert.Equal(680.0, status.RespawnAtGame);        // exactly +180s (3:00)
        Assert.Equal(180.0, status.RemainingSeconds, precision: 3);
    }

    [Fact]
    public void GetActive_JustBeforeRespawn_StillReportsRemainingSeconds()
    {
        using var timer = new NexusTurretTimer();
        timer.Start();

        PublishDestroyed("Turret_T1_C_02_A", 100.0); // respawns at 280

        var status = Assert.Single(timer.GetActive(279.0)); // 1s before respawn
        Assert.Equal(1.0, status.RemainingSeconds, precision: 3);
    }

    [Fact]
    public void GetActive_AtOrAfterRespawnGameTime_NoLongerReturnsTheTurret()
    {
        using var timer = new NexusTurretTimer();
        timer.Start();

        PublishDestroyed("Turret_T1_C_02_A", 100.0); // respawns at 280

        Assert.Empty(timer.GetActive(280.0)); // exactly at respawn
        Assert.Empty(timer.GetActive(360.0)); // well after
    }

    [Fact]
    public void BothTwinTurrets_AreTrackedIndependently()
    {
        using var timer = new NexusTurretTimer();
        timer.Start();

        PublishDestroyed("Turret_T1_C_02_A", 100.0);
        PublishDestroyed("Turret_T1_C_01_A", 100.0); // same instant — both nexus turrets down

        var active = timer.GetActive(100.0);
        Assert.Equal(2, active.Count);
        Assert.Contains(active, s => s.TurretId == "Turret_T1_C_02_A");
        Assert.Contains(active, s => s.TurretId == "Turret_T1_C_01_A");
    }

    [Fact]
    public void SameTurretDestroyedAgain_AfterExpiry_ReplacesThePriorEntry_NotDuplicated()
    {
        using var timer = new NexusTurretTimer();
        timer.Start();

        PublishDestroyed("Turret_T1_C_02_A", 100.0); // respawns at 280
        PublishDestroyed("Turret_T1_C_02_A", 400.0); // destroyed again after the prior timer elapsed

        var status = Assert.Single(timer.GetActive(400.0));
        Assert.Equal(580.0, status.RespawnAtGame); // re-destruction after expiry → fresh timer
    }

    [Fact]
    public void SameTurretDestroyedAgain_WhileActive_DoesNotResetTheRunningTimer()
    {
        // NO-INTERFERENCE invariant: a running 3:00 timer is not reset by a duplicate event.
        using var timer = new NexusTurretTimer();
        timer.Start();

        PublishDestroyed("Turret_T1_C_02_A", 100.0); // respawns at 280
        PublishDestroyed("Turret_T1_C_02_A", 160.0); // must be ignored

        var status = Assert.Single(timer.GetActive(160.0));
        Assert.Equal(280.0, status.RespawnAtGame);   // unchanged — NOT 340
        Assert.Equal(100.0, status.DestroyedAtGame);
    }

    [Fact]
    public void NewGameClockReset_StalePreviousGameTimer_IsNotShown_AndDisconnectClears()
    {
        using var timer = new NexusTurretTimer();
        timer.Start();

        PublishDestroyed("Chaos_NexusTop", 900.0); // prev game, respawns at 1080
        Assert.Empty(timer.GetActive(5.0));        // new game clock = 5s → stale filtered out

        PublishDestroyed("Chaos_NexusTop", 100.0); // new game destruction not blocked by stale entry
        Assert.Equal(280.0, Assert.Single(timer.GetActive(100.0)).RespawnAtGame);

        EventBus.EventBus.Publish("GAME.DISCONNECTED", null, "TestSource");
        Assert.Empty(timer.GetActive(100.0));
    }

    [Fact]
    public void Dispose_UnsubscribesFromEventBus_NoFurtherTracking()
    {
        var timer = new NexusTurretTimer();
        timer.Start();
        timer.Dispose();

        PublishDestroyed("Turret_T1_C_02_A", 100.0);

        Assert.Empty(timer.GetActive(100.0));
    }

    [Theory]
    // LIVE-CONFIRMED (2026-07-16): twin turrets = mid-lane L1 positions P4 (top-side) / P5 (bot-side).
    [InlineData("Turret_TChaos_L1_P4_392430785_0", true)]  // live: enemy top-side twin turret
    [InlineData("Turret_TChaos_L1_P5_342097928_0", true)]  // live: enemy bot-side twin turret
    [InlineData("Turret_TOrder_L1_P4_111222333_0", true)]  // live: ally top-side twin turret
    // A regular live lane turret (L2/P1) must NOT match — this is the class of the reported false positive.
    [InlineData("Turret_TChaos_L2_P1_555000_0", false)]    // live: top-lane turret, not a twin
    [InlineData("Turret_TChaos_L0_P3_777000_0", false)]    // live: bot-lane turret, not a twin
    // Legacy datamined ids (_C_01/_C_02 twin; kept for fixtures):
    [InlineData("Turret_T1_C_01_A", true)]   // blue bot-nexus turret
    [InlineData("Turret_T1_C_02_A", true)]   // blue top-nexus turret
    [InlineData("Turret_T2_C_01_A", true)]   // red bot-nexus turret
    [InlineData("Turret_T2_C_02_A", true)]   // red top-nexus turret
    // NOT nexus turrets — the earlier false positives that spun up a bogus timer:
    [InlineData("Turret_T1_C_03_A", false)]  // MID inhibitor (tier-3) turret — the reported bug
    [InlineData("Turret_T1_L_01_A", false)]  // TOP inhibitor (tier-3) turret
    [InlineData("Turret_T1_R_01_A", false)]  // BOT inhibitor (tier-3) turret
    [InlineData("Turret_T1_C_05_A", false)]  // mid outer turret
    [InlineData("Turret_T2_L_02_A", false)]  // top inner turret
    [InlineData("", false)]
    public void IsNexusTurretId_MatchesOnlyTheTwinTurrets_NotTier3OrLaneTurrets(string id, bool expected)
        => Assert.Equal(expected, LiveClientEventPublisher.IsNexusTurretId(id));
}
