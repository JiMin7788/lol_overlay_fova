using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Overlay.Core.Hotkeys;
using Overlay.Core.Vision;

namespace Overlay.Client;

/// <summary>
/// Overlay shell window — transparent, borderless, always-on-top, no-taskbar,
/// aligned to the League game window (windowed / borderless) with a full-monitor
/// fallback when no game window is present (e.g. preview mode).
///
/// Skill: frontend-wpf-overlay/SKILL.md
///
/// Hard Rule #3 (thread separation): this code-behind ONLY handles window-level
/// Win32 concerns (transparency flags, hit-test regions, geometry placement) and
/// composing the M02 HUD host. No I/O, HTTP, parsing, or STT/TTS here. The single
/// <see cref="DispatcherTimer"/> below only re-reads the game window rect and
/// repositions this window — a UI-thread concern, not data production. All HUD
/// data arrives via the M02 OverlayHost from UI.* events.
///
/// Hard Rule #4 (config-driven): window opacity, click-through default, and target
/// monitor index (fallback geometry) come from <see cref="OverlayConfig"/>.
///
/// Click-through: achieved by applying <c>WS_EX_TRANSPARENT | WS_EX_LAYERED</c>
/// extended window styles via <c>GetWindowLong</c>/<c>SetWindowLong</c> on the
/// native HWND obtained from <see cref="WindowInteropHelper"/> on
/// <see cref="SourceInitialized"/>. Toggle at runtime with
/// <see cref="SetClickThrough(bool)"/>. This does NOT use OpenProcess or
/// ReadProcessMemory — compliant with Anti-cheat Hard Rule #1.
/// </summary>
public partial class MainWindow : Window
{
    // ── Win32 constants ──────────────────────────────────────────────────────
    private const int GWL_EXSTYLE    = -20;
    private const int WS_EX_LAYERED  = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>League in-game client window class (windowed/borderless).</summary>
    private const string LeagueWindowClass = "RiotWindowClass";
    /// <summary>League in-game client window title (fallback lookup).</summary>
    private const string LeagueWindowTitle = "League of Legends (TM) Client";

