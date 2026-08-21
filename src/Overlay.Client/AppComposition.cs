using System.Reflection;
using System.Diagnostics;
using System.IO;
using Overlay.Core;
using Overlay.Core.Ads;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Gold;
using Overlay.Core.Hotkeys;
using Overlay.Core.Inhibitor;
using Overlay.Core.Items;
using Overlay.Core.NexusTurret;
using Overlay.Core.Recall;
using Overlay.Core.Runes;
using Overlay.Core.Tts;
using Overlay.Core.Vision;
#if !LIGHT
using Overlay.Capture;
#endif
using Overlay.Client.Hotkeys;
using Overlay.Client.Tts;

namespace Overlay.Client;

/// <summary>
/// E1 composition root: owns the app's live subsystem graph and wires the previously
/// dormant Core modules (M01/M04/M07/M08/M09/M11/M13) into startup so the MVP runs
/// end-to-end. Constructed once by <see cref="MainWindow"/>.
///
/// <para><b>Startup ordering</b> (see <see cref="InitializeAsync"/>):
/// config (this ctor) → M11 repositories (async, off the UI thread) → M07/M08/M09/M04
/// subsystems (bus subscribers) → M01 poller. The poller is started <i>last</i> so the
/// M11 repositories are ready and the bus subscribers are already listening before any
/// <c>GAME.*</c> event is published — no early event can be missed.</para>
///
/// <para><b>Threading.</b> M11 init does file reads + a couple of cache-hit async calls,
/// so it runs on a background <see cref="Task"/> (WPF <c>OnLoaded</c> is not async) and
/// can never block or crash the UI thread. M13 hotkey wiring needs the window HWND and a
/// live <c>HwndSource</c>, so <see cref="WireHotkeys"/> runs on the UI thread once the
/// handle exists (called from <c>MainWindow.OnLoaded</c>).</para>
///
/// <para><b>Graceful degradation.</b> M11 init and hotkey registration are wrapped in
/// try/catch: missing/unreadable cached data or a single conflicting hotkey is logged and
/// skipped, leaving a degraded-but-live overlay rather than a crash.</para>
///
/// <para><b>Single ConfigManager.</b> This class constructs the one shared
/// <see cref="ConfigManager"/> (<see cref="Config"/>) and hands it to <see cref="OverlayHost"/>
/// and every subsystem, closing the loop-11 duplicate-ConfigManager backlog.</para>
/// </summary>
public sealed class AppComposition : IDisposable
{
    /// <summary>The cached Data Dragon patch version checked into the Core output <c>data/</c>
    /// dir. The offline cache is keyed on this version.</summary>
    private const string DataDragonVersion = "16.13.1";

    /// <summary>Fallback champion set (the originally sampled 5) used only before the M11
    /// repository finishes loading the full roster, or if that load fails. Once
    /// <see cref="ChampionRepository.InitializeFromCache"/> completes, <see cref="ChampionIds"/>
    /// serves the entire cached roster instead.</summary>
    private static readonly string[] FallbackChampionIds =
        { "Aatrox", "Ahri", "Annie", "Zed", "Jinx" };

    private readonly ConfigManager _config = new();
    private readonly CancellationTokenSource _cts = new();

    // M29: HOME-window ad slot. Constructed in the ctor (it reads config), disposed with the rest.
    private readonly AdSlotService _ads;

    // M01
    private LiveClientPoller? _poller;
    private LiveClientEventPublisher? _publisher;

    /// <summary>Latest game snapshot captured from the M01 poller, read by the ComboRunner
    /// to build an ExecutionContext when a combo hotkey fires. Written on the poller's
    /// background thread, read on the bus's async dispatch thread — a reference-typed
    /// field, so writes/reads are atomic and "latest wins" is exactly the desired semantics.</summary>
    private volatile GameSnapshot? _latestSnapshot;

    // ComboRunner: closes the hotkey -> combo -> HUD loop (COMBO.TRIGGER -> UI.COMBO_RESULT).
    private ComboRunner? _comboRunner;

    // M09
    private SapiSpeechSynthesizer? _synth;
    private EnemyVoicePlayer? _enemyVoice;

    // M31 §C — last-seen enemy markers for the minimap afterimage. Exposed as a query so the
    // renderer reads it on the render thread (same shape as InhibitorTimer below).
    private Overlay.Core.Jungle.EnemyAfterimageTracker? _afterimages;
    /// <summary>M31 §C: live query for the last-seen enemy portraits the overlay draws.</summary>
    public Overlay.Core.Jungle.EnemyAfterimageTracker? EnemyAfterimages => _afterimages;
    private TtsScheduler? _tts;

    // M07 / M08
    private ItemTracker? _items;
    private RecallTimer? _recall;

    // (loop 125) Same-lane enemy return predictor (minimap-left return timer HUD).
    private LaneReturnPredictor? _laneReturn;
    /// <summary>Exposed so <c>MainWindow</c> can hand the overlay a live query for the current
    /// same-lane-enemy return prediction (same pattern as <see cref="LatestSnapshot"/>).</summary>
    public LaneReturnPredictor? LaneReturnPredictor => _laneReturn;

    // M30 — gated on overlay.items.enemyJunglerSpotted.enabled. Real API-field diffs
    // (CS/item change on the enemy JUNGLE-position row), no coordinate estimate — default ON,
    // same convention as InhibitorTimer/NexusTurretTimer below.
    private EnemyJunglerSpottedDetector? _enemyJunglerSpotted;

    // Enemy legendary-item-completed alert — gated on overlay.items.enemyItemAlert.enabled. Enemy
    // item-list diffs (an enemy's API data only refreshes while visible), no coordinate estimate —
    // default ON, same convention as _enemyJunglerSpotted above.
    private EnemyItemAlertDetector? _enemyItemAlert;

    // M19 §3.2 — gated on overlay.items.inhibitorTimers.enabled
    private InhibitorTimer? _inhibitorTimer;
    /// <summary>(loop 142) Exposed so the overlay can live-query the inhibitor respawn countdowns and
    /// draw the time on the minimap at each destroyed inhibitor (LoL-native-camp-timer style).</summary>
    public InhibitorTimer? InhibitorTimer => _inhibitorTimer;

