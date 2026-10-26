using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Overlay.Client.Ads;
using Overlay.Client.Views;
using Overlay.Core.EventBus;

namespace Overlay.Client;

/// <summary>
/// Pre-game "home" window and app shell. Owns the shared <see cref="AppComposition"/> (which it
/// starts immediately — the data subsystems do not need the overlay window), hosts the
/// dark-themed views, and drives the overlay window's lifecycle:
///
/// <list type="bullet">
///   <item>GAME.CONNECTED → show the transparent overlay on the UI thread and wire M13 hotkeys
///   against the overlay HWND (once).</item>
///   <item>GAME.DISCONNECTED → hide the overlay.</item>
///   <item>The preview toggle → show/hide the overlay manually for testing without a game.</item>
/// </list>
///
/// All GAME.* events arrive on the EventBus background thread, so every handler marshals to the
/// UI thread via <see cref="System.Windows.Threading.Dispatcher.Invoke(Action)"/>.
///
/// <para><b>Responsive scale.</b> A <see cref="ScaleTransform"/> on the root grid scales the whole
/// UI to ~1.5x on large monitors and shrinks it to fit small ones — layout scaling, so text stays
/// crisp. Recomputed on load and DPI change (see <see cref="ApplyResponsiveScale"/>).</para>
/// </summary>
public partial class HomeWindow : Window
{
    /// <summary>Design size the UI is authored at; the responsive scale is relative to this.</summary>
    private const double BaseW = 1040;
    /// <summary>Design height: 726 of content + the M29 ad row. (loop 528) BaseH stays 830 while
    /// AdRowHeight dropped 130→104, so the 26px of trimmed ad margin moves to the content area — a
    /// tall view (stats) shows that much more per screen. (loop 537) The window height IS this
    /// (scaled, capped to the work area) — it no longer follows each view's content, so the size
    /// stays fixed across tab switches.</summary>
    private const double BaseH = 830;
    /// <summary>The reserved M29 row (HomeWindow.xaml): the 90px 728×90 banner plus 6/8 margin. Was
    /// 130 (16/24 margin) — the slot AREA read much larger than the banner and ate content height.
    /// Reclaimed to 0 when <see cref="AdSlotService.IsConfigured"/> is false.</summary>
    private const double AdRowHeight = 104;

    /// <summary>Effective design height: <see cref="BaseH"/>, minus the ad row when it is reclaimed.</summary>
    private double _baseH = BaseH;

    /// <summary>MinHeight from XAML, captured before the ad row adjusts it (loop 521).</summary>
    private double _minHeightBase;

    /// <summary>Whether the ad row is currently reserved (a creative is showing).</summary>
    private bool _adReserved = true;

    private readonly AppComposition _composition;

    private readonly HomeView _homeView;
    private readonly UserSearchView _searchView;
    private readonly ComboSettingsView _comboView;
    private readonly SettingsView _settingsView;
    private readonly HelpView _helpView;
    private readonly StatsView _statsView;

    private readonly string _connSub;
    private readonly string _discSub;

    /// <summary>Localization keys for the current section title + status pill, so both re-localize
    /// live when the language changes.</summary>
    private string _sectionKey = "nav.home";
    private string _statusKey = "status.waiting";
    private string _statusBrush = "TextDim";

    private readonly AdBanner? _adBanner;

    private MainWindow? _overlay;

    // "Start with League" (general.autoStartWithGame): tray icon + client watch while on, and
    // closing the window then hides it instead of exiting. Exit goes through the tray menu.
    private TrayHost? _tray;
    private bool _exiting;
    private bool _hotkeysWired;

