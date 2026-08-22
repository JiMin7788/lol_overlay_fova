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
    /// <summary>Design height cap: 726 of content + the M29 ad row. (loop 528) BaseH stays 830 while
    /// AdRowHeight dropped 130→104, so the 26px of trimmed ad margin moves to the content area — a
    /// tall view (stats) shows that much more per screen. The window's real height follows content
    /// (FitHeightToContent), so this is just the upper bound.</summary>
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
            if (_composition.Config.Get("champSelect.recDir") is string recDir
                && !string.IsNullOrWhiteSpace(recDir))
            {
                recRoot = recDir;
                recs = new Overlay.Core.ChampSelect.FileRecommendationSource(recDir, bracket);
                // (item recs, loop 459) Item builds ride the same rec dir + patch resolution, so
                // the rune cards and the item section always describe the same patch.
                itemRecs = new Overlay.Core.ChampSelect.FileItemRecommendationSource(recDir, bracket);
            }

            _homeView.ChampSelectHomeSlot.Content = new Overlay.Client.ChampSelect.ChampSelectPanel(
                lcu, presets,
                () => _composition.Config.Get("champSelect.autoApply") is true,
                recs,
                getFlashKey: () => _composition.Config.Get("champSelect.flashKey") as string,
                setFlashKey: v => _composition.Config.Set("champSelect.flashKey", v),
                itemRecommendations: itemRecs,
                recDir: recRoot,
                setRecBracket: v => _composition.Config.Set("champSelect.recBracket", v),
                recBracket: bracket);

            // (2026-08-22 user request) The dashboard no longer force-raises itself on champ-select
            // entry — it was stealing foreground focus. The overlay still appears on GAME.CONNECTED
            // and the user brings the dashboard up themselves when they want it.

            // First-run Flash-key question: ONCE at app start, never again while the setting
            // holds a value (2026-07-25 request; replaces the old in-champ-select prompt).
            if (_composition.Config.Get("champSelect.flashKey") is not string { Length: > 0 })
            {
                FlashKeyBanner.Visibility = Visibility.Visible;
                FlashKeyD.Click += (_, _) => { _composition.Config.Set("champSelect.flashKey", "D"); FlashKeyBanner.Visibility = Visibility.Collapsed; };
                FlashKeyF.Click += (_, _) => { _composition.Config.Set("champSelect.flashKey", "F"); FlashKeyBanner.Visibility = Visibility.Collapsed; };
            }

            // Warm the rune icon cache off the UI thread so the first champ-select render is
            // instant instead of paying ~70 CDN fetches (user latency feedback 2026-07-25).
            _ = System.Threading.Tasks.Task.Run(Overlay.Client.ChampSelect.RunePageView.PrefetchIconsAsync);
        }

        Localization.LanguageChanged += ApplyLanguage;
        ApplyLanguage();

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

        // (loop 527) _baseH*scale is a design MAX; most views' real content is shorter, leaving a
        // tall band of empty near-black window background below them. Shrink the window to the
        // current view's rendered content once layout settles (never grow — a view taller than the
        // design height keeps it and scrolls, via MainContent's stretch, so no empty band either way).
        ScheduleFitToContent();
    }

    private void ScheduleFitToContent()
        => Dispatcher.BeginInvoke(new Action(FitHeightToContent), System.Windows.Threading.DispatcherPriority.Loaded);

    private System.Windows.Controls.ScrollViewer? _fitSv;

    private void FitHeightToContent()
    {
        if (!IsLoaded) return;
        // The current view's OWN root ScrollViewer (each view's root element) — NOT a visual-tree
        // search, which could return an inner list scroller (e.g. the champ-select rune list) whose
        // large extent would balloon the window.
        var sv = (MainContent.Content as System.Windows.Controls.ContentControl)?.Content
                 as System.Windows.Controls.ScrollViewer;
        if (sv is null || sv.ViewportHeight <= 0) return;

        // Views load their content asynchronously (the stats list fills after the view is shown), so
        // a one-shot fit would shrink to the empty view and, being one-directional, never grow back —
        // leaving the finished list clipped under the ad. Re-fit whenever this view's content height
        // changes.
        if (!ReferenceEquals(sv, _fitSv))
        {
            if (_fitSv is not null) _fitSv.ScrollChanged -= OnFitScrollChanged;
            _fitSv = sv;
            _fitSv.ScrollChanged += OnFitScrollChanged;
        }

        var wa = SystemParameters.WorkArea;
        double scale = RootScale.ScaleY > 0 ? RootScale.ScaleY : 1.0;
        double designH = Math.Min(_baseH * scale, wa.Height);   // the authored height — never exceed it

        // empty > 0: viewport has a band of empty background below the content → shrink to remove it.
        // empty < 0: content is taller than the viewport → grow back toward the design height (capped,
        // so a very tall view just uses the full design height and scrolls, never balloons the window).
        double empty = sv.ViewportHeight - sv.ExtentHeight;
        if (double.IsNaN(empty)) return;
        double target = Math.Min(Math.Max(Height - empty * scale, MinHeight), designH);
        if (Math.Abs(target - Height) < 1.0) return;
        Height = target;
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
    }

    private void OnFitScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // Only the content GROWING/SHRINKING matters (not the scroll position), and only if it did
        // not just reach the design height already.
        if (Math.Abs(e.ExtentHeightChange) > 0.5) ScheduleFitToContent();
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
        NavSettingsLabel.Text = Localization.L("nav.settings");
        NavHelpLabel.Text = Localization.L("nav.help");

        SectionTitle.Text = Localization.L(_sectionKey);
        StatusText.Text = "● " + Localization.L(_statusKey);
        StatusText.Foreground = (Brush)Application.Current.FindResource(_statusBrush);
        UpdatePreviewButton();
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

        // (loop 527) Different views have different content heights — re-fit the window to this one.
        if (IsLoaded) { ApplyResponsiveScale(); ScheduleFitToContent(); }
    }

    // ── Game state → overlay + status pill ─────────────────────────────────

    private void OnGameConnected()
    {
        SetStatus("status.detected", "Success");
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
        _overlay?.Hide();
        UpdatePreviewButton();
    }

    /// <summary>Keep the header overlay toggle label + colour in sync with the overlay's real
    /// visibility. Called from every path that shows/hides the overlay (ShowOverlay/HideOverlay,
    /// which are in turn reached from OnGameConnected/OnGameDisconnected, PreviewToggle_Click, and
    /// the SHIFT+TAB toggle hotkey), so the button always reflects reality. UI text is Korean.</summary>
    private void UpdatePreviewButton()
    {
        bool on = _overlay is { IsVisible: true };
        PreviewToggle.Content = on ? "오버레이 · ON" : "오버레이 · OFF";
        PreviewToggle.Foreground = (Brush)Application.Current.FindResource(on ? "Success" : "TextDim");
    }

    /// <summary>Preview-sample mode: when the overlay is shown with NO game connected (manual toggle
    /// for testing), publish one SAMPLE of each event-driven HUD type through the normal EventBus →
    /// coordinator → card path so the user can see and position every HUD style. Samples auto-hide
    /// after their display duration (re-toggle to see them again). Text is prefixed with "[미리보기]"
    /// so it is never mistaken for real data. No-op when a real game IS connected.</summary>
    private void PublishOverlaySamples()
    {
        if (_composition.LatestSnapshot?.HasData == true) return; // real game running → not a preview

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
        EventBus.Publish("UI.ITEM_ALERT", "[미리보기] 루덴스 완성", source: "HomeWindow.Preview");
        EventBus.Publish("UI.RECALL_TIMER", "[미리보기] 예시: 리스폰 12.3초", source: "HomeWindow.Preview");
    }

    // ── Teardown ───────────────────────────────────────────────────────────

    private void OnClosed(object? sender, EventArgs e)
    {
        _adBanner?.Shutdown();
        Localization.LanguageChanged -= ApplyLanguage;
        EventBus.Unsubscribe(_connSub);
        EventBus.Unsubscribe(_discSub);

        _overlay?.Close();
        _composition.Dispose();
    }
}
