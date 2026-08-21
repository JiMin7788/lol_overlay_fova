using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Jungle;
using Overlay.Core.Vision;

namespace Overlay.Client;

/// <summary>
/// M31 §38-H orchestration loop: the glue that connects P1 capture → P2 detection → P3 presence.
/// Subscribes to <see cref="IMinimapCaptureSource.FrameCaptured"/>; for each minimap ROI frame it
/// runs <see cref="MinimapDetector.Detect"/> against the current enemy roster's templates and feeds
/// every <see cref="MinimapSighting"/> to <see cref="JunglePresenceTracker.OnSighting"/>. A light
/// interval timer calls <see cref="JunglePresenceTracker.Tick"/> so LOST-by-timeout still fires when
/// no frames arrive (WGC delivers frame-on-change). The tracker self-publishes its
/// <c>UI.NOTIFICATION</c>/<c>VOICE.SPEAK</c> alerts, so nothing else needs wiring here.
///
/// <para>Templates are built once per roster from the enemy champions' DDragon square portraits
/// (decoded to BGRA via WPF — the Client is where image decode lives; P2's
/// <see cref="EnemyTemplate.FromSquareIcon"/> stays a pure byte-buffer function). Rebuilt only when
/// the enemy id-set changes (new game).</para>
///
/// <para>Lives in the Client (needs WPF image decode + the live snapshot). UNVERIFIED end-to-end —
/// the capture half needs a GPU + live game (CLAUDE_CODE_TODO §38); the detect/presence halves have
/// their own unit tests. The ExpectedIconRadius scaling and the confidence threshold are
/// live-tunable (§38-D).</para>
/// </summary>
public sealed class MinimapVisionPipeline : IDisposable
{
    // Icon radius as a fraction of ROI width: DetectionOptions.Default assumes ~10px on a ~280px
    // ROI. Scales the stage-1 radius filter with the calibrated minimap size. LIVE-TUNABLE (§38-D).
    private const double IconRadiusFractionOfRoi = 10.0 / 280.0;
    private const int TickIntervalMs = 200;

    private readonly IMinimapCaptureSource _capture;
    private readonly Func<GameSnapshot?> _snapshot;
    private readonly string _ddragonVersion;
    private readonly Action<string>? _log;

    private readonly MinimapDetector _detector = new();

    // §43-Y/§43-AJ: the icon radius is LEARNED from the rings actually matched, never assumed.
    private double? _calibratedRadiusPx;
    private readonly JunglePresenceTracker _tracker;

    private IReadOnlyList<EnemyTemplate> _templates = Array.Empty<EnemyTemplate>();
    private string? _templateRosterKey;
    private Timer? _tickTimer;
    private volatile bool _disposed;

    // ── Debug frame ring (§43) ──────────────────────────────────────────────────────
    //
    // A misidentification is over before a human can react, so the only way to get a fixture out of
    // a live game is to already be holding the frames when the user notices. Encoding every frame to
    // PNG was measured as the wrong trade: the CPU is affordable (~2-5ms per 280x280 encode) but the
    // DISK is not — 30fps x ~80KB is roughly 150 MB per minute. So the steady-state cost here is one
    // array copy per frame (~313KB, ~0.02ms) into a fixed ring, and PNG encoding happens only when
    // the user actually asks, on the ~2 seconds still in the ring.
    private readonly object _ringGate = new();
    private RingEntry[]? _ring;
    private int _ringNext;
    private int _ringCount;

    private readonly record struct RingEntry(
        byte[] Bgra, int Width, int Height, int Stride, long TimestampMs,
        string Sightings);