    public HomeWindow()
    {
        InitializeComponent();

        _composition = new AppComposition();
        // M19: the global overlay-toggle hotkey (default SHIFT+TAB) fires on the hotkey message-pump
        // thread; marshal the show/hide to the UI thread. Only our window's visibility changes (P4).
        _composition.OverlayToggleRequested = () => Dispatcher.Invoke(ToggleOverlay);
        _composition.Start();

        // Load the persisted UI language before views render their first frame.
        Localization.ApplyCode(_composition.Config.Get("general.language") as string);

        _homeView = new HomeView();
        _searchView = new UserSearchView();
        _comboView = new ComboSettingsView();
        _settingsView = new SettingsView();
        _helpView = new HelpView();
        _statsView = new StatsView();
        _homeView.Attach(_composition);
        _searchView.Attach(_composition);
        _comboView.Attach(_composition);
        _settingsView.Attach(_composition);
        _statsView.Attach(_composition);

        MainContent.Content = _homeView;

#if LIGHT
        // 1.0 plan (RELEASE_1_0_PLAN.md, "Out of the public product"): the stats tab and its tier
        // list need match data from a production key, which the app does not have; the search tab
        // only searches the current game's scoreboard. Hidden in the public build, kept in full.
        NavSearch.Visibility = Visibility.Collapsed;
        NavStats.Visibility = Visibility.Collapsed;
#endif

        // M29: the one ad slot. (loop 521) The row is reclaimed WHENEVER there is nothing to show —
        // no endpoint, an unfilled/failed slot, or in-game dormancy — so an empty slot never renders
        // as a black band clipping the content (the previous behavior reserved it unconditionally
        // while configured). It is restored the moment a creative actually loads, which shifts the
        // views up once; the user preferred no dead band over no shift.
        _minHeightBase = MinHeight;
        SetAdSlotReserved(false); // start collapsed; a loaded creative expands it
        if (_composition.Ads.IsConfigured)
        {
            _adBanner = new AdBanner(_composition.Ads);
            _adBanner.FilledChanged += SetAdSlotReserved;
            AdSlot.Content = _adBanner;
        }

        // M33: champ-select assistant panel (only when the kill switch left the connector alive).
        // autoApply is read live per snapshot so a Settings toggle needs no restart.
        if (_composition.Lcu is { } lcu && _composition.ChampSelectStore is { } presets)
        {
            // D4 phase-2 (local form): recommendation files from the aggregation pipeline,
            // enabled by pointing champSelect.recDir at a rec/ directory. Empty = local only.
            Overlay.Core.ChampSelect.IRunePresetSource? recs = null;
            Overlay.Core.ChampSelect.FileItemRecommendationSource? itemRecs = null;
            string recRoot = "";
            // (brackets, loop 463) Which tier band the recommendations come from. Persisted, so
            // the choice survives a restart; defaults to Platinum+ rather than the widest sample.
            string bracket = _composition.Config.Get("champSelect.recBracket") as string ?? "";
            if (bracket.Length == 0) bracket = Overlay.Core.Stats.RecBrackets.Default;
#if !LIGHT
            // Public build: no recommendation rail (same reason as the hidden stats tab).
            if (_composition.Config.Get("champSelect.recDir") is string recDir
                && !string.IsNullOrWhiteSpace(recDir))
            {
                recRoot = recDir;
                recs = new Overlay.Core.ChampSelect.FileRecommendationSource(recDir, bracket);
                // (item recs, loop 459) Item builds ride the same rec dir + patch resolution, so
                // the rune cards and the item section always describe the same patch.
                itemRecs = new Overlay.Core.ChampSelect.FileItemRecommendationSource(recDir, bracket);
            }
#endif

            _homeView.ReviewHomeSlot.Content = new Overlay.Client.Views.GameReviewCard(_composition.Reviews);

            _homeView.ChampSelectHomeSlot.Content = new Overlay.Client.ChampSelect.ChampSelectPanel(
                lcu, presets,
                () => _composition.Config.Get("champSelect.autoApply") is true,
                recs,
                getFlashKey: () => _composition.Config.Get("champSelect.flashKey") as string,
                setFlashKey: v => _composition.Config.Set("champSelect.flashKey", v),
                itemRecommendations: itemRecs,
                recDir: recRoot,
                setRecBracket: v => _composition.Config.Set("champSelect.recBracket", v),
                recBracket: bracket,
                // (M41) kill timing vs the lane opponent, through the same engine as the overlay.
                killTiming: (me, enemy) => _composition.ComboRunner?.ComputeKillTiming(me, enemy)
                    ?? (IReadOnlyList<Overlay.Core.Combo.KillTimingRow>)Array.Empty<Overlay.Core.Combo.KillTimingRow>());

            // (2026-08-22 user request) The dashboard no longer force-raises itself on champ-select
            // entry — it was stealing foreground focus. The overlay still appears on GAME.CONNECTED
            // and the user brings the dashboard up themselves when they want it.

            // First-run Flash-key question: ONCE at app start, never again while the setting
            // holds a value (2026-07-25 request; replaces the old in-champ-select prompt).
            // (design pass) Not on a first run, where the getting-started guide comes first and
            // three stacked banners were too much to read at once; it shows from the next launch.
            if (_composition.Config.Get("champSelect.flashKey") is not string { Length: > 0 }
                && _composition.Config.Get("general.firstRunGuideShown") is true)
            {
                FlashKeyBanner.Visibility = Visibility.Visible;
                FlashKeyD.Click += (_, _) => { _composition.Config.Set("champSelect.flashKey", "D"); FlashKeyBanner.Visibility = Visibility.Collapsed; };
                FlashKeyF.Click += (_, _) => { _composition.Config.Set("champSelect.flashKey", "F"); FlashKeyBanner.Visibility = Visibility.Collapsed; };
            }

            // Warm the rune icon cache off the UI thread so the first champ-select render is
            // instant instead of paying ~70 CDN fetches (user latency feedback 2026-07-25).
            _ = System.Threading.Tasks.Task.Run(Overlay.Client.ChampSelect.RunePageView.PrefetchIconsAsync);
        }

        // First-run guide: the things a new user needs (window mode, combos, target, toggle key),
        // with the hotkey they actually have. Help's [시작 가이드 다시 보기] brings it back.
        FirstRunDismiss.Click += (_, _) => DismissFirstRunGuide();
        FirstRunOpenCombo.Click += (_, _) => { DismissFirstRunGuide(); NavCombo.IsChecked = true; };
        _helpView.ShowGuideRequested += () => { NavHome.IsChecked = true; ShowFirstRunGuide(); };
        if (_composition.Config.Get("general.firstRunGuideShown") is not true)
            ShowFirstRunGuide();

        // M39 one-time telemetry disclosure: only while something would actually be sent
        // (configured + enabled) and never again once acknowledged. "보내지 않기" both opts out
        // and acknowledges — the service reads telemetry.enabled at next construction, and the
        // current session's instance simply keeps its (already sent) launch ping.
        if (_composition.Telemetry.IsConfigured
            && _composition.Config.Get("telemetry.noticeShown") is not true)
        {
            TelemetryNotice.Visibility = Visibility.Visible;
            TelemetryOk.Click += (_, _) =>
            {
                _composition.Config.Set("telemetry.noticeShown", true);
                TelemetryNotice.Visibility = Visibility.Collapsed;
            };
            TelemetryOptOut.Click += (_, _) =>
            {
                _composition.Config.Set("telemetry.enabled", false);
                _composition.Config.Set("telemetry.noticeShown", true);
                App.CrashSink = null; // opt-out applies to THIS session's crashes too, not just the next launch
                TelemetryNotice.Visibility = Visibility.Collapsed;
            };
        }

        Localization.LanguageChanged += ApplyLanguage;
        ApplyLanguage();

        // Fullscreen check: League's own game.cfg, re-read every few seconds (a cheap file read)
        // so the warning appears before the game starts and leaves once the mode is changed.
        _windowModeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _windowModeTimer.Tick += (_, _) => CheckLeagueWindowMode();
        _windowModeTimer.Start();
        CheckLeagueWindowMode();

        _connSub = EventBus.Subscribe("GAME.CONNECTED", _ => Dispatcher.Invoke(OnGameConnected));
        _discSub = EventBus.Subscribe("GAME.DISCONNECTED", _ => Dispatcher.Invoke(OnGameDisconnected));

        Loaded += (_, _) =>
        {
            ApplyResponsiveScale();
            // (loop 524) Kick the ad load from HERE — the banner starts in a collapsed row, and a
            // collapsed control's own Loaded event does not reliably fire, so it would never fetch.
            _adBanner?.EnsureStarted(this);
        };
        Closed += OnClosed;

        SyncAutostart();
        _autostartSub = _composition.Config.OnChange("general.autoStartWithGame",
            _ => Dispatcher.Invoke(SyncAutostart));
        Closing += (_, e) =>
        {
            if (_tray is null || _exiting) return;
            e.Cancel = true;
            Hide();
            // Once: closing that does not exit is a surprise the first time it happens.
            if (_composition.Config.Get("general.trayNoticeShown") is not true)
            {
                _tray.Notify(Localization.L("tray.hiddenTitle"), Localization.L("tray.hiddenBody"));
                // AI-NOTE: SettingsTests.TrayNoticeOnce counts this line; Windows may suppress the toast
                // itself (Focus Assist), so the call is the only thing a test can observe.
                AppComposition.Log("tray notice shown");
                _composition.Config.Set("general.trayNoticeShown", true);
            }
        };
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        // A second launch (shortcut while waiting in the tray) brings this window up instead.
        SingleInstance.ListenForShow(() => Dispatcher.BeginInvoke(() => ShowDashboard(activate: true)));
    }

