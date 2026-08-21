using System.Linq;
// GameSnapshot / ChampionDiedPayload / ChampionRespawnedPayload / EnemyJunglerLocator all live in
// the root Overlay.Core namespace; this file's own namespace (Overlay.Core.Jungle) is a CHILD
// namespace and does not see them without this using.
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;
using Overlay.Core.Vision;

namespace Overlay.Core.Jungle;

/// <summary>
/// M31 P3 (docs/modules/M31_MINIMAP_VISION.md §4/§7/§9) — per-enemy-champion presence state
/// machine driven by P2 <see cref="MinimapSighting"/>s, turning them into Last-Seen HUD/TTS
/// alerts. Supersedes the legacy dead-code jungle-position heuristic (<c>JungleTracker</c>,
/// removed per M19 Changelog v1.1/v1.4).
///
/// <para><b>Input is a direct method call, not an Event Bus subscription.</b> P1/P2 are
/// synchronous/pull-based (a capture source hands frames to
/// <c>MinimapDetector.Detect(frame, templates, options) -&gt; IReadOnlyList&lt;MinimapSighting&gt;</c>,
/// see their own doc comments) — nothing in this codebase publishes sightings on the Event Bus
/// today (M31 P2 Agent Report: "not referenced from AppComposition, the Event Bus, or any startup
/// path"). <see cref="OnSighting"/> is this round's half of that future frame-processing loop's
/// contract: a caller (not yet written — needs P1's native capture backend, which needs a local
/// dotnet+GPU+live-game environment per <c>CLAUDE_CODE_TODO.md</c> §38) calls it once per detected
/// sighting. <see cref="Tick"/> is the other half — see its own doc comment.</para>
///
/// <para><b>Time is driven by explicit calls, not a real background timer</b> (deliberately —
/// spec §7 asks for "pure unit tests: sighting sequences -&gt; alert sequence, debounce, death
/// suppression, cooldowns", which a real <c>System.Threading.Timer</c>-based debounce would make
/// slow/flaky to test). <see cref="Tick"/> must be called periodically by whatever eventually
/// drives the P1/P2 loop (e.g. once per processed frame, or wrapped in a lightweight interval
/// timer at that integration layer) so LOST-by-timeout and the grouped-disappear window actually
/// advance in production; nothing here starts its own clock.</para>
///
/// <para><b>Output alerts go through the EXISTING Event Bus/OverlayCoordinator toast path</b>
/// (§4: "Alerts go through the existing UI.*/OverlayCoordinator toast path + optional M09 TTS
/// line") — <c>UI.NOTIFICATION</c> is already mapped to a generic top-right toast card
/// (<c>OverlayCoordinator.TypeMap</c>/<c>OverlayHost.BuildToastCard</c>) with zero production
/// users before this round, so no HudType/OverlayCoordinator/OverlayHost changes were needed.
/// The §4 "small marker at last-seen point fading over 10s" minimap visual is NOT implemented
/// this round — it is WPF/Overlay.Client rendering, same "육안 검증 필요" category as every other
/// visual-only piece of this codebase, deferred as a follow-up (see the Agent Report).</para>
/// </summary>
public sealed class JunglePresenceTracker : IDisposable
{
    private const string Source = "M31.JunglePresence";

    /// <summary>Fired (payload: champion id string) whenever ANY enemy transitions to visible.
    /// Deliberately not <c>UI.ENEMY_PRESENCE</c>: this is a state signal for the afterimage marker,
    /// not a user-facing alert, and must not be voiced.</summary>
    public const string SightedTopic = "UI.ENEMY_SIGHTED";

    /// <summary>Fired (payload: <see cref="SightingMark"/>) on EVERY plausible sighting, carrying the
    /// map-space position. The afterimage tracker consumes this to keep, per discovered enemy, a
    /// last-seen point and time — from which "visible now vs. show a marker" is a pure function of the
    /// clock, so exactly one representation exists per enemy (§43-AX). Distinct from
    /// <see cref="SightedTopic"/>, which fires only on the visible edge.</summary>
    public const string SightingTopic = "UI.ENEMY_SIGHTING";

    /// <summary>One plausible sighting's champion and map-space point (see <see cref="SightingTopic"/>).</summary>
    public readonly record struct SightingMark(string ChampionId, double X01, double Y01);

    /// <summary>Fired (payload: <see cref="EnemyPresenceAlert"/>, Kind=Disappear) for EVERY enemy
    /// that goes unseen, one event per champion, carrying that champion's own last-seen point.
    ///
    /// <para>(2026-07-20) Kept separate from <c>UI.ENEMY_PRESENCE</c> because the alert path applies
    /// two VOICE-shaped controls that must not reach a visual marker: simultaneous losses are merged
    /// into one GroupDisappear that carries no champion id at all (so a 3-man vanish recorded at most
    /// one afterimage), and a per-champion 8s cooldown suppresses the alert entirely (so a champion
    /// re-lost inside that window recorded none). Rate-limiting speech is right; rate-limiting a
    /// last-seen marker is not — the marker IS the memory the user is relying on.</para></summary>
    public const string LostTopic = "UI.ENEMY_LOST";

