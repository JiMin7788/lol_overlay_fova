using Overlay.Core;
using Overlay.Core.Jungle;
using Overlay.Core.Overlay;
using Xunit;

namespace Overlay.Core.Tests;

/// <summary>
/// M31 §C / §43-AX — the state-based last-seen marker. There is exactly one entry per discovered
/// enemy, and whether it shows as a marker is a pure function of the clock: seen within the grace →
/// visible (the game draws the icon, no marker); past the grace → a marker at the last-seen point;
/// dead → nothing. So (live icon + marker) is always one-per-enemy, never 3 or 6.
/// </summary>
public class EnemyAfterimageTrackerTests : IDisposable
{
    private const double Grace = 1000.0;
    private sealed class FakeClock : IClock { public long NowMs { get; set; } }

    private readonly FakeClock _clock = new();
    private readonly EnemyAfterimageTracker _tracker;

    public EnemyAfterimageTrackerTests()
    {
        EventBus.EventBus.ResetForTests();
        _tracker = new EnemyAfterimageTracker(graceMs: () => Grace, clock: _clock);
        _tracker.Start();
    }

    public void Dispose() { _tracker.Dispose(); EventBus.EventBus.ResetForTests(); }

    /// <summary>Publish a sighting AT the current clock and wait for the bus to record it. The clock
    /// is not advanced here, so the recorded last-seen time is exactly now.</summary>
    private void See(string champ, double x = 0.4, double y = 0.3)
    {
        EventBus.EventBus.Publish(
            JunglePresenceTracker.SightingTopic,
            new JunglePresenceTracker.SightingMark(champ, x, y), "test");
        Thread.Sleep(60);   // UI.* dispatch is async; let it land at the current clock
    }

    private void Die(string champ)
    {
        EventBus.EventBus.Publish("GAME.CHAMPION_DIED",
            new ChampionDiedPayload(champ, "", 0, 0), "test");
        Thread.Sleep(60);
    }

    private void Respawn(string champ)
    {
        EventBus.EventBus.Publish("GAME.CHAMPION_RESPAWNED", new ChampionRespawnedPayload(champ), "test");
        Thread.Sleep(60);
    }

    [Fact]
    public void SeenThenGone_LeavesAMarkerAtTheLastSeenPoint()
    {
        _clock.NowMs = 0;
        See("LeeSin", 0.42, 0.31);

        _clock.NowMs = (long)Grace - 1;                 // still within grace → visible, no marker
        Assert.Empty(_tracker.GetActive());

        _clock.NowMs = (long)Grace + 1;                 // past grace → marker
        var m = Assert.Single(_tracker.GetActive());
        Assert.Equal("LeeSin", m.ChampionId);
        Assert.Equal(0.42, m.X01, 3);
        Assert.Equal(0.31, m.Y01, 3);
    }

    [Fact]
    public void SeenAgain_RemovesTheMarker()
    {
        _clock.NowMs = 0; See("LeeSin");
        _clock.NowMs = (long)Grace + 1;
        Assert.Single(_tracker.GetActive());

        See("LeeSin");                                  // sighting lands at clock Grace+1
        Assert.Empty(_tracker.GetActive());             // now visible again
    }

    [Fact]
    public void ANewSighting_MovesTheMarker_NeverStacks()
    {
        _clock.NowMs = 0; See("LeeSin", 0.20, 0.20);
        _clock.NowMs = 500; See("LeeSin", 0.80, 0.80);  // re-seen at a new spot, within grace

        _clock.NowMs = 500 + (long)Grace + 1;
        var m = Assert.Single(_tracker.GetActive());    // exactly one, at the newer point
        Assert.True(m.X01 > 0.7);
    }

    [Fact]
    public void EveryDiscoveredEnemy_HasExactlyOneRepresentation()
    {
        _clock.NowMs = 0;
        See("Diana", 0.1, 0.1);
        See("Karma", 0.5, 0.5);
        See("Xerath", 0.9, 0.9);

        // Karma re-seen just now (visible); the other two are past grace (markers). Sum stays 3.
        _clock.NowMs = (long)Grace + 1;
        See("Karma", 0.5, 0.5);

        var active = _tracker.GetActive();
        Assert.Equal(2, active.Count);                  // Diana + Xerath as markers
        Assert.DoesNotContain(active, a => a.ChampionId == "Karma");   // visible → no marker
        Assert.Contains(active, a => a.ChampionId == "Diana");
        Assert.Contains(active, a => a.ChampionId == "Xerath");
    }

    [Fact]
    public void ADeadEnemy_ShowsNoMarker_UntilItRespawns()
    {
        _clock.NowMs = 0; See("Sejuani", 0.3, 0.3);
        _clock.NowMs = (long)Grace + 1;
        Assert.Single(_tracker.GetActive());

        Die("Sejuani");
        Assert.Empty(_tracker.GetActive());             // in fountain, not lurking

        Respawn("Sejuani");
        Assert.Single(_tracker.GetActive());            // gone again → marker returns at last point
    }

    /// <summary>§43-AY — a flickery enemy (Xerath: seen on the minimap but detected only ~once every
    /// two seconds) must not blink a marker on and off. The grace widens to bridge its own gaps, so a
    /// gap it survives while visible would still show a marker for a well-detected enemy but not for
    /// it.</summary>
    [Fact]
    public void AFlickeryEnemy_DoesNotBlinkAMarkerBetweenSightings()
    {
        // Establish a ~1600ms typical gap: a champion the detector keeps missing while it is visible.
        long last = 0;
        for (int i = 0; i < 6; i++) { _clock.NowMs = last = i * 1600; See("Xerath", 0.6, 0.6); }
        // A well-detected enemy, seen once, right beside it on the timeline.
        _clock.NowMs = last; See("Diana", 0.2, 0.2);

        // Same ~1s gap for both: Diana (base grace) marks, Xerath (widened grace) does not.
        _clock.NowMs = last + (long)Grace + 1;
        var active = _tracker.GetActive();
        Assert.Contains(active, a => a.ChampionId == "Diana");
        Assert.DoesNotContain(active, a => a.ChampionId == "Xerath");

        // A real departure past the ceiling finally marks even the flickery one.
        _clock.NowMs = last + (long)EnemyAfterimageTracker.MaxGraceMs + 100;
        Assert.Contains(_tracker.GetActive(), a => a.ChampionId == "Xerath");
    }

    [Fact]
    public void ClearAll_ForgetsEveryEnemy()
    {
        _clock.NowMs = 0; See("Diana"); See("Karma");
        _clock.NowMs = (long)Grace + 1;
        Assert.Equal(2, _tracker.GetActive().Count);

        _tracker.ClearAll();
        Assert.Empty(_tracker.GetActive());
    }

    [Fact]
    public void ANewGame_ClearsThePreviousRoster()
    {
        _clock.NowMs = 0; See("Diana");
        _clock.NowMs = (long)Grace + 1;
        Assert.Single(_tracker.GetActive());

        EventBus.EventBus.Publish("GAME.CONNECTED", "", "test");
        Thread.Sleep(60);
        Assert.Empty(_tracker.GetActive());
    }

    [Fact]
    public void AfterDispose_SightingsAreIgnored()
    {
        _tracker.Dispose();
        _clock.NowMs = 0; See("LeeSin");
        _clock.NowMs = (long)Grace + 1;
        Assert.Empty(_tracker.GetActive());
    }
}
