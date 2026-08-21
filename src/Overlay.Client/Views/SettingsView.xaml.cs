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
    private const string DefaultSkillOverlayHotkey = "ALT+2";

    private bool _capturingComboHotkey;
    private string _comboOverlayHotkey = DefaultComboOverlayHotkey;
    private const string DefaultComboOverlayHotkey = "ALT+1";

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
    }

    // ── Sub-navigation ──────────────────────────────────────────────────────

    private void Pill_Checked(object sender, RoutedEventArgs e)
    {
        // Guards the initial IsChecked=True firing before InitializeComponent wires the panels.
        if (GeneralPanel is null) return;

        GeneralPanel.Visibility = PillGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LanguagePanel.Visibility = PillLanguage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OverlayPanel.Visibility = PillOverlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
            ItemAlertToggle.IsChecked = GetBool(cfg, "overlay.items.itemAlert.enabled", true);
            RecallTimerToggle.IsChecked = GetBool(cfg, "overlay.items.recallTimer.enabled", true);
            NotificationToggle.IsChecked = GetBool(cfg, "overlay.items.notification.enabled", true);
            StatusCardToggle.IsChecked = GetBool(cfg, "overlay.items.statusCard.enabled", true);
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
                Style = (Style)FindResource("BodyText"),
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

    private void ComboResultToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.comboResult.enabled", ComboResultToggle.IsChecked == true);

    private void ItemAlertToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.itemAlert.enabled", ItemAlertToggle.IsChecked == true);

    private void RecallTimerToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.recallTimer.enabled", RecallTimerToggle.IsChecked == true);

    private void NotificationToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.notification.enabled", NotificationToggle.IsChecked == true);

    private void StatusCardToggle_Changed(object sender, RoutedEventArgs e)
        => SetItemEnabled("overlay.items.statusCard.enabled", StatusCardToggle.IsChecked == true);

    // ── Overlay: toggle-hotkey capture (reuses the combo editor's chord pattern) ──

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyButton.Content = SL("단축키를 입력해주세요…", "Press a key combination…");
        Keyboard.Focus(HotkeyButton);
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        if (!TryCaptureChord(e, out string chord)) return;

        _toggleHotkey = chord;
        _capturingHotkey = false;
        HotkeyButton.Content = _toggleHotkey;

        // Persist + re-register against the live M13 registry (no-op if hotkeys not wired yet).
        _composition?.Config.Set("overlay.toggleHotkey", _toggleHotkey);
        _composition?.RegisterOverlayToggleHotkey();
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
        SkillHotkeyButton.Content = SL("단축키를 입력해주세요…", "Press a key combination…");
        Keyboard.Focus(SkillHotkeyButton);
    }

    private void SkillHotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingSkillHotkey) return;
        e.Handled = true;
        if (!TryCaptureChord(e, out string chord)) return;

        _skillOverlayHotkey = chord;
        _capturingSkillHotkey = false;
        SkillHotkeyButton.Content = _skillOverlayHotkey;

        // Persist + re-register the §40 overlay-card hotkeys against the live M13 registry.
        _composition?.Config.Set("overlay.skillOverlayHotkey", _skillOverlayHotkey);
        _composition?.RegisterOverlayCardHotkeys();
    }

    private void SkillHotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturingSkillHotkey) return;
        _capturingSkillHotkey = false;
        SkillHotkeyButton.Content = _skillOverlayHotkey;
    }

    /// <summary>The chord half of a capture handler, identical for all three hotkey buttons: skip
    /// lone modifiers, skip keys M13's parser cannot name, otherwise build "CTRL+ALT+X".</summary>
    /// <returns>false while still waiting for a usable key — the caller stays in capture mode.</returns>
    private static bool TryCaptureChord(KeyEventArgs e, out string chord)
    {
        chord = "";
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key)) return false;

        var token = KeyToToken(key);
        if (token is null) return false;

        var parts = new List<string>(5);
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("CTRL");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("ALT");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("SHIFT");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("WIN");
        parts.Add(token);
        chord = string.Join("+", parts);
        return true;
    }

    // ── Overlay: combo-overlay hotkey capture (§40 added the skill one and left this as a note) ──

    private void ComboHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingComboHotkey = true;
        ComboHotkeyButton.Content = SL("단축키를 입력해주세요…", "Press a key combination…");
        Keyboard.Focus(ComboHotkeyButton);
    }

    private void ComboHotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingComboHotkey) return;
        e.Handled = true;
        if (!TryCaptureChord(e, out string chord)) return;

        _comboOverlayHotkey = chord;
        _capturingComboHotkey = false;
        ComboHotkeyButton.Content = _comboOverlayHotkey;

        // Same registry call as the skill hotkey — RegisterOverlayCardHotkeys re-reads BOTH keys.
        _composition?.Config.Set("overlay.comboOverlayHotkey", _comboOverlayHotkey);
        _composition?.RegisterOverlayCardHotkeys();
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
        TitleLabel.Text = Localization.L("settings.title");
        LanguageCaption.Text = Localization.L("settings.language");
        LanguageDesc.Text = Localization.L("settings.languageDesc");
        PillGeneral.Content = SL("기본", "General");
        PillLanguage.Content = SL("언어", "Language");
        PillOverlay.Content = SL("오버레이", "Overlay");

        GeneralCaption.Text = SL("기본", "General");
        MonitorDesc.Text = SL("앱 표시 관련 기본값입니다.", "App-level display defaults.");
        MonitorLabel.Text = SL("타겟 모니터 (0 = 주 모니터)", "Target monitor (0 = primary)");

        OpacityCaption.Text = SL("불투명도", "Opacity");
        ScaleCaption.Text = SL("크기", "Size");
        MovableCaption.Text = SL("이동 여부", "Movable");
        MovableDesc.Text = SL(
            "켜면 오버레이를 드래그해 위치를 옮길 수 있습니다 (클릭 통과 해제). 끄면 클릭 통과 상태로 고정됩니다.",
            "When on, drag the overlay to reposition it (click-through off). When off, it stays click-through.");

        HotkeyCaption.Text = SL("오버레이 표시 단축키", "Overlay toggle hotkey");
        HotkeyDesc.Text = SL(
            "오버레이를 켜고 끄는 전역 단축키입니다. 기본값 SHIFT+TAB 은 롤 점수판과 겹칠 수 있어 변경할 수 있습니다.",
            "Global hotkey to show/hide the overlay. The default SHIFT+TAB can overlap LoL's scoreboard, so it is rebindable.");
        if (!_capturingHotkey)
            HotkeyButton.Content = _toggleHotkey;

        SkillHotkeyCaption.Text = SL("스킬 데미지 오버레이 단축키", "Skill-damage overlay hotkey");
        SkillHotkeyDesc.Text = SL(
            "스킬별 데미지 오버레이를 켜고 끄는 전역 단축키입니다 (기본값 ALT+2).",
            "Global hotkey to show/hide the per-skill damage overlay (default ALT+2).");
        if (!_capturingSkillHotkey)
            SkillHotkeyButton.Content = _skillOverlayHotkey;

        ComboHotkeyCaption.Text = SL("콤보 오버레이 단축키", "Combo overlay hotkey");
        ComboHotkeyDesc.Text = SL(
            "콤보 결과 오버레이를 켜고 끄는 전역 단축키입니다 (기본값 ALT+1).",
            "Global hotkey to show/hide the combo result overlay (default ALT+1).");
        if (!_capturingComboHotkey)
            ComboHotkeyButton.Content = _comboOverlayHotkey;

        ItemsCaption.Text = SL("오버레이 항목", "Overlay Items");
        ItemsDesc.Text = SL("표시할 항목을 선택합니다 (엔진은 추후 단계에서 연결됩니다).",
            "Choose which items to show (engines land in a later phase).");

        InhibCaption.Text = SL("구조물 타이머", "Structure Timers");
        InhibDesc.Text = SL("억제기 5:00 · 넥서스 포탑 3:00 리젠 카운트다운 (미니맵 위)",
            "Inhibitor 5:00 · Nexus turret 3:00 respawn countdown on the minimap");
        AdvancedCaption.Text = SL("고급 설정", "Advanced");
        MinimapCalibrateCaption.Text = SL("미니맵 위치 보정", "Minimap position calibration");
        MinimapCalibrateDesc.Text = SL("평소엔 끄기. 켜고 '이동 여부'를 켜면 미니맵 영역을 드래그·휠로 맞출 수 있음",
            "Keep off normally. Turn on + enable Movable to drag/wheel-align the minimap box");
        MinimapDebugCaption.Text = SL("미니맵 인식 디버그", "Minimap detection debug");
        MinimapDebugDesc.Text = SL(
            "오인식 원인 분석용. 최근 2초(60프레임)를 메모리에 들고 있다가 Alt+3을 누르면 PNG로 저장합니다 (logs/minimap-debug/). 켜면 약 19MB를 더 씁니다. 켠 뒤에는 앱 재시작이 필요합니다.",
            "For diagnosing misdetections. Keeps the last 2s (60 frames) in memory; press Alt+3 to write them as PNGs (logs/minimap-debug/). Costs ~19MB while on. Restart the app after enabling.");
        GoldCaption.Text = SL("글로벌 골드 비교", "Global Gold Compare");
        GoldDesc.Text = SL("팀 골드 비교 · 추정치 포함(estimate)",
            "Team gold comparison · includes estimate");
        LaneReturnLaneCaption.Text = SL("내 라인 (복귀 타이머)", "My lane (return timer)");
        LaneReturnLaneDesc.Text = SL("복귀 타이머의 이동거리 계산용. 대부분 게임은 포지션이 안 잡혀 직접 선택 필요.",
            "Sets travel distance for the enemy return timer. Most games don't report positions — pick manually.");
        LaneReturnModeCaption.Text = SL("복귀 타이머 대상", "Return timer targets");
        LaneReturnModeDesc.Text = SL("어떤 적이 죽었을 때 타이머를 띄울지 선택 (모든 적 / 지정한 적 / 같은 라인).",
            "Which enemy deaths show a return timer (all / designated / same lane).");
        TimerMmSsCaption.Text = SL("타이머 분:초 표기", "Timer as M:SS");
        TimerMmSsDesc.Text = SL("끄면 초 단위(예: 83), 켜면 분:초(예: 1:23)로 표시합니다. 적 복귀·구조물(억제기/넘서스 포탑) 타이머 공통.",
            "Off: seconds (e.g. 83). On: M:SS (e.g. 1:23). Applies to the enemy-return and structure (inhibitor/Nexus turret) timers.");

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
        CoreItemsCaption.Text = SL("기본 HUD 요소", "Core HUD Elements");
        CoreItemsDesc.Text = SL(
            "항상 표시되던 요소들입니다. 끄면 숨겨지고, 오버레이에서 드래그(이동 여부 켠 상태)로 위치를 각각 옮길 수 있습니다.",
            "Elements that were always shown. Turn off to hide; drag each on the overlay (with \"Movable\" on) to reposition it independently.");
        ComboResultCaption.Text = SL("콤보 결과 카드", "Combo Result Card");
        ItemAlertCaption.Text = SL("아이템 알림", "Item Alert");
        RecallTimerCaption.Text = SL("귀환 타이머", "Recall Timer");
        NotificationCaption.Text = SL("알림", "Notification");
        StatusCardCaption.Text = SL("내 정보 카드", "My Status Card");

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