    private readonly string _autostartSub;

    /// <summary>False when an autostart launch should show the dashboard anyway: the setting was
    /// turned off (no tray to come back from), or the League client is already running.</summary>
    internal bool CanStartHidden => _tray is { ClientAlreadyRunning: false };

    /// <summary>Show (or restore) the dashboard. Without activation when the League client is
    /// what triggered it, so the client keeps focus.</summary>
    internal void ShowDashboard(bool activate)
    {
        ShowActivated = activate;
        Show();
        ShowActivated = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (activate) Activate();
    }

    private void SyncAutostart()
    {
        bool on = _composition.Config.Get("general.autoStartWithGame") is true;
        Autostart.Apply(on);
        if (on && _tray is null)
            _tray = new TrayHost(() => IsVisible, () => ShowDashboard(activate: false),
                () => ShowDashboard(activate: true), ExitFromTray);
        else if (!on && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }
    }

    private void ExitFromTray()
    {
        _exiting = true;
        Close();
    }

    /// <summary>(loop 521) Reserve or reclaim the bottom ad row. Reserved only while a creative is
    /// actually on screen; reclaimed otherwise so an empty slot never shows as a black band under
    /// the content. Idempotent, and re-applies the responsive scale so the window height follows.</summary>
    private void SetAdSlotReserved(bool reserved)
    {
        AppComposition.AdLog($"SetAdSlotReserved({reserved}) (was {_adReserved})");
        if (reserved == _adReserved) return;
        _adReserved = reserved;

        AdRow.Height = reserved ? new GridLength(AdRowHeight) : new GridLength(0);
        AdSlot.Visibility = reserved ? Visibility.Visible : Visibility.Collapsed;
        _baseH = reserved ? BaseH : BaseH - AdRowHeight;
        MinHeight = reserved ? _minHeightBase : _minHeightBase - AdRowHeight;
        if (IsLoaded) ApplyResponsiveScale(); // before load, the Loaded handler sizes the window
    }