    // Diagnostics (surfaced to logs/minimap-vision.log every ~2s) so a live tester can see WHERE the
    // chain breaks: frames==0 → capture/native issue; templates==0 → roster/portrait issue;
    // frames>0 & templates>0 & sightings==0 → calibration/ROI or detection-threshold issue.
    private int _diagFrames;
    private int _diagSightings;
    private long _lastDiagMs = Environment.TickCount64;
    // Per-window breakdown of WHICH champions got sighted + peak confidence + last normalized ROI
    // position (0..1, same frame as StructureMinimapLayout), so a live tester can judge BOTH identity
    // and POSITION correctness (compare a champ's (x,y) to where it actually is on the minimap / to
    // StructureMinimapLayout's structure anchors).
    private readonly Dictionary<string, (int Count, double MaxConf, double X, double Y)> _diagByChamp
        = new(StringComparer.Ordinal);

    // (TODO 43-AK) Over-detection alarm. A single frame cannot legitimately contain more sightings
    // than there are enemies — each champion occupies one point on the minimap. When it does, stage 1
    // is finding icons that are not there, which is the suspected reason nothing is ever LOST (every
    // champion reads as continuously visible, so the debounce never elapses) and therefore why both
    // the afterimage and the disappear alert are silent. Counted per frame rather than per window
    // because the existing 2s totals cannot distinguish "many frames, one hit each" — which is normal
    // — from "one frame, many hits", which is not.
    private int _diagOverFrames;
    private int _diagMaxPerFrame;

    public MinimapVisionPipeline(
        IMinimapCaptureSource capture, Func<GameSnapshot?> snapshot,
        string ddragonVersion, Action<string>? log = null, Func<double>? lostDebounceMs = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _ddragonVersion = ddragonVersion;
        _log = log;
        _tracker = new JunglePresenceTracker(snapshot)
        {
            LostDebounceMsProvider = lostDebounceMs,
            Diagnostics = log,
            ImplausibleSightingObserved = OnImplausibleSighting,
        };
    }

    public void Start()
    {
        _tracker.Start();
        _capture.FrameCaptured += OnFrame;
        _tickTimer = new Timer(_ => SafeTick(), null, TickIntervalMs, TickIntervalMs);
    }