    /// <summary>How long an enemy must go unseen before the disappearance is announced on ALL
    /// channels — marker, toast and voice now share this one number.
    ///
    /// <para>Chosen from measurement rather than picked: over 363 closed flicker gaps in real
    /// captures, benign vision gaps run p50 146ms / p90 632ms / p95 1014ms / p99 2312ms. 1200ms sits
    /// just above p95, so nineteen flickers in twenty never produce anything, while a real
    /// disappearance is reported in about a second instead of the 2500ms the voice path used to
    /// wait.</para>
    ///
    /// <para>The earlier 2500ms was a workaround for unreliable detection, and it bought only four
    /// percentage points of flicker suppression over p95 for 1.3 extra seconds of latency. Now that
    /// the position must also be CONFIRMED before anything is emitted, the blips that motivated the
    /// long wait are filtered on evidence instead of on time. Config: <c>minimap.lostDebounceMs</c>.</para></summary>
    public const double DefaultLostDebounceMs = 1200.0;


    /// <summary>Consecutive sightings a position must survive before the afterimage will trust it.
    ///
    /// <para>(2026-07-20) The marker used to plant wherever the LAST sighting was, however brief.
    /// Measured run lengths separate cleanly: the champions that actually get seen persist for a
    /// median of 5-7 frames (~55ms each), while the two that generate phantoms have a median of 2
    /// and two-thirds of their runs are a single frame or two. So a one-frame misidentification was
    /// enough to move a champion's marker somewhere they had never been — and shortening the marker
    /// delay to 700ms made those ghosts appear far more readily than before, which is why the
    /// symptom persisted after that change.</para>
    ///
    /// <para>3 frames keeps roughly two thirds of genuine runs while discarding two thirds of the
    /// blips. The marker falls back to nothing rather than to an unconfirmed point: no marker is
    /// better than one in a place the enemy never was.</para></summary>
    public const int MarkerConfirmFrames = 3;

    /// <summary>How far a sighting may sit from the current run's anchor and still continue it.
    /// Beyond this the run restarts, because a confirmation must mean "seen repeatedly HERE", not
    /// merely "seen repeatedly". Without this a stray frame lands right after a good run, inherits
    /// its length, and confirms a position the champion was never at — which is the exact shape of
    /// the reported ghosts. Generous next to real movement: a champion crosses about 0.03 of the map
    /// per second, so a three-frame run (~165ms) moves far less than this.</summary>
    public const double RunCoherenceRadius01 = 0.05;

    /// <summary>§4 "rate-limited: same-champion alert cooldown 8s" (APPEAR) and §9 decision 1
    /// "per-champion 8s cooldown" (DISAPPEAR) — spec gives the same 8s figure for both.</summary>
    public const double AlertCooldownMs = 8000.0;

    /// <summary>(2026-07-25 live game) No APPEAR/DISAPPEAR alerts while the game clock is under
    /// this. At in-game ~0:00 the detector matched a red STRUCTURE icon as the enemy jungler the
    /// frame after templates built (conf 0.82 static at (0.87,0.14)) and spoke "적 정글 발견 · 탑".
    /// 20s, not 60 (user pushback: pre-minion INVADE fights are real info): before 0:20 an enemy
    /// physically cannot have crossed into anyone's vision, while a level-1 invade is spotted
    /// from ~0:25 on — that window stays live, protected by the confidence gate below instead.
    /// State still tracks; only the alerts are quiet.</summary>
    public const double OpeningQuietGameSeconds = 20.0;

    /// <summary>Through the invade window (clock under this) a jungler APPEAR must come from a
    /// sighting at or above <see cref="EarlyAppearMinConfidence"/>. The structure-lock false
    /// appears measure conf 0.82 while real matches sit at p50 0.92, so this silences icon locks
    /// during the one stretch the quiet period no longer covers, at the cost of delaying an
    /// unusually weak real match a frame or two (real tracks mix in ≥0.88 frames within ~1s).
    /// After this the static-pin veto alone carries the protection.</summary>
    public const double EarlyStrictGameSeconds = 90.0;

    /// <summary>See <see cref="EarlyStrictGameSeconds"/>.</summary>
    public const double EarlyAppearMinConfidence = 0.88;

    /// <summary>(2026-07-25, two live games + frame dump) Sightings below this are not ingested
    /// at all — not tracked, not fed to the afterimage. Every confirmed FALSE identification sat
    /// in a low band: red structure-icon locks at 0.82, and the user's OWN white-framed minimap
    /// icon matched as an enemy (Draven's self icon claimed as Galio at 0.75-0.76 in the fountain
    /// corner, dump 20260725_210759) — that one followed the player around as a ghost afterimage
    /// all game. Genuine matches run 0.91-0.93 (weakest suspected-real observation 0.85), so 0.84
    /// splits the measured gap [0.82, 0.85]. A real track's rare sub-floor frames are redundant —
    /// the same track produces high-confidence frames continuously and the lost-debounce bridges
    /// the gap.</summary>
    public const double MinIngestConfidence = 0.84;

    /// <summary>(2026-07-25 live game) A sighting stream that stays within this radius of one
    /// point for <see cref="StaticVetoMs"/> is a STRUCTURE icon lock, not a champion: the false
    /// jungler track sat pixel-still on red tower icons ((0.87,0.14) then (0.90,0.27)) at conf
    /// 0.82 for tens of seconds, while real matches wander (p50 conf 0.92, ~0.03 map/s movement).
    /// The track is quietly forgotten (state Unseen — no DISAPPEAR) and its afterimage retracted
    /// via <see cref="SightingRetractedTopic"/>. A truly motionless real champion (AFK) is
    /// re-announced as soon as it moves again. Radius is tight (structure wobble measured
    /// ≤0.003) so a shopping-but-visible champion's micro-movement escapes it.</summary>
    public const double StaticVetoRadius01 = 0.008;