    // ── Responsive scale ────────────────────────────────────────────────────

    /// <summary>Scales the root grid by <c>clamp(min(workW/baseW, workH/baseH, 1.5), 1.0, 1.5)</c>
    /// and sizes the window to <c>base*scale</c> capped to the work area, then re-centers. Called
    /// on load and on DPI change so it is never hardcoded to one monitor.</summary>
    private void ApplyResponsiveScale()
    {
        var wa = SystemParameters.WorkArea; // device-independent units, same as Width/Height
        if (wa.Width <= 0 || wa.Height <= 0) return;

        double scale = Math.Min(Math.Min(wa.Width / BaseW, wa.Height / _baseH), 1.5);
        scale = Math.Clamp(scale, 1.0, 1.5);
        RootScale.ScaleX = RootScale.ScaleY = scale;

        Width = Math.Min(BaseW * scale, wa.Width);
        Height = Math.Min(_baseH * scale, wa.Height);

        // Re-center in the work area (CenterScreen only fires once, before we resize).
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyResponsiveScale();
    }

    // ── Localization ────────────────────────────────────────────────────────

    private void ApplyLanguage()
    {
        NavHomeLabel.Text = Localization.L("nav.home");
        NavSearchLabel.Text = Localization.L("nav.search");
        NavComboLabel.Text = Localization.L("nav.combo");
        NavStatsLabel.Text = Localization.L("nav.stats");
        NavSettingsLabel.Text = Localization.L("nav.settings");
        NavHelpLabel.Text = Localization.L("nav.help");

        SectionTitle.Text = Localization.L(_sectionKey);
        StatusText.Text = "● " + Localization.L(_statusKey);
        StatusText.Foreground = (Brush)Application.Current.FindResource(_statusBrush);
        UpdatePreviewButton();

        FlashKeyBannerText.Text = Localization.L("flash.banner");
        FlashKeyD.Content = Localization.L("settings.flashKey.d");
        FlashKeyF.Content = Localization.L("settings.flashKey.f");
        FirstRunGuideTitle.Text = Localization.L("guide.title");
        FirstRunGuideText.Text = Localization.F("guide.body",
            _composition.Config.Get("overlay.toggleHotkey") as string is { Length: > 0 } t ? t : "SHIFT+TAB");
        FirstRunOpenCombo.Content = Localization.L("guide.openCombo");
        FirstRunDismiss.Content = Localization.L("guide.dismiss");
        FullscreenWarningTitle.Text = Localization.L("fullscreen.title");
        FullscreenWarningText.Text = Localization.L("fullscreen.body");
        TelemetryNoticeText.Text = Localization.L("telemetry.notice");
        TelemetryOk.Content = Localization.L("telemetry.ok");
        TelemetryOptOut.Content = Localization.L("telemetry.optOut");
    }