    private void OnFrame(MinimapFrame frame)
    {
        if (_disposed) return;
        try
        {
            _diagFrames++; // counts frames reaching the pipeline, independent of templates
            EnsureTemplates();
            if (_templates.Count == 0) return;

            var options = OptionsFor(frame.Width);
            IReadOnlyList<MinimapSighting> sightings = _detector.Detect(frame, _templates, options);
            RecordDebugFrame(frame, sightings);
            FeedRadiusCalibrator(frame, PriorRadiusPx(frame.Width));
            _diagSightings += sightings.Count;
            if (sightings.Count > _diagMaxPerFrame) _diagMaxPerFrame = sightings.Count;
            if (sightings.Count > _templates.Count) _diagOverFrames++;
            for (int i = 0; i < sightings.Count; i++)
            {
                var s = sightings[i];
                int c = _diagByChamp.TryGetValue(s.ChampionId, out var v) ? v.Count : 0;
                double mc = v.MaxConf;
                _diagByChamp[s.ChampionId] = (c + 1, Math.Max(mc, s.Confidence), s.MapPos01.X, s.MapPos01.Y);
                _tracker.OnSighting(s, frame.Flipped);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"minimap pipeline frame error ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private void SafeTick()
    {
        try
        {
            if (_disposed) return;
            _tracker.Tick();
            MaybeLogDiag();
        }
        catch (Exception ex) { _log?.Invoke($"minimap tick error: {ex.Message}"); }
    }

    /// <summary>Emit a rolling diagnostic line every ~2 s. Reads the chain in one glance:
    /// frames arriving, templates built (and for whom), sightings emitted.</summary>
    private void MaybeLogDiag()
    {
        long now = Environment.TickCount64;
        if (now - _lastDiagMs < 2000) return;

        string breakdown = _diagByChamp.Count == 0
            ? ""
            : " → " + string.Join(", ", _diagByChamp
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv => $"{kv.Key}×{kv.Value.Count}@{kv.Value.MaxConf:F2}({kv.Value.X:F2},{kv.Value.Y:F2})"));

        _log?.Invoke(
            $"pipeline diag (last {now - _lastDiagMs} ms): frames={_diagFrames}, capturing={_capture.IsCapturing}, " +
            $"templates={_templates.Count}{(_templateRosterKey is null ? "" : $" [{_templateRosterKey}]")}, sightings={_diagSightings}, " +
            $"maxPerFrame={_diagMaxPerFrame}, overFrames={_diagOverFrames}{breakdown}");

        _diagFrames = 0;
        _diagSightings = 0;
        _diagOverFrames = 0;
        _diagMaxPerFrame = 0;
        _diagByChamp.Clear();
        _lastDiagMs = now;
    }

    // ── Debug frame ring (§43) ──────────────────────────────────────────────────────

    /// <summary>Frames retained. 60 at the 30fps default is ~2 s of history and ~19 MB — long enough
    /// to cover the gap between a wrong callout and a human reacting to it.</summary>
    public const int DebugRingFrames = 60;

    /// <summary>True while frames are being retained. Off by default: this costs RAM, and a user who
    /// is not hunting a misdetection should not pay for it.</summary>
    public bool DebugCaptureEnabled { get; set; }

    /// <summary>Raised (payload: a short reason for the folder name) when the retained frames should
    /// be written out on their own. Handled by whoever owns the log directory layout.</summary>
    public event Action<string>? AutoDumpRequested;

    /// <summary>Auto-dumps allowed per game. Each is roughly 19MB, and the interesting failures
    /// cluster — without a cap one bad minute would fill the disk with near-identical evidence.</summary>
    public const int MaxAutoDumpsPerGame = 5;

    /// <summary>Minimum gap between auto-dumps. The ring only holds two seconds, so dumping more
    /// often than this mostly re-exports frames already written.</summary>
    public const double AutoDumpMinIntervalMs = 20_000;

    private int _autoDumps;
    private long _lastAutoDumpMs;

    /// <summary>(§43-AR) Writes the ring when the tracker rejects a sighting as physically
    /// impossible. That rejection is the earliest moment the system itself can tell something is
    /// wrong, and it happens while the frames are still in memory — which is the whole difficulty
    /// with a hotkey: a 20-minute session produced several visible misdetections and no dump,
    /// because the error is over before a hand reaches Alt+3.</summary>
    private void OnImplausibleSighting(string championId)
    {
        if (!DebugCaptureEnabled || _disposed) return;
        if (_autoDumps >= MaxAutoDumpsPerGame) return;

        long now = Environment.TickCount64;
        if (_autoDumps > 0 && now - _lastAutoDumpMs < AutoDumpMinIntervalMs) return;
        _lastAutoDumpMs = now;
        _autoDumps++;

        _log?.Invoke(
            $"minimap: implausible jump for '{championId}' — auto-dumping retained frames " +
            $"({_autoDumps}/{MaxAutoDumpsPerGame} this game)");
        AutoDumpRequested?.Invoke("jump-" + championId);
    }

    private void RecordDebugFrame(MinimapFrame frame, IReadOnlyList<MinimapSighting> sightings)
    {
        if (!DebugCaptureEnabled) return;

        // What the detector CONCLUDED is the whole point of the fixture — a frame without its verdict
        // cannot tell us whether the identity was wrong or the icon was never found at all.
        string verdict = sightings.Count == 0
            ? "none"
            : string.Join(" ", sightings.Select(s =>
                $"{s.ChampionId}@{s.Confidence:F3}({s.MapPos01.X:F3},{s.MapPos01.Y:F3})"));

        var copy = new byte[frame.Bgra.Length];
        Buffer.BlockCopy(frame.Bgra, 0, copy, 0, frame.Bgra.Length);

        lock (_ringGate)
        {
            _ring ??= new RingEntry[DebugRingFrames];
            _ring[_ringNext] = new RingEntry(
                copy, frame.Width, frame.Height, frame.Stride, frame.TimestampMs, verdict);
            _ringNext = (_ringNext + 1) % DebugRingFrames;
            if (_ringCount < DebugRingFrames) _ringCount++;
        }
    }

    /// <summary>Writes everything currently in the ring to <paramref name="dir"/> as PNGs plus an
    /// index file, oldest first, and returns how many frames were written.
    ///
    /// <para>Encoding happens HERE rather than per frame — that is the whole point of the ring (see
    /// the field comment). Writing ~60 PNGs takes a few hundred ms once, which is acceptable for an
    /// explicit user action and would not be at 30fps.</para></summary>
    public int DumpDebugFrames(string dir)
    {
        RingEntry[] snapshot;
        int count, next;
        lock (_ringGate)
        {
            if (_ring is null || _ringCount == 0) return 0;
            snapshot = (RingEntry[])_ring.Clone();
            count = _ringCount;
            next = _ringNext;
        }

        Directory.CreateDirectory(dir);
        var index = new List<string> { "file	timestampMs	detector_verdict" };
        int written = 0;

        // Oldest first: _ringNext points at the slot that would be overwritten next, i.e. the oldest
        // entry once the ring has wrapped.
        int start = count < DebugRingFrames ? 0 : next;
        for (int i = 0; i < count; i++)
        {
            var e = snapshot[(start + i) % DebugRingFrames];
            if (e.Bgra is null) continue;
            string name = $"frame_{i:D3}.png";
            try
            {
                var bmp = BitmapSource.Create(
                    e.Width, e.Height, 96, 96, PixelFormats.Bgra32, null, e.Bgra, e.Stride);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(dir, name));
                encoder.Save(fs);
                index.Add($"{name}	{e.TimestampMs}	{e.Sightings}");
                written++;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"minimap debug dump: {name} failed — {ex.Message}");
            }
        }

        try { File.WriteAllLines(Path.Combine(dir, "index.tsv"), index); }
        catch (Exception ex) { _log?.Invoke($"minimap debug dump: index failed — {ex.Message}"); }

        _log?.Invoke($"minimap debug dump: {written} frame(s) → {dir}");
        return written;
    }