    /// <summary>See <see cref="StaticVetoRadius01"/>.</summary>
    public const double StaticVetoMs = 8000.0;

    /// <summary>A structure lock produces near-CONTINUOUS sightings (every processed frame); a
    /// champion re-sighted at the same spot after a real gap (lost vision, respawn walk-back) is
    /// not a lock. A silence at the anchor longer than this resets the static clock, so only an
    /// unbroken pin can accumulate the <see cref="StaticVetoMs"/> needed to veto.</summary>
    public const double StaticContinuityGapMs = 2000.0;

    /// <summary>Published (payload: champion id string) when a champion's track is voided as a
    /// static structure lock — listeners holding derived state for it (the afterimage's last-seen
    /// point) must drop that state instead of aging it into a marker.</summary>
    public const string SightingRetractedTopic = "UI.ENEMY_SIGHTING_RETRACTED";


    /// <summary>How long an enemy must go unseen before a DISAPPEAR is emitted. Defaults to
    /// <see cref="DefaultLostDebounceMs"/>; exposed so tests can drive the state machine without
    /// real-time waits and so a future config wiring can tune it.</summary>
    public double LostDebounceMs { get; init; } = DefaultLostDebounceMs;


    /// <summary>Optional live override for <see cref="LostDebounceMs"/> (config-backed), read on
    /// every tick so a settings change applies without restarting. Null/invalid falls back.</summary>
    public Func<double>? LostDebounceMsProvider { get; init; }

    /// <summary>Optional diagnostic sink (wired to <c>logs/minimap-vision.log</c> in production).
    ///
    /// <para>(2026-07-20, TODO 43-AM) Exists for ONE open question: the jungler APPEAR alert has
    /// never fired in a live game, and the two candidate causes are indistinguishable from the
    /// outside. Either the two sides of the comparison in <see cref="MaybeRaiseAppear"/> are in
    /// different identity spaces ("오공" vs "MonkeyKing", i.e. <see cref="NormalizeId"/> failing
    /// because <see cref="ChampionLocalizationRepository"/> was not initialized yet), or the
    /// comparison is fine and the method is simply never reached a second time — over-detection
    /// (43-AK) keeps every champion permanently Visible, so the not-visible→visible edge that gates
    /// this call happens once per champion per game and may well happen before the snapshot is
    /// usable. Logging every call records both the values AND the call count, which separates
    /// them.</para></summary>
    public Action<string>? Diagnostics { get; init; }

    /// <summary>Raised (payload: champion id) when a sighting is rejected because the champion could
    /// not physically have moved there — see <see cref="IsPlausible"/>.
    ///
    /// <para>(§43-AR) This exists so the frames behind a misdetection can be captured without the
    /// user reacting in time. The debug ring holds the last two seconds, but it is only written to
    /// disk on a hotkey, and a wrong callout is over long before a hand reaches the keyboard — a
    /// 20-minute session produced several visible errors and not one usable dump. An implausible
    /// jump is the machine noticing the same thing the user does, a frame or two earlier.</para>
    ///
    /// <para>Fires per rejected sighting, which can be many per second; whatever listens is
    /// responsible for rate-limiting itself.</para></summary>
    public Action<string>? ImplausibleSightingObserved { get; init; }

    private double CurrentLostDebounceMs
    {
        get
        {
            if (LostDebounceMsProvider is null) return LostDebounceMs;
            try
            {
                double v = LostDebounceMsProvider();
                return v > 0 ? v : LostDebounceMs;
            }
            catch { return LostDebounceMs; }
        }
    }

    /// <summary>(§43-G) How far, in normalized map units, a champion may move between sightings
    /// before the newer one is treated as suspect.
    ///
    /// <para>Champion movement speed tops out near 500 units/s on a ~14900-unit map, so about 0.034
    /// per second; 0.15 leaves headroom for dashes, Flash, and a stale previous fix. Beyond that the
    /// sighting is either a teleport or a misidentification, and the two are told apart by whether
    /// it repeats — see <see cref="JumpCorroborationWindowMs"/>.</para></summary>
    public const double MaxPlausibleJump01 = 0.15;

    /// <summary>(§43-G) How long a suspect jump waits for a second sighting near the same spot.
    /// A real teleport keeps producing sightings there; a one-frame misidentification does not.</summary>
    public const double JumpCorroborationWindowMs = 1200.0;

    /// <summary>(§43-G) A previous fix older than this is too stale to judge a jump against —
    /// the champion has had time to legitimately cross the map.</summary>
    public const double JumpReferenceMaxAgeMs = 3000.0;

    private enum PresenceState { Unseen, Visible, Lost }

    private sealed class ChampionState
    {
        public PresenceState State = PresenceState.Unseen;
        public double LastX01;
        public double LastY01;
        public double LastSightingMs = double.NegativeInfinity;
        public double LastAppearAlertMs = double.NegativeInfinity;
        public double LastDisappearAlertMs = double.NegativeInfinity;