    // ── League window mode ──────────────────────────────────────────────────

    private readonly System.Windows.Threading.DispatcherTimer _windowModeTimer;

    /// <summary>Path of League's game.cfg once found; kept so the check still works after the
    /// client closes, and so the process is only probed until the first hit.</summary>
    private string? _gameCfgPath;

    /// <summary>Shows <see cref="FullscreenWarning"/> while League is set to exclusive fullscreen
    /// (<c>[General] WindowMode=0</c> in <c>(install)\Config\game.cfg</c>), where the overlay
    /// window cannot draw over the game. Reads a settings FILE the game wrote — no process memory.</summary>
    private void CheckLeagueWindowMode()
    {
        if (_gameCfgPath is null || !System.IO.File.Exists(_gameCfgPath))
            _gameCfgPath = FindLeagueGameCfg();
        int? mode = Overlay.Core.Vision.GameCfgReader.ReadWindowMode(_gameCfgPath);
        FullscreenWarning.Visibility = mode == Overlay.Core.Vision.GameCfgReader.WindowModeFullscreen
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary><c>Config\game.cfg</c> beside the running League client's executable (the install
    /// root, same discovery as the LCU lockfile), or null when the client is not running.</summary>
    private static string? FindLeagueGameCfg()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("LeagueClientUx"))
        {
            try
            {
                if (p.MainModule?.FileName is { } exe
                    && System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe)!, "Config", "game.cfg") is var cfg
                    && System.IO.File.Exists(cfg))
                    return cfg;
            }
            catch { /* access denied / exited mid-probe — try again next tick */ }
            finally { p.Dispose(); }
        }
        return null;
    }

    // ── First-run guide ─────────────────────────────────────────────────────

    private void ShowFirstRunGuide()
    {
        ApplyLanguage(); // the toggle key may have been rebound since the text was last built
        FirstRunGuide.Visibility = Visibility.Visible;
    }

    private void DismissFirstRunGuide()
    {
        _composition.Config.Set("general.firstRunGuideShown", true);
        FirstRunGuide.Visibility = Visibility.Collapsed;
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    private void Nav_Home(object sender, RoutedEventArgs e)
    {
        if (_homeView is null) return; // guards the initial IsChecked=True firing during init
        _homeView.Refresh();
        Show(_homeView, "nav.home");
    }

    private void Nav_Search(object sender, RoutedEventArgs e)
    {
        if (_searchView is null) return;
        Show(_searchView, "nav.search");
    }

    private void Nav_Combo(object sender, RoutedEventArgs e)
    {
        if (_comboView is null) return;
        _comboView.RefreshSavedList();
        Show(_comboView, "nav.combo");
    }

    private void Nav_Settings(object sender, RoutedEventArgs e)
    {
        if (_settingsView is null) return;
        Show(_settingsView, "nav.settings");
    }

    private void Nav_Help(object sender, RoutedEventArgs e)
    {
        if (_helpView is null) return;
        Show(_helpView, "nav.help");
    }

    private void Nav_Stats(object sender, RoutedEventArgs e)
    {
        if (_statsView is null) return;
        _statsView.Refresh(); // lazy: the table parses/builds on first entry only
        Show(_statsView, "nav.stats");
    }

    private void Show(System.Windows.Controls.UserControl view, string sectionKey)
    {
        MainContent.Content = view;
        _sectionKey = sectionKey;
        SectionTitle.Text = Localization.L(sectionKey);
        // The champ-select panel + flash-key banner belong to the HOME dashboard only
        // (2026-07-25 feedback: they used to follow the user across every tab).
        HomeDashboardExtras.Visibility = sectionKey == "nav.home"
            ? Visibility.Visible : Visibility.Collapsed;
        // (loop 537) The window is NOT re-fit per view: fit-to-content made the window height jump
        // on every tab switch. Size is fixed at the design size; shorter views just leave background.
    }

    // ── Game state → overlay + status pill ─────────────────────────────────

    private void OnGameConnected()
    {
        SetStatus("status.detected", "Success");
        if (_composition.PreviewActive) _overlay?.ClearComboCard(); // the preview's card is not this game's
        _composition.ResetScoreboard(); // a new game starts with League's scoreboard closed
        ShowOverlay();
        _homeView.Refresh();
    }

    private void OnGameDisconnected()
    {
        SetStatus("status.waiting", "TextDim");
        HideOverlay();
        _homeView.Refresh();
    }

    private void SetStatus(string statusKey, string brushKey)
    {
        _statusKey = statusKey;
        _statusBrush = brushKey;
        StatusText.Text = "● " + Localization.L(statusKey);
        StatusText.Foreground = (Brush)Application.Current.FindResource(brushKey);
        UpdatePreviewButton();
    }

    // ── Overlay show/hide ──────────────────────────────────────────────────

    private void PreviewToggle_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

    /// <summary>Show the overlay if hidden, hide it if shown. Shared by the preview button and the
    /// M19 global toggle hotkey. If the overlay has never been shown, <see cref="ShowOverlay"/>
    /// creates it and wires the M13 hotkeys (including the toggle) against its HWND.</summary>
    private void ToggleOverlay()
    {
        if (_overlay is { IsVisible: true })
            HideOverlay();
        else
            ShowOverlay();
    }

    private void ShowOverlay()
    {
        // (2026-10-19) No game: this is the preview — the overlay, a combo hotkey and the threat /
        // next-item cards then run on the sample game (AppComposition.PreviewSnapshot).
        _composition.PreviewActive = _composition.LatestSnapshot is not { HasData: true };
        try
        {
            if (_overlay is null)
            {
                _overlay = new MainWindow(_composition);
                _overlay.Show();

                if (!_hotkeysWired)
                {
                    var hwnd = new WindowInteropHelper(_overlay).Handle;
                    _composition.WireHotkeys(hwnd);
                    _hotkeysWired = true;
                }
            }
            else
            {
                _overlay.Show();
            }
        }
        catch (Exception ex)
        {
            // (loop 519) This used to vanish into Debug.WriteLine, which is invisible in a release
            // build — so a corrupt/missing Config/overlay-config.json (OverlayConfigLoader.Load
            // throws by design, "fail loud") made the overlay silently never appear AND left the
            // combo hotkeys unwired (both happen inside this try), with zero recorded evidence. The
            // ctor failing also leaves a half-built _overlay that must not be reused. Route through
            // the same file log + dialog the global handlers use, and reset so a later retry (config
            // fixed, toggle pressed again) can rebuild cleanly.
            try { _overlay?.Close(); } catch { /* half-built window; best-effort */ }
            _overlay = null;
            _hotkeysWired = false;
            App.Report("ShowOverlay", ex, showDialog: true);
        }

        UpdatePreviewButton();

        // Preview-sample mode: if no real game is connected, show one sample of each event HUD so
        // the user can preview + position them. Deferred to Background priority so the overlay's
        // OnLoaded (which composes the OverlayHost and subscribes it to UI.*) has run first —
        // otherwise the samples would be published before anything is listening.
        Dispatcher.BeginInvoke(new Action(PublishOverlaySamples),
                               System.Windows.Threading.DispatcherPriority.Background);
    }

    private void HideOverlay()
    {
        if (_composition.PreviewActive)
        {
            _overlay?.ClearComboCard();
            _composition.PreviewActive = false;
        }
        _overlay?.Hide();
        UpdatePreviewButton();
    }

    /// <summary>Keep the header overlay toggle label + colour in sync with the overlay's real
    /// visibility. Called from every path that shows/hides the overlay (ShowOverlay/HideOverlay,
    /// which are in turn reached from OnGameConnected/OnGameDisconnected, PreviewToggle_Click, and
    /// the SHIFT+TAB toggle hotkey), so the button always reflects reality.
    /// (design pass) The label names the action, not the state: "ON/OFF" read as a status, and
    /// "OFF" outside a game made new users think the overlay was broken. Outside a game the action
    /// is a preview with sample cards, so it says so.</summary>
    private void UpdatePreviewButton()
    {
        bool on = _overlay is { IsVisible: true };
        bool inGame = _statusKey == "status.detected";
        PreviewToggle.Content = Localization.L(on ? "header.previewHide"
            : inGame ? "header.overlayShow" : "header.previewShow");
        PreviewToggle.Foreground = (Brush)Application.Current.FindResource("Text");
    }

    /// <summary>Preview-sample mode: when the overlay is shown with NO game connected, put a combo
    /// card up — the preview champion's saved combo computed on the sample game, or a fixed sample when
    /// that champion has none. Every other element comes from the sample game or is drawn by the
    /// overlay itself while <see cref="AppComposition.PreviewActive"/>. No-op when a real game IS
    /// connected.</summary>
    private void PublishOverlaySamples()
    {
        if (_composition.LatestSnapshot?.HasData == true) return; // real game running → not a preview

        // (2026-10-19) A real card first: the preview champion's own saved combo, computed by the
        // engine on the preview's sample game exactly as a hotkey press would. The alert cards are
        // drawn (and held) by the overlay itself in preview, so they are no longer published here.
        if (_composition.FirstSavedComboId(_composition.PreviewChampionId) is { } comboId)
        {
            EventBus.Publish("COMBO.TRIGGER", comboId, source: "HomeWindow.Preview");
            return;
        }

        var combo = new Overlay.Core.Combo.ComboResult(
            TotalDamage: 1234, TotalMana: 0, ManaSufficient: true, KillThresholdHP: 1500,
            IsLethal: true, NodeBreakdown: Array.Empty<Overlay.Core.Combo.NodeBreakdownEntry>(),
            TotalCastTime: 0, RangeMin: 980, RangeMax: 1560);
        // UI.COMBO_RESULT carries a ComboHudResult wrapper: it must be populated the way a LIVE combo
        // is, or the preview renders the card's old shape — CasterChampion drives the ability ICONS
        // (empty ⇒ bare letter chips), and the structured Sequence drives the redesigned chip row
        // (null ⇒ the legacy CommandLabel-parse fallback). Both are set here so the preview matches
        // what a real game shows.
        var sequence = new List<Overlay.Core.Combo.ComboSequenceToken>
        {
            new("Q", IsAbility: true),
            new("A", IsAbility: false),
            new("W", IsAbility: true),
            new("R", IsAbility: true),
        };
        var comboHud = new Overlay.Core.Combo.ComboHudResult(combo, "Zed", "Q-A-W-R",
            TargetArmor: 40, TargetMr: 32, TargetMaxHp: 2100, TargetLevel: 11,
            Sequence: sequence, CasterChampion: "Zed", ComboName: "미리보기");

        EventBus.Publish("UI.COMBO_RESULT", comboHud, source: "HomeWindow.Preview");
    }

    /// <summary>(design pass) The dark app sat under a white system title bar. DWM's immersive dark
    /// mode darkens the caption (Windows 10 20H1+, attribute 20; 19 on earlier builds), and on
    /// Windows 11 the caption takes the theme's Bg color so the bar and the page read as one surface.
    /// Unsupported attributes just return an error HRESULT, which is ignored.</summary>
    private static class DarkTitleBar
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int UseImmersiveDarkMode = 20;
        private const int UseImmersiveDarkModeLegacy = 19;
        private const int CaptionColor = 35;

        public static void Apply(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeLegacy, ref on, sizeof(int));
            if (Application.Current.TryFindResource("BgColor") is Color bg)
            {
                int colorRef = bg.R | (bg.G << 8) | (bg.B << 16); // COLORREF is 0x00BBGGRR
                DwmSetWindowAttribute(hwnd, CaptionColor, ref colorRef, sizeof(int));
            }
        }
    }

    // ── Teardown ───────────────────────────────────────────────────────────

    private void OnClosed(object? sender, EventArgs e)
    {
        _tray?.Dispose();
        _windowModeTimer.Stop();
        EventBus.Unsubscribe(_autostartSub);
        _adBanner?.Shutdown();
        Localization.LanguageChanged -= ApplyLanguage;
        EventBus.Unsubscribe(_connSub);
        EventBus.Unsubscribe(_discSub);

        _overlay?.Close();
        _composition.Dispose();
    }
}
