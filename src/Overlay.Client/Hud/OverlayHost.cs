using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Overlay.Client.Render;
using Overlay.Core;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Items;
using Overlay.Core.Overlay;
using Overlay.Core.Render;

namespace Overlay.Client.Hud;

/// <summary>
/// Thin WPF host for M02 Overlay Engine: the <see cref="IOverlayView"/> implementation
/// that wires the WPF-free <see cref="OverlayCoordinator"/> to the <b>existing</b>
/// click-through <see cref="MainWindow"/> and drives M16's <see cref="RenderSurface"/>
/// each frame.
///
/// <para><b>Reuses, does not reinvent, click-through.</b> This class never sets a window
/// style or touches an HWND — click-through already lives in
/// <see cref="MainWindow.SetClickThrough(bool)"/> (WS_EX_TRANSPARENT on an independent
/// top-level window). Dashboard open/close simply toggles it. There is no injection, no
/// input hook, no <c>SendInput</c> here.</para>
///
/// <para><b>Frame-driven rendering.</b> A <see cref="DispatcherTimer"/> (UI thread) ticks
/// each frame: it purges expired HUDs, pulls the coordinator's Z-Order-sorted render list,
/// maps each <see cref="HUDPayload"/> to an M16 <see cref="DrawCommand"/>, and submits one
/// frame to the <see cref="RenderSurface"/>. Because the coordinator owns state, the
/// <see cref="ShowHud"/>/<see cref="HideHud"/> callbacks need do nothing beyond the next
/// tick's pull. The ~16ms tick makes auto-hide land well within the spec's 100ms tolerance.</para>
/// </summary>
public sealed class OverlayHost : IOverlayView, IDisposable
{
    private readonly MainWindow _window;
    private readonly RenderSurface _surface;
    private readonly RenderQueue _queue = new();
    private readonly ConfigManager _config;
    private readonly IClock _clock = new SystemClock();
    private readonly OverlayCoordinator _coordinator;
    private readonly DispatcherTimer _timer;

    /// <summary>Accessor for the latest live <see cref="GameSnapshot"/> (owned by AppComposition).
    /// Read each frame so the overlay can draw a persistent in-game status card whenever a game is
    /// connected, giving the overlay a constant useful presence between event-driven HUD cards.</summary>
    private readonly Func<GameSnapshot?> _snapshot;

    /// <summary>Reused per-payload scratch buffer for a card's DrawCommands, so the frame
    /// loop does not allocate a fresh list per HUD each tick.</summary>
    private readonly List<DrawCommand> _cardBuffer = new(16);

    /// <summary>Fetches + caches enemy champion portraits (P1, public Data Dragon CDN) for the
    /// combo card's target header. Owned here (not AppComposition): the card is the only consumer.
    /// Non-blocking — a not-yet-loaded portrait is simply skipped for the frame.</summary>
    private readonly ChampionIconProvider _championIcons = new();

    /// <summary>(M31 §C) Live query for the last-seen enemy markers drawn on the minimap.
    /// Query-shaped like <see cref=_inhibitorQuery/> rather than bus-pushed, so render state
    /// is read on the render thread. Null = feature off.</summary>
    private readonly Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.Jungle.EnemyAfterimageTracker.Afterimage>?>? _afterimageQuery;

    /// <summary>M02 loop 38 continuation 12: reports whether the configured click-to-target
    /// modifier (default Ctrl) is currently held. Injected by <c>MainWindow</c> (which owns the
    /// M13 <c>LowLevelHotkeyHook</c> reference via <c>AppComposition</c>) so <see cref="OnPortraitClick"/>
    /// can gate on it directly, rather than only relying on "the window happens to be non-click-through
    /// right now" — that can also be true in M19 movable/drag mode, which must NOT trigger a target
    /// change.</summary>
    private readonly Func<bool> _isTargetClickModifierHeld;

    /// <summary>M02 pending-change #1 (modular per-element HUD positioning): reports whether
    /// the overlay is currently in M19 movable mode (<c>overlay.movable</c>). Injected by
    /// <c>MainWindow</c> (which owns <c>_movable</c>) so this class can gate per-element
    /// dragging on the SAME flag the window already uses to decide click-through, without
    /// duplicating that state.</summary>
    private readonly Func<bool> _isMovable;

    /// <summary>(loop 141) Live query for the current enemy return predictions (from
    /// <c>LaneReturnPredictor.GetActive</c>), or null when the feature is off. Drawn each frame as a
    /// VERTICAL STACK to the LEFT of the minimap (enemy portrait + "~M:SS" estimate countdown per entry,
    /// death order top→bottom), the whole stack draggable as one via the "laneReturn" element key.</summary>
    private readonly Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.LaneReturnStatus>?>? _laneReturnQuery;

    /// <summary>(loop 142) Live query for inhibitor respawn countdowns (from
    /// <c>InhibitorTimer.GetActive</c>), or null when off. Each entry's time is drawn ON the minimap at
    /// the destroyed inhibitor's location (LoL-native jungle-timer style), live-updating each frame.</summary>
    private readonly Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.Inhibitor.InhibitorStatus>?>? _inhibitorQuery;

    /// <summary>(patch 15.1) Live query for Nexus-turret respawn countdowns (from
    /// <c>NexusTurretTimer.GetActive</c>), or null when off. Drawn ON the minimap at each destroyed
    /// Nexus turret's location, same style as <see cref="_inhibitorQuery"/> (3:00 respawn).</summary>
    private readonly Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.NexusTurret.NexusTurretStatus>?>? _nexusTurretQuery;

    /// <summary>(M31 §2 layer 0) Live query for the tracked game's parsed <c>game.cfg [HUD]</c> settings
    /// (MinimapScale/FlipMiniMap), or null when unavailable. Feeds <c>MinimapCalibrator.Compute</c> so the
    /// minimap-anchored timers follow the user's real minimap size + flip instead of the pure geometric
    /// guess. Read fresh each frame (MainWindow caches + throttles the actual file read).</summary>
    private readonly Func<Overlay.Core.Vision.GameCfgHudSettings?>? _gameCfgQuery;

    /// <summary>(user request) Live query for the always-on per-skill damage panel vs the current target
    /// (<c>ComboRunner.ComputeSkillPanel</c>), or null when unavailable. Throttled via <see cref="SkillPanel"/>
    /// so its (per-call) 6 engine evaluations run ~4x/s, not every 60fps frame.</summary>
    private readonly Func<GameSnapshot, Overlay.Core.Combo.SkillPanelResult?>? _skillPanelQuery;
    private Overlay.Core.Combo.SkillPanelResult? _skillPanelCache;
    private long _skillPanelNextMs;

    /// <summary>Last-rendered combo-card target portrait's hit rect + champion name (surface-local
    /// coordinates), reset every frame in <see cref="RenderFrame"/> and set only when
    /// <see cref="BuildComboCard"/> actually draws a portrait this frame. This is the ONLY
    /// clickable region on the card (loop 38 continuation 12 design) — a click anywhere else,
    /// or when no combo card with a resolved target is currently shown, hits nothing.</summary>
    private Rect? _portraitRect;

    /// <summary>(loop 115) Last-rendered "change target" (⇄) button hit rect (surface-local),
    /// reset every frame like <see cref="_portraitRect"/> and set only when
    /// <see cref="BuildComboCard"/> draws the button this frame. Clicking it cycles the designated
    /// target to the next living enemy — the same action as clicking the portrait — via
    /// <c>targeting.mode</c>/<c>targeting.manualTarget</c>, which <see cref="ComboRunner"/> re-reads
    /// next trigger. Gated on the same target-click modifier as the portrait.</summary>
    private Rect? _swapRect;

    /// <summary>(loop 118) Target-picker state. When <see cref="_pickerOpen"/> is true the combo card
    /// draws a row of living-enemy portraits (<see cref="_pickerRects"/>: each portrait's hit rect +
    /// its champion name); clicking one pins that champion (<c>targeting.manualTarget</c>) and closes
    /// the picker. Opened by clicking the ⇄ button or the target portrait. Rebuilt each frame while open.</summary>
    private bool _pickerOpen;
    private readonly List<(Rect Rect, string Champion)> _pickerRects = new();

    /// <summary>(§40) The always-on enemy portrait row's clickable cells this frame — each enemy's hit
    /// rect + champion name. Clicking one pins that champion as the manual combo target (same mechanism
    /// as the old ⇄ picker). Rebuilt every frame in <see cref="RenderFrame"/>.</summary>
    private readonly List<(Rect Rect, string Champion)> _rosterRects = new();

    /// <summary>(loop 118) True when the overlay wants to be INTERACTIVE (non-click-through) this frame:
    /// the cursor is hovering the combo card, or the picker is open. <see cref="MainWindow"/> polls this
    /// (alongside the held target-click modifier) to temporarily clear WS_EX_TRANSPARENT so the ⇄ button
    /// and picker portraits are clickable WITHOUT holding a key. Only ever true over the small card/picker
    /// region and only while a card is shown, so normal gameplay clicks elsewhere still fall through (P4).</summary>
    private volatile bool _wantsInteraction;
    public bool WantsInteraction => _wantsInteraction;

    /// <summary>M02 pending-change #1: each HUD element's last-rendered bounding rect this
    /// frame (surface-local), keyed by <see cref="ElementKey(HudType)"/>/"statusCard". Reset
    /// every frame in <see cref="RenderFrame"/> so a drag can only ever start on what is
    /// actually on screen THIS frame — mirrors the existing <see cref="_portraitRect"/> pattern.</summary>
    private readonly Dictionary<string, Rect> _elementRects = new();

    /// <summary>M02 pending-change #1: key of the element currently being dragged (movable
    /// mode), or null when no drag is in progress.</summary>
    private string? _draggingKey;
    private System.Windows.Point _dragAnchorMouse;
    private double _dragBaseDx, _dragBaseDy;
    private double _dragLiveDx, _dragLiveDy;

    /// <summary>(user request) When a movable-mode mouse-down lands on an enemy portrait, the champion
    /// under it — so <see cref="OnSurfaceMouseUp"/> can treat a near-zero-movement release as a TAP that
    /// selects that enemy as the combo target instead of committing a (no-op) row drag. Null when the
    /// press did not start on a portrait.</summary>
    private string? _pendingTapChampion;
    private System.Windows.Point _pendingTapStart;

    /// <summary>Base (pre-drag) absolute rect of the one-time minimap calibration box, captured when a
    /// "minimapCal" drag starts so mouse-up can commit the new absolute position as window fractions.</summary>
    private double _minimapCalBaseX, _minimapCalBaseY, _minimapCalBaseSize;

