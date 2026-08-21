using Overlay.Core;
using Overlay.Core.Jungle;
using Overlay.Core.Overlay;
using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// §43-G — reject sightings that move a champion further than they could physically have gone.
///
/// <para>From the live pass: "세트가 이상한 데서 감지되고 사라짐". A single mis-identified blob put a
/// champion across the map for one frame, which was enough to move their marker and, once it stopped
/// repeating, to fire a disappear from the wrong place.</para>
///
/// <para>Deliberately NOT a confidence-threshold change. The same session's log showed 21% of windows
/// finding no candidate at all, so trading recall for precision would worsen the larger problem. A
/// physical-plausibility gate costs no recall: a champion who is really there keeps being detected,
/// so the second sighting corroborates and the position is accepted.</para>
/// </summary>
public class ImplausibleJumpRejectionTests : IDisposable
{
    private readonly FakeClock _clock = new();
    private readonly JunglePresenceTracker _tracker;
    private readonly List<EnemyPresenceAlert> _lost = new();
    private readonly string _subId;

    private sealed class FakeClock : IClock
    {
        public long NowMs { get; set; }
    }

    public ImplausibleJumpRejectionTests()
    {
        EventBus.EventBus.ResetForTests();
        _tracker = new JunglePresenceTracker(() => null, clock: _clock);
        _tracker.Start();
        _subId = EventBus.EventBus.Subscribe(JunglePresenceTracker.LostTopic, e =>
        {
            if (e.Payload is EnemyPresenceAlert a) lock (_lost) _lost.Add(a);
        });
    }

    public void Dispose()
    {
        try { EventBus.EventBus.Unsubscribe(_subId); } catch { }
        _tracker.Dispose();
    }

    /// <summary>A champion actually present: enough consecutive nearby sightings for the position
    /// to be CONFIRMED, which the afterimage now requires before it will plant a marker.</summary>
    private void SeeConfirmed(string champ, double x, double y)
    {
        for (int i = 0; i < JunglePresenceTracker.MarkerConfirmFrames; i++)
        {
            See(champ, x, y);
            _clock.NowMs += 55;
        }
    }

    private void See(string champ, double x, double y)
        => _tracker.OnSighting(
            new MinimapSighting(champ, new MapPosition01(x, y), 0.85, _clock.NowMs), flipped: false);

    /// <summary>Drives the tracker past the lost debounce and reads the marker position it reports.</summary>
    private (double X, double Y)? LostPositionAfterTimeout()
    {
        _clock.NowMs += (long)JunglePresenceTracker.DefaultLostDebounceMs + 500;
        _tracker.Tick(_clock.NowMs);
        _clock.NowMs += 500;
        _tracker.Tick(_clock.NowMs);
        // UI.* topics dispatch asynchronously (M15 rule) — wait rather than read straight through.
        SpinWait.SpinUntil(() => { lock (_lost) return _lost.Count > 0; }, TimeSpan.FromSeconds(2));
        lock (_lost) return _lost.Count == 0 ? null : (_lost[^1].X01, _lost[^1].Y01);
    }

    [Fact]
    public void AOneFrameJumpAcrossTheMap_DoesNotMoveTheChampion()
    {
        SeeConfirmed("Sett", 0.20, 0.20);
        _clock.NowMs += 100;
        See("Sett", 0.85, 0.85);          // impossible in 100ms — a misidentification

        var pos = LostPositionAfterTimeout();
        Assert.NotNull(pos);
        Assert.Equal(0.20, pos!.Value.X, 2);
        Assert.Equal(0.20, pos!.Value.Y, 2);
    }

    [Fact]
    public void ARepeatedJump_IsAccepted_SoTeleportsAndFlashesStillWork()
    {
        SeeConfirmed("Sett", 0.20, 0.20);
        _clock.NowMs += 100;
        See("Sett", 0.85, 0.85);          // suspect — held, not trusted
        _clock.NowMs += 100;
        SeeConfirmed("Sett", 0.86, 0.84); // corroborates, and long enough to confirm the position

        var pos = LostPositionAfterTimeout();
        Assert.NotNull(pos);
        Assert.True(pos!.Value.X > 0.8, $"expected the corroborated position, got {pos.Value.X:0.##}");
    }

    [Fact]
    public void NormalMovement_IsNeverRejected()
    {
        SeeConfirmed("Sett", 0.40, 0.40);
        for (int i = 1; i <= 5; i++)
        {
            _clock.NowMs += 200;
            SeeConfirmed("Sett", 0.40 + i * 0.02, 0.40);   // inside the plausible envelope
        }

        var pos = LostPositionAfterTimeout();
        Assert.NotNull(pos);
        Assert.Equal(0.50, pos!.Value.X, 2);
    }

    [Fact]
    public void AfterALongGap_AnyPositionIsAccepted_BecauseTheOldFixIsStale()
    {
        SeeConfirmed("Sett", 0.20, 0.20);
        // Longer than the reference max age: the champion has had time to cross the map legitimately.
        _clock.NowMs += (long)JunglePresenceTracker.JumpReferenceMaxAgeMs + 1000;
        SeeConfirmed("Sett", 0.85, 0.85);

        var pos = LostPositionAfterTimeout();
        Assert.NotNull(pos);
        Assert.True(pos!.Value.X > 0.8, "a stale reference must not veto a fresh sighting");
    }

    [Fact]
    public void TheGateIsPerChampion()
    {
        SeeConfirmed("Sett", 0.20, 0.20);
        _clock.NowMs += 100;
        SeeConfirmed("Nautilus", 0.85, 0.85);   // a DIFFERENT champion — no reason to doubt it

        _clock.NowMs += (long)JunglePresenceTracker.DefaultLostDebounceMs + 500;
        _tracker.Tick(_clock.NowMs);
        _clock.NowMs += 500;
        _tracker.Tick(_clock.NowMs);
        SpinWait.SpinUntil(
            () => { lock (_lost) return _lost.Any(a => a.ChampionId == "Nautilus"); },
            TimeSpan.FromSeconds(2));

        lock (_lost)
        {
            var naut = _lost.FirstOrDefault(a => a.ChampionId == "Nautilus");
            Assert.True(naut.X01 > 0.8, "another champion's position must not be judged against Sett's");
        }
    }
}