    /// <summary>Feeds detected icon centres to the radius calibrator and adopts its answer once the
    /// evidence is in. Uses the centres we already found: the starting radius only has to be close
    /// enough to detect SOMETHING, and the calibrator corrects it from the border profile.</summary>
    /// <summary>Learns the icon radius by reading the icon EDGE out of the pixels, via
    /// <see cref="MinimapIconRadiusCalibrator"/>.
    ///
    /// <para>(§43-AQ) That calibrator was written, unit-tested, and then never connected: what ran
    /// instead was the median of the radii at which rings were matched, taken from the first 60
    /// matches of the game and then frozen for its whole duration. Both halves of that are wrong. The
    /// matched radii come from a grid anchored on the current estimate, so they can only agree with
    /// it; and the first 60 matches happen in the opening seconds, when almost nothing is visible, so
    /// an unrepresentative sample decided the rest of the game.</para>
    ///
    /// <para>Measured over two real games at the same 407px ROI, the frozen value came out 18.9px in
    /// one and 13.1px in the other. 13.1 is not a small error: replaying that game's frames, the
    /// enemy jungler was identified 0 times at 13.1 and 53 times at the calibrator's answer, which is
    /// why his APPEAR alert fired once all game. The calibrator returns 18.1px on BOTH games, and
    /// detection at 18.1 matches the best value found by sweeping the radius by hand.</para>
    ///
    /// <para><b>It never feeds on itself, and it never freezes.</b> Calibration centres are found
    /// fresh every frame at the PRIOR — not taken from detection, which runs at the learned radius —
    /// so the loop that once ran 14.5 -> 9.4 -> 8.0px (centres from a shrinking radius multiplying
    /// spurious small blobs) cannot form: the probe range is fixed no matter what has been learned.</para>
    ///
    /// <para>Freezing on the first answer was itself a bug. It published at ~250 samples — the opening
    /// ~10 seconds — and held that for the game. That is fine when champions are visible early, but a
    /// roster whose icons are slow to detect (a Live game of Anivia/Ashe/Malphite, whose blue
    /// portraits the red-ring finder is slower to pick up) has almost nothing but small red map
    /// objects on screen in those seconds, and the profile's outermost ring peak sits at ~10px, not
    /// the real ~18. Frozen, that 10.9px wrecked the whole game: two of the five enemies were never
    /// detected at all. Because the accumulated profile is monotonic, its outermost peak MOVES out to
    /// the true radius as champions finally appear, so this keeps re-resolving and adopts the newest
    /// answer. It settles once the roster is on the map and stays there.</para></summary>
    private void FeedRadiusCalibrator(MinimapFrame frame, double prior)
    {
        // Fresh centres at the prior, independent of the detection radius — this is what makes
        // continuous re-resolution safe (see the doc-comment's feedback note).
        foreach (var icon in MinimapRingFinder.Find(frame, prior, DetectionOptions.Default.RedDominanceMargin))
            if (icon.IsEnemy)
                _radiusCalibrator.Observe(frame, icon.CentreX, icon.CentreY, prior);

        if (_radiusCalibrator.Resolve(prior) is not { } resolved) return;

        if (_calibratedRadiusPx is { } cur && Math.Abs(cur - resolved) < 0.5)
        {
            _calibratedRadiusPx = resolved;   // adopt small drift silently
            return;
        }
        _log?.Invoke(
            $"minimap: icon radius CALIBRATED to {resolved:F1}px " +
            $"(prior {prior:F1}px, {_radiusCalibrator.Samples} samples)");
        _calibratedRadiusPx = resolved;
    }