    // Patch 15.1 — gated on overlay.items.nexusTurretTimers.enabled
    private NexusTurretTimer? _nexusTurretTimer;
    /// <summary>Exposed so the overlay can live-query Nexus-turret respawn countdowns (3:00,
    /// patch 15.1) and draw them on the minimap, same pattern as <see cref="InhibitorTimer"/>.</summary>
    public NexusTurretTimer? NexusTurretTimer => _nexusTurretTimer;

    // M19 §3.3 — gated on overlay.items.globalGold.enabled. Constructed alongside the M01
    // poller (needs its SnapshotAvailable feed directly, not just a GAME.* event payload).
    private GlobalGoldPanel? _globalGold;

    // M04 (kept live for the session; a future dashboard UI drives it)
    private ComboEditor? _comboEditor;

    // M06 — the single RuneEngine instance shared by _comboEditor's ComboEngine AND
    // _comboRunner: ComboRunner arms manual-flag state on this exact instance right before
    // ComboEngine.Execute reads it, so the two must not be separate RuneEngine objects.
    private RuneEngine? _runeEngine;

    // Honest lower-bound missing-HP estimator (see TargetHealthTracker's class doc comment) —
    // shared cross-cutting service, same lifetime pattern as _runeEngine: constructed once here,
    // subscribed to GAME.CHAMPION_DIED/GAME.CHAMPION_RESPAWNED, handed to _comboRunner so every
    // real combo trigger both reads from and records damage into the SAME instance.
    private TargetHealthTracker? _targetHealthTracker;

    // M13. Uses the low-level keyboard hook (LowLevelHotkeyHook) rather than RegisterHotKey
    // (Win32HotkeyHook) because RegisterHotKey's WM_HOTKEY messages are unreliable / not
    // delivered over a focused League game window; the LL hook fires globally over the game.
    // Win32HotkeyHook.cs is left in place but is now unused.
    private LowLevelHotkeyHook? _hotkeyHook;
    private HotkeyRegistry? _hotkeys;

    /// <summary>M19: registration id of the overlay visibility toggle hotkey, kept so a rebind can
    /// unregister the old combo before registering the new one. Null when unregistered/skipped.</summary>
    private string? _toggleHotkeyRegId;

    /// <summary>M19 default overlay toggle combo. Note: SHIFT+TAB overlaps LoL's scoreboard modifier,
    /// so it is rebindable in Settings → 오버레이. The <see cref="LowLevelHotkeyHook"/> maps TAB
    /// (and other named keys), so SHIFT+TAB is matched over the focused game.</summary>
    private const string DefaultToggleHotkey = "SHIFT+TAB";

    /// <summary>Invoked (on the hotkey/message-pump thread) when the overlay toggle hotkey fires.
    /// <c>HomeWindow</c> sets this to marshal a show/hide of the overlay window to the UI thread.
    /// The toggle only changes OUR window's visibility — it sends no input to the game (P4).</summary>
    public Action? OverlayToggleRequested { get; set; }

    /// <summary>Raised once <see cref="ChampionRepository"/>'s background load (see
    /// <see cref="InitializeAsync"/>) has finished — success or degraded/failed, so a listener is
    /// never left hanging. <see cref="ChampionIds"/> only serves the full ~173-champion roster
    /// after this fires; before that it serves the small <see cref="FallbackChampionIds"/> set.
    /// Fired on whatever thread the M11 background load completes on (NOT the UI thread) — mirrors
    /// <see cref="OverlayToggleRequested"/>: the subscriber is responsible for marshaling to the UI
    /// thread via <c>Dispatcher.Invoke</c> before touching any UI element.</summary>
    public event Action? ChampionsReady;

    /// <summary>The single shared config, injected into <see cref="OverlayHost"/> and every
    /// subsystem. Owned (and disposed) by this composition root.</summary>
    public ConfigManager Config => _config;

    /// <summary>The shared M04 combo editor (constructed in <see cref="StartSubsystems"/>).
    /// Exposed so <c>HomeWindow</c>'s combo-settings view drives the same editor whose saves
    /// the overlay/ComboRunner read. Null until the background init reaches subsystem start.</summary>
    public ComboEditor? ComboEditor => _comboEditor;

    /// <summary>The shared M04/M13 combo runner (constructed in <see cref="StartSubsystems"/>).
    /// Exposed so <c>MainWindow.OnLoaded</c> can attach the M02
    /// <see cref="Overlay.Core.Overlay.OverlayCoordinator"/> to it once the overlay host exists
    /// (M02/M04/M13 combo-hotkey toggle-off wiring). Null until the background init reaches
    /// subsystem start.</summary>
    public ComboRunner? ComboRunner => _comboRunner;

    /// <summary>The M13 low-level keyboard hook (constructed in <see cref="WireHotkeys"/>). Exposed
    /// so the M02 overlay window can query currently-held modifier state
    /// (<see cref="LowLevelHotkeyHook.IsModifierHeld"/>) for the click-to-target modifier gate
    /// (loop 38 continuation 12) instead of installing a second hook. Null until hotkeys are
    /// wired (mirrors every other subsystem accessor's pre-init null).</summary>
    public LowLevelHotkeyHook? HotkeyHook => _hotkeyHook;

    /// <summary>Latest per-tick game snapshot captured from the M01 poller (or null before the
    /// first tick / when no game is running). Read by the home views for live stats/scoreboard.</summary>
    public GameSnapshot? LatestSnapshot => _latestSnapshot;

    /// <summary>M29 ad slot backing the HOME banner. Constructed here (not in the window) so it
    /// subscribes to GAME.CONNECTED/DISCONNECTED for the whole app lifetime and there is exactly one
    /// instance — its dormancy rule is what keeps the in-game path ad-free.</summary>
    public AdSlotService Ads => _ads;

    /// <summary>The champion set available to the combo builder / stats — the FULL cached
    /// roster once the M11 repository has loaded (so combos can be built for any of the ~173
    /// cached champions), falling back to the sampled 5 until then / if the load failed.</summary>
    public static IReadOnlyList<string> ChampionIds
        => ChampionRepository.IsInitialized && ChampionRepository.LoadedIds.Count > 0
            ? ChampionRepository.LoadedIds
            : FallbackChampionIds;

