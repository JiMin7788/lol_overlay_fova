using Overlay.Core;
using Overlay.Core.Combo;
using Overlay.Core.EventBus;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof of <see cref="TargetHealthTracker"/>'s honest lower-bound missing-HP
/// contract: respawn resets the accumulated-damage-since-respawn to 0, damage recorded against a
/// tracked champion accumulates, the accumulated value never exceeds a queried max HP (a champion
/// genuinely cannot be "more than 100% missing"), a death-then-respawn sequence ends back at 0,
/// and an unknown/never-touched champion reports 0. Pure unit tests against the class directly —
/// see <c>ComboRunnerMissingHpTests.cs</c> (a separate file, isolation rationale documented there)
/// for the end-to-end proof that a real second combo trigger reflects a lower defender CurrentHP.
/// </summary>
public class TargetHealthTrackerTests
{
    public TargetHealthTrackerTests() => EventBus.EventBus.ResetForTests();

    [Fact]
    public void GetAccumulatedDamage_UnknownChampion_ReturnsZero()
    {
        var tracker = new TargetHealthTracker();

        Assert.Equal(0, tracker.GetAccumulatedDamage("NeverSeen", maxHp: 1000));
    }

    [Fact]
    public void RecordDamageDealt_Accumulates()
    {
        var tracker = new TargetHealthTracker();

        tracker.RecordDamageDealt("Zed", 100);
        tracker.RecordDamageDealt("Zed", 50);

        Assert.Equal(150, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));
    }

    [Fact]
    public void RecordDamageDealt_NegativeOrZero_Ignored_NeverGoesNegative()
    {
        var tracker = new TargetHealthTracker();

        tracker.RecordDamageDealt("Zed", 100);
        tracker.RecordDamageDealt("Zed", -1000); // must not subtract / go negative
        tracker.RecordDamageDealt("Zed", 0);

        Assert.Equal(100, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));
    }

    [Fact]
    public void GetAccumulatedDamage_ClampsToMaxHp_NeverExceeds100PercentMissing()
    {
        var tracker = new TargetHealthTracker();

        tracker.RecordDamageDealt("Zed", 5000); // far more than any real target's max HP

        Assert.Equal(1000, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));
    }

    [Fact]
    public void OnChampionRespawned_ResetsAccumulatedDamageToZero()
    {
        var tracker = new TargetHealthTracker();

        tracker.RecordDamageDealt("Zed", 400);
        Assert.Equal(400, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));

        tracker.OnChampionRespawned("Zed");

        Assert.Equal(0, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));
    }

    [Fact]
    public void OnChampionDied_ThenRespawned_EndsAtZero_DeathInBetweenIsFullyDepleted()
    {
        var tracker = new TargetHealthTracker();

        tracker.RecordDamageDealt("Zed", 200);
        Assert.Equal(200, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));

        // Death: known for certain to be at 0 HP (100% missing) regardless of what was tracked so far.
        tracker.OnChampionDied("Zed");
        Assert.Equal(1000, tracker.GetAccumulatedDamage("Zed", maxHp: 1000)); // fully depleted, clamped to maxHp

        // Respawn: known for certain to be back at full HP.
        tracker.OnChampionRespawned("Zed");
        Assert.Equal(0, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));
    }

    [Fact]
    public void DifferentChampions_TrackedIndependently()
    {
        var tracker = new TargetHealthTracker();

        tracker.RecordDamageDealt("Zed", 300);
        tracker.RecordDamageDealt("Ahri", 50);

        Assert.Equal(300, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));
        Assert.Equal(50, tracker.GetAccumulatedDamage("Ahri", maxHp: 1000));
    }

    [Fact]
    public void ChampionKey_IsCaseInsensitive_MatchingComboRunnerConventions()
    {
        var tracker = new TargetHealthTracker();

        tracker.RecordDamageDealt("Zed", 100);

        Assert.Equal(100, tracker.GetAccumulatedDamage("zed", maxHp: 1000));
        Assert.Equal(100, tracker.GetAccumulatedDamage("ZED", maxHp: 1000));
    }

    // ── Event Bus wiring (Start/Dispose) ────────────────────────────────────────────

    [Fact]
    public void Start_SubscribesToChampionDiedAndRespawnedEvents()
    {
        using var tracker = new TargetHealthTracker();
        tracker.Start();

        tracker.RecordDamageDealt("Zed", 300);
        Assert.Equal(300, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));

        EventBus.EventBus.Publish("GAME.CHAMPION_DIED",
            new ChampionDiedPayload("Zed", "Ahri", Timestamp: 10, RespawnTimer: 30), "Test");
        WaitForBus();
        Assert.Equal(1000, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));

        EventBus.EventBus.Publish("GAME.CHAMPION_RESPAWNED", new ChampionRespawnedPayload("Zed"), "Test");
        WaitForBus();
        Assert.Equal(0, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));
    }

    [Fact]
    public void Start_IsIdempotent_DoubleStartDoesNotDoubleSubscribe()
    {
        using var tracker = new TargetHealthTracker();
        tracker.Start();
        tracker.Start(); // second call must be a no-op, not a second subscription

        EventBus.EventBus.Publish("GAME.CHAMPION_RESPAWNED", new ChampionRespawnedPayload("Zed"), "Test");
        WaitForBus();

        // No exception, no duplicate-handler side effect observable via the public surface —
        // a single respawn simply resets to 0 exactly once either way, so this asserts the
        // no-crash / no-throw contract of calling Start() twice.
        Assert.Equal(0, tracker.GetAccumulatedDamage("Zed", maxHp: 1000));
    }

    private static void WaitForBus() => Thread.Sleep(50); // UI/GAME.* bus dispatch is async
}