    private readonly MinimapIconRadiusCalibrator _radiusCalibrator = new();

    /// <summary>Starting estimate before anything has been measured: a fixed fraction of the ROI.
    /// Only ever a seed for the calibrator's probe range — the icon scale is a separate in-game
    /// setting, so this is not reliable on its own (see <see cref="MinimapIconRadiusCalibrator"/>).</summary>
    private static double PriorRadiusPx(int roiWidth) => Math.Max(3.0, roiWidth * IconRadiusFractionOfRoi);

    private DetectionOptions OptionsFor(int roiWidth)
    {
        double radius = _calibratedRadiusPx ?? PriorRadiusPx(roiWidth);
        return DetectionOptions.Default with
        {
            ExpectedIconRadiusPx = radius,
            ToleranceRadiusPx = Math.Max(2.0, radius * 0.4),
        };
    }

    // ── Templates from the enemy roster ───────────────────────────────────────────

    private void EnsureTemplates()
    {
        var snap = _snapshot();
        if (snap is null) return;

        string? myTeam = ResolveActiveTeam(snap);
        if (myTeam is null) return; // can't distinguish allies from enemies → no guessing (P2)

        // (2026-07-20) Build the FULL roster, allies included. Allies are not rendered as sightings
        // — MinimapDetector drops a candidate whose best match is an ally — they exist so the
        // matcher has a correct answer available. With enemies only, an ally icon that survived the
        // red prefilter (an ally Gragas is red-heavy) could only ever be labeled as some enemy,
        // which is exactly the "ally Gragas reported as enemy Anivia" the live pass hit.
        var roster = new List<(string Id, bool IsEnemy)>();
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (string.IsNullOrEmpty(p.ChampionName)) continue;
            string id = ChampionSummary.ResolveKoreanName(p.ChampionName) ?? p.ChampionName;
            if (string.IsNullOrEmpty(id) || roster.Any(r => r.Id == id)) continue;
            roster.Add((id, !string.Equals(p.Team, myTeam, StringComparison.Ordinal)));
        }
        if (!roster.Any(r => r.IsEnemy)) return;   // no enemies to find → nothing to detect

