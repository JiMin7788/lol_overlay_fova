using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // ToggleButton (afterimage per-role gates)
using System.Windows.Input;

namespace Overlay.Client.Views;

/// <summary>
/// Settings view (M19 §1–§2). A three-way sub-navigation — 기본 (General) / 언어 (Language) /
/// 오버레이 (Overlay) — over the shared M14 <see cref="Overlay.Core.Config.ConfigManager"/>
/// (<c>AppComposition.Config</c>). Language persists to <c>general.language</c> and applies live via
/// <see cref="Localization"/>; the overlay controls persist to <c>overlay.*</c> and are applied to
/// the live overlay window through <c>ConfigManager.OnChange</c> subscriptions in
/// <see cref="MainWindow"/> (opacity, movable) or read by the render pipeline (scale).
///
/// <para><b>Localization note.</b> The new settings labels are held in a self-contained ko/en helper
/// (<see cref="SL"/>) rather than the shared <c>Localization</c> table, so this slice does not have to
/// edit that file (parallel-edit ownership). They still switch live via <c>LanguageChanged</c>. Moving
/// them into <c>Localization</c> is a small backlog cleanup.</para>
/// </summary>
public partial class SettingsView : UserControl
{
    private AppComposition? _composition;

    /// <summary>Guards programmatic control updates (during attach / language sync) so setting a
    /// value in code does not re-persist or re-fire the change handlers.</summary>
    private bool _syncing;

    // Toggle-hotkey capture state (mirrors the combo editor's key-capture pattern).
    private bool _capturingHotkey;
    private string _toggleHotkey = DefaultToggleHotkey;

    private const string DefaultToggleHotkey = "SHIFT+TAB";

    // Skill-damage overlay (§40, default Alt+2) hotkey capture — same chord-capture pattern as the toggle.
    private bool _capturingSkillHotkey;
    private string _skillOverlayHotkey = DefaultSkillOverlayHotkey;
    private const string DefaultSkillOverlayHotkey = "ALT+`"; // (M46, user request) was ALT+2

    private bool _capturingComboHotkey;
    private string _comboOverlayHotkey = DefaultComboOverlayHotkey;
    private const string DefaultComboOverlayHotkey = "ALT+1";

    // (2026-10-20, user request) Global gold panel key; TAB = follow League's scoreboard.
    private bool _capturingGoldHotkey;
    private string _goldHotkey = DefaultGoldHotkey;
    private const string DefaultGoldHotkey = "TAB";

    // (M46, user request) Next-item card key; P = League's shop key.
    private bool _capturingNextItemHotkey;
    private string _nextItemHotkey = DefaultNextItemHotkey;
    private const string DefaultNextItemHotkey = "P";

    // (M46) A chord being captured: any modifiers plus up to three keys, finished when one of them is released or the third goes down.
    private readonly Overlay.Core.Hotkeys.ChordCapture _chordCapture = new();

    /// <summary>(M31 §C) Afterimage per-role gates, in the order they appear in the UI. Keys must match
    /// <c>OverlayHost.AfterimageRoleEnabled</c>'s <c>minimap.afterimage.roles.{key}</c> lookup and the
    /// role strings <c>EnemyPresenceAlert</c> carries.</summary>
    private static readonly (string Key, string Ko, string En)[] AfterimageRoles =
    {
        ("top", "탑", "Top"), ("jungle", "정글", "Jungle"), ("mid", "미드", "Mid"),
        ("adc", "원딜", "ADC"), ("support", "서폿", "Support"),
    };

    private readonly Dictionary<string, ToggleButton> _afterimageRoleToggles = new();
    private readonly Dictionary<string, TextBlock> _afterimageRoleLabels = new();

    public SettingsView()
    {
        InitializeComponent();
        Localization.LanguageChanged += ApplyLanguage;
        Localization.LanguageChanged += ApplyFlashKeyLanguage;
        ApplyLanguage();
        ApplyFlashKeyLanguage();
    }

    public void Attach(AppComposition composition)
    {
        _composition = composition;
        LoadFromConfig();
        UpdateFeatureVisibility();
    }