    public AppComposition()
    {
        // M29: the slot subscribes to GAME.CONNECTED here — before Start() — so the in-game
        // dormancy rule holds even if a game is already running when the app launches.
        _ads = new AdSlotService(GetBool("ads.enabled", true), GetString("ads.endpoint", ""));
        // ads.enabled defaults TRUE while ads.endpoint defaults EMPTY, so the out-of-the-box state
        // is "ads on, nothing to fetch": AdSlotService nulls a blank endpoint, IsConfigured goes
        // false, and HomeWindow collapses the whole row. That is correct behaviour but it is also
        // completely silent, and silence reads as a bug — a tester can only conclude the ads are
        // broken. Say it once at startup instead. (tools/ad_test_server.py serves a local manifest
        // if you want to see the slot actually render.)
        if (!_ads.IsConfigured && GetBool("ads.enabled", true))
            Log("ads: enabled but no ads.endpoint configured — the slot is collapsed and will "
                + "never fill. This is configuration, not a failure.");

        // M33: champ-select assistant. Kill switch champSelect.enabled (default true); when off,
        // neither the connector loop nor the HOME panel ever exists.
        if (GetBool("champSelect.enabled", true))
        {
            Lcu = new Overlay.Core.Lcu.LcuConnector();
            ChampSelectStore = new Overlay.Core.ChampSelect.ChampSelectPresets(_config);
        }
    }

    /// <summary>M33 LCU connector, or null when champSelect.enabled is false. Started with the
    /// rest of the graph; the HOME panel is its only consumer (zero in-game surface).</summary>
    public Overlay.Core.Lcu.LcuConnector? Lcu { get; }

    /// <summary>M33 per-champion rune/spell preset store (null iff <see cref="Lcu"/> is null).</summary>
    public Overlay.Core.ChampSelect.ChampSelectPresets? ChampSelectStore { get; }

    /// <summary>Kick off the background subsystem graph: M11 repositories, then the
    /// M07/M08/M09/M04 subsystems, then the M01 poller. Runs off the UI thread; any failure
    /// is contained so it cannot take down the window. Call once from the UI thread.</summary>
    public void Start()
    {
        // M33: LCU probe loop is independent of the game-data graph — start it directly (it
        // idles cheaply at 5s probes until a League client exists).
        Lcu?.Start(_cts.Token);

        // Fire-and-forget: the internal try/catch keeps a startup failure from surfacing as
        // an unobserved task exception, and the UI thread is never blocked.
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // ── M11: repositories from the offline cache (before the poller so GAME.* handlers
        //    have item/rune/champion data ready). The DDragon/CDragon clients skip download
        //    when files are already cached, so this works offline. ─────────────────────────
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var ddragonCacheDir = Path.Combine(baseDir, "data", "ddragon", DataDragonVersion);

            // item.ko_KR.json (Part 3) is fetched by DDragonClient.EnsureCachedAsync alongside
            // item.json, but an old cache predating that fetch (or an offline first run before it
            // succeeds) won't have it yet — read defensively and pass null, which ParseItems
            // treats as "no Korean names available" (ItemData.NameKo stays null everywhere).
            var itemKoPath = Path.Combine(ddragonCacheDir, "item.ko_KR.json");
            var itemKoJson = File.Exists(itemKoPath) ? File.ReadAllText(itemKoPath) : null;
            ItemRepository.Initialize(
                DDragonParser.ParseItems(
                    File.ReadAllText(Path.Combine(ddragonCacheDir, "item.json")), itemKoJson));
            RuneRepository.Initialize(
                DDragonParser.ParseRunes(File.ReadAllText(Path.Combine(ddragonCacheDir, "runesReforged.json"))));

            var communityDragonCacheDir = Path.Combine(baseDir, "data", "communitydragon");

            var specialPropertiesDir = Path.Combine(baseDir, "Data", "special_properties");
            if (!Directory.Exists(specialPropertiesDir))
                specialPropertiesDir = null; // InitializeFromCache tolerates null

            // Load the WHOLE cached roster (every champion with a bundled BIN), not just the
            // sampled 5. Runs on a threadpool thread (Task.Run) so parsing ~170 BINs never
            // touches the UI thread — the enclosing task ran synchronously up to here.
            await Task.Run(
                () => ChampionRepository.InitializeFromCache(
                    ddragonCacheDir, communityDragonCacheDir, specialPropertiesDir),
                _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // Degraded overlay: repos stay empty; consumers treat lookups as "unknown".
            Log($"M11 repository init failed; continuing with a degraded overlay: {ex}");
        }

        // ── Champion name localization (best-effort CommunityDragon fetch): its own try/catch,
        //    separate from the M11 block above, since this is strictly less critical than the
        //    repositories that feed damage/combo calculations — a failure here (offline first
        //    run, CDN outage) must never abort the rest of startup. Localization.ChampionName
        //    falls back to its small built-in table, then the raw id, when this repository is
        //    uninitialized or missing an id. ──────────────────────────────────────────────────
        try
        {
            var cdClient = new CommunityDragonClient();
            var summaryPath = await cdClient.EnsureChampionSummaryCachedAsync(_cts.Token).ConfigureAwait(false);
            var summaryJson = await File.ReadAllTextAsync(summaryPath, _cts.Token).ConfigureAwait(false);
            ChampionLocalizationRepository.Initialize(ChampionLocalizationParser.Parse(summaryJson));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log($"Champion name localization fetch failed; falling back to Localization's built-in table: {ex}");
        }

        // Fired unconditionally on both the success and degraded/failed paths above (only the
        // cancelled-shutdown path returns before reaching here) so a listener — e.g.
        // ComboSettingsView, which may have already rendered the small fallback roster before this
        // load finished — always gets a chance to refresh, never hangs waiting forever.
        try { ChampionsReady?.Invoke(); } catch { /* listener's own problem; must not abort startup */ }

        // ── M07/M08/M09/M04: bus subscribers. Started before the poller so no published
        //    GAME.* event is missed. ────────────────────────────────────────────────────────
        try
        {
            StartSubsystems();
        }
        catch (Exception ex)
        {
            Log($"Subsystem start failed: {ex}");
        }

        // ── M01: local Live Client poller (127.0.0.1:2999). With no game running it simply
        //    reports not-connected — that is expected and never crashes the app. ─────────────
        try
        {
            // Tolerant poll config: the Live Client /allgamedata endpoint returns a large JSON
            // and is frequently slow (>200ms) during gameplay. The default 90ms timeout + 3-fail
            // threshold produced FALSE disconnects on brief slowness, which hid the overlay
            // mid-game (and left combo hotkeys firing on a hidden window). A 1s timeout + 250ms
            // interval + higher threshold (see DisconnectFailureThreshold) only declares
            // disconnect on ~sustained failure (game actually closed), not a transient hiccup.
            _poller = new LiveClientPoller(new PollerConfig
            {
                PollInterval = TimeSpan.FromMilliseconds(250),
                RequestTimeout = TimeSpan.FromMilliseconds(1000),
            });
            _publisher = new LiveClientEventPublisher(_poller);
            // Keep the latest snapshot so the ComboRunner can build an ExecutionContext on
            // demand. 'cur' is the poller's REUSABLE buffer (_current), which it swaps and
            // re-parses in place on the very next tick — so we must COPY it, not store the
            // reference, or a combo firing mid-parse (the runner reads _latestSnapshot on an
            // arbitrary hotkey press, off the poll thread) could read a torn/half-updated
            // snapshot. GameSnapshot.CopyTo gives us a stable per-tick capture the poller
            // never mutates. The volatile field publishes the new reference to the reader.
            _poller.SnapshotAvailable += (prev, cur, initial) =>
            {
                var capture = new GameSnapshot();
                cur.CopyTo(capture);
                _latestSnapshot = capture;
            };

            // M19 §3.3 Global Gold Compare — opt-in overlay item (Settings → 오버레이 항목).
            // Needs the poller's own SnapshotAvailable feed (full scoreboard in one tick),
            // so it is constructed here rather than in StartSubsystems (before the poller exists).
            if (GetBool("overlay.items.globalGold.enabled", false))
            {
                _globalGold = new GlobalGoldPanel(_poller);
                _globalGold.Start();
            }

            _poller.Start(_cts.Token);
        }
        catch (Exception ex)
        {
            Log($"M01 poller start failed: {ex}");
        }
    }

