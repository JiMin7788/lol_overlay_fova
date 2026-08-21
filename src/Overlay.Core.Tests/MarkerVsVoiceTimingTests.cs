using Overlay.Core;
using Overlay.Core.Jungle;
using Overlay.Core.Overlay;
using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// §43-V — the marker, the toast and the spoken callout are ONE event.
///
/// <para>They used to run on separate timings and gates (700ms against 2500ms, per-champion against
/// group-merged, confirmed-position-required against not). Each split was defensible on its own, but
/// in play the three appeared to contradict each other — a toast reading "3 enemies" beside a single
/// marker, or a callout for a champion with no marker at all. The user asked for them to be bundled,
/// so every channel now derives from one decision, one position and one payload.</para>
/// </summary>
public class MarkerVsVoiceTimingTests : IDisposable
{
    private sealed class FakeClock : IClock { public long NowMs { get; set; } }

    private readonly FakeClock _clock = new();
    private readonly JunglePresenceTracker _tracker;
    private readonly List<EnemyPresenceAlert> _marker = new();
    private readonly List<EnemyPresenceAlert> _spoken = new();
    private readonly string _lostSub, _alertSub;

    public MarkerVsVoiceTimingTests()
    {
        EventBus.EventBus.ResetForTests();
        _tracker = new JunglePresenceTracker(() => null, clock: _clock);
        _tracker.Start();
        _lostSub = EventBus.EventBus.Subscribe(JunglePresenceTracker.LostTopic,
            e => { if (e.Payload is EnemyPresenceAlert a) lock (_marker) _marker.Add(a); });
        _alertSub = EventBus.EventBus.Subscribe("UI.ENEMY_PRESENCE",
            e => { if (e.Payload is EnemyPresenceAlert a && a.Kind != EnemyAlertKind.Appear)
                       lock (_spoken) _spoken.Add(a); });
    }

    public void Dispose()
    {
        try { EventBus.EventBus.Unsubscribe(_lostSub); } catch { }
        try { EventBus.EventBus.Unsubscribe(_alertSub); } catch { }
        _tracker.Dispose();
    }

    private void See(string champ, double x = 0.4, double y = 0.4)
        => _tracker.OnSighting(
            new MinimapSighting(champ, new MapPosition01(x, y), 0.85, _clock.NowMs), flipped: false);

    /// <summary>A champion actually present: enough consecutive nearby sightings to confirm the
    /// position, which every channel now requires.</summary>
    private void SeeConfirmed(string champ, double x = 0.4, double y = 0.4)
    {
        for (int i = 0; i < JunglePresenceTracker.MarkerConfirmFrames; i++)
        {
            See(champ, x, y);
            _clock.NowMs += 55;
        }
    }

    private void Advance(long ms)
    {
        for (long done = 0; done < ms; done += 100)
        {
            _clock.NowMs += 100;
            _tracker.Tick(_clock.NowMs);
        }
    }

    private void Settle() => SpinWait.SpinUntil(() => false, 120);
    private int Markers() { lock (_marker) return _marker.Count; }
    private int Spoken() { lock (_spoken) return _spoken.Count; }

    [Fact]
    public void ATypicalFlicker_ProducesNothingOnAnyChannel()
    {
        // 300ms is the measured p75 of benign flicker.
        SeeConfirmed("LeeSin");
        Advance(300);
        SeeConfirmed("LeeSin");
        Settle();

        Assert.Equal(0, Markers());
        Assert.Equal(0, Spoken());
    }

    [Fact]
    public void ARealDisappearance_FiresEveryChannel_WithTheSameChampionAndPlace()
    {
        SeeConfirmed("LeeSin", 0.31, 0.62);
        Advance((long)JunglePresenceTracker.DefaultLostDebounceMs + 500);
        Settle();

        Assert.True(Markers() > 0);
        Assert.True(Spoken() > 0);
        lock (_marker) lock (_spoken)
        {
            Assert.Equal(_spoken[^1].ChampionId, _marker[^1].ChampionId);
            Assert.Equal(_spoken[^1].X01, _marker[^1].X01, 3);
            Assert.Equal(_spoken[^1].Y01, _marker[^1].Y01, 3);
            Assert.Equal(_spoken[^1].ZoneKey, _marker[^1].ZoneKey);
        }
    }

    /// <summary>
    /// The voice used to announce a disappearance the marker had refused to draw, because only the
    /// marker required a corroborated position. Silence must now be silence everywhere.
    /// </summary>
    [Fact]
    public void AnUnconfirmedBlip_IsSilentOnEveryChannel()
    {
        See("Sett", 0.9, 0.1);              // one frame, never repeated
        Advance((long)JunglePresenceTracker.DefaultLostDebounceMs + 800);
        Settle();

        Assert.Equal(0, Markers());
        Assert.Equal(0, Spoken());
    }

    /// <summary>
    /// Simultaneous losses used to collapse into one group toast carrying no champion id, which the
    /// marker could not follow — the toast said "3 enemies" while at most one marker appeared.
    /// Each champion now reports itself on both channels.
    /// </summary>
    [Fact]
    public void ThreeChampionsLostTogether_ReportThreeOnBothChannels()
    {
        SeeConfirmed("LeeSin", 0.20, 0.20);
        SeeConfirmed("Ahri", 0.50, 0.50);
        SeeConfirmed("Jinx", 0.80, 0.80);
        Advance((long)JunglePresenceTracker.DefaultLostDebounceMs + 500);
        Settle();

        lock (_marker) lock (_spoken)
        {
            Assert.Equal(3, _marker.Select(a => a.ChampionId).Distinct().Count());
            Assert.Equal(
                _marker.Select(a => a.ChampionId).OrderBy(x => x, StringComparer.Ordinal),
                _spoken.Select(a => a.ChampionId).OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void AStrayFrame_DoesNotDragTheReportOffTheConfirmedPosition()
    {
        SeeConfirmed("LeeSin", 0.25, 0.25);
        See("LeeSin", 0.90, 0.90);          // one stray frame, then silence
        Advance((long)JunglePresenceTracker.DefaultLostDebounceMs + 500);
        Settle();

        Assert.True(Markers() > 0);
        lock (_marker)
        {
            Assert.Equal(0.25, _marker[^1].X01, 2);
            Assert.Equal(0.25, _marker[^1].Y01, 2);
        }
    }

    /// <summary>The unified delay is measurement-derived: just above the p95 of benign flicker
    /// (1014ms) and well under the 2500ms the voice path used to wait.</summary>
    [Fact]
    public void TheUnifiedDelaySitsAboveMeasuredFlicker()
    {
        Assert.InRange(JunglePresenceTracker.DefaultLostDebounceMs, 1014.0, 2000.0);
    }
}