    /// <summary>Hide switches for features this build or the remote flags have turned off, so the
    /// page only offers what will actually do something. The minimap-vision cards are compiled
    /// out of the public (LIGHT) build; recall / lane-return follow <c>flags.json</c>, which can
    /// arrive after startup — hence re-run whenever the overlay page is opened.</summary>
    private void UpdateFeatureVisibility()
    {
        if (_composition is null) return;
#if LIGHT
        EnemyPresenceCard.Visibility = Visibility.Collapsed;
        AdvancedCard.Visibility = Visibility.Collapsed;
        NotificationRow.Visibility = Visibility.Collapsed;
#endif
        LaneReturnSection.Visibility = _composition.IsFeatureForcedOff("laneReturn")
            ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Sub-navigation ──────────────────────────────────────────────────────

    private void Pill_Checked(object sender, RoutedEventArgs e)
    {
        // Guards the initial IsChecked=True firing before InitializeComponent wires the panels.
        if (GeneralPanel is null) return;

        GeneralPanel.Visibility = PillGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OverlayPanel.Visibility = PillOverlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (PillOverlay.IsChecked == true) UpdateFeatureVisibility();
    }

    // ── Initial load from config ────────────────────────────────────────────

    private void LoadFromConfig()
    {
        if (_composition is null) return;
        var cfg = _composition.Config;

        _syncing = true;
        try
        {
            SyncSelectionToCurrentLanguage();

            MonitorBox.Text = GetInt(cfg, "overlay.targetMonitor", 0).ToString(CultureInfo.InvariantCulture);

            OpacitySlider.Value = GetDouble(cfg, "overlay.opacity", 1.0);
            ScaleSlider.Value = GetDouble(cfg, "overlay.scale", 1.0);
            MovableToggle.IsChecked = GetBool(cfg, "overlay.movable", false);

            // Rune auto-apply (default TRUE — equipped-rune reflection is the intended default).
            RuneAutoApplyToggle.IsChecked = GetBool(cfg, "runes.autoApply", true);
            // M39 telemetry (default TRUE — first-run disclosure banner owns the initial notice).
            TelemetryToggle.IsChecked = GetBool(cfg, "telemetry.enabled", true);
            // Start with League (default FALSE — never auto-launch without an explicit opt-in).
            AutostartToggle.IsChecked = GetBool(cfg, "general.autoStartWithGame", false);
            // Flash key (champSelect.flashKey): unset -> neither radio checked (the first-run
            // banner owns the initial ask); user can change it here anytime.
            var flashKey = cfg.Get("champSelect.flashKey") as string;
            FlashKeyDRadio.IsChecked = string.Equals(flashKey, "D", StringComparison.OrdinalIgnoreCase);
            FlashKeyFRadio.IsChecked = string.Equals(flashKey, "F", StringComparison.OrdinalIgnoreCase);

            _toggleHotkey = GetString(cfg, "overlay.toggleHotkey", DefaultToggleHotkey);
            HotkeyButton.Content = _toggleHotkey;

            _skillOverlayHotkey = GetString(cfg, "overlay.skillOverlayHotkey", DefaultSkillOverlayHotkey);
            SkillHotkeyButton.Content = _skillOverlayHotkey;

            _comboOverlayHotkey = GetString(cfg, "overlay.comboOverlayHotkey", DefaultComboOverlayHotkey);
            ComboHotkeyButton.Content = _comboOverlayHotkey;

            _goldHotkey = GetString(cfg, "overlay.globalGoldHotkey", DefaultGoldHotkey);
            GoldHotkeyButton.Content = _goldHotkey;

            _nextItemHotkey = GetString(cfg, "overlay.nextItemHotkey", DefaultNextItemHotkey);
            NextItemHotkeyButton.Content = _nextItemHotkey;
            var nextItemMode = GetString(cfg, "overlay.nextItemMode", "damage");
            foreach (var obj in NextItemModeCombo.Items)
                if (obj is ComboBoxItem { Tag: string modeTag } && string.Equals(modeTag, nextItemMode, StringComparison.OrdinalIgnoreCase))
                    NextItemModeCombo.SelectedItem = obj;
            // Combos are saved elsewhere; re-check the overlap whenever Settings is opened.
            RefreshHotkeyWarnings();
            IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) RefreshHotkeyWarnings(); };

            // Both structure timers default ENABLED (ConfigSchema.InhibitorTimers /
            // .NexusTurretTimers, and AppComposition reads them with default true). Reading
            // false here made a fresh install show the switch OFF while the timers were on.
            // ON when EITHER is on, so a half-on state is visible and can be switched fully off.
            InhibToggle.IsChecked = GetBool(cfg, "overlay.items.inhibitorTimers.enabled", true)
                                 || GetBool(cfg, "overlay.items.nexusTurretTimers.enabled", true);
            MinimapCalibrateToggle.IsChecked = GetBool(cfg, "overlay.minimapCalibrate", false);
            MinimapDebugToggle.IsChecked = GetBool(cfg, "minimap.debugCapture", false);
            GoldToggle.IsChecked = GetBool(cfg, "overlay.items.globalGold.enabled", false);

            // (loop 130) select the saved lane (default "auto") in the return-timer lane dropdown.
            var laneReturnLane = GetString(cfg, "overlay.laneReturnLane", "auto");
            foreach (var obj in LaneReturnLaneCombo.Items)
                if (obj is ComboBoxItem { Tag: string laneTag }
                    && string.Equals(laneTag, laneReturnLane, StringComparison.OrdinalIgnoreCase))
                {
                    LaneReturnLaneCombo.SelectedItem = obj;
                    break;
                }

            // (loop 141) select the saved return-timer mode (default "all").
            var laneReturnMode = GetString(cfg, "overlay.laneReturnMode", "all");
            foreach (var obj in LaneReturnModeCombo.Items)
                if (obj is ComboBoxItem { Tag: string modeTag }
                    && string.Equals(modeTag, laneReturnMode, StringComparison.OrdinalIgnoreCase))
                {
                    LaneReturnModeCombo.SelectedItem = obj;
                    break;
                }

            // (user request) timer display format: seconds (default) vs M:SS.
            TimerMmSsToggle.IsChecked = GetBool(cfg, "overlay.timerFormatMmSs", false);

            // (M31 §B/§C) Enemy presence. Defaults mirror ConfigSchema so an untouched config shows
            // the same state the engine actually uses.
            EnemyVoiceToggle.IsChecked = string.Equals(
                GetString(cfg, "voice.enemyVoicePack", "prerecorded"), "prerecorded",
                System.StringComparison.OrdinalIgnoreCase);
            var voiceDetail = GetString(cfg, "voice.enemyVoiceDetail", "simple");
            foreach (var obj in EnemyVoiceDetailCombo.Items)
                if (obj is ComboBoxItem it && (it.Tag as string) == voiceDetail)
                    EnemyVoiceDetailCombo.SelectedItem = obj;

            EnemyVoiceVolumeSlider.Value = GetDouble(cfg, "voice.enemyVoiceVolume", 1.0);
            EnemyVoiceVolumeValue.Text = FormatPercent(EnemyVoiceVolumeSlider.Value);

            AfterimageToggle.IsChecked = GetBool(cfg, "minimap.afterimage.enabled", true);
            AfterimageOpacitySlider.Value = GetDouble(cfg, "minimap.afterimage.opacity", 0.75);
            AfterimageOpacityValue.Text = FormatPercent(AfterimageOpacitySlider.Value);
            EnsureAfterimageRoleToggles();
            foreach (var (key, _, _) in AfterimageRoles)
                // Absent role key = enabled, matching OverlayHost.AfterimageRoleEnabled's default.
                _afterimageRoleToggles[key].IsChecked = GetBool(cfg, "minimap.afterimage.roles." + key, true);

            // M02 pending-change #1: core HUD element toggles, opt-OUT (default true).
            ComboResultToggle.IsChecked = GetBool(cfg, "overlay.items.comboResult.enabled", true);
            CompactCardsToggle.IsChecked = GetBool(cfg, "overlay.compactCards", false);
            ItemAlertToggle.IsChecked = GetBool(cfg, "overlay.items.itemAlert.enabled", true);
            NotificationToggle.IsChecked = GetBool(cfg, "overlay.items.notification.enabled", true);
            StatusCardToggle.IsChecked = GetBool(cfg, "overlay.items.statusCard.enabled", true);
            ThreatCardToggle.IsChecked = GetBool(cfg, "overlay.items.threatCard.enabled", true);
            NextItemCardToggle.IsChecked = GetBool(cfg, "overlay.items.nextItemCard.enabled", true);
        }
        finally
        {
            _syncing = false;
        }