        // (§43-G) A sighting that jumped implausibly far is held here until a second one
        // corroborates it, rather than being trusted or discarded outright.
        public double PendingX01;
        public double PendingY01;
        public double PendingAtMs = double.NegativeInfinity;

        /// <summary>Marker already published for the CURRENT absence; reset when seen again.</summary>
        public bool MarkerPublished;

        /// <summary>Consecutive nearby sightings in the run currently in progress.</summary>
        public int RunLength;

        /// <summary>Anchor the current run is measured against.</summary>
        public double RunX01;
        public double RunY01;

        /// <summary>Last position that survived <see cref="MarkerConfirmFrames"/> consecutive
        /// sightings. <see cref="HasConfirmed"/> is false until one does.</summary>
        public bool HasConfirmed;
        public double ConfirmedX01;
        public double ConfirmedY01;

        /// <summary>Static-lock anchor (see <see cref="StaticVetoRadius01"/>): the point every
        /// sighting since <see cref="StaticSinceMs"/> has stayed within the veto radius of.
        /// <see cref="StaticLastMs"/> is the newest sighting at the anchor (vetoed ones included)
        /// so the continuity rule can tell an unbroken pin from a coincidental revisit.</summary>
        public double StaticX01;
        public double StaticY01;
        public double StaticSinceMs = double.NegativeInfinity;
        public double StaticLastMs = double.NegativeInfinity;

        /// <summary>Retraction + diagnostic already emitted for the CURRENT lock (2026-07-25 live
        /// run: one lock logged 442 identical lines, once per frame). Reset when the anchor
        /// resets, so each distinct lock still announces exactly once.</summary>
        public bool StaticVetoAnnounced;
    }


    private readonly Func<GameSnapshot?> _currentSnapshot;
    private readonly Func<string?> _settingsOverrideChampionName;
    private readonly MapZoneLookup _zones;
    private readonly IClock _clock;

    private readonly object _gate = new();
    private readonly Dictionary<string, ChampionState> _states = new(StringComparer.Ordinal);

    private string? _diedSubId;
    private string? _respawnedSubId;
    private string? _connectedSubId;
    private string? _disconnectedSubId;