    private void StartSubsystems()
    {
        // M09 TTS — gate on config; only construct SAPI when enabled.
        if (GetBool("voice.ttsEnabled", false))
        {
            var volume = GetDouble("voice.ttsVolume", 0.8);
            var cooldownMs = (int)(GetDouble("voice.ttsCooldownSeconds", 5) * 1000);
            _synth = new SapiSpeechSynthesizer(volume);
            _tts = new TtsScheduler(_synth, cooldownMs: cooldownMs);
            _tts.Start();
        }

        // M31 §B enemy-presence voice. Independent of voice.ttsEnabled: these are prerecorded
        // clips, not synthesis, so they work with SAPI off. The player itself no-ops when
        // voice.enemyVoicePack isn't "prerecorded", and reads the config per alert so the
        // pack/detail toggles apply without a restart.
        _enemyVoice = new EnemyVoicePlayer(
            () => (GetString("voice.enemyVoicePack", "prerecorded"),
                   GetString("voice.enemyVoiceDetail", "simple"),
                   GetDouble("voice.enemyVoiceVolume", 1.0)),
            log: msg => Log(msg));
        _enemyVoice.Start();

        // M31 §C afterimage tracker — always constructed; the renderer gates on
        // minimap.afterimage.enabled, so there is no state to rebuild when it is toggled.
        // visibleGraceMs: how long after a sighting an enemy still counts as visible (no marker) —
        // above the flicker band so a still-visible champion never sprouts a marker (§43-AX).
        // 1200, not 1000: benign same-spot detection gaps measure p95 1014ms, and the adaptive
        // EWMA grace converges to the MEAN gap, so tail gaps pierced the old 1000ms base and drew
        // a ghost under a live icon (user-reported 2026-07-25). 1200 is the user's chosen
        // tradeoff (2026-07-25): covers p95 with ~18% headroom while keeping a real fog
        // departure's marker delay low; rare tail ghosts remain possible — tune this key
        // per-user without a rebuild if they recur.
        _afterimages = new Overlay.Core.Jungle.EnemyAfterimageTracker(
            graceMs: () => GetDouble("minimap.afterimage.visibleGraceMs", 1200));
        _afterimages.Start();

        // M07 Item Tracker
        _items = new ItemTracker();
        _items.Start();

        // M08 Recall Timer
        _recall = new RecallTimer();
        _recall.Start();

        // M19 §3.2 Inhibitor Timer — default ON (loop 71, user request; real API-backed data).
        if (GetBool("overlay.items.inhibitorTimers.enabled", true))
        {
            _inhibitorTimer = new InhibitorTimer();
            _inhibitorTimer.Start();
        }

        // Patch 15.1 Nexus-turret respawn timer — default ON (real API-backed data, TurretKilled → 3:00).
        if (GetBool("overlay.items.nexusTurretTimers.enabled", true))
        {
            _nexusTurretTimer = new NexusTurretTimer();
            _nexusTurretTimer.Start();
        }

        // (loop 125) Same-lane enemy return predictor — default ON. Needs the static map-distance presets;
        // if that bundled file is missing/corrupt the feature simply stays off (never crashes startup).
        if (GetBool("overlay.items.laneReturn.enabled", true))
        {
            try
            {
                _laneReturn = new LaneReturnPredictor(() => LatestSnapshot, MapConstantsLoader.Load(), _config);
                _laneReturn.Start();
            }
            catch (Exception ex)
            {
                Log($"LaneReturnPredictor disabled (map constants load failed): {ex.Message}");
            }
        }

        // M30 Enemy Jungler Spotted Alert — default ON (real API field diffs, honest: no
        // coordinate/direction claim). Silently does nothing outside ranked/draft, where the
        // Live Client never populates `position` (see EnemyJunglerLocator).
        if (GetBool("overlay.items.enemyJunglerSpotted.enabled", true))
        {
            _enemyJunglerSpotted = new EnemyJunglerSpottedDetector(() => _latestSnapshot);
            _enemyJunglerSpotted.Start();
        }

        // Enemy legendary-item-completed alert — default ON. Enemy item-list diffs imply the enemy
        // was just visible (the API only refreshes an enemy row while in sight), so no minimap-image
        // coupling is needed; ally completions stay with ItemTracker above.
        if (GetBool("overlay.items.enemyItemAlert.enabled", true))
        {
            _enemyItemAlert = new EnemyItemAlertDetector(() => _latestSnapshot);
            _enemyItemAlert.Start();
        }

        // M04 Combo Editor (kept live for the session; a future dashboard UI drives it).
        // Share one stateless ComboEngine — and one RuneEngine (see _runeEngine's doc comment) —
        // with the ComboRunner below.
        _runeEngine = new RuneEngine();
        var comboEngine = new ComboEngine(new DamageEngine(), _runeEngine);
        _comboEditor = new ComboEditor(comboEngine, _config);

        // One-time recovery of pre-upgrade (loop 44/45) hotkey bindings: the old flat
        // "hotkeys.comboSlots.{hotkey}" entries have no champion dimension and can never
        // fire-time-match after the composite-key upgrade, so they were silently orphaned (the
        // user had to re-bind). Rewrite them under "{hotkey}::{championId}" from each bound combo's
        // own saved champion, BEFORE RegisterComboHotkeys reads the map. Idempotent + defensive: a
        // config-migration failure must never abort startup.
        try
        {
            _comboEditor.MigrateLegacyHotkeyBindings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Legacy hotkey migration skipped: " + ex.Message);
        }

        // Missing-HP tracker: constructed and started for lifecycle parity with the rest of this
        // method, but loop 38 continuation 21 REVERTED wiring it into ComboRunner (see below) per
        // user feedback — a combo trigger's starting defender HP must be the target's MAX HP, not
        // a cross-time/cross-combo damage-since-respawn estimate (see M05 changelog). The tracker
        // itself stays alive/subscribed in case a future feature wants a cross-time estimate again.
        _targetHealthTracker = new TargetHealthTracker();
        _targetHealthTracker.Start();

        // ComboRunner: subscribe to COMBO.TRIGGER now (before hotkeys can fire) so the
        // hotkey -> combo -> HUD loop is closed. It reads the latest poller snapshot to
        // build the ExecutionContext when a combo fires. The shared _runeEngine lets it arm
        // manual rune flags on the exact instance ComboEngine.Execute reads from. targetHealthTracker
        // is intentionally NOT passed (loop 38 continuation 21 revert) — ComboRunner.BuildDefenderFor
        // falls back to its documented null-tracker behavior (CurrentHP == MaxHP at the start of
        // every combo trigger), which is what lets a same-combo sequence like Garen Q->E->W->R
        // correctly read R's missing-HP term off the running remaining-HP DamageEngine.Simulate
        // already threads through the node loop (Q/E/W's damage reduces it before R reads it).
        _comboRunner = new ComboRunner(comboEngine, _config, () => _latestSnapshot,
            runeEngine: _runeEngine);
        _comboRunner.Start();
    }

