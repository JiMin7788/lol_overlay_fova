using System.Collections.Concurrent;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Overlay;

namespace Overlay.Core.Jungle;

/// <summary>
/// M31 §C — the last-seen minimap marker, kept so the overlay can leave a half-transparent portrait
/// where an enemy was before they slipped into fog.
///
/// <para><b>State, not events (§43-AX).</b> This used to ADD a mark on a Disappear event and REMOVE
/// it on a Sighted event, which meant a missed or misattributed event left the set wrong — the user
/// saw the count of (live icon + marker) drift to 3 or 6 when it should always be 5, one
/// representation per discovered enemy. The rule is now a pure function of the clock: for every enemy
/// ever seen, remember only its LAST-seen point and time; if it has been seen within
/// <see cref="_graceMs"/> it is "visible" (the game is drawing its icon, so no marker), otherwise a
/// marker is shown at that point. There is exactly one entry per enemy, so the two representations
/// can never both be present nor both absent.</para>
///
/// <para>The grace is not zero. Detection flickers even on a champion in plain view (measured benign
/// gaps p90 632ms / p95 1014ms), and a marker drawn during such a gap would sit on top of the live
/// icon — a spurious sixth thing. A grace above the flicker band keeps a still-visible enemy from
/// sprouting a marker; only a real departure into fog outlasts it.</para>
///
/// <para>Query surface, polled by the renderer each frame (mirrors <c>InhibitorTimer.GetActive</c>).
/// Thread-safe: sightings arrive on the bus thread while the render thread reads.</para>
/// </summary>
public sealed class EnemyAfterimageTracker : IDisposable
{
    /// <param name="EwmaGapMs">A running estimate of this enemy's typical gap BETWEEN sightings while
    /// visible — how flickery its detection is. Used to widen the grace for a champion the detector
    /// keeps losing, so its marker does not blink on and off while it is still on the map.</param>
    private readonly record struct Seen(double X01, double Y01, long AtMs, double EwmaGapMs);

    private readonly ConcurrentDictionary<string, Seen> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ceiling on the adaptive grace. Even an enemy the detector loses constantly must get a
    /// marker within this long of a real departure, or it would never mark at all.</summary>
    public const double MaxGraceMs = 3000.0;

    /// <summary>How many typical gaps to tolerate before marking — the marker appears only once the
    /// enemy has been missing for longer than a couple of its own normal flicker intervals.</summary>
    private const double GraceGapFactor = 2.0;
    // A dead enemy is in fountain, not lurking where last seen, so its marker is suppressed until it
    // is seen again (which also clears the flag).
    private readonly ConcurrentDictionary<string, bool> _dead = new(StringComparer.OrdinalIgnoreCase);

    private readonly IClock _clock;
    private readonly Func<double> _graceMs;
    private string? _sightingSub, _diedSub, _respawnedSub, _connectedSub, _retractedSub, _disconnectedSub;
    private bool _disposed;

    /// <param name="graceMs">How long after a sighting an enemy still counts as visible (no marker).
    /// Above the flicker band — see the class doc.</param>
    /// <param name="clock">Time source; defaults to the system clock.</param>
    public EnemyAfterimageTracker(Func<double>? graceMs = null, IClock? clock = null)
    {
        _clock = clock ?? new SystemClock();
        // 1200 covers the measured p95 benign flicker gap (1014ms) with ~18% headroom; the
        // EWMA-adaptive grace converges to the mean gap, so the base must cover the tail on its
        // own (§43 user report 2026-07-25: ghost drawn under a live icon during a tail gap).
        // User-chosen tradeoff; kept in sync with AppComposition's
        // minimap.afterimage.visibleGraceMs default.
        _graceMs = graceMs ?? (() => 1200.0);
    }

    /// <summary>A last-seen marker: who, where, and which role gate applies (role is unresolved here,
    /// so always the master-switch gate).</summary>
    public readonly record struct Afterimage(
        string ChampionId, string RoleKey, double X01, double Y01, long AtMs);