        UpdateOpacityValueLabel();
        UpdateScaleValueLabel();
    }

    // ── Language ────────────────────────────────────────────────────────────

    private void SyncSelectionToCurrentLanguage()
    {
        var wasSyncing = _syncing;
        _syncing = true;
        LanguageCombo.SelectedIndex = Localization.CurrentLanguage == Localization.Lang.En ? 1 : 0;
        _syncing = wasSyncing;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code }) return;

        var lang = Localization.Parse(code);
        _composition?.Config.Set("general.language", Localization.ToCode(lang));
        Localization.SetLanguage(lang); // raises LanguageChanged → all views re-localize live
    }

    // ── General: target monitor ─────────────────────────────────────────────

    private void MonitorBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Digits only (monitor index).
        foreach (var ch in e.Text)
            if (!char.IsDigit(ch)) { e.Handled = true; return; }
    }

    private void MonitorBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        if (!int.TryParse(MonitorBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || idx < 0)
        {
            idx = 0;
            MonitorBox.Text = "0";
        }
        _composition.Config.Set("overlay.targetMonitor", idx);
    }

    // ── Overlay: opacity ────────────────────────────────────────────────────

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityValueLabel();
        if (_syncing || _composition is null) return;
        _composition.Config.Set("overlay.opacity", OpacitySlider.Value);
    }

    private void UpdateOpacityValueLabel()
    {
        if (OpacityValue is not null)
            OpacityValue.Text = ((int)System.Math.Round(OpacitySlider.Value * 100)) + "%";
    }

    // ── Overlay: size / scale ───────────────────────────────────────────────

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateScaleValueLabel();
        if (_syncing || _composition is null) return;
        _composition.Config.Set("overlay.scale", ScaleSlider.Value);
    }

    private void UpdateScaleValueLabel()
    {
        if (ScaleValue is not null)
            ScaleValue.Text = ScaleSlider.Value.ToString("0.00", CultureInfo.InvariantCulture) + "x";
    }

    // ── Overlay: movable ────────────────────────────────────────────────────

    private void MovableToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        // Persist only — MainWindow reacts live via ConfigManager.OnChange (click-through), and
        // OverlayHost reacts to the same flag for per-element dragging (M02 pending-change #1).
        _composition.Config.Set("overlay.movable", MovableToggle.IsChecked == true);
    }

    // ── Runes: auto-apply equipped runes ────────────────────────────────────

    private void RuneAutoApplyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        // Persist only — ComboRunner re-reads runes.autoApply live on every combo trigger, so the
        // change takes effect without restarting the runner or the app.
        _composition.Config.Set("runes.autoApply", RuneAutoApplyToggle.IsChecked == true);
    }

    // ── Start with League ───────────────────────────────────────────────────

    /// <summary>HomeWindow follows the key (Run entry + tray), so this only writes it.</summary>
    private void AutostartToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        _composition.Config.Set("general.autoStartWithGame", AutostartToggle.IsChecked == true);
    }

    // ── M39: anonymous telemetry + crash reports ────────────────────────────

    private void TelemetryToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        bool on = TelemetryToggle.IsChecked == true;
        _composition.Config.Set("telemetry.enabled", on);
        // Off must bite THIS session (unhook the crash sink), not just the next launch. On only
        // re-hooks: the service snapshots its enabled flag at construction, so a session that
        // STARTED disabled stays inert until restart (RecordCrash no-ops on IsConfigured) — the
        // opt-out direction is the one that may not wait.
        App.CrashSink = on ? _composition.Telemetry.RecordCrash : null;
    }

    private void FlashKey_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_composition is null) return;
        _composition.Config.Set("champSelect.flashKey", FlashKeyFRadio.IsChecked == true ? "F" : "D");
    }

    // ── Overlay: item enable toggles (persist only; engines land later) ──────

    /// <summary>One switch, both keys. The caption has always read "Inhibitor 5:00 · Nexus
    /// turret 3:00", but only the inhibitor key was written, so turning "Structure Timers" off
    /// left the nexus-turret countdown on the minimap with nothing in the UI to remove it.</summary>
    private void InhibToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool on = InhibToggle.IsChecked == true;
        SetItemEnabled("overlay.items.inhibitorTimers.enabled", on);
        SetItemEnabled("overlay.items.nexusTurretTimers.enabled", on);
    }

    /// <summary>Minimap-position calibration MODE (default off). When on + movable, OverlayHost draws the
    /// draggable/resizable minimap box; otherwise the timers use the auto (HUD-formula) rect. Persist only —
    /// OverlayHost re-reads overlay.minimapCalibrate live each frame.</summary>
    private void MinimapCalibrateToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        _composition.Config.Set("overlay.minimapCalibrate", MinimapCalibrateToggle.IsChecked == true);
    }

    private void GoldToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.globalGold.enabled", GoldToggle.IsChecked == true);

    // ── Advanced section (collapsed by default) — mirrors ComboSettingsView's fold pattern ──
    private void AdvancedHeader_Click(object sender, MouseButtonEventArgs e)
    {
        bool show = AdvancedSection.Visibility != Visibility.Visible;
        AdvancedSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AdvancedChevron.Text = show ? "▾" : "▸";
    }

    // (loop 130) My lane for the same-lane enemy return timer's travel distance.
    private void LaneReturnLaneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        if (LaneReturnLaneCombo.SelectedItem is not ComboBoxItem { Tag: string lane }) return;
        _composition.Config.Set("overlay.laneReturnLane", lane);
    }

    // (loop 141) Which enemy deaths get a return timer (all / designated / same-lane).
    private void LaneReturnModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        if (LaneReturnModeCombo.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        _composition.Config.Set("overlay.laneReturnMode", mode);
    }

    // (user request) Timer display format toggle (seconds vs M:SS) — read live by OverlayHost.FormatTimerSeconds.
    private void TimerMmSsToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.timerFormatMmSs", TimerMmSsToggle.IsChecked == true);

    // ── M31 §B/§C: enemy-presence voice + minimap afterimage ──────────────────────────

    /// <summary>Writes the pack name rather than a bool: <c>EnemyVoicePlayer</c> gates on
    /// <c>pack == "prerecorded"</c>, so anything else silences it.</summary>
    private void EnemyVoiceToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        _composition.Config.Set("voice.enemyVoicePack",
            EnemyVoiceToggle.IsChecked == true ? "prerecorded" : "off");
    }

    private void EnemyVoiceDetailCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        if (EnemyVoiceDetailCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            _composition.Config.Set("voice.enemyVoiceDetail", tag);
    }

    private void EnemyVoiceVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EnemyVoiceVolumeValue is not null)
            EnemyVoiceVolumeValue.Text = FormatPercent(EnemyVoiceVolumeSlider.Value);
        if (_syncing || _composition is null) return;
        _composition.Config.Set("voice.enemyVoiceVolume", EnemyVoiceVolumeSlider.Value);
    }

    /// <summary>The hotkey is only registered while this is on, so a rebind pass is needed after
    /// flipping it — otherwise turning it on mid-session would leave the dump key dead.</summary>
    private void MinimapDebugToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        _composition.Config.Set("minimap.debugCapture", MinimapDebugToggle.IsChecked == true);
        _composition.RegisterOverlayCardHotkeys();
    }

    private void AfterimageToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("minimap.afterimage.enabled", AfterimageToggle.IsChecked == true);

    private void AfterimageOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AfterimageOpacityValue is not null)
            AfterimageOpacityValue.Text = FormatPercent(AfterimageOpacitySlider.Value);
        if (_syncing || _composition is null) return;
        _composition.Config.Set("minimap.afterimage.opacity", AfterimageOpacitySlider.Value);
    }

    private static string FormatPercent(double v)
        => ((int)System.Math.Round(v * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>Builds the five per-role toggles once. Done in code rather than XAML so the role list
    /// stays a single source of truth with <see cref="AfterimageRoles"/> — adding a role means editing
    /// one array, not one array plus five blocks of markup.</summary>
    private void EnsureAfterimageRoleToggles()
    {
        if (_afterimageRoleToggles.Count > 0) return;
        foreach (var (key, _, _) in AfterimageRoles)
        {
            var label = new TextBlock
            {
                Style = (Style)FindResource("FieldLabel"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            var toggle = new ToggleButton
            {
                Style = (Style)FindResource("ToggleSwitch"),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = key,
            };
            toggle.Checked += AfterimageRoleToggle_Changed;
            toggle.Unchecked += AfterimageRoleToggle_Changed;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 18, 8),
            };
            row.Children.Add(label);
            row.Children.Add(toggle);
            AfterimageRolesPanel.Children.Add(row);

            _afterimageRoleToggles[key] = toggle;
            _afterimageRoleLabels[key] = label;
        }
    }

    private void AfterimageRoleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        if (sender is ToggleButton { Tag: string key } t)
            _composition.Config.Set("minimap.afterimage.roles." + key, t.IsChecked == true);
    }

    private void SetItemEnabled(string key, bool enabled)
    {
        if (_syncing || _composition is null) return;
        _composition.Config.Set(key, enabled);
    }

    // ── M02 pending-change #1: core HUD element enable toggles (opt-out, default on) ──

    private void CompactCardsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        _composition.Config.Set("overlay.compactCards", CompactCardsToggle.IsChecked == true);
    }

    private void ComboResultToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.comboResult.enabled", ComboResultToggle.IsChecked == true);

    private void ItemAlertToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.itemAlert.enabled", ItemAlertToggle.IsChecked == true);


    private void NotificationToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.notification.enabled", NotificationToggle.IsChecked == true);

    private void StatusCardToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.statusCard.enabled", StatusCardToggle.IsChecked == true);

    private void ThreatCardToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.threatCard.enabled", ThreatCardToggle.IsChecked == true);

    private void NextItemCardToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.nextItemCard.enabled", NextItemCardToggle.IsChecked == true);

    private void NextItemModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _composition is null) return;
        if (NextItemModeCombo.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        _composition.Config.Set("overlay.nextItemMode", mode);
    }

    // ── Overlay hotkey capture (reuses the combo editor's chord pattern) ──
    // (M46, user request) A chord may be up to three keys (CTRL+SHIFT+1, ALT+Q+W, Q+W+E): each button collects the keys
    // pressed together and takes the chord when one of them is released or the third goes down. Esc cancels.

    private void BeginCapture(Button button)
    {
        _chordCapture.Reset();
        button.Content = SL("단축키를 입력해주세요…", "Press a key combination…");
        Keyboard.Focus(button);
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        BeginCapture(HotkeyButton);
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        if (IsCancelKey(e)) { _capturingHotkey = false; HotkeyButton.Content = _toggleHotkey; return; }
        if (TryCaptureChord(e, out string chord)) CommitToggleHotkey(chord);
    }

    private void HotkeyButton_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        if (TryFinishChord(e, out string chord)) CommitToggleHotkey(chord);
    }

    private void CommitToggleHotkey(string chord)
    {
        if (RejectChord(chord, "toggle", HotkeyWarn)) return;

        _toggleHotkey = chord;
        _capturingHotkey = false;
        HotkeyButton.Content = _toggleHotkey;

        // Persist + re-register against the live M13 registry (no-op if hotkeys not wired yet).
        _composition?.Config.Set("overlay.toggleHotkey", _toggleHotkey);
        _composition?.RegisterOverlayToggleHotkey();
        RefreshHotkeyWarnings();
    }

    private void HotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        HotkeyButton.Content = _toggleHotkey;
    }

    // ── Overlay: skill-damage overlay hotkey capture (§40, same chord pattern as the toggle) ──

    private void SkillHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingSkillHotkey = true;
        BeginCapture(SkillHotkeyButton);
    }

    private void SkillHotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingSkillHotkey) return;
        e.Handled = true;
        if (IsCancelKey(e)) { _capturingSkillHotkey = false; SkillHotkeyButton.Content = _skillOverlayHotkey; return; }
        if (TryCaptureChord(e, out string chord)) CommitSkillHotkey(chord);
    }

    private void SkillHotkeyButton_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturingSkillHotkey) return;
        e.Handled = true;
        if (TryFinishChord(e, out string chord)) CommitSkillHotkey(chord);
    }

    private void CommitSkillHotkey(string chord)
    {
        if (RejectChord(chord, "skill", SkillHotkeyWarn)) return;

        _skillOverlayHotkey = chord;
        _capturingSkillHotkey = false;
        SkillHotkeyButton.Content = _skillOverlayHotkey;

        // Persist + re-register the §40 overlay-card hotkeys against the live M13 registry.
        _composition?.Config.Set("overlay.skillOverlayHotkey", _skillOverlayHotkey);
        _composition?.RegisterOverlayCardHotkeys();
        RefreshHotkeyWarnings();
    }

    private void SkillHotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturingSkillHotkey) return;
        _capturingSkillHotkey = false;
        SkillHotkeyButton.Content = _skillOverlayHotkey;
    }

    // ── Overlay: global gold panel key (2026-10-20, user request) ──

    private void GoldHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingGoldHotkey = true;
        BeginCapture(GoldHotkeyButton);
    }

    private void GoldHotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingGoldHotkey) return;
        e.Handled = true;
        if (IsCancelKey(e)) { _capturingGoldHotkey = false; GoldHotkeyButton.Content = _goldHotkey; return; }
        if (TryCaptureChord(e, out string chord)) CommitGoldHotkey(chord);
    }

    private void GoldHotkeyButton_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturingGoldHotkey) return;
        e.Handled = true;
        if (TryFinishChord(e, out string chord)) CommitGoldHotkey(chord);
    }

    private void CommitGoldHotkey(string chord)
    {
        if (RejectChord(chord, "gold", GoldHotkeyWarn)) return;

        _goldHotkey = chord;
        _capturingGoldHotkey = false;
        GoldHotkeyButton.Content = _goldHotkey;

        _composition?.Config.Set("overlay.globalGoldHotkey", _goldHotkey);
        _composition?.RegisterOverlayCardHotkeys();
        RefreshHotkeyWarnings();
    }

    private void GoldHotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturingGoldHotkey) return;
        _capturingGoldHotkey = false;
        GoldHotkeyButton.Content = _goldHotkey;
    }

    // ── Overlay: next-item card key (M46, user request; default P = League's shop key) ──

    private void NextItemHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingNextItemHotkey = true;
        BeginCapture(NextItemHotkeyButton);
    }

    private void NextItemHotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingNextItemHotkey) return;
        e.Handled = true;
        if (IsCancelKey(e)) { _capturingNextItemHotkey = false; NextItemHotkeyButton.Content = _nextItemHotkey; return; }
        if (TryCaptureChord(e, out string chord)) CommitNextItemHotkey(chord);
    }

    private void NextItemHotkeyButton_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturingNextItemHotkey) return;
        e.Handled = true;
        if (TryFinishChord(e, out string chord)) CommitNextItemHotkey(chord);
    }

    private void CommitNextItemHotkey(string chord)
    {
        if (RejectChord(chord, "nextitem", NextItemHotkeyWarn)) return;

        _nextItemHotkey = chord;
        _capturingNextItemHotkey = false;
        NextItemHotkeyButton.Content = _nextItemHotkey;

        _composition?.Config.Set("overlay.nextItemHotkey", _nextItemHotkey);
        _composition?.RegisterOverlayCardHotkeys();
        RefreshHotkeyWarnings();
    }

    private void NextItemHotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturingNextItemHotkey) return;
        _capturingNextItemHotkey = false;
        NextItemHotkeyButton.Content = _nextItemHotkey;
    }

    /// <summary>The key-down half of a capture, shared by every hotkey button: a lone modifier and a key M13's parser
    /// cannot name are skipped; any other key joins the chord. Returns true (and the chord) once the chord holds
    /// <see cref="Overlay.Core.Hotkeys.HotkeyCombo.MaxKeys"/> non-modifier keys (modifiers do not count); otherwise
    /// the chord waits for a key-up.</summary>
    private bool TryCaptureChord(KeyEventArgs e, out string chord)
    {
        chord = "";
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key) || KeyToToken(key) is not { } token) return false;
        if (_chordCapture.OnKeyDown(ToHotkeyModifiers(Keyboard.Modifiers), token) is not { } done) return false;
        chord = done;
        return true;
    }

    /// <summary>The key-up half: releasing one of the chord's keys completes it ("CTRL+ALT+X", "Q+W").</summary>
    private bool TryFinishChord(KeyEventArgs e, out string chord)
    {
        chord = "";
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key) || KeyToToken(key) is not { } token) return false;
        if (_chordCapture.OnKeyUp(token) is not { } done) return false;
        chord = done;
        return true;
    }

    private static Overlay.Core.Hotkeys.HotkeyModifiers ToHotkeyModifiers(ModifierKeys mods)
    {
        var result = Overlay.Core.Hotkeys.HotkeyModifiers.None;
        if (mods.HasFlag(ModifierKeys.Control)) result |= Overlay.Core.Hotkeys.HotkeyModifiers.Control;
        if (mods.HasFlag(ModifierKeys.Alt)) result |= Overlay.Core.Hotkeys.HotkeyModifiers.Alt;
        if (mods.HasFlag(ModifierKeys.Shift)) result |= Overlay.Core.Hotkeys.HotkeyModifiers.Shift;
        if (mods.HasFlag(ModifierKeys.Windows)) result |= Overlay.Core.Hotkeys.HotkeyModifiers.Win;
        return result;
    }

    // ── Overlay: combo-overlay hotkey capture (§40 added the skill one and left this as a note) ──

    private void ComboHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingComboHotkey = true;
        BeginCapture(ComboHotkeyButton);
    }

    private void ComboHotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingComboHotkey) return;
        e.Handled = true;
        if (IsCancelKey(e)) { _capturingComboHotkey = false; ComboHotkeyButton.Content = _comboOverlayHotkey; return; }
        if (TryCaptureChord(e, out string chord)) CommitComboHotkey(chord);
    }

    private void ComboHotkeyButton_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturingComboHotkey) return;
        e.Handled = true;
        if (TryFinishChord(e, out string chord)) CommitComboHotkey(chord);
    }

    private void CommitComboHotkey(string chord)
    {
        if (RejectChord(chord, "combo", ComboHotkeyWarn)) return;

        _comboOverlayHotkey = chord;
        _capturingComboHotkey = false;
        ComboHotkeyButton.Content = _comboOverlayHotkey;

        // Same registry call as the skill hotkey — RegisterOverlayCardHotkeys re-reads BOTH keys.
        _composition?.Config.Set("overlay.comboOverlayHotkey", _comboOverlayHotkey);
        _composition?.RegisterOverlayCardHotkeys();
        RefreshHotkeyWarnings();
    }

    // ── Hotkey validation (design pass) ──
    // A chord the registry would refuse used to be saved and shown as if it worked; the failure
    // only reached a Debug log. Now: Esc cancels a capture, a reserved chord or one another overlay
    // key already holds is refused in place, and a key a saved combo holds is flagged — combos
    // register first and win the key, so the overlay toggle on it never fires.

    private static bool IsCancelKey(KeyEventArgs e)
        => (e.Key == Key.System ? e.SystemKey : e.Key) == Key.Escape && Keyboard.Modifiers == ModifierKeys.None;

    private static string Canonical(string chord)
    {
        try { return Overlay.Core.Hotkeys.HotkeyCombo.Parse(chord).Canonical; }
        catch (FormatException) { return chord.ToUpperInvariant(); }
    }

    /// <summary>Shows why <paramref name="chord"/> can't be bound to <paramref name="self"/> and
    /// returns true (the caller keeps capturing), or clears the line and returns false.</summary>
    private bool RejectChord(string chord, string self, TextBlock warn)
    {
        string canonical = Canonical(chord);
        string? problem = null;
        if (Overlay.Core.Hotkeys.HotkeyRegistry.ReservedCombos.Contains(canonical))
            problem = SL("{0}은(는) Windows가 쓰는 키라 지정할 수 없어요. 다른 키를 눌러 주세요.",
                         "{0} is reserved by Windows. Press a different key.");
        else if ((self != "toggle" && Canonical(_toggleHotkey) == canonical)
                 || (self != "combo" && Canonical(_comboOverlayHotkey) == canonical)
                 || (self != "skill" && Canonical(_skillOverlayHotkey) == canonical)
                 || (self != "gold" && Canonical(_goldHotkey) == canonical)
                 || (self != "nextitem" && Canonical(_nextItemHotkey) == canonical))
            problem = SL("{0}은(는) 다른 오버레이 단축키가 쓰고 있어요. 다른 키를 눌러 주세요.",
                         "{0} is already another overlay hotkey. Press a different key.");
        if (problem is null) return false;
        warn.Text = string.Format(problem, chord);
        warn.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>Flags each overlay hotkey that a saved combo also uses (any champion).</summary>
    private void RefreshHotkeyWarnings()
    {
        var comboKeys = new HashSet<string>(StringComparer.Ordinal);
        if (_composition?.Config.Get("hotkeys.comboSlots") is IDictionary<string, object?> slots)
            foreach (var (key, value) in slots)
                if (!string.IsNullOrWhiteSpace(value?.ToString()))
                    comboKeys.Add(Canonical(Overlay.Core.Combo.ComboEditor.SplitSlotKey(key).Item1));

        void Apply(TextBlock warn, string chord)
        {
            bool shadowed = comboKeys.Contains(Canonical(chord));
            warn.Text = shadowed
                ? string.Format(SL("지금은 콤보가 {0}을(를) 쓰고 있어서 이 단축키는 동작하지 않아요. 다른 키로 바꾸세요.",
                                   "A saved combo uses {0}, so this hotkey never fires. Pick a different key."), chord)
                : "";
            warn.Visibility = shadowed ? Visibility.Visible : Visibility.Collapsed;
        }
        Apply(HotkeyWarn, _toggleHotkey);
        Apply(ComboHotkeyWarn, _comboOverlayHotkey);
        Apply(SkillHotkeyWarn, _skillOverlayHotkey);
        Apply(GoldHotkeyWarn, _goldHotkey);
        Apply(NextItemHotkeyWarn, _nextItemHotkey);
    }

    private void ComboHotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturingComboHotkey) return;
        _capturingComboHotkey = false;
        ComboHotkeyButton.Content = _comboOverlayHotkey;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin or Key.System;

    /// <summary>Normalizes a WPF <see cref="Key"/> to the token M13's HotkeyCombo parser
    /// understands. Mirrors the combo editor. Note: the current Win32 hook only maps
    /// digits/letters/F-keys to a virtual-key, so a captured TAB/SPACE/etc. parses fine but is
    /// skipped at OS registration (logged) — the user should pick a letter/F-key combo.</summary>
    private static string? KeyToToken(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return ((int)(key - Key.NumPad0)).ToString();
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.F1 && key <= Key.F24) return key.ToString();
        return key switch
        {
            Key.Space => "SPACE",
            Key.Enter => "ENTER",
            Key.Tab => "TAB",
            Key.OemTilde => "`", // (M46) the backtick key: ALT+` = the skill overlay
            Key.Escape => "ESCAPE",
            Key.Insert => "INSERT",
            Key.Delete => "DELETE",
            Key.Home => "HOME",
            Key.End => "END",
            Key.PageUp => "PAGEUP",
            Key.PageDown => "PAGEDOWN",
            Key.Up => "UP",
            Key.Down => "DOWN",
            Key.Left => "LEFT",
            Key.Right => "RIGHT",
            _ => null,
        };
    }

    // ── Localization (existing shared keys + local ko/en for the new labels) ──

    private void ApplyLanguage()
    {
        LanguageCaption.Text = Localization.L("settings.language");
        LanguageDesc.Text = Localization.L("settings.languageDesc");
        PillGeneral.Content = SL("기본", "General");
        PillOverlay.Content = SL("오버레이", "Overlay");

        AppCaption.Text = SL("앱", "App");
        RuneCaption.Text = SL("룬 · 점멸", "Runes & Flash");
        DisplayCaption.Text = SL("표시", "Display");
        WindowModeNote.Text = SL(
            "오버레이는 롤이 '전체 창 모드(테두리 없음)' 또는 '창 모드'일 때만 게임 위에 보여요. 전체 화면이라면 게임 내 설정(ESC) > 비디오 > 창 모드에서 바꿔 주세요.",
            "The overlay only shows over League in Borderless or Windowed mode. If the game is in Fullscreen, change it in game: Settings (ESC) > Video > Window Mode.");
        HotkeysCaption.Text = SL("단축키", "Hotkeys");
        MonitorLabel.Text = SL("미리보기 모니터", "Preview monitor");
        MonitorHint.Text = SL("게임 창이 없을 때(미리보기) 오버레이를 띄울 모니터입니다. 0 = 주 모니터. 게임 중에는 항상 롤 창을 따라갑니다.",
            "Where the overlay shows when no game window exists (preview). 0 = primary. In game it always follows the League window.");

        AutostartToggleCaption.Text = SL("롤 실행 시 자동 실행", "Start with League");
        AutostartToggleDesc.Text = SL(
            "Windows 시작 시 트레이에서 대기하다가 롤 클라이언트가 켜지면 Fova 창을 띄웁니다. 켜 두면 창을 닫아도 트레이에 남고, 트레이 아이콘 메뉴의 [종료]로 끕니다.",
            "Waits in the tray from Windows sign-in and opens Fova when the League client starts. While on, closing the window keeps Fova in the tray; exit from the tray icon's menu.");

        TelemetryCaption.Text = SL("데이터", "Data");
        TelemetryToggleCaption.Text = SL("익명 사용 통계·오류 리포트", "Anonymous usage stats & crash reports");
        TelemetryToggleDesc.Text = SL(
            "익명 설치 ID·앱 버전·OS 버전, 그리고 오류 발생 시 오류 내용을 보냅니다. 게임/계정 정보는 절대 포함되지 않습니다.",
            "Sends an anonymous install id, app version, OS version, and — on a crash — the error details. Never any game or account data.");

        OpacityCaption.Text = SL("불투명도", "Opacity");
        ScaleCaption.Text = SL("크기", "Size");
        MovableCaption.Text = SL("위치 이동 모드", "Move mode");
        MovableDesc.Text = SL(
            "켜면 Ctrl+Shift를 누른 채 드래그해 오버레이 위치를 옮길 수 있습니다. 키를 안 누른 동안에는 클릭이 게임으로 통과됩니다.",
            "When on, hold Ctrl+Shift and drag to reposition overlay elements. Clicks pass through to the game while the keys are not held.");

        HotkeyCaption.Text = SL("오버레이 전체 표시/숨김", "Show/hide overlay");
        HotkeyDesc.Text = SL(
            "기본값 SHIFT+TAB은 롤 점수판 키와 겹칠 수 있습니다. 버튼을 누른 뒤 새 키를 입력하세요 (최대 3키 조합, 예: CTRL+SHIFT+1, ALT+Q+W).",
            "The default SHIFT+TAB can overlap LoL's scoreboard key. Click, then press the new keys (up to 3 keys, e.g. CTRL+SHIFT+1, ALT+Q+W).");
        if (!_capturingHotkey)
            HotkeyButton.Content = _toggleHotkey;

        SkillHotkeyCaption.Text = SL("스킬 데미지 오버레이", "Skill-damage overlay");
        SkillHotkeyDesc.Text = SL("스킬별 예상 데미지 표시를 켜고 끕니다 (기본값 ALT+`). 같은 키를 콤보에 지정하면 콤보가 우선합니다.",
            "Shows/hides the per-skill damage overlay (default ALT+`). A combo bound to the same key takes precedence.");
        if (!_capturingSkillHotkey)
            SkillHotkeyButton.Content = _skillOverlayHotkey;

        ComboHotkeyCaption.Text = SL("콤보 오버레이", "Combo overlay");
        ComboHotkeyDesc.Text = SL("콤보 예상 데미지 표시를 켜고 끕니다 (기본값 ALT+1). 같은 키를 콤보에 지정하면 콤보가 우선합니다.",
            "Shows/hides the combo damage overlay (default ALT+1). A combo bound to the same key takes precedence.");
        if (!_capturingComboHotkey)
            ComboHotkeyButton.Content = _comboOverlayHotkey;

        GoldHotkeyCaption.Text = SL("글로벌 골드 표시", "Global gold panel");
        GoldHotkeyDesc.Text = SL("기본값 TAB은 롤 점수판을 열 때 함께 보여 줍니다. 다른 키를 지정하면 그 키로 켜고 끕니다 (TAB을 다시 지정하면 원래대로).",
            "The default TAB shows it with League's scoreboard. Set another key to toggle it on its own (set TAB again to go back).");
        if (!_capturingGoldHotkey)
            GoldHotkeyButton.Content = _goldHotkey;

        NextItemHotkeyCaption.Text = SL("다음 아이템 추천 표시", "Next-item card");
        NextItemHotkeyDesc.Text = SL("기본값 P(롤 상점 키): 상점을 열 때 함께 켜지고 닫을 때 꺼집니다. 살아 있어도 지금 빌드 기준으로 추천합니다. 키는 최대 3개까지 조합할 수 있어요.",
            "Default P (League's shop key): shows when you open the shop, hides when you close it, dead or alive. Chords of up to 3 keys.");
        if (!_capturingNextItemHotkey)
            NextItemHotkeyButton.Content = _nextItemHotkey;

        ItemsCaption.Text = SL("오버레이 항목", "Overlay Items");
        ItemsDesc.Text = SL("끄면 게임 화면에서 숨겨집니다. 위치는 '위치 이동 모드'를 켠 뒤 오버레이에서 직접 드래그해 옮깁니다.",
            "Turn off to hide in game. To reposition, turn on Move mode and drag the element on the overlay.");

        InhibCaption.Text = SL("구조물 타이머", "Structure Timers");
        InhibDesc.Text = SL("억제기 5:00 · 넥서스 포탑 3:00 리젠 카운트다운 (미니맵 위)",
            "Inhibitor 5:00 · Nexus turret 3:00 respawn countdown on the minimap");
        AdvancedCaption.Text = SL("고급 설정", "Advanced");
        MinimapCalibrateCaption.Text = SL("미니맵 위치 보정", "Minimap position calibration");
        MinimapCalibrateDesc.Text = SL("평소엔 끄기. 켜고 '위치 이동 모드'를 켠 뒤 Ctrl+Shift를 누른 채 드래그·휠로 미니맵 영역을 맞출 수 있음",
            "Keep off normally. Turn on + enable Move mode, then hold Ctrl+Shift to drag/wheel-align the minimap box");
        MinimapDebugCaption.Text = SL("미니맵 인식 디버그", "Minimap detection debug");
        MinimapDebugDesc.Text = SL(
            "오인식 원인 분석용. 최근 2초(60프레임)를 메모리에 들고 있다가 Alt+3을 누르면 PNG로 저장합니다 (logs/minimap-debug/). 켜면 약 19MB를 더 씁니다. 켠 뒤에는 앱 재시작이 필요합니다.",
            "For diagnosing misdetections. Keeps the last 2s (60 frames) in memory; press Alt+3 to write them as PNGs (logs/minimap-debug/). Costs ~19MB while on. Restart the app after enabling.");
        GoldCaption.Text = SL("글로벌 골드 비교", "Global Gold Compare");
        GoldDesc.Text = SL("게임 중 점수판(Tab)을 열면 함께 뜹니다 · 팀 골드는 추정치",
            "Shows while the in-game scoreboard (Tab) is open · team gold is an estimate");
        LaneReturnLaneCaption.Text = SL("내 라인 (복귀 타이머)", "My lane (return timer)");
        LaneReturnLaneDesc.Text = SL("복귀 타이머의 이동거리 계산용. 대부분 게임은 포지션이 안 잡혀 직접 선택 필요.",
            "Sets travel distance for the enemy return timer. Most games don't report positions — pick manually.");
        LaneReturnModeCaption.Text = SL("복귀 타이머 대상", "Return timer targets");
        LaneReturnModeDesc.Text = SL("어떤 적이 죽었을 때 타이머를 띄울지 선택 (모든 적 / 지정한 적 / 같은 라인).",
            "Which enemy deaths show a return timer (all / designated / same lane).");
        TimerMmSsCaption.Text = SL("타이머 분:초 표기", "Timer as M:SS");
        TimerMmSsDesc.Text = SL("끄면 초 단위(예: 83), 켜면 분:초(예: 1:23)로 표시합니다. 오버레이의 모든 타이머에 적용됩니다.",
            "Off: seconds (e.g. 83). On: M:SS (e.g. 1:23). Applies to every timer on the overlay.");

        // M31 §B/§C: enemy-presence voice + minimap afterimage.
        EnemyPresenceCaption.Text = SL("적 위치 안내", "Enemy Presence");
        EnemyPresenceDesc.Text = SL(
            "미니맵에서 적이 보이거나 사라질 때 음성으로 알려주고, 마지막으로 보인 자리에 초상화를 남깁니다.",
            "Speaks when an enemy appears or vanishes on the minimap, and leaves a portrait where they were last seen.");
        EnemyVoiceCaption.Text = SL("음성 안내", "Voice alerts");
        EnemyVoiceDesc.Text = SL("녹음된 음성 조각을 이어붙여 재생합니다 (예: \"정글 · 적 캠프 · 사라짐\").",
            "Plays prerecorded pieces spliced together (e.g. \"jungle · enemy camp · vanished\").");
        EnemyVoiceDetailCaption.Text = SL("위치 상세도", "Location detail");
        EnemyVoiceDetailDesc.Text = SL(
            "간단은 넓은 구역 이름(\"적 캠프\", \"탑라인\"), 상세는 가장 가까운 캠프·오브젝트 이름(\"적 레드\", \"윗 바위게\")을 말합니다. 라인은 상세에서도 라인 이름 그대로입니다.",
            "Simple names a broad zone (\"enemy camp\", \"top lane\"); detail names the nearest camp or objective (\"enemy red\", \"top scuttle\"). Lanes keep their lane name in either mode.");
        EnemyVoiceDetailSimpleItem.Content = SL("간단", "Simple");
        EnemyVoiceDetailDetailItem.Content = SL("상세", "Detail");
        EnemyVoiceVolumeCaption.Text = SL("음성 음량", "Voice volume");
        AfterimageCaption.Text = SL("미니맵 잔상", "Minimap afterimage");
        AfterimageDesc.Text = SL("적이 시야에서 사라지면 마지막 위치에 흑백 초상화를 남기고, 다시 보이면 지웁니다.",
            "Leaves a grayscale portrait at the last-seen spot when an enemy drops out of vision; clears it when they reappear.");
        AfterimageOpacityCaption.Text = SL("잔상 투명도", "Afterimage opacity");
        AfterimageRolesCaption.Text = SL("잔상 표시 대상", "Show afterimage for");
        AfterimageRolesDesc.Text = SL("역할별로 끌 수 있습니다. 정글만 켜두면 미니맵이 훨씬 깔끔합니다.",
            "Gate it per role — leaving only Jungle on keeps the minimap much cleaner.");
        EnsureAfterimageRoleToggles();
        foreach (var (key, ko, en) in AfterimageRoles)
            _afterimageRoleLabels[key].Text = SL(ko, en);

        // M02 pending-change #1: core HUD element toggles.
        ComboResultCaption.Text = SL("콤보 결과 카드", "Combo Result Card");
        CompactCardsCaption.Text = SL("간단히 보기", "Compact cards");
        CompactCardsDesc.Text = SL(
            "콤보·스킬 카드에서 적 방어력/마법 저항과 체력 게이지를 빼고, 데미지와 스킬 아이콘, 작은 초상화만 보여 줍니다.",
            "Combo and skill cards drop the enemy armor/MR and the HP gauge, keeping damage, skill icons and a small portrait.");
        TargetKeysCaption.Text = SL("대상 바꾸기", "Change target");
        TargetKeysDesc.Text = SL(
            "ALT+F1 · F2 · F3 · F5 · F6 — 초상화 줄 왼쪽부터 1~5번째 적을 콤보 대상으로 고릅니다. ALT+F4는 Windows의 창 닫기 키라(게임 중엔 롤이 꺼짐) 쓰지 않습니다.",
            "ALT+F1 · F2 · F3 · F5 · F6 pick the 1st-5th enemy of the portrait row as the combo target. ALT+F4 is left alone: it is Windows' close-window key and would close League.");
        ItemAlertCaption.Text = SL("내 아이템 완성 알림", "My item completed");
        NotificationCaption.Text = SL("알림", "Notification");
        StatusCardCaption.Text = SL("내 정보 카드", "My Status Card");
        ThreatCardCaption.Text = SL("상대 킬각 위험선", "Enemy Kill Threat");
        NextItemCardCaption.Text = SL("다음 아이템 추천", "Next Item");
        NextItemModeCaption.Text = SL("아이템 추천 기준", "Next-item ranking");
        NextItemModeDesc.Text = SL("딜: 지금 대상에게 내 콤보 데미지가 가장 많이 느는 아이템. 탱커: 적 전원의 풀콤보(추정)를 가장 잘 버티는 아이템 (체력·방어력·마법 저항력 기준, 보호막·회복은 계산 안 됨).",
            "Damage: the items that add the most to my combo on the current target. Tank: the items that best withstand every enemy's full combo (estimated; HP, armor and MR, no shields or healing).");

        SyncSelectionToCurrentLanguage();
    }

    /// <summary>Local ko/en label selector (see class note on localization ownership).</summary>
    private static string SL(string ko, string en)
        => Localization.CurrentLanguage == Localization.Lang.En ? en : ko;

    // ── Config value coercion (M14 returns numbers as double, bools as bool) ──

    private static double GetDouble(Overlay.Core.Config.ConfigManager cfg, string key, double fallback)
        => cfg.Get(key) switch { double d => d, int i => i, _ => fallback };

    private static bool GetBool(Overlay.Core.Config.ConfigManager cfg, string key, bool fallback)
        => cfg.Get(key) is bool b ? b : fallback;

    private static int GetInt(Overlay.Core.Config.ConfigManager cfg, string key, int fallback)
        => cfg.Get(key) switch { double d => (int)d, int i => i, _ => fallback };

    private static string GetString(Overlay.Core.Config.ConfigManager cfg, string key, string fallback)
        => cfg.Get(key) is string s && s.Length > 0 ? s : fallback;

    private void ApplyFlashKeyLanguage()
    {
        FlashKeyCaption.Text = Localization.L("settings.flashKey");
        FlashKeyDesc.Text = Localization.L("settings.flashKey.desc");
        FlashKeyDRadio.Content = Localization.L("settings.flashKey.d");
        FlashKeyFRadio.Content = Localization.L("settings.flashKey.f");
    }
}