    /// <param name="currentSnapshot">Latest polled snapshot, e.g. <c>() => AppComposition.LatestSnapshot</c>
    /// (mirrors <see cref="EnemyJunglerSpottedDetector"/>'s constructor convention) — used to
    /// resolve the enemy jungler for the APPEAR gate (<see cref="EnemyJunglerIdentifier"/>).</param>
    /// <param name="settingsOverrideChampionName">Last-resort jungler-ID override (§9 decision 1
    /// step 3) — a callback rather than a raw string so it reflects a live settings change; not
    /// read from config directly (no config-wiring round exists yet for this feature, see
    /// Agent Report). Null/no-op by default.</param>
    /// <param name="zones">Zone lookup for alert text; defaults to <see cref="MapZoneLookup.Default"/>.</param>
    /// <param name="clock">Time source for debounce/cooldown; defaults to the system clock.</param>
    public JunglePresenceTracker(
        Func<GameSnapshot?> currentSnapshot,
        Func<string?>? settingsOverrideChampionName = null,
        MapZoneLookup? zones = null,
        IClock? clock = null)
    {
        _currentSnapshot = currentSnapshot ?? throw new ArgumentNullException(nameof(currentSnapshot));
        _settingsOverrideChampionName = settingsOverrideChampionName ?? (() => null);
        _zones = zones ?? MapZoneLookup.Default;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Subscribe to M01's death/respawn events this tracker consumes. Idempotent.</summary>
    public void Start()
    {
        if (_diedSubId is not null) return;
        _diedSubId = EventBus.EventBus.Subscribe("GAME.CHAMPION_DIED", OnChampionDied);
        _respawnedSubId = EventBus.EventBus.Subscribe("GAME.CHAMPION_RESPAWNED", OnChampionRespawned);
        // Game boundaries reset the whole roster SILENTLY (2026-07-26 user report: game end
        // made every visible enemy "disappear" — sightings stop all at once, the lost debounce
        // elapses, and five callouts fire over a finished game).
        _connectedSubId = EventBus.EventBus.Subscribe("GAME.CONNECTED", _ => ResetAll());
        _disconnectedSubId = EventBus.EventBus.Subscribe("GAME.DISCONNECTED", _ => ResetAll());
    }

    // ── Input #1: sightings (direct call — see class doc) ─────────────────────────

    /// <summary>Feed one P2 detection. <paramref name="flipped"/> is the
    /// <c>MinimapFrame.Flipped</c> that produced <paramref name="sighting"/> (not carried on the
    /// sighting itself — see <see cref="MinimapCoordinateTransform"/>).</summary>
    public void OnSighting(MinimapSighting sighting, bool flipped)
    {
        if (string.IsNullOrEmpty(sighting.ChampionId)) return;
        if (sighting.Confidence < MinIngestConfidence) return; // measured false-ID band — see const

        var (x01, y01) = MinimapCoordinateTransform.ToMapSpace(sighting.MapPos01, flipped);
        double now = _clock.NowMs;
        bool raiseAppear = false;
        bool implausible = false;

        lock (_gate)
        {
            var st = GetOrCreate(sighting.ChampionId);

            // (§43-G, live feedback: "세트가 이상한 데서 감지되고 사라짐") Reject a sighting that
            // teleports the champion further than they could physically have moved — UNLESS a second
            // sighting lands near the same place shortly after. Deliberately NOT a confidence-threshold
            // change: the log already shows 21% of windows finding nothing at all, so anything that
            // trades recall for precision makes the bigger problem worse. This costs nothing on
            // recall because a genuinely-present champion keeps being re-detected.
            if (!IsPlausible(st, x01, y01, now))
                implausible = true;
        }

        // Reported OUTSIDE the state lock: the listener writes ~19MB of PNGs, and holding the lock
        // across that would stall every other sighting in the frame.
        if (implausible)
        {
            ImplausibleSightingObserved?.Invoke(sighting.ChampionId);
            return;
        }

        bool staticVeto = false;
        bool announceVeto = false;
        lock (_gate)
        {
            var st = GetOrCreate(sighting.ChampionId);
            st.PendingAtMs = double.NegativeInfinity;

            // Static structure-lock veto (see StaticVetoRadius01). Anchor updates FIRST so a real
            // champion walking away from a vetoed point releases the veto on the same sighting,
            // and a sighting gap (StaticContinuityGapMs) resets the clock — only an UNBROKEN pin
            // at one point can veto.
            if (st.StaticSinceMs > double.NegativeInfinity
                && now - st.StaticLastMs <= StaticContinuityGapMs
                && Distance01(st.StaticX01, st.StaticY01, x01, y01) <= StaticVetoRadius01)
            {
                st.StaticLastMs = now;
                if (now - st.StaticSinceMs >= StaticVetoMs)
                {
                    st.State = PresenceState.Unseen;   // quiet forget: Tick never emits a DISAPPEAR
                    st.HasConfirmed = false;
                    st.RunLength = 0;
                    staticVeto = true;
                    if (!st.StaticVetoAnnounced)
                    {
                        st.StaticVetoAnnounced = true;
                        announceVeto = true;           // retraction + diag once per lock
                    }
                }
            }
            else
            {
                st.StaticX01 = x01;
                st.StaticY01 = y01;
                st.StaticSinceMs = now;
                st.StaticLastMs = now;
                st.StaticVetoAnnounced = false;
            }

            if (!staticVeto)
            {

            bool wasNotVisible = st.State != PresenceState.Visible;
            st.MarkerPublished = false;

            // A run is broken by a vision gap OR by the sighting moving away from where the run
            // started — "seen repeatedly HERE", not merely "seen repeatedly".
            bool continues = !wasNotVisible
                && Distance01(st.RunX01, st.RunY01, x01, y01) <= RunCoherenceRadius01;
            if (continues)
            {
                st.RunLength++;
            }
            else
            {
                st.RunLength = 1;
                st.RunX01 = x01;
                st.RunY01 = y01;
            }

            if (st.RunLength >= MarkerConfirmFrames)
            {
                st.HasConfirmed = true;
                st.ConfirmedX01 = x01;
                st.ConfirmedY01 = y01;
            }

            st.State = PresenceState.Visible;
            st.LastX01 = x01;
            st.LastY01 = y01;
            st.LastSightingMs = now;
            raiseAppear = wasNotVisible;
            }

            // Cancel any not-yet-flushed disappear for this champion — it was re-sighted before
            // the grouping window (DisappearGroupWindowMs) even announced it, so the earlier LOST
            // is now stale (§4: alerts should reflect current Last-Seen state, not a transient
            // blip already superseded by a fresh sighting).
        }

        if (staticVeto)
        {
            if (announceVeto)
            {
                // Structure lock: retract downstream last-seen state (the afterimage would
                // otherwise age this point into a marker). Once per lock, not per frame.
                EventBus.EventBus.Publish(SightingRetractedTopic, sighting.ChampionId, Source);
                Diagnostics?.Invoke(
                    $"static-veto '{sighting.ChampionId}' at ({x01:F2},{y01:F2}) — " +
                    $"pinned ≥{StaticVetoMs / 1000:F0}s within {StaticVetoRadius01:F3}, structure lock suppressed");
            }
            return;
        }

        // Every plausible sighting feeds the afterimage's last-seen store (§43-AX). Published here,
        // after the plausibility gate, so a rejected teleport never moves a marker.
        EventBus.EventBus.Publish(SightingTopic, new SightingMark(sighting.ChampionId, x01, y01), Source);

        if (raiseAppear)
        {
            // (2026-07-20) Every enemy that becomes visible publishes this, INDEPENDENT of the
            // jungler-only APPEAR callout below. §C's afterimage is supposed to clear the moment you
            // can actually see the champion, but it was listening to APPEAR — which only ever fires
            // for the jungler — so the other four enemies' markers were never removed and simply
            // accumulated on the minimap for the rest of the game. Separate topic, so adding this
            // does not turn the voice callouts into five-champion spam.
            EventBus.EventBus.Publish(SightedTopic, sighting.ChampionId, Source);
            MaybeRaiseAppear(sighting.ChampionId, x01, y01, now, sighting.Confidence);
        }
    }

    // ── Input #2: time advancement (explicit — see class doc) ─────────────────────

    /// <summary>Advance debounce/grouping state to <paramref name="nowMs"/> (defaults to the
    /// clock). Must be called periodically by whatever eventually drives the P1/P2 frame loop for
    /// LOST-by-timeout and the grouped-disappear window to fire in production; every unit test in
    /// this round calls it directly with synthetic timestamps instead of waiting on a real timer.</summary>
    public void Tick(double? nowMs = null)
    {
        double now = nowMs ?? _clock.NowMs;
        var newlyLost = new List<(string championId, double x01, double y01)>();
        double debounce = CurrentLostDebounceMs;

        lock (_gate)
        {
            foreach (var (championName, st) in _states)
            {
                if (st.State != PresenceState.Visible) continue;
                if (now - st.LastSightingMs < debounce) continue;

                st.State = PresenceState.Lost;

                // ONE decision drives the marker, the toast and the callout, and it uses the
                // CONFIRMED position. A champion whose position was never corroborated produces
                // nothing on any channel — the voice used to announce a disappearance the marker
                // had refused to draw, which is one of the ways the three contradicted each other.
                if (st.HasConfirmed)
                    newlyLost.Add((championName, st.ConfirmedX01, st.ConfirmedY01));
            }
        }

        // Resolve role/zone OUTSIDE the state lock — RaiseDisappear reads the snapshot callback.
        foreach (var (championId, x01, y01) in newlyLost)
            RaiseDisappear(championId, x01, y01, now);
    }

    // ── Death/respawn cross-check (§4: "dying is not disappearing") ───────────────

    /// <summary>Forgets every champion without emitting anything — game start/end boundaries
    /// are not "five enemies vanished".</summary>
    private void ResetAll()
    {
        lock (_gate) _states.Clear();
    }

    private void OnChampionDied(Event evt)
    {
        if (evt.Payload is not ChampionDiedPayload died) return;
        lock (_gate)
        {
            if (_states.TryGetValue(NormalizeId(died.ChampionName), out var st))
                st.State = PresenceState.Unseen; // no disappear alert
        }
    }

    private void OnChampionRespawned(Event evt)
    {
        if (evt.Payload is not ChampionRespawnedPayload respawned) return;
        lock (_gate)
        {
            if (_states.TryGetValue(NormalizeId(respawned.ChampionName), out var st))
                st.State = PresenceState.Unseen;
        }
    }

    // ── Alerts ──────────────────────────────────────────────────────────────────

    /// <summary>APPEAR is jungler-only (§9 decision 1: "APPEAR alerts = enemy JUNGLER only").</summary>
    private void MaybeRaiseAppear(string championName, double x01, double y01, double now, double confidence)
    {
        var snap = _currentSnapshot();
        // Opening quiet period (see OpeningQuietGameSeconds): template-build/structure noise at
        // 0:00 — before 0:20 nothing can genuinely be spotted; the invade window stays live.
        if (snap is { HasData: true } && snap.GameTime < OpeningQuietGameSeconds)
        {
            Diagnostics?.Invoke($"appear-gate '{championName}' suppressed: opening quiet " +
                                $"(gameTime {snap.GameTime:F0}s < {OpeningQuietGameSeconds:F0}s)");
            return;
        }
        // Invade-window strictness (see EarlyStrictGameSeconds): structure locks measure conf
        // 0.82, real matches p50 0.92 — a weak match may announce a frame later, never a lock.
        if (snap is { HasData: true } && snap.GameTime < EarlyStrictGameSeconds
            && confidence < EarlyAppearMinConfidence)
        {
            Diagnostics?.Invoke($"appear-gate '{championName}' suppressed: early-game weak match " +
                                $"(conf {confidence:F2} < {EarlyAppearMinConfidence:F2} " +
                                $"at gameTime {snap.GameTime:F0}s)");
            return;
        }
        string? jungler = EnemyJunglerIdentifier.Find(snap, _settingsOverrideChampionName());
        // Both sides normalized: the identifier reports the raw client name, the sighting an
        // English id. Comparing them directly is what silenced this alert on a Korean client.
        string normalizedJungler = jungler is null ? "" : NormalizeId(jungler);
        string normalizedSighting = NormalizeId(championName);
        bool matches = jungler is not null
            && string.Equals(normalizedJungler, normalizedSighting, StringComparison.OrdinalIgnoreCase);

        // 43-AM: one line per call, values AND call count (see Diagnostics).
        Diagnostics?.Invoke(
            $"appear-gate sighting='{championName}'->'{normalizedSighting}' " +
            $"jungler='{jungler ?? "(null)"}'->'{normalizedJungler}' " +
            $"locRepoInit={ChampionLocalizationRepository.IsInitialized} match={matches}");

        if (!matches) return;

        lock (_gate)
        {
            var st = GetOrCreate(championName);
            if (now - st.LastAppearAlertMs < AlertCooldownMs) return;
            st.LastAppearAlertMs = now;
        }

        // APPEAR is jungler-only (gated above), so the role is "jungle" by construction.
        var (zoneKey, zoneLabel) = _zones.ZoneKeyLabel(x01, y01);
        Raise(new EnemyPresenceAlert(
                  EnemyAlertKind.Appear, championName, "jungle", zoneKey, zoneLabel, x01, y01, GroupCount: 1),
              $"적 정글 발견 · {zoneLabel}", $"jungle-appear-{championName}");
    }

    /// <summary>DISAPPEAR covers all five enemies (§9 decision 1), buffered briefly so a
    /// simultaneous multi-champion vision drop becomes one grouped toast (§4/§9).</summary>
    /// <summary>Emits ONE disappearance for one champion on every channel at once.
    ///
    /// <para>(2026-07-20, user request "이벤트 묶어") The marker, the toast and the spoken line used to
    /// run on separate timings and gates — 700ms against 2500ms, per-champion against group-merged,
    /// confirmed-position-required against not. That was deliberate (each channel costs a different
    /// amount when wrong) but it read as the three contradicting each other: a toast saying "3 enemies"
    /// beside a single marker, or a callout for a champion with no marker at all. They now share one
    /// decision, one position and one payload, so whatever one of them says, the others say too.</para>
    ///
    /// <para>Group merging is gone with it. It existed to collapse a simultaneous multi-champion drop
    /// into a single toast, but the merged alert carried NO champion id, which is precisely why the
    /// marker could not follow it.</para></summary>
    private void RaiseDisappear(string championId, double x01, double y01, double now)
    {
        // Opening quiet period — same reasoning as MaybeRaiseAppear; state has already moved to
        // Lost above, so tracking is unaffected, only the announcement is dropped.
        if (_currentSnapshot() is { HasData: true } snap0 && snap0.GameTime < OpeningQuietGameSeconds)
        {
            Diagnostics?.Invoke($"disappear '{championId}' suppressed: opening quiet " +
                                $"(gameTime {snap0.GameTime:F0}s < {OpeningQuietGameSeconds:F0}s)");
            return;
        }

        var (roleKey, roleLabel) = ResolveRole(championId);
        var (zoneKey, zoneLabel) = _zones.ZoneKeyLabel(x01, y01);

        lock (_gate)
        {
            var st = _states[championId];
            if (now - st.LastDisappearAlertMs < AlertCooldownMs) return;
            st.LastDisappearAlertMs = now;
        }

        var alert = new EnemyPresenceAlert(
            EnemyAlertKind.Disappear, championId, roleKey, zoneKey, zoneLabel, x01, y01, GroupCount: 1);

        // The afterimage listens on its own topic; publish the SAME alert object to both so the two
        // can never describe different things.
        EventBus.EventBus.Publish(LostTopic, alert, Source);
        Raise(alert, $"적 {roleLabel} 사라짐 · {zoneLabel}", $"jungle-disappear-{championId}");
    }

    /// <summary>Publishes all three channels for one presence alert: the HUD toast, the spoken line,
    /// and the §0 structured presence event.
    ///
    /// <para>The toast is now a <see cref="HUDPayload"/> (not a bare string) so the card can render the
    /// enemy's champion PORTRAIT + a severity color from <paramref name="alert"/>'s Kind/GroupCount —
    /// still on the <c>UI.NOTIFICATION</c> event (the coordinator uses an embedded HUDPayload verbatim;
    /// see <c>OverlayCoordinator.TryConvert</c>). The id is keyed by kind+champion so an APPEAR and a
    /// DISAPPEAR (or several disappears) can be on screen at once rather than replacing each other, while
    /// a re-published identical alert live-updates its own card. <see cref="Message"/> carries the exact
    /// display text so the wording/role-label logic stays here in one place.</para>
    ///
    /// <para><c>UI.ENEMY_PRESENCE</c> (the structured struct) is unchanged — still published for the
    /// pre-recorded voice player (§B) and minimap afterimage renderer (§C).</para></summary>
    private void Raise(EnemyPresenceAlert alert, string message, string cooldownKey)
    {
        string id = $"UI.NOTIFICATION:{alert.Kind}:{alert.ChampionId}";
        var hud = new HUDPayload(id, HudType.EnemyPresence,
            new EnemyPresenceHud(message, alert.ChampionId, alert.Kind, alert.GroupCount));
        EventBus.EventBus.Publish("UI.NOTIFICATION", hud, Source);

        var speech = new SpeechRequest(Guid.NewGuid().ToString(), message, SpeechPriority.Normal, _clock.NowMs, cooldownKey);
        EventBus.EventBus.Publish("VOICE.SPEAK", speech, Source);

        EventBus.EventBus.Publish("UI.ENEMY_PRESENCE", alert, Source);
    }

    /// <summary>§A step 2 — resolve a disappearing enemy's role from the live snapshot (M30 pattern).
    /// Order: (1) the scoreboard <see cref="ScoreboardEntry.Position"/> field, matched to
    /// <paramref name="championId"/> via <see cref="ChampionSummary.ResolveKoreanName"/> (the row's
    /// champion name is the client-language display name in a live KR game, e.g. "가렌"); (2) role
    /// ITEM fallback (M30/LaneReturnPredictor loop-136: jungle pet 1101–1107, support 3865–3871) when
    /// position is blank (practice tool/ARAM/normal-blind); (3) unknown → <c>RoleKey=""</c> with the
    /// champion's Korean name as the label (for the toast) — §B maps that to its <c>unknown</c> voice.</summary>
    private (string RoleKey, string RoleLabel) ResolveRole(string championId)
    {
        var snap = _currentSnapshot();
        var row = FindRow(snap, championId);

        if (row is not null)
        {
            var byPosition = RoleFromPosition(row.Position);
            if (byPosition is { } rp) return rp;

            var byItem = RoleFromItems(row);
            if (byItem is { } ri) return ri;
        }

        // Unknown role: label = champion's Korean display name for the toast (row name if we matched
        // one, else the localization table, else the raw id as a last resort).
        string korean = row?.ChampionName is { Length: > 0 } n ? n
            : ChampionLocalizationRepository.Get(championId) ?? championId;
        return ("", korean);
    }

    /// <summary>The enemy scoreboard row whose champion resolves to <paramref name="championId"/>
    /// (English DDragon id). Handles the row name being either the English id already or a
    /// client-language name (reverse-translated via <see cref="ChampionSummary.ResolveKoreanName"/>).</summary>
    private static ScoreboardEntry? FindRow(GameSnapshot? snap, string championId)
    {
        if (snap is null || !snap.HasData) return null;
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            string englishId = ChampionSummary.ResolveKoreanName(p.ChampionName) ?? p.ChampionName;
            if (string.Equals(englishId, championId, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    /// <summary>Live Client <see cref="ScoreboardEntry.Position"/> → (RoleKey, Korean label), or null
    /// when the field is blank/unrecognized (§A: TOP→탑, JUNGLE→정글, MIDDLE→미드, BOTTOM→원딜, UTILITY→서폿).</summary>
    private static (string RoleKey, string RoleLabel)? RoleFromPosition(string position) => position.ToUpperInvariant() switch
    {
        "TOP" => ("top", "탑"),
        "JUNGLE" => ("jungle", "정글"),
        "MIDDLE" => ("mid", "미드"),
        "BOTTOM" => ("adc", "원딜"),
        "UTILITY" => ("support", "서폿"),
        _ => null,
    };

    /// <summary>Role-item fallback (only jungle &amp; support carry an identifying inventory item —
    /// see <c>LaneReturnPredictor.RoleItemToLaneKey</c>, loop-136). Null when neither is carried.</summary>
    private static (string RoleKey, string RoleLabel)? RoleFromItems(ScoreboardEntry row)
    {
        for (int j = 0; j < row.ItemCount && j < row.ItemIds.Length; j++)
        {
            int id = row.ItemIds[j];
            if (id is >= 1101 and <= 1107) return ("jungle", "정글");
            if (id is 3865 or 3866 or 3867 or 3871) return ("support", "서폿");
        }
        return null;
    }

    /// <summary>Collapses a champion name into the SAME identity space the P2 sightings use
    /// (the DDragon English id).
    ///
    /// <para>(2026-07-20) This is the fix for two silent failures on a non-English client. Sightings
    /// carry <c>ChampionSummary.ResolveKoreanName(...)</c> output — "LeeSin" — while both
    /// <see cref="EnemyJunglerIdentifier.Find"/> and the M01 death/respawn payloads carry the raw
    /// Live Client name — "리 신". Every comparison between the two was an Ordinal string compare
    /// that could never match, so on a Korean client the APPEAR alert never fired at all, and the
    /// "dying is not disappearing" cross-check never ran, which turned each enemy death into a
    /// false DISAPPEAR alert. On an English client both sides happened to agree, which is why this
    /// survived the test suite and the earlier live passes.</para></summary>
    /// <summary>Decides whether a sighting is physically consistent with the champion's last known
    /// fix. Call inside the state lock. Records a suspect position so the NEXT sighting can confirm
    /// it (a real teleport corroborates itself within a frame or two; noise does not).</summary>
    private static bool IsPlausible(ChampionState st, double x01, double y01, double now)
    {
        // No usable reference: nothing to contradict, so accept.
        if (double.IsNegativeInfinity(st.LastSightingMs)
            || now - st.LastSightingMs > JumpReferenceMaxAgeMs)
            return true;

        double dx = x01 - st.LastX01, dy = y01 - st.LastY01;
        if (dx * dx + dy * dy <= MaxPlausibleJump01 * MaxPlausibleJump01)
            return true;

        // Suspect. Accept only if a recent suspect sighting agrees with this one.
        bool corroborated =
            now - st.PendingAtMs <= JumpCorroborationWindowMs
            && Distance01(st.PendingX01, st.PendingY01, x01, y01) <= MaxPlausibleJump01;

        st.PendingX01 = x01;
        st.PendingY01 = y01;
        st.PendingAtMs = now;
        return corroborated;
    }

    private static double Distance01(double ax, double ay, double bx, double by)
        => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    private static string NormalizeId(string championName)
        => string.IsNullOrEmpty(championName)
            ? championName
            : ChampionDb.ChampionSummary.ResolveKoreanName(championName) ?? championName;

    private ChampionState GetOrCreate(string championName)
    {
        // Canonicalize at the single entry point so every key in _states lives in one identity
        // space, whatever the caller passed.
        championName = NormalizeId(championName);
        if (!_states.TryGetValue(championName, out var st))
        {
            st = new ChampionState();
            _states[championName] = st;
        }
        return st;
    }

    public void Dispose()
    {
        if (_diedSubId is not null) { EventBus.EventBus.Unsubscribe(_diedSubId); _diedSubId = null; }
        if (_respawnedSubId is not null) { EventBus.EventBus.Unsubscribe(_respawnedSubId); _respawnedSubId = null; }
        if (_connectedSubId is not null) { EventBus.EventBus.Unsubscribe(_connectedSubId); _connectedSubId = null; }
        if (_disconnectedSubId is not null) { EventBus.EventBus.Unsubscribe(_disconnectedSubId); _disconnectedSubId = null; }
    }
}