    /// <summary>Starts consuming the sighting/death stream. Idempotent.</summary>
    public void Start()
    {
        if (_disposed || _sightingSub is not null) return;
        _sightingSub = EventBus.EventBus.Subscribe(JunglePresenceTracker.SightingTopic, OnSighting);
        _diedSub = EventBus.EventBus.Subscribe("GAME.CHAMPION_DIED", OnDied);
        _respawnedSub = EventBus.EventBus.Subscribe("GAME.CHAMPION_RESPAWNED", OnRespawned);
        // A new game must not inherit the previous game's roster of markers, and a FINISHED
        // game must not keep ghosts on screen (2026-07-26 user report).
        _connectedSub = EventBus.EventBus.Subscribe("GAME.CONNECTED", _ => ClearAll());
        _disconnectedSub = EventBus.EventBus.Subscribe("GAME.DISCONNECTED", _ => ClearAll());
        // A track voided as a static structure lock (2026-07-25) must not age into a marker —
        // its "last seen" point was never a champion.
        _retractedSub = EventBus.EventBus.Subscribe(JunglePresenceTracker.SightingRetractedTopic,
            e => { if (e.Payload is string id && id.Length > 0) Clear(id); });
    }

    private void OnSighting(EventBus.Event e)
    {
        if (e.Payload is not JunglePresenceTracker.SightingMark m || string.IsNullOrEmpty(m.ChampionId)) return;
        long now = _clock.NowMs;
        // Seed so a first sighting yields exactly the base grace (ewma*factor == base), then let real
        // gaps pull it toward how flickery this enemy actually is.
        double ewma = _graceMs() / GraceGapFactor;
        if (_lastSeen.TryGetValue(m.ChampionId, out var prev))
        {
            double gap = now - prev.AtMs;
            // Only flicker-scale gaps teach the detector's reliability; a gap longer than the ceiling
            // is a real fog absence, not flicker, and must not inflate the estimate.
            ewma = gap <= MaxGraceMs ? 0.6 * prev.EwmaGapMs + 0.4 * gap : prev.EwmaGapMs;
        }
        _lastSeen[m.ChampionId] = new Seen(m.X01, m.Y01, now, ewma);
        _dead.TryRemove(m.ChampionId, out _);   // seeing it means it is alive and on the map
    }

    private void OnDied(EventBus.Event e)
    {
        if (e.Payload is ChampionDiedPayload d)
            _dead[NormalizeId(d.ChampionName)] = true;
    }

    private void OnRespawned(EventBus.Event e)
    {
        if (e.Payload is ChampionRespawnedPayload r)
            _dead.TryRemove(NormalizeId(r.ChampionName), out _);
    }

    private static string NormalizeId(string name)
        => string.IsNullOrEmpty(name) ? name : ChampionSummary.ResolveKoreanName(name) ?? name;

    /// <summary>Current markers: every discovered enemy not seen within the grace and not dead, at its
    /// last-seen point. Snapshot — safe to enumerate while sightings keep arriving.</summary>
    public IReadOnlyList<Afterimage> GetActive()
    {
        long now = _clock.NowMs;
        double baseGrace = _graceMs();
        var list = new List<Afterimage>(_lastSeen.Count);
        foreach (var (id, seen) in _lastSeen)
        {
            if (_dead.ContainsKey(id)) continue;          // dead → no marker
            // Grace widens for a flickery enemy: tolerate a couple of its own typical gaps before
            // marking, so a still-visible champion the detector keeps losing does not blink a marker.
            double grace = Math.Clamp(seen.EwmaGapMs * GraceGapFactor, baseGrace, MaxGraceMs);
            if (now - seen.AtMs < grace) continue;        // seen recently enough → visible, game draws it
            list.Add(new Afterimage(id, string.Empty, seen.X01, seen.Y01, seen.AtMs));
        }
        return list;
    }

    /// <summary>Drops a marker directly (e.g. the champion became visible by another route).</summary>
    public void Clear(string championId)
    {
        _lastSeen.TryRemove(championId, out _);
        _dead.TryRemove(championId, out _);
    }

    /// <summary>Forgets every enemy — call on game end/start so one game's markers never bleed into
    /// the next.</summary>
    public void ClearAll()
    {
        _lastSeen.Clear();
        _dead.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sub in new[] { _sightingSub, _diedSub, _respawnedSub, _connectedSub, _retractedSub, _disconnectedSub })
            if (sub is not null) { try { EventBus.EventBus.Unsubscribe(sub); } catch { /* already gone */ } }
        _sightingSub = _diedSub = _respawnedSub = _connectedSub = _retractedSub = _disconnectedSub = null;
        ClearAll();
    }
}