    private OverlayHost(MainWindow window, Panel host, ConfigManager config, Func<GameSnapshot?> snapshot,
                        Func<bool> isTargetClickModifierHeld, Func<bool> isMovable,
                        Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.LaneReturnStatus>?>? laneReturnQuery,
                        Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.Inhibitor.InhibitorStatus>?>? inhibitorQuery,
                        Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.NexusTurret.NexusTurretStatus>?>? nexusTurretQuery,
                        Func<Overlay.Core.Vision.GameCfgHudSettings?>? gameCfgQuery,
                        Func<GameSnapshot, Overlay.Core.Combo.SkillPanelResult?>? skillPanelQuery,
                        Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.Jungle.EnemyAfterimageTracker.Afterimage>?>? afterimageQuery)
    {
        _window = window;
        // E1: the shared ConfigManager is injected by AppComposition (one instance for the
        // whole app) instead of constructing a duplicate here.
        _config = config;
        _snapshot = snapshot;
        _isTargetClickModifierHeld = isTargetClickModifierHeld;
        _isMovable = isMovable;
        _laneReturnQuery = laneReturnQuery; // (loop 125) same-lane enemy return prediction, null = feature off
        _inhibitorQuery = inhibitorQuery;   // (loop 142) inhibitor respawn countdowns, null = feature off
        _nexusTurretQuery = nexusTurretQuery; // (patch 15.1) Nexus-turret respawn countdowns, null = feature off
        _gameCfgQuery = gameCfgQuery;         // (M31 §2 layer 0) game.cfg minimap calibration, null = geometric prior only
        _skillPanelQuery = skillPanelQuery;   // (user request) always-on per-skill damage panel, null = off
        _afterimageQuery = afterimageQuery;   // (M31 §C) last-seen enemy portraits on the minimap, null = off

        _surface = new RenderSurface();
        // Add behind the existing data widgets so the widget shell stays on top.
        host.Children.Insert(0, _surface);
        // M02 loop 38 continuation 12 / M02 pending-change #1: the only mouse wiring this class
        // adds. Does not touch window style (see class doc "Reuses, does not reinvent,
        // click-through") — MainWindow's click-through toggle is what makes this reachable at
        // all: either the target-click modifier is held (see OnPortraitClick) or movable mode
        // is on (see TryBeginElementDrag), both already non-click-through for their own reasons.
        _surface.MouseLeftButtonDown += OnSurfaceMouseDown;
        _surface.MouseMove += OnSurfaceMouseMove;
        _surface.MouseLeftButtonUp += OnSurfaceMouseUp;
        _surface.MouseWheel += OnSurfaceMouseWheel; // one-time minimap calibration resize (movable mode)

        // Keep the render surface the SAME size as the overlay window. The surface lives in a Canvas
        // (WidgetCanvas), which does not stretch its children, and its size was previously set only
        // once at Start(); so when the tracked LoL window changed size (resolution or fullscreen/
        // borderless/windowed switch) the overlay stopped covering — and auto-fit-scaling to — the
        // real viewport. Following SizeChanged keeps ActualHeight (and thus the auto-fit scale) in
        // step with the live window so the HUD stays proportional to the active LoL window.
        _window.SizeChanged += (_, _) => SyncSurfaceToWindow();

        _coordinator = new OverlayCoordinator(this, _config, _clock);

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16), // ~60fps; << the 100ms auto-hide bound
        };
        _timer.Tick += (_, _) => RenderFrame();
    }

    /// <summary>Compose the overlay engine over an existing <see cref="MainWindow"/> and
    /// start rendering + <c>UI.*</c> subscription. Call once from
    /// <c>MainWindow.OnLoaded</c> after geometry is applied.</summary>
    public static OverlayHost Start(MainWindow window, Panel host, ConfigManager config,
                                    Func<GameSnapshot?> snapshot, Func<bool> isTargetClickModifierHeld,
                                    Func<bool> isMovable, Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.LaneReturnStatus>?>? laneReturnQuery = null,
                                    Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.Inhibitor.InhibitorStatus>?>? inhibitorQuery = null,
                                    Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.NexusTurret.NexusTurretStatus>?>? nexusTurretQuery = null,
                                    Func<Overlay.Core.Vision.GameCfgHudSettings?>? gameCfgQuery = null,
                                    Func<GameSnapshot, Overlay.Core.Combo.SkillPanelResult?>? skillPanelQuery = null,
                                    Func<System.Collections.Generic.IReadOnlyList<Overlay.Core.Jungle.EnemyAfterimageTracker.Afterimage>?>? afterimageQuery = null)
    {
        var self = new OverlayHost(window, host, config, snapshot, isTargetClickModifierHeld, isMovable, laneReturnQuery, inhibitorQuery, nexusTurretQuery, gameCfgQuery, skillPanelQuery, afterimageQuery);
        self.SyncSurfaceToWindow();
        self._coordinator.Start();
        self._timer.Start();
        return self;
    }

    /// <summary>The Core coordinator, exposed so integration code can call
    /// <see cref="OverlayCoordinator.OpenDashboard"/> etc. directly if needed.</summary>
    public OverlayCoordinator Coordinator => _coordinator;

    /// <summary>M02 loop 38 continuation 12 — combo-overlay click-to-target. Only does anything
    /// when (a) the configured modifier is currently held (the window is only reachable at all
    /// while that's true, but movable/drag mode also makes it reachable, so this is checked
    /// explicitly rather than assumed) and (b) the click lands inside this frame's actual
    /// rendered portrait rect. On a hit, cycles to the next living enemy (wrapping) via
    /// <see cref="ComboRunner.NextLivingEnemy"/> and pins it the same way the Settings-dropdown
    /// target picker already does (<c>targeting.mode</c>/<c>targeting.manualTarget</c> in the
    /// shared config) — <see cref="ComboRunner"/> re-reads those fresh on the next
    /// <c>COMBO.TRIGGER</c>, so no ComboRunner change was needed for this to take effect.</summary>
    private void OnPortraitClick(object sender, MouseButtonEventArgs e)
    {
        // (loop 118) No modifier gate: reaching here already means the surface is interactive — the
        // cursor is over the card so MainWindow cleared click-through (hover-to-interact), or the
        // target-click modifier is held. Either way this click is a deliberate interaction.
        var pt = e.GetPosition(_surface);

        // Picker OPEN: a click on an enemy portrait pins that champion; a click elsewhere dismisses it.
        if (_pickerOpen)
        {
            foreach (var (rect, champion) in _pickerRects)
            {
                if (!rect.Contains(pt)) continue;
                _config.Set("targeting.mode", "Manual");
                _config.Set("targeting.manualTarget", champion);
                _pickerOpen = false;
                e.Handled = true;
                return;
            }
            _pickerOpen = false; // clicked outside the portraits → dismiss
            e.Handled = true;
            return;
        }

        // Picker CLOSED: clicking the ⇄ button (or the target portrait) OPENS the enemy picker.
        bool onSwap = _swapRect is { } srect && srect.Contains(pt);
        bool onPortrait = _portraitRect is { } prect && prect.Contains(pt);
        if (onSwap || onPortrait)
        {
            _pickerOpen = true;
            e.Handled = true;
        }
    }

    /// <summary>M02 pending-change #1 (modular per-element HUD positioning): dispatches a
    /// surface mouse-down to either per-element drag-start (movable mode) or the pre-existing
    /// click-to-target portrait handler — the two are mutually exclusive because
    /// <c>MainWindow.UpdateTargetClickThrough</c> already skips its own gate entirely while
    /// movable mode is on, so only one of these two reasons is ever why the window is
    /// non-click-through at a given moment.</summary>
    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pt = e.GetPosition(_surface);

        // (§40) Portrait-row selection: click an enemy portrait to pin it as the combo target. Takes
        // priority in NON-movable mode; in movable mode the row is dragged as one element instead.
        if (!_isMovable() && TrySelectRosterTarget(pt))
        {
            e.Handled = true;
            return;
        }

        // (loop 121) The target-picker interaction takes PRIORITY over element dragging so the ⇄ button
        // and the enemy picker work even in movable mode (where clicks would otherwise start a drag and
        // a small click would just do nothing — the cause of "hover lights up but click is dead").
        bool onSwap = _swapRect is { } sr && sr.Contains(pt);
        bool onPortrait = _portraitRect is { } pr && pr.Contains(pt);
        if (onSwap || onPortrait || _pickerOpen)
        {
            OnPortraitClick(sender, e);
            return;
        }

        if (_isMovable())
        {
            // Remember a possible TAP on an enemy portrait so mouse-up can distinguish a quick click
            // (→ select that enemy as the combo target, like non-movable mode) from an actual drag
            // (→ reposition the portrait row). Without this, in movable mode every portrait click was
            // swallowed as a zero-distance row drag and the target never changed (user report).
            _pendingTapChampion = RosterChampionAt(pt);
            _pendingTapStart = pt;
            TryBeginElementDrag(e);
            return;
        }

        OnPortraitClick(sender, e);
    }

    /// <summary>(§40) If <paramref name="pt"/> is over a portrait-row cell, pins that champion as the
    /// manual combo target (same config keys as the old ⇄ picker) and returns true.</summary>
    private bool TrySelectRosterTarget(System.Windows.Point pt)
    {
        foreach (var (rect, champion) in _rosterRects)
        {
            if (!rect.Contains(pt)) continue;
            _config.Set("targeting.mode", "Manual");
            _config.Set("targeting.manualTarget", champion);
            return true;
        }
        return false;
    }

    /// <summary>The roster champion whose hit cell contains <paramref name="pt"/> this frame, or null.
    /// Same rects as <see cref="TrySelectRosterTarget"/> but without mutating config — used to remember a
    /// movable-mode tap target for <see cref="OnSurfaceMouseUp"/>.</summary>
    private string? RosterChampionAt(System.Windows.Point pt)
    {
        foreach (var (rect, champion) in _rosterRects)
            if (rect.Contains(pt)) return champion;
        return null;
    }

    /// <summary>M02 pending-change #1: hit-tests the click against this frame's actual
    /// rendered element rects (<see cref="_elementRects"/> — same "only what's on screen THIS
    /// frame is clickable" guarantee as the portrait rect) and, on a hit, begins dragging that
    /// ONE element. Mouse capture ensures drag continues even if the pointer leaves the
    /// element's rect mid-drag.</summary>
    private void TryBeginElementDrag(MouseButtonEventArgs e)
    {
        var pt = e.GetPosition(_surface);
        foreach (var (key, rect) in _elementRects)
        {
            if (!rect.Contains(pt)) continue;

            if (key == "minimapCal")
            {
                // Calibration box drags as an ABSOLUTE rect (not a positions.{key} offset): capture its
                // current absolute box so mouse-up can commit the moved top-left as window fractions.
                _minimapCalBaseX = rect.X;
                _minimapCalBaseY = rect.Y;
                _minimapCalBaseSize = rect.Width;
                _draggingKey = key;
                _dragAnchorMouse = pt;
                _dragBaseDx = _dragBaseDy = _dragLiveDx = _dragLiveDy = 0;
                _surface.CaptureMouse();
                e.Handled = true;
                return;
            }

            var (dx, dy) = GetOffset(key);
            _draggingKey = key;
            _dragAnchorMouse = pt;
            _dragBaseDx = dx;
            _dragBaseDy = dy;
            _dragLiveDx = dx;
            _dragLiveDy = dy;
            _surface.CaptureMouse();
            e.Handled = true;
            return;
        }
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingKey is null) return;

        var pt = e.GetPosition(_surface);
        _dragLiveDx = _dragBaseDx + (pt.X - _dragAnchorMouse.X);
        _dragLiveDy = _dragBaseDy + (pt.Y - _dragAnchorMouse.Y);
    }

    /// <summary>Commits the drag's final offset to <c>overlay.positions.{key}.x/.y</c> — only
    /// on mouse-up, not on every move, so dragging does not flood
    /// <c>SYSTEM.CONFIG_CHANGED</c>/debounced-write churn (matches the existing debounce
    /// design in <see cref="ConfigManager"/> but avoids generating hundreds of Set calls for a
    /// single drag gesture in the first place).</summary>
    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingKey is null) return;

        if (_draggingKey == "minimapCal")
        {
            // Commit the moved calibration box as an ABSOLUTE rect in window fractions (survives resolution
            // changes reasonably). Size is unchanged by a move (wheel handles size).
            double w = _surface.ActualWidth, h = _surface.ActualHeight;
            if (w > 0 && h > 0)
            {
                _config.Set("overlay.minimapCalibration.enabled", true);
                _config.Set("overlay.minimapCalibration.x", (_minimapCalBaseX + _dragLiveDx) / w);
                _config.Set("overlay.minimapCalibration.y", (_minimapCalBaseY + _dragLiveDy) / h);
                _config.Set("overlay.minimapCalibration.size", _minimapCalBaseSize / h);
            }
            _surface.ReleaseMouseCapture();
            _draggingKey = null;
            e.Handled = true;
            return;
        }

        // (user request) Movable-mode TAP on an enemy portrait = select that enemy as the combo target
        // (not a row move). If the press started on a portrait and the pointer barely moved, treat it as
        // a click: pin the target and DON'T commit a position change.
        if (_draggingKey == "enemyPortraitRow" && _pendingTapChampion is { } tapChamp)
        {
            var up = e.GetPosition(_surface);
            double moved = Math.Abs(up.X - _pendingTapStart.X) + Math.Abs(up.Y - _pendingTapStart.Y);
            if (moved < 5.0)
            {
                _config.Set("targeting.mode", "Manual");
                _config.Set("targeting.manualTarget", tapChamp);
                _surface.ReleaseMouseCapture();
                _draggingKey = null;
                _pendingTapChampion = null;
                e.Handled = true;
                return;
            }
        }
        _pendingTapChampion = null;

        _config.Set("overlay.positions." + _draggingKey + ".x", _dragLiveDx);
        _config.Set("overlay.positions." + _draggingKey + ".y", _dragLiveDy);
        _surface.ReleaseMouseCapture();
        _draggingKey = null;
        e.Handled = true;
    }

    /// <summary>M02 pending-change #1: the persisted (or, while actively being dragged,
    /// live-in-progress) position offset for <paramref name="key"/>, in DIPs, added to that
    /// element's default computed anchor. Reading the live value during a drag (rather than
    /// re-reading config, which is only written on mouse-up) is what makes the drag track the
    /// pointer smoothly frame-to-frame.</summary>
    private (double dx, double dy) GetOffset(string key)
    {
        if (_draggingKey == key) return (_dragLiveDx, _dragLiveDy);

        double dx = _config.Get("overlay.positions." + key + ".x") is double dxv ? dxv : 0;
        double dy = _config.Get("overlay.positions." + key + ".y") is double dyv ? dyv : 0;
        return (dx, dy);
    }

    /// <summary>M02 pending-change #1: whether the given element key's enable toggle
    /// (<c>overlay.items.{key}.enabled</c>) is on. Defensive default true on a missing/non-bool
    /// value — the schema (<c>ConfigSchema.OverlayItemsConfig</c>) always supplies an explicit
    /// bool for every key covered here, so this fallback only matters for a key this class
    /// might query that isn't in the schema, which should not happen in practice.</summary>
    private bool IsEnabled(string key)
        => _config.Get("overlay.items." + key + ".enabled") is bool b ? b : true;

    /// <summary>(M31 §C) Afterimage master switch — default ON, same convention as the other
    /// minimap widgets.</summary>
    private bool AfterimageEnabled()
        => _config.Get("minimap.afterimage.enabled") is bool b ? b : true;

    /// <summary>(M31 §C) Portrait opacity. Clamped so a hand-edited config can't make markers
    /// invisible or opaque enough to be mistaken for a live sighting.
    ///
    /// <para>Default raised 0.5 → 0.75 on 2026-07-20: 50% was the originally requested figure, but
    /// in a live game the marker read as too faint. Note this default only applies to a config that
    /// has never stored the key — an existing install already has 0.5 written, so that user changes
    /// it with the settings slider.</para></summary>
    private double AfterimageOpacity()
        => _config.Get("minimap.afterimage.opacity") switch
        {
            double d => Math.Clamp(d, 0.1, 0.9),
            int i => Math.Clamp(i, 0.1, 0.9),
            _ => DefaultAfterimageOpacity,
        };

    private const double DefaultAfterimageOpacity = 0.75;

    /// <summary>(M31 §C) Per-role gate. A role with no entry counts as enabled, so an older
    /// config keeps every marker. An alert whose role never resolved (empty key) has no gate to
    /// consult and obeys the master switch alone.</summary>
    private bool AfterimageRoleEnabled(string roleKey)
        => string.IsNullOrEmpty(roleKey)
           || _config.Get("minimap.afterimage.roles." + roleKey) is not bool b
           || b;

    /// <summary>The minimap on-screen rect the anchored timers (inhibitor / Nexus-turret / enemy-return)
    /// place against. Uses the user's ONE-TIME calibration (<c>overlay.minimapCalibration</c>, aligned once
    /// in movable mode) when enabled; otherwise the HUD-layout-derived geometric + game.cfg auto estimate.
    /// <paramref name="flipped"/> (game.cfg FlipMiniMap) only decides which side the return stack goes.</summary>
    private Overlay.Core.Render.RenderBounds ResolveMinimapRect(double w, double h, out bool flipped)
    {
        var auto = Overlay.Core.Vision.MinimapCalibrator.Compute(w, h, _gameCfgQuery?.Invoke());
        flipped = auto.Flipped;
        if (_config.Get("overlay.minimapCalibration.enabled") is bool en && en
            && _config.Get("overlay.minimapCalibration.size") is double sz && sz > 0)
        {
            double cx = _config.Get("overlay.minimapCalibration.x") is double xv ? xv : 0;
            double cy = _config.Get("overlay.minimapCalibration.y") is double yv ? yv : 0;
            double edge = sz * h;
            return new Overlay.Core.Render.RenderBounds(cx * w, cy * h, edge, edge);
        }
        return auto.Rect;
    }

    /// <summary>Mouse-wheel over the minimap-calibration box (movable mode) resizes it, persisting the new
    /// square edge (as a window-height fraction) to <c>overlay.minimapCalibration</c>. Keeps the top-left
    /// fixed so the corner the user aligned stays put.</summary>
    private void OnSurfaceMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (!_isMovable()) return;
        var pt = e.GetPosition(_surface);
        if (!(_elementRects.TryGetValue("minimapCal", out var r) && r.Contains(pt))) return;

        double w = _surface.ActualWidth, h = _surface.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var box = ResolveMinimapRect(w, h, out _);
        double edge = box.Width + (e.Delta > 0 ? 6.0 : -6.0); // ~6 px per wheel notch
        if (edge < 20) edge = 20;

        _config.Set("overlay.minimapCalibration.enabled", true);
        _config.Set("overlay.minimapCalibration.x", box.X / w);
        _config.Set("overlay.minimapCalibration.y", box.Y / h);
        _config.Set("overlay.minimapCalibration.size", edge / h);
        e.Handled = true;
    }

    /// <summary>True when the user turned ON minimap-calibration mode in client settings
    /// (<c>overlay.minimapCalibrate</c>). Default OFF, so the cyan calibration box + hint never show
    /// in-game unless explicitly enabled — the calibration UI lives in settings, not on the live overlay.</summary>
    private bool CalibrateMode()
        => _config.Get("overlay.minimapCalibrate") is bool b && b;

    /// <summary>Draws a clean minimap timer number — NO background panel (keeps the map visible), just the
    /// digits with a 1px dark shadow for legibility, roughly centered on the structure at (px,py).</summary>
    private void AddMinimapTimerLabel(double px, double py, string label, uint color, double f)
    {
        double tx = px - label.Length * f * 0.28; // ~half the text width → centered on the structure icon
        double ty = py - f * 0.6;
        _cardBuffer.Add(Text(label, tx + 1, ty + 1, 0xC0000000, f)); // shadow
        _cardBuffer.Add(Text(label, tx, ty, color, f));
    }

    /// <summary>True when the user chose M:SS timer display (<c>overlay.timerFormatMmSs</c>); default seconds.</summary>
    private bool TimerMmSs() => _config.Get("overlay.timerFormatMmSs") is bool b && b;

    /// <summary>(user request) Formats a remaining-seconds value for the return / 구조물(억제기·녹서즐) timers:
    /// plain integer SECONDS by default (e.g. "83"), or "M:SS" (e.g. "1:23") when the user turned on the
    /// timer-format toggle. Ceils and floors at 0.</summary>
    private string FormatTimerSeconds(double seconds)
    {
        int t = (int)System.Math.Ceiling(System.Math.Max(0, seconds));
        return TimerMmSs()
            ? (t / 60).ToString(CultureInfo.InvariantCulture) + ":" + (t % 60).ToString("00", CultureInfo.InvariantCulture)
            : t.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Maps a <see cref="HudType"/> to the short key used by both
    /// <c>overlay.items.{key}.enabled</c> and <c>overlay.positions.{key}</c>. Names match the
    /// pre-existing M19 §2 item keys where one already existed (inhibitorTimers/
    /// globalGold) for backward compatibility with configs saved before this change.</summary>
    private static string ElementKey(HudType type) => type switch
    {
        HudType.ComboResult    => "comboResult",
        HudType.ItemAlert      => "itemAlert",
        HudType.RecallTimer    => "recallTimer",
        HudType.Notification   => "notification",
        HudType.InhibitorTimer => "inhibitorTimers",
        HudType.GlobalGold     => "globalGold",
        HudType.EnemyJunglerDebug => "enemyJunglerDebug",
        HudType.EnemyJunglerSpottedAlert => "enemyJunglerSpotted", // same toggle as the detector
        HudType.EnemyItemAlert => "enemyItemAlert",
        HudType.EnemyPresence  => "notification", // the presence toast shares the notification toggle
        _                      => "notification",
    };

    // ── IOverlayView (rendering is frame-driven; Show/Hide are pull-model no-ops) ──

    public void ShowHud(HUDPayload payload) { /* picked up by the next RenderFrame tick */ }

    public void HideHud(string hudId) { /* picked up by the next RenderFrame tick */ }

    public void OpenDashboard() => _window.SetClickThrough(false);

    public void CloseDashboard() => _window.SetClickThrough(true);

    // ── Frame loop ────────────────────────────────────────────────────────────────

    private void RenderFrame()
    {
        _coordinator.PurgeExpired(_clock.NowMs);

        // M02 loop 38 continuation 12: cleared every frame so a click can only ever hit the
        // portrait actually drawn THIS frame (a stale rect from a since-dismissed/replaced card
        // must never be clickable).
        _portraitRect = null;
        _swapRect = null; // (loop 115) same per-frame reset for the change-target button
        _pickerRects.Clear(); // (loop 118) rebuilt this frame only while the picker is open
        _rosterRects.Clear(); // (§40) enemy portrait-row cells hit-testable only this frame
        // M02 pending-change #1: same "only this frame's actual rects are hit-testable"
        // guarantee, for per-element dragging.
        _elementRects.Clear();

        GetSurfaceSize(out double w, out double h);
        double scale = ReadScale(h);

        _queue.BeginFrame();
        // Guard against the very first frame before layout has sized the surface — anchoring
        // top-center / top-right needs a real width/height, so we simply skip building cards
        // (submit an empty frame) until we have one.
        if (w > 0 && h > 0)
        {
            // Persistent in-game status card: rendered directly every frame while a game is
            // connected (NOT routed through the auto-hide coordinator), at a low ZOrder so the
            // event-driven cards below always draw over it. Gives the overlay a constant presence.
            // M02 pending-change #1: gated on its own enable toggle (statusCard), default on.
            var snap = _snapshot();
            if (snap?.HasData == true && IsEnabled("statusCard"))
            {
                _cardBuffer.Clear();
                BuildStatusCard(_cardBuffer, snap, scale);
                for (int i = 0; i < _cardBuffer.Count; i++)
                    _queue.Enqueue(_cardBuffer[i], StatusCardZOrder);
            }

            // (§40) The top-center combo STACK: always-on enemy portrait row + the combo-damage
            // overlay + the per-skill overlay, as one centered vertical column. The combo/skill cards
            // read the current ComboHudResult (pulled from the coordinator's active HUD); the portrait
            // row is independent (shows whenever a game with enemies is connected).
            ComboHudResult? comboHud = null;
            foreach (var item in _coordinator.GetRenderList())
                if (item.Payload.Type == HudType.ComboResult && item.Payload.Content is ComboHudResult ch)
                { comboHud = ch; break; }
            BuildComboStack(w, h, scale, comboHud);

            // (2026-07-26) 적 제어 와드 현황 — carried control wards per enemy, from the public
            // scoreboard items. Draggable left-edge panel, gated like every other HUD element.
            if (IsEnabled("enemyWards"))
            {
                var wardSnap = _snapshot();
                if (wardSnap is { HasData: true })
                {
                    _cardBuffer.Clear();
                    BuildEnemyWardPanel(_cardBuffer, wardSnap, w, h, scale);
                    Enqueue(_cardBuffer, ComboStackZOrder);
                }
            }

            // Running top-right stack Y: cards advance it by their OWN height so mixed-height cards
            // (the compact string toast vs. the taller enemy-item card) never overlap. Starts at the
            // top margin; non-stacking cards (minimap-anchored / top-center) leave it untouched.
            double toastY = 12.0 * scale;
            foreach (var item in _coordinator.GetRenderList())
            {
                // M02 pending-change #1: per-HUD-type enable toggle, extended from the original
                // 3 M19 §3 items to every HUD type (see ConfigSchema.OverlayItemsConfig).
                if (!IsEnabled(ElementKey(item.Payload.Type))) continue;
                // (§40) ComboResult is drawn by BuildComboStack above (top-center), not here.
                if (item.Payload.Type == HudType.ComboResult) continue;

                _cardBuffer.Clear();
                BuildCard(_cardBuffer, item.Payload, item.ExpiryMs, w, h, scale, ref toastY);
                for (int i = 0; i < _cardBuffer.Count; i++)
                    _queue.Enqueue(_cardBuffer[i], item.ZOrder);
            }

            // (user request) 적 복귀 예측(귀환+라인 복귀) timers were MOVED OUT of this minimap-left stack
            // and INTO the always-on enemy portrait row (BuildPortraitRow): a dead enemy's countdown there
            // now shows the return estimate (~M:SS) when LaneReturnPredictor is tracking that enemy, and
            // falls back to the raw respawn timer otherwise. The separate draggable "laneReturn" stack is
            // intentionally gone.

            // (loop 142) Inhibitor respawn countdowns — the LIVE time drawn ON the minimap at each
            // destroyed inhibitor's location (LoL-native jungle/inhibitor-timer style). Anchored to the
            // minimap (not draggable). Gated on the inhibitorTimers toggle.
            var inhibs = _inhibitorQuery?.Invoke();
            if (inhibs is { Count: > 0 } && IsEnabled("inhibitorTimers"))
            {
                var mm = ResolveMinimapRect(w, h, out _);
                if (mm.Width > 0)
                {
                    double f = 11 * scale;
                    _cardBuffer.Clear();
                    foreach (var ib in inhibs)
                    {
                        if (Overlay.Core.Overlay.StructureMinimapLayout.Inhibitor(ib.InhibitorId) is not { } pos) continue;
                        double px = mm.X + pos.nx * mm.Width;
                        double py = mm.Y + pos.ny * mm.Height;
                        string label = FormatTimerSeconds(ib.RemainingSeconds);
                        AddMinimapTimerLabel(px, py, label, AccentGold, f); // LoL gold, no panel
                    }
                    for (int i = 0; i < _cardBuffer.Count; i++)
                        _queue.Enqueue(_cardBuffer[i], StatusCardZOrder + 2); // over the minimap-anchored layer
                }
            }

            // (patch 15.1) Nexus ("twin") turret respawn countdowns — same minimap-anchored LIVE style as the
            // inhibitor timers above, drawn at each destroyed Nexus turret's location. 3:00 respawn. Gated on
            // the nexusTurretTimers toggle. Amber-ish tint to distinguish from the purple inhibitor chips.
            var nexusTurrets = _nexusTurretQuery?.Invoke();
            // "구조물 타이머" is ONE toggle (inhibitorTimers key) covering inhibitors AND Nexus turrets.
            if (nexusTurrets is { Count: > 0 } && IsEnabled("inhibitorTimers"))
            {
                var mm = ResolveMinimapRect(w, h, out _);
                if (mm.Width > 0)
                {
                    double f = 11 * scale;
                    _cardBuffer.Clear();
                    foreach (var nt in nexusTurrets)
                    {
                        if (Overlay.Core.Overlay.StructureMinimapLayout.NexusTurret(nt.TurretId) is not { } pos) continue;
                        double px = mm.X + pos.nx * mm.Width;
                        double py = mm.Y + pos.ny * mm.Height;
                        string label = FormatTimerSeconds(nt.RemainingSeconds);
                        AddMinimapTimerLabel(px, py, label, 0xFFF0C674, f); // amber, no panel
                    }
                    for (int i = 0; i < _cardBuffer.Count; i++)
                        _queue.Enqueue(_cardBuffer[i], StatusCardZOrder + 2);
                }
            }

            // (M31 §C) Last-seen enemy "afterimage": the champion's portrait left at the spot they
            // dropped out of vision, at half opacity so it reads as a memory rather than a live
            // sighting. Removed when that champion is seen again (the tracker's rule), not on a
            // timer — a stale marker is worse than none once you know where they actually are.
            // Same minimap rect as the capture/detect path, so the marker lands where the enemy
            // was actually detected.
            var afterimages = _afterimageQuery?.Invoke();
            if (afterimages is { Count: > 0 } && AfterimageEnabled())
            {
                var mm = ResolveMinimapRect(w, h, out _);
                if (mm.Width > 0)
                {
                    // Proportional to the minimap, like the detector's icon-radius estimate —
                    // never a hardcoded pixel size, since the minimap scales with HUD settings.
                    double size = Math.Max(10.0, mm.Width * 0.085);
                    double opacity = AfterimageOpacity();
                    _cardBuffer.Clear();
                    foreach (var a in afterimages)
                    {
                        if (!AfterimageRoleEnabled(a.RoleKey)) continue;
                        string? pref = _championIcons.GetPortraitReference(a.ChampionId);
                        if (pref is null) continue;   // no portrait cached — skip rather than draw a blank chip

                        double px = mm.X + a.X01 * mm.Width - size / 2;
                        double py = mm.Y + a.Y01 * mm.Height - size / 2;
                        // flags: bit1 circular clip only. Grayscale (bit0) was dropped 2026-07-20 —
                        // stacked with the opacity it made the marker too faint to read in a live
                        // game, and desaturating also removed the champion's own colors, which are
                        // the fastest way to tell WHICH enemy the marker is. Opacity alone carries
                        // the "this is a memory, not a live sighting" meaning.
                        _cardBuffer.Add(Icon(pref, px, py, size, size, 2, opacity));
                    }
                    for (int i = 0; i < _cardBuffer.Count; i++)
                        _queue.Enqueue(_cardBuffer[i], StatusCardZOrder + 2);
                }
            }

            // One-time minimap calibration box — ONLY when the user turned on calibrate mode in settings
            // (overlay.minimapCalibrate, default OFF) AND is in movable mode. So in-game there is no border
            // box / hint by default; the timers just place against the auto (or previously-saved) rect.
            if (_isMovable() && CalibrateMode())
            {
                var cbox = ResolveMinimapRect(w, h, out _);
                double bx = cbox.X, by = cbox.Y, bs = cbox.Width;
                if (_draggingKey == "minimapCal")
                {
                    bx = _minimapCalBaseX + _dragLiveDx;
                    by = _minimapCalBaseY + _dragLiveDy;
                    bs = _minimapCalBaseSize;
                }
                if (bs > 0)
                {
                    double tk = 2 * scale;
                    const uint cyan = 0xFF00E5FF;
                    _cardBuffer.Clear();
                    _cardBuffer.Add(Rect(bx, by, bs, tk, cyan));
                    _cardBuffer.Add(Rect(bx, by + bs - tk, bs, tk, cyan));
                    _cardBuffer.Add(Rect(bx, by, tk, bs, cyan));
                    _cardBuffer.Add(Rect(bx + bs - tk, by, tk, bs, cyan));
                    _cardBuffer.Add(Text("미니맵 영역 · 드래그=이동, 휠=크기", bx + 4 * scale, by + 4 * scale, cyan, 11 * scale));
                    _elementRects["minimapCal"] = new Rect(bx, by, bs, bs);
                    for (int i = 0; i < _cardBuffer.Count; i++)
                        _queue.Enqueue(_cardBuffer[i], StatusCardZOrder + 3);
                }
            }
        }
        _surface.Submit(_queue.EndFrame());

        // (loop 118) After the frame is laid out, decide whether the overlay should be interactive
        // (non-click-through) so the ⇄ button / picker are clickable WITHOUT holding a key — see
        // UpdateInteractionHover. Done post-layout so _elementRects["comboResult"] is this frame's rect.
        UpdateInteractionHover();
    }

    /// <summary>(loop 118) Sets <see cref="_wantsInteraction"/>: true when the picker is open, or the
    /// OS cursor is currently over the combo card. <see cref="MainWindow.UpdateTargetClickThrough"/>
    /// polls it to clear click-through over just that region, so the target-change button and enemy
    /// portraits accept a plain click (no modifier). Uses GetCursorPos because a click-through window
    /// receives no WPF mouse events to hover-test with. Never throws.</summary>
    private void UpdateInteractionHover()
    {
        // Picker open, or the target-click modifier is held (a keyboard fallback in case the cursor
        // hover-test below is off, e.g. under unusual DPI), always want interaction.
        bool want = _pickerOpen || _isTargetClickModifierHeld();
        // (§40) Also want interaction when hovering the enemy portrait ROW, so a plain click selects a
        // target without holding a key (same hover-to-interact as the combo card).
        if (!want && GetCursorPos(out var np))
        {
            try
            {
                var local = _surface.PointFromScreen(new System.Windows.Point(np.X, np.Y));
                if (_elementRects.TryGetValue("comboResult", out var card) && card.Contains(local)) want = true;
                else if (_elementRects.TryGetValue("enemyPortraitRow", out var row) && row.Contains(local)) want = true;
            }
            catch (System.InvalidOperationException) { /* surface not connected to a PresentationSource yet */ }
        }
        _wantsInteraction = want;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    // ── HUD → visual card mapping ───────────────────────────────────────────────────
    //
    // Each HUD payload becomes a SMALL set of layered DrawCommands forming a readable
    // "card": a drop-shadow Rect + a semi-opaque dark background Rect + an accent stripe
    // + one or more Text lines. Within one card the commands share the payload's zOrder;
    // the RenderQueue keeps enqueue order stable for equal zOrder, so shadow → background
    // → accent → text layer correctly under the painter's-algorithm renderer.
    //
    // Colours are packed 0xAARRGGBB. The dark card behind the text is what keeps the HUD
    // readable over a bright game background.

    // Colours UNCHANGED (original LoL gold/slate identity — user: keep colours, change only form).
    // Only the three chip/panel background tones below are new, and they are neutral shades of the
    // existing slate/gold scheme (no new accent hue) to support the OP.GG-style FORM (chips, panels).
    private const uint CardBackground = 0xE6181B22; // semi-opaque dark slate
    private const uint CardShadow = 0x55000000;      // subtle drop shadow
    private const uint PanelBg = 0xE60F1116;         // slightly darker slate: inset kill-readout panel
    private const uint TextPrimary = 0xFFFFFFFF;     // white
    private const uint TextDim = 0xFFB8C0CC;         // muted grey-blue
    private const uint AccentGold = 0xFFC8AA6E;      // LoL gold
    private const uint KeyChipBg = 0xFF2A2418;       // ability-key chip background (dark gold-tint)
    private const uint NeutralChipBg = 0xFF232730;   // auto-attack / neutral chip background (slate)
    private const uint LethalGreen = 0xFF4ADE80;     // "처치 가능"
    private const uint LethalGreenHollow = 0x804ADE80; // (M28 §3.3) translucent "hollow" green — a kill promised only UNDER assumptions
    private const uint WarnAmber = 0xFFF2C14E;       // "킬각까지"
    private const uint DangerRed = 0xFFE05A3B;       // "마나 부족"

    /// <summary>Route a payload to its card builder. A <see cref="HudType.ComboResult"/>
    /// whose content is a real <see cref="ComboHudResult"/> renders the rich combo card;
    /// the jungle-reference and inhibitor-timer types (M02 pending-change #2) render anchored
    /// to the minimap instead of the fixed top-right toast stack; everything else renders as a
    /// stacked top-right toast card.</summary>
    private void BuildCard(List<DrawCommand> into, HUDPayload payload, long expiryMs, double w, double h,
                           double scale, ref double toastY)
    {
        // (§40) HudType.ComboResult is drawn by BuildComboStack (top-center), never through this
        // generic path — RenderFrame skips it in the render loop.

        // Enemy legendary-item alert: structured content → the icon-forward card (enemy champion
        // portrait + the item's icon), joining the top-right stack.
        if (payload.Type == HudType.EnemyItemAlert && payload.Content is EnemyItemAlert enemyItem)
        {
            BuildEnemyItemCard(into, enemyItem, expiryMs, w, scale, ref toastY);
            return;
        }

        // Enemy appear/disappear presence: structured content → the champion-portrait toast (replaces
        // the old plain-string notification), joining the top-right stack.
        if (payload.Type == HudType.EnemyPresence && payload.Content is EnemyPresenceHud presence)
        {
            BuildEnemyPresenceCard(into, presence, expiryMs, w, scale, ref toastY);
            return;
        }

        string message = payload.Content?.ToString() ?? string.Empty;

        // M02 pending-change #2 (minimap-anchored timers): the inhibitor timer renders over the
        // minimap instead of joining the top-right toast stack, so it must NOT consume a
        // toastIndex slot (that would leave a gap in the other toasts' stacking order).
        if (payload.Type is HudType.InhibitorTimer)
        {
            BuildMinimapAnchoredCard(into, payload.Type, message, w, h, scale);
            return;
        }

        // M30: the real "적 정글 발견" alert is its own top-center card (anchored above the
        // combo/skill HUD, per user request) with a fade-out tail — not part of the generic
        // top-right toast stack, so it must not consume a toastIndex slot either.
        if (payload.Type is HudType.EnemyJunglerSpottedAlert)
        {
            BuildEnemyJunglerSpottedCard(into, message, expiryMs, w, h, scale);
            return;
        }

        BuildToastCard(into, payload.Type, message, w, scale, ref toastY);
    }

    /// <summary>M30 real "적 정글 발견" alert: a small card centered top-of-screen, anchored
    /// directly ABOVE where the combo/skill HUD (<see cref="BuildComboCard"/>'s <c>y = h * 0.14</c>
    /// anchor, "유저 스킬 인터페이스") starts — so it never overlaps that card even when both are
    /// visible at once. Shown for its full 3s (<c>OverlayCoordinator.EnemyJunglerSpottedAlertDurationMs</c>)
    /// at full opacity, then linearly fades to transparent over the last <see cref="FadeTailMs"/> of
    /// its life, computed purely from <paramref name="expiryMs"/> vs wall-clock now (no separate
    /// animation timer/state — recomputed fresh every frame like every other card here).</summary>
    private void BuildEnemyJunglerSpottedCard(List<DrawCommand> into, string message, long expiryMs,
                                               double w, double h, double scale)
    {
        const long FadeTailMs = 600;

        double alpha = 1.0;
        if (expiryMs != long.MaxValue)
        {
            long remaining = expiryMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (remaining <= 0) return; // about to be purged this frame anyway
            if (remaining < FadeTailMs) alpha = remaining / (double)FadeTailMs;
        }

        double cardW = 260 * scale;
        double cardH = 42 * scale;
        double stripeW = 4 * scale;
        double fontF = 15 * scale;

        double x = Math.Max(0, (w - cardW) / 2);
        double y = h * 0.14 - cardH - 10 * scale; // directly above the combo/skill HUD anchor

        const string key = "enemyJunglerSpotted"; // same toggle as the detector (single setting)
        var (dx, dy) = GetOffset(key);
        x += dx;
        y += dy;
        _elementRects[key] = new Rect(x, y, cardW, cardH);

        uint accent = WithAlpha(0xFFE0473B, alpha); // urgent red
        into.Add(Rect(x + 3, y + 3, cardW, cardH, WithAlpha(CardShadow, alpha)));
        into.Add(Rect(x, y, cardW, cardH, WithAlpha(CardBackground, alpha)));
        into.Add(Rect(x, y, stripeW, cardH, accent));
        into.Add(Text(message, x + stripeW + 10 * scale, y + (cardH - fontF * 1.2) / 2,
                      WithAlpha(TextPrimary, alpha), fontF));
    }

    /// <summary>Scales an ARGB color's alpha byte by <paramref name="alpha"/> (0-1), keeping RGB
    /// unchanged. Used for the enemy-jungler-spotted alert's fade-out tail.</summary>
    private static uint WithAlpha(uint argb, double alpha)
    {
        byte a = (byte)Math.Clamp((argb >> 24) * alpha, 0, 255);
        return ((uint)a << 24) | (argb & 0x00FFFFFF);
    }

    /// <summary>M02 pending-change #2: renders the jungle-reference panel / inhibitor-timer
    /// list directly over the minimap's on-screen rect (<see cref="MinimapRectCalculator"/>)
    /// instead of the fixed top-right toast position. Same card chrome as
    /// <see cref="BuildToastCard"/>; only the anchor differs. Still applies the Feature #1
    /// per-element drag offset on top of the computed anchor — that offset doubles as the
    /// manual correction mechanism for MinimapRectCalculator's documented calibration
    /// uncertainty (drag the card once while movable if the auto-anchor is off for your
    /// resolution/HUD scale; the correction persists from then on).</summary>
    private void BuildMinimapAnchoredCard(List<DrawCommand> into, HudType type, string message,
                                          double w, double h, double scale)
    {
        var minimap = MinimapRectCalculator.Compute(w, h);

        double cardW = 230 * scale;
        double cardH = 40 * scale;
        double stripeW = 4 * scale;
        double fontF = 14 * scale;

        // Anchor the card's top-left to the minimap's top-left, so it draws over the minimap's
        // on-screen rect per the pending-change wording ("render directly on top of the
        // minimap's on-screen rectangle").
        double x = minimap.X;
        double y = minimap.Y;

        string key = ElementKey(type);
        var (dx, dy) = GetOffset(key);
        x += dx;
        y += dy;
        _elementRects[key] = new Rect(x, y, cardW, cardH);

        uint accent = AccentFor(type);

        into.Add(Rect(x + 3, y + 3, cardW, cardH, CardShadow));
        into.Add(Rect(x, y, cardW, cardH, CardBackground));
        into.Add(Rect(x, y, stripeW, cardH, accent));
        into.Add(Text(message, x + stripeW + 10 * scale, y + (cardH - fontF * 1.2) / 2,
                      TextPrimary, fontF));
    }

    // ── §40 top-center combo stack: portrait row + combo overlay + skill overlay ────
    // A centered vertical column (ComboOverlayMockup.dc.html): always-on enemy portrait row, then the
    // combo-damage overlay (Alt+1), then the per-skill overlay (Alt+2). Each element is individually
    // drag-positionable + toggle-able; hidden elements collapse the column via the running `y`.

    private void Enqueue(List<DrawCommand> buf, int z)
    {
        for (int i = 0; i < buf.Count; i++) _queue.Enqueue(buf[i], z);
    }

    private string? ReadManualTarget() => _config.Get("targeting.manualTarget") as string;

    /// <summary>(user request) The latest always-on per-skill damage panel, recomputed at most ~4x/s (each
    /// call runs 6 engine evaluations — too heavy for every 60fps frame) and cached between. Null when the
    /// query is unwired or there is no game/target.</summary>
    private Overlay.Core.Combo.SkillPanelResult? SkillPanel()
    {
        if (_skillPanelQuery is null) return null;
        long now = _clock.NowMs;
        if (now >= _skillPanelNextMs)
        {
            _skillPanelNextMs = now + 250;
            var snap = _snapshot();
            _skillPanelCache = snap is not null ? _skillPanelQuery(snap) : null;
        }
        return _skillPanelCache;
    }

    private void BuildComboStack(double w, double h, double scale, ComboHudResult? hud)
    {
        double centerX = w / 2, gap = 10 * scale, y = h * 0.05;

        if (IsEnabled("enemyPortraitRow"))
        {
            var snap = _snapshot();
            var roster = snap is not null
                ? ComboRunner.EnemyRoster(snap)
                : (IReadOnlyList<EnemyRosterEntry>)System.Array.Empty<EnemyRosterEntry>();
            if (roster.Count > 0)
            {
                string? selected = hud?.TargetChampion;
                if (string.IsNullOrEmpty(selected)) selected = ReadManualTarget();
                _cardBuffer.Clear();
                BuildPortraitRow(_cardBuffer, roster, selected, centerX, ref y, scale);
                Enqueue(_cardBuffer, ComboStackZOrder);
                y += gap;
            }
        }

        // The combo-damage card still needs a TRIGGERED combo (the ComboHudResult); the per-skill card is
        // ALWAYS-ON (its own standalone panel), so it must NOT be gated on `hud`.
        if (hud is not null && IsEnabled("comboResult"))
        {
            _cardBuffer.Clear();
            BuildComboCard(_cardBuffer, hud, centerX, ref y, scale);
            Enqueue(_cardBuffer, ComboStackZOrder);
            y += gap;
        }

        // (user request) Skill-damage overlay: every ability + AA + passive's standalone damage vs the
        // current target, regardless of which combo (if any) is running. Shown whenever the skill overlay
        // is enabled and a target resolves.
        if (IsEnabled("skillOverlay") && SkillPanel() is { } panel)
        {
            _cardBuffer.Clear();
            BuildSkillCard(_cardBuffer, panel, centerX, ref y, scale);
            Enqueue(_cardBuffer, ComboStackZOrder);
            y += gap;
        }
    }

    /// <summary>① Always-on enemy portrait row. Circular champion portraits (dead → grayscale + a
    /// respawn countdown ring); click one to pin it as the combo target (<see cref="_rosterRects"/>).
    /// The whole row is one draggable element ("enemyPortraitRow").</summary>
    /// <summary>(2026-07-26) 적 제어 와드 패널 — vertical card on the left edge: title row, then
    /// one row per enemy (square portrait + a dark count slot), mirroring the reference layout
    /// the user supplied. Counts come from <see cref="Overlay.Core.Items.ControlWardCounter"/>
    /// (carried wards only — placed wards are not public data). Draggable as "enemyWards".</summary>
    private void BuildEnemyWardPanel(List<DrawCommand> into, GameSnapshot snap, double w, double h, double scale)
    {
        var counts = Overlay.Core.Items.ControlWardCounter.CountEnemies(snap);
        if (counts.Count == 0) return;
        var roster = ComboRunner.EnemyRoster(snap);
        if (roster.Count == 0) return;
        // NOTE (2026-07-26, twice-verified): the dedicated ward slot (V) exists for everyone
        // this season and its content is NOT in the API's item list — the counts below are the
        // CARRIED wards in the normal inventory only, documented in the Help manual.
        double icon = 30 * scale, slotW = 34 * scale, gap = 6 * scale;
        double padH = 10 * scale, padV = 8 * scale;
        double titleF = 12 * scale, titleH = titleF * 1.6;
        double rowH = icon + 6 * scale;
        double panelW = padH * 2 + icon + gap + slotW;
        double panelH = padV * 2 + titleH + roster.Count * rowH;

        var (dx, dy) = GetOffset("enemyWards");
        double x0 = 12 * scale + dx, y0 = h * 0.30 + dy;
        _elementRects["enemyWards"] = new Rect(x0, y0, panelW, panelH);

        into.Add(Rect(x0 + 3, y0 + 3, panelW, panelH, CardShadow));
        into.Add(Rect(x0, y0, panelW, panelH, PanelBorder));
        into.Add(Rect(x0 + 1, y0 + 1, panelW - 2, panelH - 2, RowBg));

        into.Add(Text(Localization.L("ward.title"), x0 + padH, y0 + padV, TextPrimary, titleF));

        double y = y0 + padV + titleH;
        foreach (var e in roster)
        {
            string? pref = _championIcons.GetPortraitReference(e.ChampionName);
            if (pref is not null)
                into.Add(Icon(pref, x0 + padH, y, icon, icon, e.IsDead ? 1 : 0)); // square, gray when dead
            else
                into.Add(Rect(x0 + padH, y, icon, icon, NeutralChipBg));

            // Count slot: dark box like the reference; the number brightens when wards are held.
            double sx = x0 + padH + icon + gap;
            into.Add(Rect(sx, y, slotW, icon, 0xCC14181F));
            counts.TryGetValue(e.ChampionName, out int c);
            uint color = c > 0 ? TextPrimary : NameDim;
            double f = 15 * scale;
            into.Add(TextMono(c.ToString(), sx + slotW / 2 - f * 0.30, y + icon / 2 - f * 0.62, color, f));


            y += rowH;
        }
    }

    private void BuildPortraitRow(List<DrawCommand> into, IReadOnlyList<EnemyRosterEntry> roster,
                                  string? selected, double centerX, ref double y, double scale)
    {
        // (user request) Dead-enemy countdown = 적 복귀 예측(귀환+라인 복귀), migrated here from the old
        // minimap-left stack. LaneReturnPredictor only tracks enemies relevant to the active player's
        // lane; enemies it isn't tracking fall back to the raw respawn timer below.
        var laneReturns = _laneReturnQuery?.Invoke();

        double portrait = 56 * scale, cellW = 64 * scale, cellGap = 8 * scale;
        double padH = 10 * scale, padV = 8 * scale;
        double nameGap = 5 * scale, nameF = 10 * scale, nameH = nameF * 1.4;
        double inset = 2 * scale, innerP = portrait - 2 * inset;

        int n = roster.Count;
        double rowW = n * cellW + (n - 1) * cellGap + 2 * padH;
        double rowH = 2 * padV + portrait + nameGap + nameH;

        var (dx, dy) = GetOffset("enemyPortraitRow");
        double x0 = centerX - rowW / 2 + dx, y0 = y + dy;
        _elementRects["enemyPortraitRow"] = new Rect(x0, y0, rowW, rowH);

        into.Add(Rect(x0 + 3, y0 + 3, rowW, rowH, CardShadow));
        into.Add(Rect(x0, y0, rowW, rowH, PanelBorder));
        into.Add(Rect(x0 + 1, y0 + 1, rowW - 2, rowH - 2, RowBg));

        double cx = x0 + padH, cyTop = y0 + padV;
        foreach (var e in roster)
        {
            double pcx = cx + (cellW - portrait) / 2, pcy = cyTop;
            bool isSel = selected is not null && string.Equals(e.ChampionName, selected, StringComparison.OrdinalIgnoreCase);
            // (user request) The enemy counts as "returning" while DEAD **or** while a lane-return prediction
            // is still pending (the travel time AFTER respawn) — so the countdown no longer ends at respawn.
            double? ret = LookupReturnSeconds(laneReturns, e.ChampionName);
            bool returning = e.IsDead || ret is not null;
            uint ring = isSel ? RingSelected : (returning ? RingDead : RingNormal);

            if (isSel)
                into.Add(Ellipse(pcx - 3 * scale, pcy - 3 * scale, portrait + 6 * scale, portrait + 6 * scale, 0x59C8AA6E));

            into.Add(Ellipse(pcx, pcy, portrait, portrait, ring));
            string? pref = _championIcons.GetPortraitReference(e.ChampionName);
            if (pref is not null)
                into.Add(Icon(pref, pcx + inset, pcy + inset, innerP, innerP, 2 | (returning ? 1 : 0)));
            else
            {
                into.Add(Ellipse(pcx + inset, pcy + inset, innerP, innerP, NeutralChipBg));
                string ini = e.ChampionName.Length > 0 ? e.ChampionName.Substring(0, 1).ToUpperInvariant() : "?";
                into.Add(Text(ini, pcx + portrait * 0.36, pcy + portrait * 0.22, TextPrimary, 20 * scale));
            }

            if (returning)
            {
                // (user request) NO opaque gray/dark disk over the portrait — keep the grayscale icon a
                // VISIBLE silhouette; only overlay the countdown (with a 1px shadow for legibility). Shows
                // the 적 복귀 예측(~M:SS) while it's tracked, else the raw respawn timer (초) as a fallback.
                // Number format (seconds vs M:SS) follows the user's timer toggle; "~" marks the return
                // ESTIMATE, the exact respawn fallback has no prefix.
                string rs = ret is { } rsec ? "~" + FormatTimerSeconds(rsec) : FormatTimerSeconds(e.RespawnTimer);
                double rf = rs.Length >= 4 ? 13 * scale : 20 * scale; // the wider M:SS string needs a smaller face
                double tx = pcx + portrait * 0.5 - rs.Length * rf * 0.275;
                double ty = pcy + portrait * 0.5 - rf * 0.65;
                into.Add(TextMono(rs, tx + 1 * scale, ty + 1 * scale, 0xC0000000, rf)); // shadow
                into.Add(TextMono(rs, tx, ty, RespawnText, rf));
            }

            string name = Localization.ChampionName(e.ChampionName);
            double nameW = name.Length * nameF * 0.62;
            into.Add(Text(name, cx + (cellW - nameW) / 2, cyTop + portrait + nameGap, isSel ? NameSelected : NameDim, nameF));

            _rosterRects.Add((new Rect(pcx, pcy, portrait, portrait), e.ChampionName));
            cx += cellW + cellGap;
        }

        y += rowH;
    }

    /// <summary>The pending 적 복귀 예측(귀환+라인 복귀) remaining-seconds for <paramref name="champion"/>
    /// from <see cref="_laneReturnQuery"/>, or null when that enemy isn't being tracked (→ the caller
    /// falls back to the raw respawn timer). Case-insensitive match on champion name.</summary>
    private static double? LookupReturnSeconds(
        System.Collections.Generic.IReadOnlyList<Overlay.Core.LaneReturnStatus>? laneReturns, string champion)
    {
        if (laneReturns is null || string.IsNullOrEmpty(champion)) return null;
        for (int i = 0; i < laneReturns.Count; i++)
            if (string.Equals(laneReturns[i].ChampionName, champion, StringComparison.OrdinalIgnoreCase))
                return laneReturns[i].RemainingSeconds;
        return null;
    }

    /// <summary>Shared header for the combo/skill overlays: armor + MR readout, target name, mode tag.</summary>
    private void BuildOverlayHeader(List<DrawCommand> into, double targetArmor, double targetMr, string targetChampion,
                                    double x, double cy,
                                    double cardW, double padX, double scale, string tag, uint tagBg, double tagRightX)
    {
        double hx = x + padX, f = 12 * scale, icn = 14 * scale, iconGap = 3 * scale;
        string armor = targetArmor.ToString("0", CultureInfo.InvariantCulture);
        string mr = targetMr.ToString("0", CultureInfo.InvariantCulture);
        // §40: real stat icons (extracted transparent PNGs) instead of "방"/"마" text labels — the
        // armor shield + magic-resist ring, each followed by the resolved value in its own colour. A
        // missing icon file simply draws nothing (Icon → null), degrading to number-only.
        into.Add(Icon(StatIconPath("armor"), hx, cy, icn, icn));
        hx += icn + iconGap;
        into.Add(TextMono(armor, hx, cy + 1 * scale, ArmorText, f));
        hx += armor.Length * f * 0.62 + 9 * scale;
        into.Add(Icon(StatIconPath("magic_resist"), hx, cy, icn, icn));
        hx += icn + iconGap;
        into.Add(TextMono(mr, hx, cy + 1 * scale, MrText, f));
        hx += mr.Length * f * 0.62 + 10 * scale;
        string name = string.IsNullOrEmpty(targetChampion) ? "대상 없음" : Localization.ChampionName(targetChampion);
        into.Add(Text(name, hx, cy, NameSelected, 14 * scale));

        double tagW = tag.Length * 12 * scale + 12 * scale, tagH = 16 * scale, tagX = tagRightX - tagW;
        into.Add(Rect(tagX, cy, tagW, tagH, tagBg));
        into.Add(Text(tag, tagX + 6 * scale, cy + 1 * scale, TagTextDark, 10 * scale));
    }

    /// <summary>Big top-right circular target portrait shared by the combo/skill overlays.</summary>
    private void BuildBigPortrait(List<DrawCommand> into, ComboHudResult hud, double bigPX, double cy, double bigP, double scale)
    {
        into.Add(Ellipse(bigPX, cy, bigP, bigP, RingSelected));
        double bi = 2 * scale, ip = bigP - 2 * bi;
        string? pref = string.IsNullOrEmpty(hud.TargetChampion) ? null : _championIcons.GetPortraitReference(hud.TargetChampion);
        if (pref is not null)
            into.Add(Icon(pref, bigPX + bi, cy + bi, ip, ip, 2));
        else
        {
            into.Add(Ellipse(bigPX + bi, cy + bi, ip, ip, NeutralChipBg));
            if (!string.IsNullOrEmpty(hud.TargetChampion))
                into.Add(Text(hud.TargetChampion.Substring(0, 1).ToUpperInvariant(), bigPX + bigP * 0.36, cy + bigP * 0.22, TextPrimary, 22 * scale));
        }
    }

    /// <summary>④ Combo-damage overlay: armor/MR header, a max-HP kill-threshold bar (solid red = HP
    /// surviving the MAX combo, translucent spread = min→max, yellow line = min-damage cut, ticks per
    /// 100/1000 HP), MAX/MIN damage boxes (RangeMax/RangeMin) — collapsed to a single 확정 box when the
    /// two ends print identically (§76) — the combo sequence chips carrying M28 §3's knob glyphs, and
    /// the big target portrait.
    ///
    /// <para>(§77 regression) The knob glyphs (gold frame + ▲, fill-bar underline, ×n badge, the
    /// trailing 가정 N chip) were implemented in loop 158 and silently lost in commit 8a9e543 when this
    /// card was rewritten and the chips switched from key letters to ability icons — <c>Sequence</c> and
    /// <c>AssumptionCount</c> kept being published the whole time, nothing consumed them. Restored here
    /// in the icon card's idiom (glyphs draw OVER the icon rather than beside a letter).</para>
    private void BuildComboCard(List<DrawCommand> into, ComboHudResult hud, double centerX, ref double y, double scale)
    {
        var cr = hud.Result;
        double cardW = 412 * scale, padX = 14 * scale, padY = 13 * scale;
        double headerH = 20 * scale, hpBarH = 22 * scale, boxH = 48 * scale, chipSize = 30 * scale;
        double m1 = 10 * scale, m2 = 12 * scale, m3 = 13 * scale;
        // (M20 §6.2) Optional kill-angle line between the damage boxes and the chip row: the
        // kill-feasible ceiling + its placement, and the suffix threshold. Only reserves height
        // when there is actually something to say, so every other combo's card is unchanged.
        string? killAngleLine = BuildKillAngleLine(cr);
        double killF = 11 * scale, killH = killAngleLine is null ? 0 : killF * 1.35 + 7 * scale;
        double cardH = padY + headerH + m1 + hpBarH + m2 + boxH + killH + m3 + chipSize + padY;

        var (dx, dy) = GetOffset("comboResult");
        double x = centerX - cardW / 2 + dx, yy = y + dy;
        _elementRects["comboResult"] = new Rect(x, yy, cardW, cardH);

        into.Add(Rect(x + 3, yy + 4, cardW, cardH, CardShadow));
        into.Add(Rect(x, yy, cardW, cardH, PanelBorder));
        into.Add(Rect(x + 1, yy + 1, cardW - 2, cardH - 2, OverlayPanelBg));

        double innerX = x + padX, innerR = x + cardW - padX, cy = yy + padY;
        double bigP = 52 * scale, bigPX = innerR - bigP;

        BuildBigPortrait(into, hud, bigPX, cy, bigP, scale);
        BuildOverlayHeader(into, hud.TargetArmor, hud.TargetMr, hud.TargetChampion, x, cy, cardW, padX, scale, "콤보", ComboTagBg, bigPX - 8 * scale);
        cy += headerH + m1;

        double barW = cardW - 2 * padX, maxHp = hud.TargetMaxHp;
        // (issue: HP bar covers the portrait) The health bar previously spanned the FULL inner width
        // (barW), so its right end ran under the big target portrait at top-right. Stop it at the "콤보"
        // tag's right edge (bigPX - 8*scale, the same limit BuildOverlayHeader right-aligns the tag to)
        // so it clears the portrait. The MAX/MIN damage boxes below still use the full barW.
        double hpBarW = (bigPX - 8 * scale) - innerX;
        if (hpBarW < barW * 0.4) hpBarW = barW * 0.4; // safety floor for very small scales
        double rMax = cr.RangeMax > 0 ? cr.RangeMax : cr.TotalDamage;
        double rMin = cr.RangeMin > 0 ? cr.RangeMin : rMax;
        into.Add(Rect(innerX, cy, hpBarW, hpBarH, HpBarBg));
        if (maxHp > 0)
        {
            double remMax = System.Math.Clamp((maxHp - rMax) / maxHp, 0, 1);
            double remMin = System.Math.Clamp((maxHp - rMin) / maxHp, 0, 1);
            into.Add(Rect(innerX, cy, hpBarW * remMax, hpBarH, HpSurviveRed));
            if (remMin > remMax)
                into.Add(Rect(innerX + hpBarW * remMax, cy, hpBarW * (remMin - remMax), hpBarH, HpSpreadRed));
            for (int hp = 100; hp < maxHp; hp += 100)
            {
                double tx = innerX + (hp / maxHp) * hpBarW;
                if (hp % 1000 == 0) into.Add(Rect(tx - 1 * scale, cy, 2 * scale, hpBarH, 0xF2000000));
                else into.Add(Rect(tx, cy + hpBarH * 0.5, 1 * scale, hpBarH * 0.5, 0xB3000000));
            }
            double minX = innerX + hpBarW * remMin;
            into.Add(Rect(minX - 1 * scale, cy - 2 * scale, 2 * scale, hpBarH + 4 * scale, HpMinLine));
        }
        cy += hpBarH + m2;

        double boxGap = 9 * scale, boxW = (barW - boxGap) / 2, numF = 30 * scale;
        string maxText = rMax.ToString("0", CultureInfo.InvariantCulture);
        string minText = rMin.ToString("0", CultureInfo.InvariantCulture);
        double capF = 9 * scale, capY = cy + boxH / 2 + 5 * scale;

        // (§76) A combo with no real variance is ONE number, not a range. The judge already exists in
        // the model — DamageRange.Width is documented as "0 for a certain value" — and only the UI was
        // ignoring it, drawing "50" beside "50" and inviting the reader to see a spread. The test is the
        // FORMATTED strings, not the doubles: both ends are rounded and printed with "0", so a sub-0.5
        // difference prints identically and would otherwise be an invisible range.
        if (string.Equals(maxText, minText, StringComparison.Ordinal))
        {
            into.Add(Rect(innerX, cy, barW, boxH, MaxBoxBorder));
            into.Add(Rect(innerX + 1, cy + 1, barW - 2, boxH - 2, MaxBoxBg));
            into.Add(TextMono(maxText, innerX + 13 * scale, cy + (boxH - numF) / 2, MaxDmgNum, numF));
            into.Add(Text("확정", innerX + barW - 34 * scale, cy + boxH / 2 - 7 * scale, MaxDmgLabel, 11 * scale));
        }
        else
        {
            into.Add(Rect(innerX, cy, boxW, boxH, MaxBoxBorder));
            into.Add(Rect(innerX + 1, cy + 1, boxW - 2, boxH - 2, MaxBoxBg));
            into.Add(TextMono(maxText, innerX + 13 * scale, cy + (boxH - numF) / 2, MaxDmgNum, numF));
            into.Add(Text("최대", innerX + boxW - 30 * scale, cy + boxH / 2 - 7 * scale, MaxDmgLabel, 11 * scale));
            double minBoxX = innerX + boxW + boxGap;
            into.Add(Rect(minBoxX, cy, boxW, boxH, MinBoxBorder));
            into.Add(Rect(minBoxX + 1, cy + 1, boxW - 2, boxH - 2, MinBoxBg));
            into.Add(TextMono(minText, minBoxX + 13 * scale, cy + (boxH - numF) / 2, MinDmgNum, numF));
            into.Add(Text("최소", minBoxX + boxW - 30 * scale, cy + boxH / 2 - 7 * scale, MinDmgNum, 11 * scale));

            // (§76) P4: an end that moved because of an ASSUMPTION says so, in the same gold the
            // "가정 N" chip uses. Without this the user sees a spread on a fixed-damage ability and has
            // no way to learn that a setting — not the game — produced it.
            if (hud.CeilingFromAmplifierRunes)
                into.Add(Text("증폭룬 최선", innerX + boxW - 62 * scale, capY, AccentGold, capF));
            if (hud.FloorFromAssumedEnemyRunes)
                into.Add(Text("적 방어룬 가정", minBoxX + boxW - 72 * scale, capY, AccentGold, capF));
        }
        cy += boxH;

        if (killAngleLine is not null)
        {
            into.Add(Text(killAngleLine, innerX, cy + 7 * scale, NameDim, killF));
            cy += killH;
        }
        cy += m3;

        var seq = hud.Sequence;
        if (seq is null || seq.Count == 0)
        {
            var fb = new List<ComboSequenceToken>();
            foreach (var t in (hud.CommandLabel ?? "").Split('-', StringSplitOptions.RemoveEmptyEntries))
                fb.Add(new ComboSequenceToken(t, t is "Q" or "W" or "E" or "R" or "q" or "w" or "e" or "r"));
            seq = fb;
        }
        double chipX = innerX, chipGap = 6 * scale, chipInset = 1.5 * scale;
        foreach (var token in seq)
        {
            if (chipX + chipSize > innerR) break;
            bool auto = !token.IsAbility;
            uint col = auto ? SlotA : SlotColor(token.Label.ToUpperInvariant());

            // (M28 §3, restored — see BuildComboCard's doc comment) A binary condition the user
            // assumed MET draws a gold FRAME behind the chip: the chip background is painted on top
            // of a slightly larger gold rect, leaving a thin border — the "empowered/glowing" read.
            if (token.Knob == ComboKnobShape.MaxDamage)
            {
                double b = 1.5 * scale;
                into.Add(RectSharp(chipX - b, cy - b, chipSize + 2 * b, chipSize + 2 * b, AccentGold));
            }

            // (user request) Combo-sequence ability chips are SHARP squares again ("정사각형으로 복구").
            // The render layer rounds every Rect by default, which had softened these into rounded
            // squares. Basic-attack (auto) keeps the default rounded chip behind its sword icon
            // ("기본공격 제외").
            into.Add(auto ? Rect(chipX, cy, chipSize, chipSize, col)
                          : RectSharp(chipX, cy, chipSize, chipSize, col));

            // (issue: use skill icons, not P/Q/W/E/R letters) Prefer the real Data Dragon icon:
            // a summoner-spell token draws that spell's icon, an ability token draws the combo
            // champion's P/Q/W/E/R spell/passive icon. Both are non-blocking (a not-yet-cached icon
            // returns null and appears on a later frame), so we fall back to the sword (auto-attack)
            // or the key letter until the icon is ready — exactly the pre-template behaviour.
            string? iconPath = null;
            if (token.SummonerSpellId is { Length: > 0 } sid)
                iconPath = DDragonIconProvider.SummonerIconPathOrNull(sid);
            else if (token.IsAbility && !string.IsNullOrEmpty(hud.CasterChampion))
                iconPath = AbilityIconProvider.AbilityIconPathOrNull(hud.CasterChampion, token.Label);

            if (iconPath is not null)
                into.Add(Icon(iconPath, chipX + chipInset, cy + chipInset, chipSize - 2 * chipInset, chipSize - 2 * chipInset));
            else if (auto)
                into.Add(BasicAttackIcon(chipX + chipSize * 0.1, cy + chipSize * 0.1, chipSize * 0.8, chipSize * 0.8, 0xFFE6E8EC));
            else
                into.Add(Text(token.Label, chipX + chipSize * 0.32, cy + chipSize * 0.16, TextPrimary, 13 * scale));

            // (M28 §3, restored) The knob glyphs, drawn OVER the icon so they survive the switch from
            // letter chips to ability icons — that switch is exactly what dropped them (see §77).
            switch (token.Knob)
            {
                case ComboKnobShape.MaxDamage: // ▲ corner badge, top-right (pairs with the gold frame)
                    into.Add(Text("▲", chipX + chipSize - 8 * scale, cy - 4 * scale, AccentGold, 10 * scale));
                    break;
                case ComboKnobShape.Slider: // fill-bar underline at the knob's fraction (full = solid)
                    into.Add(Rect(chipX, cy + chipSize + 1 * scale,
                        chipSize * System.Math.Clamp(token.SliderFraction, 0, 1), 2 * scale, AccentGold));
                    break;
                case ComboKnobShape.Count: // ×n badge, bottom-right
                    into.Add(Text("×" + token.Count.ToString(CultureInfo.InvariantCulture),
                        chipX + chipSize - 12 * scale, cy + chipSize - 11 * scale, AccentGold, 10 * scale));
                    break;
            }
            chipX += chipSize + chipGap;
        }

        // (M28 §3.3, restored) The assumption-count chip after the sequence: the card confesses how
        // many per-node knobs the user pushed above their conservative floor. Gold with dark text, so
        // an assumed number can never be mistaken for an observed one (P4).
        if (hud.AssumptionCount > 0 && chipX + 44 * scale <= innerR)
        {
            double gW = 44 * scale, gF = 12 * scale;
            into.Add(Rect(chipX, cy, gW, chipSize, AccentGold));
            into.Add(Text("가정 " + hud.AssumptionCount.ToString(CultureInfo.InvariantCulture),
                chipX + 5 * scale, cy + (chipSize - gF * 1.2) / 2, 0xFF16181F, gF));
            chipX += gW + chipGap;
        }

        // (issue: combo name field) The saved combo's name, right-aligned at the card's bottom-right
        // (the chip row is the lowest content row). Clamped so it never overlaps the chips; skipped
        // when there is no name. Width is estimated from the string length (same approach the header
        // tag uses) since the render layer measures text itself.
        if (!string.IsNullOrEmpty(hud.ComboName))
        {
            double nameF = 12 * scale;
            double estW = hud.ComboName.Length * nameF * 0.95; // generous (Korean glyphs are wide)
            double nameX = innerR - estW;
            double minX = chipX + 6 * scale; // keep clear of the last chip
            if (nameX < minX) nameX = minX;
            double nameY = cy + (chipSize - nameF * 1.2) / 2;
            into.Add(Text(hud.ComboName, nameX, nameY, NameDim, nameF));
        }

        y += cardH;
    }

    /// <summary>
    /// (M20 §6.2 / §6.3, CLAUDE_CODE_TODO §75) The combo card's kill-angle line, or null when this
    /// combo has nothing HP-dependent to say. Two independent facts, joined only because they answer
    /// the same player question and a compact card has room for one row:
    /// <list type="bullet">
    /// <item>the kill-feasible CEILING — the total when the variance-heavy cast is placed at the
    /// latest point the target is still alive (<see cref="ComboResult.BurstCeilingDamage"/>), with
    /// the placement itself spelled out. The authored-order total keeps the headline boxes above;
    /// this never overwrites it.</item>
    /// <item>the pre-existing suffix threshold (<see cref="ComboResult.SuffixThresholdHP"/>) — "cast
    /// R at or below this HP and the tail finishes" — which was computed but never surfaced.</item>
    /// </list>
    /// §6.1's <c>OrderingDeltaKillThresholdHp</c> stays HUD-invisible by decision, not oversight: it
    /// restates the same ordering fact in kill-threshold HP, and the ceiling line already says it in
    /// the damage units this card is about (recorded in CLAUDE_CODE_TODO §75).
    /// Display only — P4 assistive, the player still casts.
    /// </summary>
    private static string? BuildKillAngleLine(ComboResult cr)
    {
        string? ceiling = cr.BurstCeiling switch
        {
            BurstCeilingStatus.Optimized =>
                $"킬각 상한 {cr.BurstCeilingDamage.ToString("0", CultureInfo.InvariantCulture)} ({cr.BurstCeilingSequence})",
            BurstCeilingStatus.AlreadyOptimal =>
                $"킬각 상한 {cr.BurstCeilingDamage.ToString("0", CultureInfo.InvariantCulture)} (현재 순서가 최적)",
            BurstCeilingStatus.VarianceUnnecessary =>
                $"{cr.BurstCeilingNodeLabel ?? "변동기"} 없이도 처치",
            BurstCeilingStatus.MultipleVarianceNodes => "변동 스킬 2개 이상 — 작성순 그대로",
            _ => null,
        };

        string? suffix = cr.SuffixThresholdHP is { } s && cr.FinisherNodeLabel is { } label
            ? $"{label} 전 체력 ≤ {s.ToString("0", CultureInfo.InvariantCulture)}"
            : null;

        if (ceiling is null) return suffix;
        return suffix is null ? ceiling : ceiling + "  ·  " + suffix;
    }

    /// <summary>Per-skill (P/Q/W/E/R/A) damage overlay: same header as the combo card, then six slot
    /// boxes with each ability's damage (<see cref="ComboRunner.SkillDamageBySlot"/>). A "0" slot is
    /// dimmed. Basic attack (A) shows the sword icon.</summary>
    private void BuildSkillCard(List<DrawCommand> into, Overlay.Core.Combo.SkillPanelResult panel, double centerX, ref double y, double scale)
    {
        var slots = panel.Slots;
        double cardW = 412 * scale, padX = 14 * scale, padY = 13 * scale;
        double headerH = 20 * scale, m = 13 * scale, boxIcon = 45 * scale, dmgF = 19 * scale;
        double boxAreaH = boxIcon + 7 * scale + dmgF * 1.2;
        double cardH = padY + headerH + m + boxAreaH + padY;

        var (dx, dy) = GetOffset("skillOverlay");
        double x = centerX - cardW / 2 + dx, yy = y + dy;
        _elementRects["skillOverlay"] = new Rect(x, yy, cardW, cardH);

        into.Add(Rect(x + 3, yy + 4, cardW, cardH, CardShadow));
        into.Add(Rect(x, yy, cardW, cardH, PanelBorder));
        into.Add(Rect(x + 1, yy + 1, cardW - 2, cardH - 2, OverlayPanelBg));

        double cy = yy + padY;
        BuildOverlayHeader(into, panel.TargetArmor, panel.TargetMr, panel.TargetChampion, x, cy, cardW, padX, scale, "스킬별", SkillTagBg, x + cardW - padX);
        cy += headerH + m;

        double innerX = x + padX, innerW = cardW - 2 * padX, boxGap = 10 * scale;
        double cellW = (innerW - 5 * boxGap) / 6, bx = innerX;
        double boxInset = 2 * scale;
        foreach (var s in slots)
        {
            double iconX = bx + (cellW - boxIcon) / 2;
            bool auto = s.Slot == "A";
            // (user request) Match the combo overlay: SQUARE (sharp) box + the REAL Data Dragon ability icon.
            // Basic attack keeps the rounded box + sword icon (same exception as the combo chips).
            into.Add(auto ? Rect(iconX, cy, boxIcon, boxIcon, SlotColor(s.Slot))
                          : RectSharp(iconX, cy, boxIcon, boxIcon, SlotColor(s.Slot)));

            string? iconPath = auto || string.IsNullOrEmpty(panel.CasterChampion)
                ? null
                : AbilityIconProvider.AbilityIconPathOrNull(panel.CasterChampion, s.Slot);
            if (iconPath is not null)
                into.Add(Icon(iconPath, iconX + boxInset, cy + boxInset, boxIcon - 2 * boxInset, boxIcon - 2 * boxInset));
            else if (auto)
                into.Add(BasicAttackIcon(iconX + boxIcon * 0.1, cy + boxIcon * 0.1, boxIcon * 0.8, boxIcon * 0.8, 0xFFE6E8EC));
            else
                into.Add(Text(s.Slot, iconX + boxIcon * 0.33, cy + boxIcon * 0.22, TextPrimary, 19 * scale));

            string d = s.Damage.ToString("0", CultureInfo.InvariantCulture);
            uint dc = s.Damage > 0.5 ? SkillDmgOn : SkillDmgOff;
            into.Add(TextMono(d, bx + cellW / 2 - d.Length * 5 * scale, cy + boxIcon + 7 * scale, dc, dmgF));
            bx += cellW + boxGap;
        }

        y += cardH;
    }

    /// <summary>Compact toast card stacked at the top-right (Item/Recall/Notification/Jungle):
    /// dark rounded background + a coloured left accent stripe + the message text. The
    /// <paramref name="index"/> offsets each card down so several show at once without
    /// overlapping. M02 pending-change #1: instance method (not static) so it can record its
    /// drawn rect for this type's per-element drag, and apply that type's own offset.</summary>
    private void BuildToastCard(List<DrawCommand> into, HudType type, string message,
                                double w, double scale, ref double toastY)
    {
        double cardW = 230 * scale;
        double cardH = 40 * scale;
        double margin = 12 * scale;
        double gap = 8 * scale;
        double stripeW = 4 * scale;
        double fontF = 14 * scale;

        double x = w - cardW - margin;
        double y = toastY; // running top-right stack position (advanced by this card's height below)

        string key = ElementKey(type);
        var (dx, dy) = GetOffset(key);
        x += dx;
        y += dy;
        _elementRects[key] = new Rect(x, y, cardW, cardH);

        uint accent = AccentFor(type);

        into.Add(Rect(x + 3, y + 3, cardW, cardH, CardShadow));
        into.Add(Rect(x, y, cardW, cardH, CardBackground));
        into.Add(Rect(x, y, stripeW, cardH, accent));
        into.Add(Text(message, x + stripeW + 10 * scale, y + (cardH - fontF * 1.2) / 2,
                      TextPrimary, fontF));

        toastY += cardH + gap;
    }

    /// <summary>Enemy legendary-item-completed card: a wider top-right toast with a severity stripe,
    /// the enemy's champion PORTRAIT + the completed ITEM'S icon side by side, a category label + item
    /// name, and a bottom lifetime gauge. Icons fetched non-blocking (a not-yet-cached one falls back
    /// to a neutral chip). Joins the running top-right stack via <paramref name="toastY"/>.</summary>
    private void BuildEnemyItemCard(List<DrawCommand> into, EnemyItemAlert alert, long expiryMs,
                                    double w, double scale, ref double toastY)
    {
        double cardW = 300 * scale, cardH = 58 * scale, margin = 12 * scale, gap = 8 * scale;
        double stripeW = 6 * scale, pad = 9 * scale, portrait = 40 * scale, itemIcon = 30 * scale;
        double catF = 12 * scale, mainF = 16 * scale, barH = 3 * scale;
        const uint sev = WarnAmber;

        double x = w - cardW - margin, y = toastY;
        const string key = "enemyItemAlert";
        var (dx, dy) = GetOffset(key);
        x += dx; y += dy;
        _elementRects[key] = new Rect(x, y, cardW, cardH);

        into.Add(Rect(x + 3, y + 3, cardW, cardH, CardShadow));
        into.Add(Rect(x, y, cardW, cardH, CardBackground));
        into.Add(Rect(x, y, stripeW, cardH, sev));

        double iconX = x + stripeW + 8 * scale, portraitY = y + (cardH - portrait) / 2;
        string? champRef = _championIcons.GetPortraitReference(alert.ChampionName);
        if (champRef is not null) into.Add(Icon(champRef, iconX, portraitY, portrait, portrait));
        else into.Add(Rect(iconX, portraitY, portrait, portrait, NeutralChipBg));

        double itemX = iconX + portrait + 5 * scale, itemY = y + (cardH - itemIcon) / 2;
        string? itemRef = DDragonIconProvider.ItemIconPathOrNull(alert.ItemId);
        if (itemRef is not null) into.Add(Icon(itemRef, itemX, itemY, itemIcon, itemIcon));
        else into.Add(Rect(itemX, itemY, itemIcon, itemIcon, KeyChipBg));

        double textX = itemX + itemIcon + 10 * scale, cy = y + pad;
        into.Add(Text("적 핵심 아이템", textX, cy, sev, catF));
        into.Add(Text("적 핵심 아이템", textX + 0.6 * scale, cy, sev, catF));
        cy += catF * 1.35 + 2 * scale;
        string main = alert.ItemName + " 완성";
        into.Add(Text(main, textX, cy, TextPrimary, mainF));
        into.Add(Text(main, textX + 0.6 * scale, cy, TextPrimary, mainF));

        double frac = 1.0;
        if (expiryMs != long.MaxValue)
            frac = Math.Clamp((expiryMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 4000.0, 0.0, 1.0);
        into.Add(Rect(x, y + cardH - barH, cardW, barH, 0x22FFFFFF));
        into.Add(Rect(x, y + cardH - barH, cardW * frac, barH, sev));

        toastY += cardH + gap;
    }

    /// <summary>Enemy appear/disappear presence card (rich replacement for the plain-string
    /// notification): champion PORTRAIT + a severity stripe (appear/single = amber, 3+ group = red) +
    /// the tracker's message + a lifetime gauge. Group alerts have no single champion, so they show a
    /// neutral chip with the enemy count. Joins the running top-right stack.</summary>
    private void BuildEnemyPresenceCard(List<DrawCommand> into, EnemyPresenceHud presence, long expiryMs,
                                        double w, double scale, ref double toastY)
    {
        double cardW = 280 * scale, cardH = 50 * scale, margin = 12 * scale, gap = 8 * scale;
        double stripeW = 6 * scale, portrait = 36 * scale, msgF = 15 * scale, barH = 3 * scale;
        uint sev = presence.Kind == EnemyAlertKind.GroupDisappear && presence.GroupCount >= 3 ? DangerRed : WarnAmber;

        double x = w - cardW - margin, y = toastY;
        const string key = "notification";
        var (dx, dy) = GetOffset(key);
        x += dx; y += dy;
        _elementRects[key] = new Rect(x, y, cardW, cardH);

        into.Add(Rect(x + 3, y + 3, cardW, cardH, CardShadow));
        into.Add(Rect(x, y, cardW, cardH, CardBackground));
        into.Add(Rect(x, y, stripeW, cardH, sev));

        double iconX = x + stripeW + 8 * scale, iconY = y + (cardH - portrait) / 2;
        string? champRef = string.IsNullOrEmpty(presence.ChampionId) ? null : _championIcons.GetPortraitReference(presence.ChampionId);
        if (champRef is not null) into.Add(Icon(champRef, iconX, iconY, portrait, portrait));
        else
        {
            into.Add(Rect(iconX, iconY, portrait, portrait, NeutralChipBg));
            if (presence.Kind == EnemyAlertKind.GroupDisappear)
                into.Add(Text(presence.GroupCount.ToString(CultureInfo.InvariantCulture),
                              iconX + portrait * 0.32, iconY + portrait * 0.22, TextPrimary, portrait * 0.5));
        }

        double textX = iconX + portrait + 10 * scale, textY = y + (cardH - msgF * 1.2) / 2;
        into.Add(Text(presence.Message, textX, textY, TextPrimary, msgF));
        into.Add(Text(presence.Message, textX + 0.6 * scale, textY, TextPrimary, msgF));

        double frac = 1.0;
        if (expiryMs != long.MaxValue)
            frac = Math.Clamp((expiryMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 4000.0, 0.0, 1.0);
        into.Add(Rect(x, y + cardH - barH, cardW, barH, 0x22FFFFFF));
        into.Add(Rect(x, y + cardH - barH, cardW * frac, barH, sev));

        toastY += cardH + gap;
    }

    /// <summary>ZOrder for the persistent status card. Below the coordinator's lowest event card
    /// (Notification == 40) so every event HUD draws over it.</summary>
    private const int StatusCardZOrder = 0;

    /// <summary>(§40) ZOrder for the top-center combo stack (portrait row + combo + skill overlays).
    /// Above the status card; the combo card previously drew at the ComboResult HUD priority (100).</summary>
    private const int ComboStackZOrder = 100;

    /// <summary>Persistent in-game status card, anchored top-left: the active player's champion,
    /// level, gold, and AD/AP pulled straight from the latest <see cref="GameSnapshot"/>. Rendered
    /// directly each frame (not auto-hidden) so the overlay is never blank while in a game. Guards
    /// against zero-size / missing champion. Respects <c>overlay.scale</c> like the other cards.
    /// M02 pending-change #1: instance method so it can apply/record its own drag offset
    /// (key "statusCard").</summary>
    private void BuildStatusCard(List<DrawCommand> into, GameSnapshot snap, double scale)
    {
        double pad = 12 * scale;
        double margin = 12 * scale;
        double titleF = 13 * scale;
        double lineF = 14 * scale;
        double lineGap = 5 * scale;
        double stripeH = 3 * scale;

        double titleLH = titleF * 1.35;
        double lineLH = lineF * 1.35;

        string champion = ResolveActiveChampion(snap);
        string title = string.IsNullOrEmpty(champion) ? "내 정보" : champion;
        string stats = "Lv " + snap.Level.ToString(CultureInfo.InvariantCulture)
                     + "  ·  " + snap.CurrentGold.ToString("0", CultureInfo.InvariantCulture) + "g"
                     + "  ·  AD " + snap.Stats.AttackDamage.ToString("0", CultureInfo.InvariantCulture)
                     + "  ·  AP " + snap.Stats.AbilityPower.ToString("0", CultureInfo.InvariantCulture);
        string? benchLine = BuildBenchmarkLine(snap);

        double cardW = 240 * scale;
        double cardH = pad + titleLH + lineGap + lineLH
                     + (benchLine is null ? 0 : lineGap + lineLH) + pad;
        if (cardW <= 0 || cardH <= 0) return;

        double x = margin;
        double y = margin;

        var (dx, dy) = GetOffset("statusCard");
        x += dx;
        y += dy;
        _elementRects["statusCard"] = new Rect(x, y, cardW, cardH);

        double innerX = x + pad;

        into.Add(Rect(x + 3, y + 3, cardW, cardH, CardShadow));
        into.Add(Rect(x, y, cardW, cardH, CardBackground));
        into.Add(Rect(x, y, cardW, stripeH, AccentGold));

        double cy = y + pad;
        into.Add(Text(title, innerX, cy, AccentGold, titleF));
        cy += titleLH + lineGap;
        into.Add(Text(stats, innerX, cy, TextDim, lineF));
        if (benchLine is not null)
        {
            cy += lineLH + lineGap;
            into.Add(Text(benchLine, innerX, cy, TextDim, lineF));
        }
    }

    /// <summary>(benchmarks, loop 459) Lazily-created benchmark distributions — same rec dir,
    /// patch resolution AND tier bracket as the rune/item recommendations, so the HUD line and the
    /// champ-select rail describe one sample. Null when unset/unavailable.</summary>
    private Overlay.Core.Stats.FileBenchmarkSource? _benchmarks;
    private bool _benchmarksInit;
    private string _benchmarkBracket = "";

    /// <summary>The status card's live CS-benchmark line, or null when it has nothing honest to
    /// say (early game, no benchmark data for this champion, or scoreboard not populated).
    /// P1/P2 clean: compares the player's OWN public creep score / game clock against a static
    /// tier-bracket distribution — no enemy data, no inference. The comparison basis is that
    /// bracket's FULL-GAME CS/min distribution (the aggregation stores final totals / minutes), so
    /// the label names the bracket rather than claiming a same-minute cohort.</summary>
    private string? BuildBenchmarkLine(GameSnapshot snap)
    {
        if (snap.GameTime < 300) return null; // <5min: rates are still noise, say nothing

        if (!_benchmarksInit)
        {
            _benchmarksInit = true;
            if (_config.Get("champSelect.recDir") is string recDir && !string.IsNullOrWhiteSpace(recDir))
            {
                _benchmarkBracket = _config.Get("champSelect.recBracket") as string ?? "";
                if (_benchmarkBracket.Length == 0)
                    _benchmarkBracket = Overlay.Core.Stats.RecBrackets.Default;
                _benchmarks = new Overlay.Core.Stats.FileBenchmarkSource(recDir, _benchmarkBracket);
            }
        }
        if (_benchmarks is null) return null;

        // The active player's own scoreboard row (CS lives there, not on ActivePlayerStats).
        string me = snap.ActivePlayerSummonerName;
        if (string.IsNullOrEmpty(me)) return null;
        Overlay.Core.ScoreboardEntry? row = null;
        for (int i = 0; i < snap.PlayerCount; i++)
            if (string.Equals(snap.Players[i].SummonerName, me, StringComparison.Ordinal))
            { row = snap.Players[i]; break; }
        if (row is null || row.CreepScore <= 0) return null;

        var entry = _benchmarks.GetMainRole(
            row.ChampionName, Overlay.Core.ChampionDb.ChampionSummary.ResolveKoreanName(row.ChampionName));
        if (entry is null) return null;

        double csPerMin = row.CreepScore / (snap.GameTime / 60.0);
        double pct = entry.CsPercentile(csPerMin);
        // 상위 X% (higher percentile = better CS). Clamped at ~5/~95 by the estimator, shown
        // with "~" so a clamped tail never reads as an exact rank.
        double top = 100 - pct;
        string topText = top <= 5 ? "~5" : top >= 95 ? "~95" : top.ToString("0", CultureInfo.InvariantCulture);
        string bracketKey = "stats.bracket." + _benchmarkBracket;
        string bracketLabel = Localization.L(bracketKey);
        if (bracketLabel == bracketKey) bracketLabel = _benchmarkBracket;
        return $"CS {csPerMin.ToString("0.0", CultureInfo.InvariantCulture)}/분"
             + $"  ·  {bracketLabel} {entry.Role} 상위 {topText}% (표본 {entry.Games})";
    }

    // Inhibitor / Nexus-turret minimap position decoding moved to the testable, off-Windows-buildable
    // Overlay.Core.Overlay.StructureMinimapLayout (see StructureMinimapLayoutTests). The render loops
    // above call it directly. Kept out of this WPF host so the exact "where is each chip drawn" logic
    // can be proven in unit tests against the real captured structure ids.

    /// <summary>Resolve the active player's champion name by matching
    /// <see cref="GameSnapshot.ActivePlayerSummonerName"/> against the scoreboard rows. Returns an
    /// empty string when no match (e.g. spectator / not yet populated).</summary>
    private static string ResolveActiveChampion(GameSnapshot snap)
    {
        string me = snap.ActivePlayerSummonerName;
        if (string.IsNullOrEmpty(me)) return string.Empty;
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (string.Equals(p.SummonerName, me, StringComparison.Ordinal))
                return Localization.ChampionName(p.ChampionName);
        }
        return string.Empty;
    }

    private static uint AccentFor(HudType type) => type switch
    {
        HudType.ItemAlert => 0xFFE05A3B,      // orange-red
        HudType.RecallTimer => 0xFF3B82F6,    // blue
        HudType.InhibitorTimer => 0xFF9B59F6, // purple (M19 §3.2)
        HudType.GlobalGold => 0xFFF2C14E,     // amber/gold (M19 §3.3, estimate)
        HudType.EnemyJunglerDebug => 0xFF4ADE80, // green (M30 temp debug panel)
        HudType.EnemyItemAlert => WarnAmber,   // amber (warn severity for an enemy legendary buy)
        _ => 0xFF6E8FC8,                       // notification: muted blue
    };

    // ── DrawCommand factory helpers (allocation of the readonly-struct command only) ──

    private static DrawCommand Rect(double x, double y, double w, double h, uint color)
        => new(DrawCommandType.Rect, new RenderBounds(x, y, w, h), new RenderStyle(color));

    /// <summary>A Rect with NO corner rounding (Value flag = 1). The renderer rounds every normal Rect;
    /// this variant is for elements that must read as true squares — the combo-sequence ability chips.</summary>
    private static DrawCommand RectSharp(double x, double y, double w, double h, uint color)
        => new(DrawCommandType.Rect, new RenderBounds(x, y, w, h), new RenderStyle(color), null, 1);

    private static DrawCommand Text(string s, double x, double y, uint color, double font)
        => new(DrawCommandType.Text, new RenderBounds(x, y, 0, 0),
               new RenderStyle(color, 1.0, font), s);

    // Currently unused (the combo card's lethality progress bar was removed per user request,
    // loop 38 continuation 23) — kept as a generic progress-bar draw helper for any future
    // bar-style HUD element (e.g. mana/cooldown), not dead in the "abandoned" sense.
    private static DrawCommand Bar(double x, double y, double w, double h, uint color, double fraction)
        => new(DrawCommandType.ProgressBar, new RenderBounds(x, y, w, h),
               new RenderStyle(color), null, fraction);

    /// <summary>Icon draw command: <paramref name="reference"/> is a local image path/URI the
    /// <see cref="DrawCommandRenderer"/> loads + caches. Style colour is unused for images
    /// (only opacity applies), so full-opacity white is passed as a neutral placeholder.</summary>
    private static DrawCommand Icon(string reference, double x, double y, double w, double h)
        => new(DrawCommandType.Icon, new RenderBounds(x, y, w, h),
               new RenderStyle(TextPrimary), reference);

    // ── §40 combo-overlay draw helpers ──────────────────────────────────────────────

    /// <summary>Filled ellipse inscribed in the bounds (circular portraits).</summary>
    private static DrawCommand Ellipse(double x, double y, double w, double h, uint color)
        => new(DrawCommandType.Ellipse, new RenderBounds(x, y, w, h), new RenderStyle(color));

    /// <summary>A monospace text run (Value==1 selects the mono face) — combo-overlay damage numbers.</summary>
    private static DrawCommand TextMono(string s, double x, double y, uint color, double font)
        => new(DrawCommandType.Text, new RenderBounds(x, y, 0, 0),
               new RenderStyle(color, 1.0, font), s, 1);

    /// <summary>Icon with §40 render flags packed in Value: bit0 grayscale (dead), bit1 circular clip.</summary>
    private static DrawCommand Icon(string reference, double x, double y, double w, double h, int flags)
        => new(DrawCommandType.Icon, new RenderBounds(x, y, w, h),
               new RenderStyle(TextPrimary, 1.0), reference, flags);

    /// <summary>Icon with render flags AND an explicit opacity — M31 §C draws the last-seen
    /// portrait semi-transparent so it reads as a memory, not a live sighting.</summary>
    private static DrawCommand Icon(string reference, double x, double y, double w, double h, int flags, double opacity)
        => new(DrawCommandType.Icon, new RenderBounds(x, y, w, h),
               new RenderStyle(TextPrimary, opacity), reference, flags);

    /// <summary>The built-in basic-attack (auto) sword icon, tinted by <paramref name="color"/>.</summary>
    private static DrawCommand BasicAttackIcon(double x, double y, double w, double h, uint color)
        => new(DrawCommandType.Icon, new RenderBounds(x, y, w, h), new RenderStyle(color), "@basic-attack");

    // §40 mockup palette (exact hex from ComboOverlayMockup.dc.html, packed 0xAARRGGBB).
    private const uint OverlayPanelBg   = 0xE60B0F16; // rgba(11,15,22,.9) — combo/skill card background
    private const uint RowBg            = 0xB8090C12; // rgba(9,12,18,.72) — portrait row background
    private const uint PanelBorder      = 0xFF2A2F3A; // #2a2f3a card border
    private const uint RingSelected     = 0xFFC8AA6E; // gold ring on the selected enemy
    private const uint RingNormal       = 0xFF3B4250; // #3b4250 ring
    private const uint RingDead         = 0xFF3A4150; // #3a4150 ring (dead)
    private const uint NameSelected     = 0xFFE6E8EC; // #E6E8EC selected name
    private const uint NameDim          = 0xFF8B929F; // #8B929F unselected name
    private const uint RespawnRingBg    = 0x80040608; // rgba(4,6,10,.5)
    private const uint RespawnRingBorder= 0xFF5B6472; // #5b6472
    private const uint RespawnText      = 0xFFCDD3DC; // #cdd3dc
    private const uint ArmorText        = 0xFFF0D08A; // #f0d08a armor
    private const uint MrText           = 0xFF8AB4F0; // #8ab4f0 mr
    private const uint ComboTagBg       = 0xFFC8AA6E; // gold combo tag
    private const uint SkillTagBg       = 0xFF8AB4F0; // blue skill tag
    private const uint TagTextDark      = 0xFF0F1117; // #0F1117 tag label
    private const uint HpBarBg          = 0xFF07090D; // #07090d gauge track
    private const uint HpSurviveRed     = 0xFFE23327; // solid red = HP surviving max combo
    private const uint HpSpreadRed      = 0x66E23327; // translucent red = min→max spread
    private const uint HpMinLine        = 0xFFFFE066; // #ffe066 min-damage cut line
    private const uint MaxBoxBg         = 0xFF1A1310; private const uint MaxBoxBorder = 0xFF3A2420;
    private const uint MinBoxBg         = 0xFF1A170F; private const uint MinBoxBorder = 0xFF3A3220;
    private const uint MaxDmgNum        = 0xFFFF7A6A; private const uint MaxDmgLabel  = 0xFFFF9A8A;
    private const uint MinDmgNum        = 0xFFFFD76A;
    private const uint SkillDmgOn       = 0xFFE6E8EC; private const uint SkillDmgOff  = 0xFF5B6472;
    // slot colors: P gold / Q blue / W green / E purple / R red / A slate
    private const uint SlotP = 0xFFC8AA6E, SlotQ = 0xFF3B82F6, SlotW = 0xFF22C55E,
                       SlotE = 0xFFA855F7, SlotR = 0xFFF87171, SlotA = 0xFF64748B;

    private static uint SlotColor(string slot) => slot switch
    {
        "P" => SlotP, "Q" => SlotQ, "W" => SlotW, "E" => SlotE, "R" => SlotR, _ => SlotA,
    };

    /// <summary>Absolute path to a bundled §40 stat icon (Assets/stats/{name}.png, copied next to the
    /// assembly by the csproj). The renderer loads/caches it; a missing file resolves to no image.</summary>
    private static string StatIconPath(string name)
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "stats", name + ".png");

    /// <summary>Best-effort surface size for anchoring. Prefers the laid-out
    /// <see cref="RenderSurface.ActualWidth"/>/Height, falling back to the explicit
    /// Width/Height then the window size before first layout.</summary>
    private void GetSurfaceSize(out double w, out double h)
    {
        w = _surface.ActualWidth;
        h = _surface.ActualHeight;
        if (w <= 0 || double.IsNaN(w)) w = _surface.Width;
        if (h <= 0 || double.IsNaN(h)) h = _surface.Height;
        if (w <= 0 || double.IsNaN(w)) w = _window.Width;
        if (h <= 0 || double.IsNaN(h)) h = _window.Height;
    }

    /// <summary>Resize the render surface to match the current overlay-window client size, so the
    /// Canvas-hosted surface always covers — and the HUD auto-fit always scales to — the live game
    /// window (which MainWindow keeps aligned to the LoL client rect). Called at Start and on every
    /// window SizeChanged.</summary>
    private void SyncSurfaceToWindow()
    {
        double w = _window.ActualWidth  > 0 ? _window.ActualWidth  : _window.Width;
        double h = _window.ActualHeight > 0 ? _window.ActualHeight : _window.Height;
        if (w > 0 && !double.IsNaN(w)) _surface.Width  = w;
        if (h > 0 && !double.IsNaN(h)) _surface.Height = h;
    }

    /// <summary>Reference game-viewport height (DIPs) the HUD geometry was authored against — the
    /// combo/skill card, portrait row, toast, etc. base sizes all look right at this height. A 4K
    /// (3840×2160) game viewport at 100% display scaling. The auto-fit factor below scales everything
    /// down proportionally on smaller viewports so the overlay isn't ~2× oversized at 1080p.</summary>
    private const double ReferenceViewportHeight = 2160.0;

    /// <summary>Read the effective HUD scale = user's <c>overlay.scale</c> knob (M19 §2, clamped 0.5–2.0)
    /// × an automatic resolution-fit factor (<paramref name="surfaceHeight"/> ÷
    /// <see cref="ReferenceViewportHeight"/>). The auto factor makes the overlay match the actual game
    /// viewport instead of always drawing at 4K size — fixes the "overlay ~2× too big / runs off a 1080p
    /// laptop screen" report. Defaults to 1.0 when the scale config is unset/invalid; the auto factor is
    /// clamped so a tiny/oversized viewport can't make the HUD vanish or explode.</summary>
    private double ReadScale(double surfaceHeight)
    {
        double s = _config.Get("overlay.scale") is double d ? d : 1.0;
        s = s > 0 ? Math.Clamp(s, 0.5, 2.0) : 1.0;

        // Auto-fit to the game viewport's DEVICE-pixel height, not the WPF DIP height. Under Windows
        // display scaling (e.g. a 4K monitor at 150%, or a 1080p game window on that desktop) a
        // full-viewport surface reports only ~1440/720 DIP, so dividing the DIP height by the 2160
        // (4K device-px) reference produced ~0.67-0.4 and the whole overlay rendered far too small.
        // Multiplying by the surface DPI recovers the real on-screen pixels, so a 4K viewport maps to
        // autoFit=1.0 regardless of the display-scaling %, and the overlay stays proportional to the
        // actual LoL window across fullscreen / borderless / windowed.
        double deviceHeight = surfaceHeight > 0 ? surfaceHeight * SurfaceDpiScaleY() : 0;
        double autoFit = deviceHeight > 0 ? deviceHeight / ReferenceViewportHeight : 1.0;
        autoFit = Math.Clamp(autoFit, 0.4, 2.0);

        return s * autoFit;
    }

    /// <summary>Vertical device-pixels-per-DIP for the render surface (1.0 at 100% display scaling,
    /// 1.5 at 150%, …). Used by <see cref="ReadScale"/> to convert the DIP surface height into the
    /// game viewport's real device-pixel height so auto-fit is display-scaling-independent. Falls back
    /// to 1.0 before the surface is connected to a PresentationSource.</summary>
    private double SurfaceDpiScaleY()
    {
        var src = System.Windows.PresentationSource.FromVisual(_surface);
        if (src?.CompositionTarget is { } ct)
        {
            double m = ct.TransformToDevice.M22;
            if (m > 0 && !double.IsNaN(m)) return m;
        }
        return 1.0;
    }

    public void Dispose()
    {
        _timer.Stop();
        _coordinator.Dispose();
        // The ConfigManager is owned by AppComposition (shared instance) and disposed there,
        // not here — this host only borrows it.
    }
}