    /// <summary>Wire M13 global hotkeys. Must run on the UI thread (<c>MainWindow.OnLoaded</c>)
    /// because the low-level keyboard hook is installed on — and its callback dispatched on — the
    /// installing thread, which needs a message pump (the WPF Dispatcher provides one). Each
    /// configured mapping is registered independently so a single reserved/conflicting/invalid
    /// hotkey cannot abort startup. <paramref name="hwnd"/> is no longer needed by the hook (the
    /// LL hook is global) but the signature is kept so the call site is unchanged.</summary>
    public void WireHotkeys(IntPtr hwnd)
    {
        try
        {
            _hotkeyHook = new LowLevelHotkeyHook();
            _hotkeys = new HotkeyRegistry(_hotkeyHook);
            _hotkeyHook.HotkeyPressed += id => _hotkeys.FireByOsId(id);

            RegisterComboHotkeys();

            // M19: overlay visibility toggle (default SHIFT+TAB) alongside the combo hotkeys.
            RegisterOverlayToggleHotkey();

            // §40: combo-damage overlay (Alt+1) + per-skill overlay (Alt+2) toggles.
            RegisterOverlayCardHotkeys();
        }
        catch (Exception ex)
        {
            Log($"Hotkey wiring failed; continuing without global hotkeys: {ex}");
        }
    }

    /// <summary>Registration ids of the currently-registered combo hotkeys, so a
    /// <see cref="RefreshComboHotkeys"/> can drop the old set before re-registering.</summary>
    private readonly List<string> _comboHotkeyRegIds = new();

    /// <summary>Re-registers the combo hotkeys from the current <c>hotkeys.comboSlots</c> config.
    /// Call this (on the UI thread) after a combo is saved/bound/deleted so a combo created AFTER
    /// the overlay was first wired still gets its hotkey — otherwise pressing it does nothing.
    /// No-op until the overlay HWND has been wired (WireHotkeys will pick up existing combos then).</summary>
    public void RefreshComboHotkeys()
    {
        if (_hotkeys is null) return; // not wired yet; WireHotkeys registers current combos on first show.
        RegisterComboHotkeys();
    }

    /// <summary>Unregisters the previously-registered combo hotkeys, then re-registers one OS-level
    /// hotkey per DISTINCT raw hotkey found across <c>hotkeys.comboSlots</c> (composite
    /// <c>{hotkey}::{championId}</c> keys — see <see cref="ComboEditor.ComposeSlotKey"/>). Several
    /// champions' combos can share the same raw hotkey now (that's the whole point of the composite
    /// key — loop 44 bug 1, where binding Garen's combo to "A" used to silently unbind Ahri's own
    /// "A" combo), so only ONE OS registration can exist per raw hotkey; the callback below resolves
    /// which champion's combo to fire AT FIRE TIME rather than baking one comboId in at registration
    /// time (loop 44 bug 2, where pressing "A" as Annie fired whatever other champion's combo
    /// currently occupied "A"). hotkeys.comboSlots comes back from M14 as an
    /// IDictionary&lt;string,object?&gt; of JSON values; read it defensively. Each Register is
    /// wrapped so one reserved/conflicting combo cannot abort the rest.</summary>
    private void RegisterComboHotkeys()
    {
        if (_hotkeys is null) return;

        foreach (var id in _comboHotkeyRegIds)
        {
            try { _hotkeys.Unregister(id); } catch { /* already gone */ }
        }
        _comboHotkeyRegIds.Clear();

        if (_config.Get("hotkeys.comboSlots") is not IDictionary<string, object?> slots) return;

        // Group every (championId, comboId) binding by its raw hotkey — a composite key with no
        // '::' at all is a pre-upgrade legacy entry (see ComboEditor.SplitSlotKey); it degrades to
        // an empty championId rather than throwing, but can never fire-time-match a real champion.
        var byRawHotkey = new Dictionary<string, List<(string ChampionId, string ComboId)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (compositeKey, comboValue) in slots)
        {
            var comboId = comboValue?.ToString();
            if (string.IsNullOrWhiteSpace(compositeKey) || string.IsNullOrWhiteSpace(comboId))
                continue; // null value = a deleted combo's dropped slot; skip.

            var (rawHotkey, championId) = ComboEditor.SplitSlotKey(compositeKey);
            if (string.IsNullOrWhiteSpace(rawHotkey)) continue;

            if (!byRawHotkey.TryGetValue(rawHotkey, out var bindings))
                byRawHotkey[rawHotkey] = bindings = new List<(string, string)>();
            bindings.Add((championId, comboId));
        }