    // ── Win32 types ──────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int    cbSize;
        public RECT   rcMonitor;
        public RECT   rcWork;
        public uint   dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
                                          ref RECT lprcMonitor, IntPtr dwData);

    // ── Win32 P/Invoke ───────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
                                                   MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    // game.cfg discovery (M31 §2 layer 0) — mirrors Overlay.Capture.WindowProbe.TryGetGameCfgPath so
    // the client project needs no reference to Overlay.Capture. PATH query only (QueryFullProcessImageName
    // with PROCESS_QUERY_LIMITED_INFORMATION) — explicitly NOT process-memory access (P3-safe).
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, uint uFlags);

    // Re-assert topmost above a focused (borderless/windowed) game each tick.
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    /// <summary>WS_EX_NOACTIVATE: the overlay must never steal foreground focus from the game
    /// (so showing/repositioning it doesn't yank the player out) — applied while click-through.</summary>
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // ── Fields ───────────────────────────────────────────────────────────────
    private IntPtr _hwnd = IntPtr.Zero;
    private readonly OverlayConfig _config;

    /// <summary>Tracks the game window and repositions the overlay on move/resize.
    /// Created in <see cref="OnLoaded"/> (once the HWND and PresentationSource exist),
    /// stopped/disposed on close. UI-thread only — see Hard Rule #3 note in class doc.</summary>
    private DispatcherTimer? _trackTimer;

    /// <summary>(M31 §2 layer 0) Cached parsed game.cfg <c>[HUD]</c> settings (MinimapScale/FlipMiniMap)
    /// for the tracked game, refreshed at most every ~5s from the 400 ms track tick and handed to the
    /// overlay's <c>MinimapCalibrator</c>. Null until the first successful read (overlay then uses the
    /// pure geometric prior). UI-thread only, like <see cref="_trackTimer"/>.</summary>
    private GameCfgHudSettings? _gameCfg;
    private DateTime _lastGameCfgReadUtc = DateTime.MinValue;

    /// <summary>Last game-window rect (device px) applied to the overlay, so the timer
    /// only repositions when the rect actually changes. <c>null</c> means "currently on
    /// the monitor-fill fallback" — set when the game window is not found.</summary>
    private RECT? _lastGameRect;

    /// <summary>Whether the monitor-fill fallback has been applied at least once. Needed because
    /// <see cref="_lastGameRect"/> is null in TWO different states — "never positioned yet" and
    /// "currently on the fallback" — and the fallback branch used to skip on null alone, so an
    /// overlay opened with no game running was never sized or positioned at all.</summary>
    private bool _fallbackApplied;

    /// <summary>M02 Overlay Engine host (HUD coordinator + M16 render loop). Composed in
    /// <see cref="OnLoaded"/> once window geometry is known; kept alive for the window's
    /// lifetime. Reuses this window's existing click-through — no new injection path.</summary>
    private Overlay.Client.Hud.OverlayHost? _overlayHost;

    /// <summary>E1 composition root: owns the live subsystem graph (M01 poller, M11 repos,
    /// M07/M08/M09/M13/M04) and the single shared <see cref="ConfigManager"/>. Injected by
    /// <c>HomeWindow</c> (which owns and disposes it) — the overlay only borrows it so both the
    /// home app and the overlay share one config/subsystem graph. Not disposed here.</summary>
    private readonly AppComposition? _composition;

    /// <summary>M19: when true, the overlay is interactive/draggable (click-through OFF) so the user
    /// can reposition it; when false it is click-through (default in-game). Driven by
    /// <c>overlay.movable</c> in the shared config.</summary>
    private bool _movable;

    /// <summary>M19: EventBus subscription ids for the live <c>overlay.opacity</c>/<c>overlay.movable</c>
    /// config listeners, released on close.</summary>
    private string? _opacitySub;
    private string? _movableSub;

    /// <summary>M02 loop 38 continuation 12 (combo-overlay click-to-target): the modifier that
    /// must be held for the overlay to temporarily accept input, read from
    /// <c>overlay.targetClickModifier</c> (default Control). Polled each <see cref="_targetClickTimer"/>
    /// tick against <see cref="AppComposition.HotkeyHook"/>'s own held-modifier tracking.</summary>
    private HotkeyModifiers _targetClickModifier = HotkeyModifiers.Control;

    /// <summary>Short-interval poll timer for the click-to-target modifier gate. A poll (not an
    /// event hook) is necessary because while click-through the window receives NO input events
    /// at all — there is nothing to attach a key handler to. 30ms keeps the toggle imperceptibly
    /// close to "instant" without adding a second OS-level hook (reuses the existing M13 one).</summary>
    private DispatcherTimer? _targetClickTimer;

    /// <summary>M16 벤치마크 훅: config <c>diagnostics.frameDropMeter</c>(기본 false)가 켜진
    /// 세션에서만 생성. CompositionTarget.Rendering 기반 프레임드랍 계측을 10초마다
    /// <c>logs/framedrop.log</c>에 기록한다 — 릴리즈 체크리스트의 "60fps 드랍률 1% 미만" 실측용.
    /// 프로덕션 기본값에서는 코드 경로 자체가 실행되지 않는다.</summary>
    private Render.FrameDropMeter? _frameMeter;
    private DispatcherTimer? _frameMeterTimer;

    /// <summary>True while THIS mechanism (not M19 movable mode) has cleared WS_EX_TRANSPARENT for
    /// the held click-to-target modifier, so <see cref="UpdateTargetClickThrough"/> knows to restore
    /// it the instant the modifier is released rather than on every tick regardless of state.</summary>
    private bool _targetClickThroughSuppressed;

    /// <summary>Live-config subscription id for <c>overlay.targetClickModifier</c>, released on close.</summary>
    private string? _targetClickModifierSub;

    // ── Constructor ──────────────────────────────────────────────────────────
    public MainWindow()
    {
        // Load config before InitializeComponent so properties are available.
        // Startup failure here is intentional (loud fail per RecallConfigLoader
        // pattern) — a missing config is not recoverable at session start.
        _config = OverlayConfigLoader.Load();

        InitializeComponent();

        // Never take foreground focus when shown/updated — the game must keep focus while the
        // overlay floats above it (paired with WS_EX_NOACTIVATE + topmost re-assertion).
        ShowActivated = false;

        // Apply config-driven opacity (Hard Rule #4). The window background is
        // always Transparent; this affects non-transparent HUD regions.
        Opacity = _config.WindowOpacity;

        // Wire up window events before the handle is created.
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        // M19 whole-window drag-to-move used to be wired here (OnOverlayMouseDown → DragMove()).
        // M02 pending-change #1 (modular per-element HUD positioning) replaces that monolithic
        // "move everything as one block" behavior: while movable, OverlayHost now hit-tests and
        // drags ONE HUD element at a time (OverlayHost.TryBeginElementDrag), persisting a
        // per-type offset (overlay.positions.{key}) instead of the whole window's Left/Top. No
        // window-level mouse-down handler is needed for dragging anymore.
    }

    /// <summary>Overlay ctor used by <c>HomeWindow</c>: injects the shared
    /// <see cref="AppComposition"/> so the overlay reuses the already-running subsystem graph
    /// and its single <see cref="ConfigManager"/> instead of constructing its own.</summary>
    public MainWindow(AppComposition composition) : this()
    {
        _composition = composition;
    }

    // ── Window event handlers ────────────────────────────────────────────────

    /// <summary>
    /// Called once the Win32 HWND exists but before the window is shown.
    /// Apply click-through extended styles here — earliest safe point for
    /// interop with the native handle.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Apply click-through default from config (Hard Rule #4).
        SetClickThrough(_config.ClickThroughByDefault);

        // Verify hardware render tier (frontend-wpf-overlay SKILL "Window setup").
        // RenderCapability.Tier high-word: 0 = software, 1 = partial HW, 2 = full HW.
        int renderTier = RenderCapability.Tier >> 16;
        _ = renderTier; // available to future HUD code; suppress unused warning
    }

    /// <summary>
    /// Called after layout is complete. Place the overlay over the game window (or
    /// the configured monitor as a fallback), start the follow-timer, then compose
    /// the M02 HUD host over the shared config.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateGeometry();

        // Follow the game window as it moves/resizes and switch between game-track
        // and monitor-fill as the game appears/disappears. Lightweight: one rect read
        // per tick, repositions only on change (Hard Rule #3 — UI-thread placement only).
        _trackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _trackTimer.Tick += (_, _) => { UpdateGeometry(); RefreshGameCfg(); };
        _trackTimer.Start();

        // E1: the composition root (subsystem graph + shared ConfigManager) is owned and
        // started by HomeWindow and injected here. The overlay only composes the M02 Overlay
        // Engine over the shared config — it neither constructs nor starts the graph.
        if (_composition is not null)
        {
            _overlayHost = Overlay.Client.Hud.OverlayHost.Start(
                this, WidgetCanvas, _composition.Config, () => _composition.LatestSnapshot,
                IsTargetClickModifierHeld, () => _movable,
                // (loop 125) same-lane enemy return prediction, queried live each frame (null when off)
                () => _composition.LaneReturnPredictor?.GetActive(
                    System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                // (loop 142; loop 150 in-game-clock basis) inhibitor respawn countdowns, live each frame.
                // Driven by the latest snapshot's GameTime (Live Client in-game seconds), NOT wall-clock —
                // so the countdown tracks the real match clock. Null snapshot / no game → no timers.
                () => _composition.InhibitorTimer?.GetActive(_composition.LatestSnapshot?.GameTime ?? 0.0),
                // (patch 15.1) Nexus-turret respawn countdowns, same in-game-clock basis.
                () => _composition.NexusTurretTimer?.GetActive(_composition.LatestSnapshot?.GameTime ?? 0.0),
                // (M31 §2 layer 0) game.cfg minimap calibration (size + flip), cached/throttled by the track tick.
                () => _gameCfg,
                // (user request) always-on per-skill damage panel vs the current target (throttled inside OverlayHost).
                snap => _composition.ComboRunner?.ComputeSkillPanel(snap),
                // (M31 §C) last-seen enemy portraits drawn half-transparent on the minimap, queried
                // live each frame. Cleared by the tracker when that champion is spotted again.
                () => _composition.EnemyAfterimages?.GetActive());

            // M02/M04/M13 combo-hotkey toggle-off wiring: give ComboRunner the coordinator so it
            // can tell whether its own combo card is still on screen (OverlayCoordinator.IsActive)
            // and clear it (ClearHud) instead of re-triggering when the same combo's hotkey fires
            // again. ComboRunner is built long before this (no WPF host yet), so this attach is the
            // wiring point, not constructor injection.
            _composition.ComboRunner?.AttachOverlayCoordinator(_overlayHost.Coordinator);

            // M31 (loop 165): activate minimap-vision capture. Kill switch minimap.vision (default
            // ON) is checked inside; the source starts/stops with GAME.CONNECTED/DISCONNECTED. Pass
            // the tracked League HWND getter (window ownership lives here, not in Overlay.Capture).
            // Unconditional marker BEFORE the call: if this line is absent from logs/minimap-vision.log
            // after a clean build, OnLoaded never reached here (or the running exe is a stale build).
            try
            {
                string mvLog = System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "minimap-vision.log");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mvLog)!);
                System.IO.File.AppendAllText(mvLog,
                    $"{DateTime.Now:HH:mm:ss.fff} MainWindow.OnLoaded → WireMinimapCapture(leagueHwnd={FindLeagueWindow()}){Environment.NewLine}");
            }
            catch { /* diagnostics must never crash startup */ }
            _composition.WireMinimapCapture(FindLeagueWindow);

            // M19: apply overlay opacity + movable from the shared config, then react to live changes.
            // OnChange callbacks arrive on the EventBus thread → marshal to the UI thread.
            ApplyOverlaySettingsFromConfig();
            _opacitySub = _composition.Config.OnChange("overlay.opacity",
                v => Dispatcher.Invoke(() => SetOverlayOpacity(ToDouble(v, Opacity))));
            _movableSub = _composition.Config.OnChange("overlay.movable",
                v => Dispatcher.Invoke(() => SetMovable(ToBool(v, _movable))));

            // M02 loop 38 continuation 12 (combo-overlay click-to-target): apply the configured
            // modifier, react to live rebinds, and start the poll that gates WS_EX_TRANSPARENT on
            // it being held. See UpdateTargetClickThrough for why this must be a poll, not a hook.
            ApplyTargetClickModifierFromConfig();
            _targetClickModifierSub = _composition.Config.OnChange("overlay.targetClickModifier",
                v => Dispatcher.Invoke(() => _targetClickModifier = ParseModifier(v as string)));
            _targetClickTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(30),
            };
            _targetClickTimer.Tick += (_, _) => UpdateTargetClickThrough();
            _targetClickTimer.Start();

            // M16 benchmark hook (release checklist "60fps 드랍률 <1%"): opt-in via config
            // diagnostics.frameDropMeter — absent/false in production, so no per-frame handler is
            // ever attached outside a measurement session.
            if (_composition.Config.Get("diagnostics.frameDropMeter") is true)
            {
                _frameMeter = new Render.FrameDropMeter();
                _frameMeter.Start();
                // Keep the render pipeline genuinely busy for the whole session: on a static
                // scene WPF idles between HUD invalidations, and those idle pauses read as
                // 25-50ms "drops" that are not stutter (measured: 100% of gaps mild, hard
                // ceiling at exactly 7 vsyncs, zero >50ms tail). A near-invisible perpetual
                // animation forces continuous vsync-locked rendering so any remaining gap is a
                // REAL missed frame under load — the thing the M16 budget is about.
                var pacer = new System.Windows.Shapes.Rectangle
                {
                    Width = 2,
                    Height = 2,
                    Fill = System.Windows.Media.Brushes.White,
                    Opacity = 0.01,
                    IsHitTestVisible = false,
                };
                WidgetCanvas.Children.Add(pacer);
                pacer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
                    0.01, 0.02, new Duration(TimeSpan.FromMilliseconds(500)))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    AutoReverse = true,
                });
                _frameMeterTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(10),
                };
                _frameMeterTimer.Tick += (_, _) => LogFrameDrop("tick");
                _frameMeterTimer.Start();
            }
        }
    }

    /// <summary>Appends one framedrop.log line: totals + rate since the meter started.</summary>
    private void LogFrameDrop(string tag)
    {
        if (_frameMeter is null) return;
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "framedrop.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // RenderCapability.Tier's high word: 0 = software rendering (would explain everything),
            // 2 = full hardware. Gap histogram tells stall size: mild 25-50ms / moderate 50-100 /
            // severe >100.
            System.IO.File.AppendAllText(path,
                $"{DateTime.Now:HH:mm:ss.fff} {tag} total={_frameMeter.TotalFrames} dropped={_frameMeter.DroppedFrames} rate={_frameMeter.DropRate:P3}" +
                $" gaps[mild={_frameMeter.GapMild} mod={_frameMeter.GapModerate} sev={_frameMeter.GapSevere}] maxGap={_frameMeter.MaxGapMs:F1}ms" +
                $" renderTier={System.Windows.Media.RenderCapability.Tier >> 16}{Environment.NewLine}");
        }
        catch { /* diagnostics must never crash the overlay */ }
    }

    /// <summary>M19: reads <c>overlay.opacity</c>/<c>overlay.movable</c> from the shared config and
    /// applies them (falling back to the overlay-shell config / defaults). Called once on load.</summary>
    private void ApplyOverlaySettingsFromConfig()
    {
        if (_composition is null) return;
        var cfg = _composition.Config;
        SetOverlayOpacity(ToDouble(cfg.Get("overlay.opacity"), _config.WindowOpacity));
        SetMovable(ToBool(cfg.Get("overlay.movable"), false));
    }

    /// <summary>M02 loop 38 continuation 12: reads <c>overlay.targetClickModifier</c> from the
    /// shared config (default Control on missing/invalid). Called once on load and by the live
    /// config listener on rebind.</summary>
    private void ApplyTargetClickModifierFromConfig()
    {
        if (_composition is null) return;
        _targetClickModifier = ParseModifier(_composition.Config.Get("overlay.targetClickModifier") as string);
    }

    private static HotkeyModifiers ParseModifier(string? value)
        => !string.IsNullOrWhiteSpace(value) && Enum.TryParse<HotkeyModifiers>(value, ignoreCase: true, out var m)
            ? m
            : HotkeyModifiers.Control;

    /// <summary>M02 loop 38 continuation 12: true when the configured click-to-target modifier is
    /// currently held, per the M13 <c>LowLevelHotkeyHook</c>'s own KEYDOWN/KEYUP tracking (no
    /// GetAsyncKeyState/GetKeyboardState — see that class's policy note). Drives both
    /// <see cref="UpdateTargetClickThrough"/> and <c>OverlayHost</c>'s portrait click gate.</summary>
    private bool IsTargetClickModifierHeld()
        => _composition?.HotkeyHook?.IsModifierHeld(_targetClickModifier) ?? false;

    /// <summary>M02 loop 38 continuation 12 — combo-overlay click-to-target. Polled at a short
    /// interval rather than event-driven: while click-through, this window receives NO input events
    /// at all, so there is nothing to hang a key handler off. While the configured modifier is held,
    /// temporarily clears WS_EX_TRANSPARENT so normal WPF hit-testing applies to the combo card's
    /// portrait (<c>OverlayHost.OnPortraitClick</c>); restored the instant the modifier is released
    /// (next ~30ms tick — no fixed delay/timer-based restore beyond the poll interval itself).
    /// Skipped entirely while <see cref="_movable"/> is true: the window is already non-click-through
    /// for M19 dragging in that mode and must not be touched here. P4: this only ever removes
    /// click-through while the user is actively, deliberately holding a key they configured for
    /// exactly this purpose — never during normal play.</summary>
    private void UpdateTargetClickThrough()
    {
        if (_movable) return;

        // (loop 118) Non-click-through when the modifier is held OR the overlay wants interaction
        // (cursor hovering the combo card / target picker open) — so the ⇄ button and enemy-portrait
        // picker are clickable WITHOUT holding a key. WantsInteraction is only ever true over the small
        // card/picker region while a card is shown, so normal gameplay clicks elsewhere still fall
        // through (P4). Same poll-driven suppress/restore as the modifier path.
        bool held = IsTargetClickModifierHeld() || (_overlayHost?.WantsInteraction ?? false);
        if (held && !_targetClickThroughSuppressed)
        {
            SetClickThrough(false);
            _targetClickThroughSuppressed = true;
        }
        else if (!held && _targetClickThroughSuppressed)
        {
            SetClickThrough(true);
            _targetClickThroughSuppressed = false;
        }
    }

    /// <summary>Tear down the follow-timer and the overlay's own render host when the
    /// overlay window closes. The composition root is owned by HomeWindow and disposed there.</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        // M16 benchmark hook: write the FINAL measurement line before teardown.
        if (_frameMeter is not null)
        {
            LogFrameDrop("FINAL");
            _frameMeter.Stop();
            _frameMeter = null;
            _frameMeterTimer?.Stop();
            _frameMeterTimer = null;
        }

        _trackTimer?.Stop();
        _trackTimer = null;
        _targetClickTimer?.Stop();
        _targetClickTimer = null;
        _overlayHost?.Dispose();

        // M19: release the live config listeners.
        if (_opacitySub is not null) Overlay.Core.EventBus.EventBus.Unsubscribe(_opacitySub);
        if (_movableSub is not null) Overlay.Core.EventBus.EventBus.Unsubscribe(_movableSub);
        // M02 loop 38 continuation 12: release the target-click-modifier config listener.
        if (_targetClickModifierSub is not null) Overlay.Core.EventBus.EventBus.Unsubscribe(_targetClickModifierSub);
        _opacitySub = null;
        _movableSub = null;
        _targetClickModifierSub = null;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>M19: set the overlay window opacity live, clamped to the settings range [0.2, 1.0].
    /// Called on load and from the <c>overlay.opacity</c> config listener.</summary>
    public void SetOverlayOpacity(double opacity)
        => Opacity = System.Math.Clamp(opacity, 0.2, 1.0);

    /// <summary>M19: toggle movable mode. Movable → interactive/draggable (click-through OFF); not
    /// movable → click-through ON (default in-game). Called on load and from the
    /// <c>overlay.movable</c> config listener.</summary>
    public void SetMovable(bool movable)
    {
        _movable = movable;
        SetClickThrough(!movable);
        // M02 loop 38 continuation 12: movable mode owns click-through while it's on; drop any
        // stale suppression flag from the target-click gate so the next UpdateTargetClickThrough
        // tick recomputes cleanly from the actual (now movable-driven) window state instead of
        // skipping a needed restore/suppress because of a flag this call didn't set.
        _targetClickThroughSuppressed = false;
    }

    private static double ToDouble(object? value, double fallback)
        => value switch { double d => d, int i => i, _ => fallback };

    private static bool ToBool(object? value, bool fallback)
        => value is bool b ? b : fallback;

    /// <summary>
    /// Enable or disable Win32 click-through on this window.
    ///
    /// When <paramref name="enabled"/> is <see langword="true"/>,
    /// <c>WS_EX_TRANSPARENT | WS_EX_LAYERED</c> are set so all mouse input
    /// falls through to the game window beneath. When <see langword="false"/>,
    /// <c>WS_EX_TRANSPARENT</c> is removed so the window accepts mouse input
    /// (e.g. for settings/dashboard mode).
    /// </summary>
    public void SetClickThrough(bool enabled)
    {
        if (_hwnd == IntPtr.Zero) return;

        int style = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (enabled)
            // Click-through overlay: transparent to input AND never activatable, so the game
            // keeps focus and stays above nothing of ours by accident.
            style |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        else
            // Interactive mode: accept mouse input (clear WS_EX_TRANSPARENT) but KEEP WS_EX_NOACTIVATE
            // (loop 172 — user report: clicking/dragging the overlay stole foreground focus from the
            // game, forcing a re-click on the game to move again). WS_EX_NOACTIVATE lets the window
            // still RECEIVE clicks/drag messages (WPF hit-testing is unaffected) while never becoming
            // the activated/foreground window — so the game keeps focus even while the user drags the
            // card or clicks the ⇄ button.
            style = (style | WS_EX_LAYERED | WS_EX_NOACTIVATE) & ~WS_EX_TRANSPARENT;

        SetWindowLong(_hwnd, GWL_EXSTYLE, style);
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Position/size the overlay to match the League game window's client area when it
    /// exists, otherwise fall back to filling the configured monitor. Called on load and
    /// on every follow-timer tick; repositions only when the source rect actually changes
    /// to avoid layout churn. Fully guarded so a P/Invoke failure degrades to monitor-fill
    /// and never crashes.
    /// </summary>
    /// <summary>Force this window to the top of the Z-order without activating it (so the game
    /// keeps focus). Called each tracking tick so a focused game can't bury the overlay.</summary>
    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        Topmost = true;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void UpdateGeometry()
    {
        try
        {
            if (TryGetGameClientRect(out RECT rect))
            {
                // Re-assert TOPMOST every tick: a focused borderless/windowed game re-raises
                // itself above our window when it gains foreground, so a one-time Topmost=true
                // isn't enough — without this the overlay hides behind the focused game and only
                // reappears on Alt-Tab. SWP_NOACTIVATE keeps the game's focus.
                ReassertTopmost();

                // Only reposition when the game rect changed since the last apply.
                if (_lastGameRect is { } prev && RectEquals(prev, rect))
                    return;

                ApplyDeviceRect(rect);
                _lastGameRect = rect;
            }
            else
            {
                // Game window not found → monitor-fill fallback (preview mode).
                // Re-apply when we were tracking the game, or when nothing has been applied yet:
                // at startup _lastGameRect is ALSO null, so guarding on it alone skipped the very
                // first apply and left a preview-mode overlay at WPF's default window geometry
                // ("overlay-only layout is wrong, in-game is fine").
                if (_lastGameRect is null && _fallbackApplied)
                    return;

                ApplyMonitorGeometry();
                _fallbackApplied = true;
                _lastGameRect = null;
            }
        }
        catch
        {
            // Any interop/PresentationSource failure: fall back to monitor-fill.
            try { ApplyMonitorGeometry(); _fallbackApplied = true; } catch { /* last-resort: leave as-is */ }
            _lastGameRect = null;
        }
    }

    /// <summary>
    /// Locate the League game window and return its client-area bounds in device pixels
    /// (screen coordinates). Returns <see langword="false"/> when the window is not present
    /// or its rect cannot be read. Client area (not the whole window) is used so the overlay
    /// covers the game viewport rather than any title bar / borders.
    /// </summary>
    /// <summary>(M31 §2 layer 0) Best-effort: locate the tracked game's <c>game.cfg</c> and cache its
    /// parsed <c>[HUD]</c> minimap settings, throttled to ~5 s so the 400 ms track tick can call it every
    /// time. Feeds the overlay's minimap calibrator (size + flip). Never throws; keeps the last-known cfg
    /// on any failure. game.cfg is a plain settings file read — NOT process-memory access (P3-safe).</summary>
    private void RefreshGameCfg()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastGameCfgReadUtc).TotalSeconds < 5) return;
        _lastGameCfgReadUtc = now;
        try
        {
            string? path = ResolveGameCfgPath();
            if (path is null) return;
            var cfg = GameCfgReader.Read(path);
            if (!cfg.IsEmpty) _gameCfg = cfg;
        }
        catch { /* best-effort; keep the last-known cfg */ }
    }

    /// <summary>Resolve <c>(install)\Config\game.cfg</c> from the tracked League window's process image
    /// path — mirrors <c>Overlay.Capture.WindowProbe.TryGetGameCfgPath</c> so this project needs no
    /// reference to Overlay.Capture. Uses <c>QueryFullProcessImageName</c> with
    /// PROCESS_QUERY_LIMITED_INFORMATION: a PATH query, explicitly NOT memory access (P3-safe).</summary>
    private static string? ResolveGameCfgPath()
    {
        IntPtr hwnd = FindWindow(LeagueWindowClass, null);
        if (hwnd == IntPtr.Zero) hwnd = FindWindow(null, LeagueWindowTitle);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;

            // …\<install>\Game\League of Legends.exe → …\<install>\Config\game.cfg
            string exe = sb.ToString();
            string? gameDir = System.IO.Path.GetDirectoryName(exe);
            string? installDir = gameDir is null ? null : System.IO.Path.GetDirectoryName(gameDir);
            if (string.IsNullOrEmpty(installDir)) return null;
            return System.IO.Path.Combine(installDir, "Config", "game.cfg");
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>The tracked League window handle (RiotWindowClass, or the window title as a
    /// fallback), or <see cref="IntPtr.Zero"/> if the game isn't running. Passed to
    /// <c>AppComposition.WireMinimapCapture</c> as the M31 capture target (evaluated at each
    /// capture Start, so a game that launches later is picked up on GAME.CONNECTED).</summary>
    private static IntPtr FindLeagueWindow()
    {
        IntPtr hwnd = FindWindow(LeagueWindowClass, null);
        if (hwnd == IntPtr.Zero) hwnd = FindWindow(null, LeagueWindowTitle);
        return (hwnd != IntPtr.Zero && IsWindow(hwnd)) ? hwnd : IntPtr.Zero;
    }

    private static bool TryGetGameClientRect(out RECT screenRect)
    {
        screenRect = default;

        IntPtr hwnd = FindWindow(LeagueWindowClass, null);
        if (hwnd == IntPtr.Zero)
            hwnd = FindWindow(null, LeagueWindowTitle);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return false;

        if (!GetClientRect(hwnd, out RECT client))
            return false;

        // Zero-sized client area (minimized) → treat as not available.
        int w = client.Right - client.Left;
        int h = client.Bottom - client.Top;
        if (w <= 0 || h <= 0)
            return false;

        // Map client-space top-left to screen coordinates.
        var topLeft = new POINT { X = client.Left, Y = client.Top };
        if (!ClientToScreen(hwnd, ref topLeft))
            return false;

        screenRect = new RECT
        {
            Left   = topLeft.X,
            Top    = topLeft.Y,
            Right  = topLeft.X + w,
            Bottom = topLeft.Y + h,
        };
        return true;
    }

    /// <summary>Apply a device-pixel screen rect to this window, converting to WPF DIPs
    /// via the PresentationSource DPI matrix (correct at non-100% display scaling).</summary>
    private void ApplyDeviceRect(RECT bounds)
    {
        double dpiScaleX = 1.0, dpiScaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            dpiScaleX = source.CompositionTarget.TransformFromDevice.M11;
            dpiScaleY = source.CompositionTarget.TransformFromDevice.M22;
        }

        Left   = bounds.Left * dpiScaleX;
        Top    = bounds.Top  * dpiScaleY;
        Width  = (bounds.Right  - bounds.Left) * dpiScaleX;
        Height = (bounds.Bottom - bounds.Top)  * dpiScaleY;
    }

    private static bool RectEquals(RECT a, RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>
    /// Fallback geometry: enumerate physical monitors via <c>EnumDisplayMonitors</c> and
    /// position this window over the monitor at <see cref="OverlayConfig.TargetMonitorIndex"/>
    /// (falls back to index 0 if out of range). Used when the game window is not found
    /// (e.g. the preview toggle with no game running).
    /// </summary>
    private void ApplyMonitorGeometry()
    {
        var monitorRects = new List<RECT>();
        bool CollectMonitor(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
                monitorRects.Add(info.rcMonitor);
            return true;
        }
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, CollectMonitor, IntPtr.Zero);

        if (monitorRects.Count == 0)
        {
            // Fallback: cover the WPF virtual screen (should never happen).
            Left   = SystemParameters.VirtualScreenLeft;
            Top    = SystemParameters.VirtualScreenTop;
            Width  = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            return;
        }

        int idx = _config.TargetMonitorIndex;
        if (idx < 0 || idx >= monitorRects.Count)
            idx = 0;

        ApplyDeviceRect(monitorRects[idx]);
    }
}