        string key = string.Join(",", roster.Select(r => (r.IsEnemy ? "E:" : "A:") + r.Id)
                                            .OrderBy(s => s, StringComparer.Ordinal));
        if (key == _templateRosterKey) return; // roster unchanged → keep existing templates

        var built = new List<EnemyTemplate>(roster.Count);
        int circles = 0, squares = 0;
        foreach (var (id, isEnemy) in roster)
        {
            byte[]? bgra = TryLoadPortraitBgra(id, out int size, out bool wasCircle);
            if (bgra is null) { _log?.Invoke($"minimap: no portrait for '{id}' — not identifiable"); continue; }
            try
            {
                built.Add(EnemyTemplate.FromSquareIcon(bgra, size, size, id, isEnemy: isEnemy));
                if (wasCircle) circles++; else squares++;
            }
            catch (Exception ex) { _log?.Invoke($"minimap: template build failed for '{id}': {ex.Message}"); }
        }

        _templates = built;
        // The circle icons download in the background, so the first build of a game can fall back to
        // Data Dragon squares. Leaving the roster key unset makes the next frame rebuild, which picks
        // them up as they land — a square-portrait template is the difference between finding a
        // champion every frame and never finding him at all (§43-AP), so it is worth retrying for.
        _templateRosterKey = squares == 0 ? key : null;
        // New roster = new game, and the capture geometry may have changed with it.
        _radiusCalibrator.Reset();
        _calibratedRadiusPx = null;
        _autoDumps = 0;   // the cap is per game, and a new roster is a new game
        int enemies = built.Count(t => t.IsEnemy);
        _log?.Invoke($"minimap: built {built.Count}/{roster.Count} templates " +
                     $"({enemies} enemy + {built.Count - enemies} ally decoy; " +
                     $"{circles} circle + {squares} square) [{key}]");
        LogPairwiseSimilarity(built);
    }

    /// <summary>Percentiles of <see cref="MinimapDetector.MaxAchievableMargin"/> measured over all
    /// 1891 pairs of the 62 cached minimap icons (loop 501). A roster pair is only interesting
    /// relative to this: "0.21" means nothing on its own, "closest 5% of all champion pairs" does.
    /// Re-measure with <c>py tools/measure_icon_separability.py</c> if the icon cache grows or
    /// <see cref="EnemyTemplate.MatchRadiusFraction"/> changes; it prints these three lines ready to
    /// paste. Not a unit test: Overlay.Core.Tests has no image decoder and no icons in its output,
    /// and adding a PNG dependency to compute three constants is not worth it. The MATH behind the
    /// bound is covered by <c>TemplateSeparabilityTests</c>; this is the calibration beside it.</summary>
    private const double BoundP1 = 0.194;
    private const double BoundP5 = 0.220;
    private const double BoundMedian = 0.309;

    /// <summary>(§43-AD) Logs which champions in THIS roster sit closest together, once per roster
    /// build, so the user is told at game start that a pair is awkward and a post-mortem can tell
    /// "the matcher was wrong" apart from "this roster was hard for a colour matcher".
    ///
    /// <para>Logging only. Nothing here changes what the detector does — mutually rejecting risky
    /// pairs is a separate open item, and §43-M requires it be MEASURED before adoption.</para>
    ///
    /// <para>Deliberately NOT phrased as a verdict. The bound never comes near the matcher's margin
    /// on real icons (see <see cref="MinimapDetector.MaxAchievableMargin"/>), so calling a pair
    /// "inseparable" would be inventing certainty. It reports a rank and a percentile.</para></summary>
    private void LogPairwiseSimilarity(List<EnemyTemplate> built)
    {
        if (_log is null || built.Count < 2) return;

        var pairs = new List<(EnemyTemplate A, EnemyTemplate B, double Bound)>();
        for (int i = 0; i < built.Count; i++)
            for (int j = i + 1; j < built.Count; j++)
                pairs.Add((built[i], built[j], MinimapDetector.MaxAchievableMargin(built[i], built[j])));

        // Only pairs involving an enemy matter: two allies being alike costs nothing, because an
        // ally winner is dropped either way.
        var relevant = pairs.Where(p => p.A.IsEnemy || p.B.IsEnemy)
                            .OrderBy(p => p.Bound)         // closest first
                            .ToList();
        if (relevant.Count == 0) return;

        int closest5 = relevant.Count(p => p.Bound < BoundP5);
        _log.Invoke($"minimap: roster separability — {relevant.Count} enemy-involving pairs, "
                    + $"closest {relevant[0].Bound:F3}, median {Median(relevant):F3} "
                    + $"(champion-pool median {BoundMedian:F3}); {closest5} in the pool's closest 5%");

        foreach (var p in relevant.Take(3))
        {
            string rank = p.Bound < BoundP1 ? "closest 1% of all champion pairs"
                        : p.Bound < BoundP5 ? "closest 5%"
                        : p.Bound < BoundMedian ? "closer than average"
                        : "comfortably apart";
            _log.Invoke($"minimap:   {Side(p.A)}{p.A.ChampionId} vs {Side(p.B)}{p.B.ChampionId} "
                        + $"— bound {p.Bound:F3} ({rank})");
        }

        static string Side(EnemyTemplate t) => t.IsEnemy ? "" : "(ally) ";

        static double Median(List<(EnemyTemplate A, EnemyTemplate B, double Bound)> sorted)
            => sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2].Bound
                : (sorted[sorted.Count / 2 - 1].Bound + sorted[sorted.Count / 2].Bound) / 2.0;
    }

    private static string? ResolveActiveTeam(GameSnapshot snap)
    {
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (SamePlayer(p.RiotId, snap.ActivePlayerRiotId)
                || SamePlayer(p.SummonerName, snap.ActivePlayerSummonerName)
                || SamePlayer(p.RiotId, snap.ActivePlayerSummonerName)
                || SamePlayer(p.SummonerName, snap.ActivePlayerRiotId))
            {
                return string.IsNullOrEmpty(p.Team) ? null : p.Team;
            }
        }
        return null;
    }

    private static bool SamePlayer(string a, string b)
        => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Decode a champion's minimap icon to tightly-packed square BGRA, preferring the
    /// CommunityDragon circle icon the game actually draws and falling back to the bundled Data
    /// Dragon square portrait. Null when neither is readable — that champion just won't be
    /// identifiable. <paramref name="wasCircle"/> reports which source was used, so the caller can
    /// retry once the better one has finished downloading.</summary>
    private byte[]? TryLoadPortraitBgra(string championId, out int size, out bool wasCircle)
    {
        size = 0;
        wasCircle = false;
        try
        {
            string? circle = DDragonIconProvider.ChampionCircleIconPathOrNull(championId);
            string path = circle ?? Path.Combine(
                AppContext.BaseDirectory, "data", "ddragon", _ddragonVersion, "img", "champion", championId + ".png");
            wasCircle = circle is not null;
            if (!File.Exists(path)) return null;

            var decoder = BitmapDecoder.Create(
                new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource src = decoder.Frames[0];
            if (src.Format != PixelFormats.Bgra32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            int w = src.PixelWidth, h = src.PixelHeight;
            int stride = w * 4;
            var full = new byte[stride * h];
            src.CopyPixels(full, stride, 0);

            if (w == h) { size = w; return full; }

            // DDragon portraits are square; this only guards a malformed asset (top-left crop).
            int n = Math.Min(w, h);
            var sq = new byte[n * n * 4];
            for (int y = 0; y < n; y++) Array.Copy(full, y * stride, sq, y * n * 4, n * 4);
            size = n;
            return sq;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _tickTimer?.Dispose();
        _tickTimer = null;
        try { _capture.FrameCaptured -= OnFrame; } catch { /* ignore */ }
        _tracker.Dispose();
    }
}