        foreach (var (rawHotkey, bindings) in byRawHotkey)
        {
            try
            {
                var regId = _hotkeys.Register(
                    rawHotkey,
                    () =>
                    {
                        // Resolved fresh on every fire (not once at Register time above) — that's
                        // the fix for loop 44 bug 2.
                        var activeChampionId = ResolveActiveChampionId();
                        if (activeChampionId is not null)
                        {
                            foreach (var binding in bindings)
                            {
                                if (string.Equals(binding.ChampionId, activeChampionId, StringComparison.Ordinal))
                                {
                                    Overlay.Core.EventBus.EventBus.Publish("COMBO.TRIGGER", binding.ComboId, "M13");
                                    return;
                                }
                            }
                            return; // active champion has no combo bound to this key -> do nothing.
                        }

                        // Active champion unresolvable (e.g. not in a game yet, or a transient
                        // snapshot gap) and only one combo occupies this key anyway: fall back to
                        // firing it, preserving today's single-combo behavior when there's no real
                        // ambiguity to resolve, rather than going silent in the common case.
                        if (bindings.Count == 1)
                            Overlay.Core.EventBus.EventBus.Publish("COMBO.TRIGGER", bindings[0].ComboId, "M13");
                    },
                    "M04");
                _comboHotkeyRegIds.Add(regId);
            }
            catch (Exception ex)
            {
                // Reserved/conflicting/invalid/OS-refused combo: log + skip, don't abort.
                Log($"Hotkey '{rawHotkey}' skipped: {ex.Message}");
            }
        }
    }

    /// <summary>Resolves the champion the user is CURRENTLY PLAYING as an English Data Dragon id —
    /// never a localized display name — so a fire-time hotkey lookup can compare it against
    /// <see cref="SavedCombo.ChampionId"/>. Matches the active player's scoreboard row the same way
    /// <c>HomeView.ResolveActiveChampion</c> does (summoner-name match), but unlike that DISPLAY-only
    /// helper (which calls <see cref="Localization.ChampionName"/>), this reverse-translates a Korean
    /// champion name back to its English id via <see cref="ChampionSummary.ResolveKoreanName"/> —
    /// loop 44 confirmed the Live Client API returns Korean champion names for the active player too
    /// on this user's client. Null when there's no live snapshot or no matching scoreboard row.</summary>
    private string? ResolveActiveChampionId()
    {
        var snap = _latestSnapshot;
        if (snap is null) return null;

        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (p.SummonerName == snap.ActivePlayerSummonerName && !string.IsNullOrEmpty(p.ChampionName))
                return ChampionSummary.ResolveKoreanName(p.ChampionName) ?? p.ChampionName;
        }
        return null;
    }

    /// <summary>(loop 173) The active player's English champion id from the live snapshot, or null when
    /// there's no game / no matching row. Public accessor over <see cref="ResolveActiveChampionId"/> so
    /// the combo settings view can auto-select the champion being played on game start.</summary>
    public string? ActivePlayerChampionId => ResolveActiveChampionId();

    /// <summary>Registers (or re-registers, after a rebind) the M19 overlay-toggle hotkey from
    /// <c>overlay.toggleHotkey</c> (default <see cref="DefaultToggleHotkey"/>). Safe to call before
    /// hotkeys are wired (no-op) and repeatedly. Registration is wrapped so a reserved/conflicting/
    /// OS-refused combo (SHIFT+TAB included — see field note) is logged and skipped, never crashing.
    /// On fire it invokes <see cref="OverlayToggleRequested"/>, which only toggles our window's
    /// visibility — no input is sent to the game (P4).</summary>
    public void RegisterOverlayToggleHotkey()
    {
        if (_hotkeys is null) return; // hotkeys not wired yet; WireHotkeys will register it.

        if (_toggleHotkeyRegId is not null)
        {
            try { _hotkeys.Unregister(_toggleHotkeyRegId); } catch { /* ignore */ }
            _toggleHotkeyRegId = null;
        }

        var hotkey = _config.Get("overlay.toggleHotkey") as string;
        if (string.IsNullOrWhiteSpace(hotkey)) hotkey = DefaultToggleHotkey;

        try
        {
            _toggleHotkeyRegId = _hotkeys.Register(hotkey, () => OverlayToggleRequested?.Invoke(), "M19");
        }
        catch (Exception ex)
        {
            // Reserved/conflicting/invalid/OS-refused (e.g. TAB unsupported by the Win32 hook):
            // log + skip so startup never crashes; the user can rebind in Settings → 오버레이.
            Log($"Overlay toggle hotkey '{hotkey}' skipped: {ex.Message}");
        }
    }

    /// <summary>§40 overlay-card hotkey registration ids, kept so a rebind (from Settings → 오버레이)
    /// can drop the old set before re-registering — mirrors <see cref="_comboHotkeyRegIds"/>.</summary>
    private readonly List<string> _overlayCardHotkeyRegIds = new();

    /// <summary>§40: registers (or re-registers, after a rebind) the combo-damage overlay (default Alt+1)
    /// and per-skill overlay (default Alt+2) toggle hotkeys. Each flips its config enable flag, which
    /// <c>OverlayHost</c> re-reads every frame — no input is sent to the game (P4). Rebindable via
    /// <c>overlay.comboOverlayHotkey</c>/<c>overlay.skillOverlayHotkey</c> (Settings calls this after a
    /// rebind). Public + idempotent: drops the previous registrations first, so calling it repeatedly
    /// never double-registers. A reserved/invalid combo is logged and skipped, not fatal.</summary>
    public void RegisterOverlayCardHotkeys()
    {
        if (_hotkeys is null) return;

        foreach (var id in _overlayCardHotkeyRegIds)
        {
            try { _hotkeys.Unregister(id); } catch { /* already gone */ }
        }
        _overlayCardHotkeyRegIds.Clear();

        string comboKey = _config.Get("overlay.comboOverlayHotkey") as string ?? "Alt+1";
        string skillKey = _config.Get("overlay.skillOverlayHotkey") as string ?? "Alt+2";
        try { _overlayCardHotkeyRegIds.Add(_hotkeys.Register(comboKey, () => ToggleConfigFlag("overlay.items.comboResult.enabled", true), "§40.combo")); }
        catch (Exception ex) { Log($"Combo-overlay hotkey '{comboKey}' skipped: {ex.Message}"); }
        try { _overlayCardHotkeyRegIds.Add(_hotkeys.Register(skillKey, () => ToggleConfigFlag("overlay.items.skillOverlay.enabled", false), "§40.skill")); }
        catch (Exception ex) { Log($"Skill-overlay hotkey '{skillKey}' skipped: {ex.Message}"); }

        // §43: dump the last ~2s of minimap frames when the user sees a wrong callout. Registered
        // only while minimap.debugCapture is on, so a normal session never binds the key at all.
        if (GetBool("minimap.debugCapture", false))
        {
            string dumpKey = _config.Get("minimap.debugDumpHotkey") as string ?? "Alt+3";
            try { _overlayCardHotkeyRegIds.Add(_hotkeys.Register(dumpKey, () => DumpMinimapDebugFrames(), "§43.dump")); }
            catch (Exception ex) { Log($"Minimap debug-dump hotkey '{dumpKey}' skipped: {ex.Message}"); }
        }
    }

    /// <summary>§43: writes the retained minimap frames to a timestamped folder under
    /// <c>logs/minimap-debug/</c>. Named by wall-clock time so several dumps in one game stay
    /// separate and a later report can point at one of them.</summary>
    private void DumpMinimapDebugFrames(string? reason = null)
    {
        if (_minimapVision is null) { Log("Minimap debug dump: vision pipeline is not running."); return; }
        try
        {
            // The reason is in the folder name so an auto-dump can be told from a hotkey one, and so
            // the champion the detector tripped over is visible without opening the log.
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string dir = Path.Combine(
                AppContext.BaseDirectory, "logs", "minimap-debug",
                reason is null ? stamp : $"{stamp}_{reason}");
            int n = _minimapVision.DumpDebugFrames(dir);
            Log(n > 0
                ? $"Minimap debug dump: {n} frame(s) written to {dir}"
                : "Minimap debug dump: nothing retained yet (is minimap.debugCapture on?).");
        }
        catch (Exception ex) { Log($"Minimap debug dump failed: {ex.Message}"); }
    }

    /// <summary>Flips a boolean config flag (its enable toggle); the overlay reacts on its next frame.</summary>
    private void ToggleConfigFlag(string key, bool fallback)
        => _config.Set(key, !GetBool(key, fallback));

    private bool GetBool(string key, bool fallback)
        => _config.Get(key) is bool b ? b : fallback;

    private double GetDouble(string key, double fallback)
        => _config.Get(key) switch
        {
            double d => d,
            int i => i,
            _ => fallback,
        };

    /// <summary>Reads a string config value. An empty/whitespace entry falls back rather than
    /// being treated as a real mode name (M31 §B reads modes like "prerecorded"/"simple").</summary>
    private string GetString(string key, string fallback)
        => _config.Get(key) is string s && !string.IsNullOrWhiteSpace(s) ? s : fallback;

    private static void Log(string message)
        => Debug.WriteLine($"[AppComposition] {message}");

    /// <summary>M31 diagnostic sink for the minimap-vision pipeline. Writes to
    /// <c>logs/minimap-vision.log</c> (next to the exe) AS WELL AS Debug — because the capture/detect
    /// path is UNVERIFIED and Debug.WriteLine is invisible in a normal run, this file is how the user
    /// sees whether capture started, frames arrive, templates built, and sightings fire (mirrors the
    /// M30 enemy-jungler-debug.log approach). Never throws.</summary>
    private static readonly string MinimapLogPath =
        Path.Combine(AppContext.BaseDirectory, "logs", "minimap-vision.log");

    private static void MinimapLog(string message)
    {
        Debug.WriteLine($"[Minimap] {message}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MinimapLogPath)!);
            EnsureBuildStampHeader();
            File.AppendAllText(MinimapLogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* diagnostics must never crash the app */ }
    }

    /// <summary>Marker line identifying the binary that wrote the entries below it.</summary>
    public const string BuildStampPrefix = "=== BUILD ";

    private static bool _buildStampWritten;

    /// <summary>Writes the build stamp once per process, before the first log line.
    ///
    /// <para>(§43-P) A log has to be able to identify the binary that produced it. Two rounds of
    /// fixes were once analysed from logs that a stale build had written — the copy step had been
    /// failing while the app was running, and nothing in the log said so. The convention "check the
    /// dll timestamp" only holds while a human remembers it; this cannot be forgotten, and the
    /// analysis tooling refuses logs whose stamp does not match the build under test.</para></summary>
    private static void EnsureBuildStampHeader()
    {
        if (_buildStampWritten) return;
        _buildStampWritten = true;

        string stamp = typeof(AppComposition).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        File.AppendAllText(MinimapLogPath,
            $"{BuildStampPrefix}{stamp} | started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
    }

    /// <summary>M31 minimap-vision capture source. Null unless the kill switch
    /// (<c>minimap.vision</c>) is on and <see cref="WireMinimapCapture"/> has run. Owned here.</summary>
    // In a LIGHT build nothing assigns these — WireMinimapCapture is compiled out — but every
    // reader (Dispose, DumpMinimapDebugFrames) still compiles and correctly sees null. Suppressed
    // rather than #if-ed away so the two builds keep the same field set and the same null checks.
#if LIGHT
#pragma warning disable CS0649
#endif
    private IMinimapCaptureSource? _minimapCapture;
    private MinimapVisionPipeline? _minimapVision;
#if LIGHT
#pragma warning restore CS0649
#endif
    private readonly List<string> _minimapCaptureSubs = new();

    /// <summary>M31 §5 minimap-vision capture wiring (kill switch <c>minimap.vision</c>, DEFAULT ON
    /// as of loop 165 — user "스위치 on 및 모듈 활성화"). Idempotent; call once a game-window HWND
    /// getter is available (mirrors <see cref="WireHotkeys"/>). Creates the capture source and drives
    /// its lifecycle off <c>GAME.CONNECTED/DISCONNECTED</c> (M31 §1 "per GAME.CONNECTED"), so capture
    /// runs only while a game is up. No-op when the kill switch is off. <paramref name="getGameWindow"/>
    /// supplies the tracked League HWND (M19 window tracking, owned by MainWindow).</summary>
    public void WireMinimapCapture(Func<IntPtr> getGameWindow)
    {
#if LIGHT
        // LIGHT build: the capture backend is not compiled in and Overlay.Capture is not even
        // referenced, so there is nothing to wire. Left as a no-op rather than removing the call
        // site, so the two builds differ by one #if instead of by control flow.
        MinimapLog("LIGHT build — minimap capture is compiled out; the enemy-jungler alerts run "
                   + "off the Live Client API and are unaffected.");
#else
        // Log entry BEFORE any early-return so the log file always appears and reveals why capture
        // may not start (the #1 cause of a missing log: a stale user_config.json persisted
        // minimap.vision=false from an earlier default-OFF build, which wins over the ON default).
        object? rawVision = _config.Get("minimap.vision");
        MinimapLog($"WireMinimapCapture: minimap.vision={rawVision?.ToString() ?? "(null)"}, alreadyWired={_minimapCapture is not null}, log={MinimapLogPath}");

        if (_minimapCapture is not null) return;        // already wired
        if (!GetBool("minimap.vision", true))
        {
            MinimapLog("minimap.vision is OFF → capture NOT started. Set \"vision\": true under \"minimap\" in user_config.json (or delete that file) and restart.");
            return;
        }

        int fps = (int)GetDouble("minimap.captureFps", 30);
        try
        {
            _minimapCapture = new MinimapCaptureSource(
                getGameWindow, fps, log: MinimapLog,
                // Same manual-calibration box the overlay renders structures on → detection and the
                // inhibitor/Nexus chips stay on ONE map basis (OverlayHost.ResolveMinimapRect mirror).
                manualRoiFraction: ResolveManualMinimapRoi);
            MinimapLog($"wiring minimap capture (fps={fps}) — logging to {MinimapLogPath}");

            // §38-H orchestration loop: FrameCaptured → MinimapDetector.Detect → JunglePresenceTracker
            // .OnSighting/.Tick (self-publishes UI.NOTIFICATION/VOICE.SPEAK). Subscribe it to the source
            // BEFORE the source starts so no early frame is missed.
            // §43 debug ring — off unless the user turned it on (costs ~19MB while enabled).
            _minimapVision = new MinimapVisionPipeline(
                _minimapCapture, () => _latestSnapshot, DataDragonVersion, MinimapLog,
                lostDebounceMs: () => GetDouble("minimap.lostDebounceMs", 2500))
            {
                DebugCaptureEnabled = GetBool("minimap.debugCapture", false),
            };
            // §43-AR: the pipeline asks for a dump when it catches a physically impossible sighting,
            // because the hotkey cannot be pressed fast enough to catch one (see OnImplausibleSighting).
            _minimapVision.AutoDumpRequested += reason => DumpMinimapDebugFrames(reason);
            _minimapVision.Start();

            // Start/stop with the game. Bus handlers arrive on the EventBus thread; Start/Stop are
            // internally locked + idempotent. Try an immediate start too, in case a game is already up.
            _minimapCaptureSubs.Add(
                Overlay.Core.EventBus.EventBus.Subscribe("GAME.CONNECTED", _ => _minimapCapture?.Start()));
            _minimapCaptureSubs.Add(
                Overlay.Core.EventBus.EventBus.Subscribe("GAME.DISCONNECTED", _ => _minimapCapture?.Stop()));
            _minimapCapture.Start();
        }
        catch (Exception ex)
        {
            MinimapLog($"minimap capture wiring failed: {ex.GetType().Name}: {ex.Message}");
        }
#endif
    }

    /// <summary>The user's one-time manual minimap-calibration box as normalized fractions
    /// (X of width, Y and Size of height), or null when disabled. Read from the SAME
    /// <c>overlay.minimapCalibration</c> keys OverlayHost.ResolveMinimapRect uses, so the capture
    /// ROI and the structure-chip render rect are identical when the user has aligned the box.</summary>
    private (double X, double Y, double Size)? ResolveManualMinimapRoi()
    {
        if (_config.Get("overlay.minimapCalibration.enabled") is bool en && en
            && _config.Get("overlay.minimapCalibration.size") is double sz && sz > 0)
        {
            double x = _config.Get("overlay.minimapCalibration.x") is double xv ? xv : 0;
            double y = _config.Get("overlay.minimapCalibration.y") is double yv ? yv : 0;
            return (x, y, sz);
        }
        return null;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }

        _comboRunner?.Dispose();
        _runeEngine?.Dispose();
        _targetHealthTracker?.Dispose();
        _poller?.Dispose();
        _publisher?.Dispose();
        _tts?.Dispose();
        _synth?.Dispose();
        _enemyVoice?.Dispose();
        _afterimages?.Dispose();
        _items?.Dispose();
        _recall?.Dispose();
        _laneReturn?.Dispose();
        _enemyJunglerSpotted?.Dispose();
        _enemyItemAlert?.Dispose();
        _inhibitorTimer?.Dispose();
        _nexusTurretTimer?.Dispose();
        _globalGold?.Dispose();
        _hotkeyHook?.Dispose();
        foreach (var id in _minimapCaptureSubs)
        {
            try { Overlay.Core.EventBus.EventBus.Unsubscribe(id); } catch { /* already gone */ }
        }
        _minimapVision?.Dispose();
        _minimapCapture?.Dispose();
        _ads.Dispose(); // M29: flushes the batched impression beacons
        Lcu?.Dispose(); // M33

        _cts.Dispose();
        _config.Dispose();
    }
}
