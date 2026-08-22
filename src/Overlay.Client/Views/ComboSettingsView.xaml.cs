using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Items;

namespace Overlay.Client.Views;

/// <summary>
/// Combo-settings view: a drag-and-drop builder over the shared M04 <see cref="ComboEditor"/>
/// (create → add nodes → save → bind hotkey), plus a saved-combo list read straight from the
/// shared config.
///
/// <para><b>Drag-and-drop.</b> The palette renders each champion skill as a colored chip
/// (P/Q/W/E/R/AA). Dragging a palette chip DOWN into the sequence zone appends a clone of that
/// node (unique id, so a skill can repeat) at the insertion indicator's position. Dragging a
/// placed chip UP/out of the sequence zone deletes it; dropping it back on the zone reorders it.
/// A translucent <see cref="Popup"/> ghost follows the cursor during the drag and a thin accent
/// bar marks the drop position.</para>
///
/// <para><b>Hotkey capture.</b> "Set Hotkey" enters capture mode ("press a key…"); the next
/// non-modifier key chord (Ctrl/Alt/Shift/Win + key) is captured via PreviewKeyDown, normalized
/// to "ALT+1" / "CTRL+SHIFT+F1", and used as the combo's hotkey on save.</para>
/// </summary>
public partial class ComboSettingsView : UserControl
{
    /// <summary>A sequence/palette entry: the editable <see cref="ComboNode"/> plus the slot
    /// letter it was built from (P/Q/W/E/R/AA), which drives the chip's color/label and is not
    /// recoverable from a cloned node id.</summary>
    private sealed record Chip(ComboNode Node, string Slot);

    /// <summary>In-process drag payload passed through <see cref="DragDrop.DoDragDrop"/>.</summary>
    private sealed record DragPayload(Chip Chip, bool FromSequence);

    /// <summary>In-process drag payload for the item drag-and-hold attach gesture (M04 "Pending
    /// User-Reported Changes" item 3, item half only — <see cref="TryStartItemDrag"/> raises this when
    /// a "hypothetical build" item chip is dragged; a sequence chip's own drop handlers
    /// (<see cref="BuildChip"/>) look for this specific type so an item drag never triggers the
    /// unrelated skill reorder/delete logic <see cref="DragPayload"/> drives.</summary>
    private sealed record ItemDragPayload(string ItemId);

    /// <summary>In-process drag payload for the rune drag-and-hold attach gesture (M04 "Pending
    /// User-Reported Changes" item 3, rune half — <see cref="TryStartRuneDrag"/> raises this when an
    /// attachable rune chip is dragged; a sequence chip's own drop handlers (<see cref="BuildChip"/>)
    /// look for this specific type so a rune drag never triggers the unrelated skill reorder/delete
    /// logic <see cref="DragPayload"/> drives, nor the item-attach logic <see cref="ItemDragPayload"/>
    /// drives.</summary>
    private sealed record RuneDragPayload(string RuneId);

    private AppComposition? _composition;

    /// <summary>Every champion id offered by the picker (task 4/loop-38 item 4), unfiltered — the
    /// full <see cref="AppComposition.ChampionIds"/> roster. <see cref="RebuildChampionTiles"/>
    /// filters this by <see cref="ChampionSearchBox"/>'s text into the visible tile grid.</summary>
    private readonly List<string> _allChampionIds = new();

    /// <summary>The currently selected champion id (replaces a ComboBox's SelectedItem — the picker
    /// is now a search-filtered, capped-scroll icon-tile grid; loop 38 pending items 4/5).</summary>
    private string? _selectedChampionId;

    /// <summary>Champion id → square portrait, loaded lazily per visible tile via
    /// <see cref="DDragonIconProvider"/> and cached for the session so re-filtering never re-fetches
    /// an already-shown portrait.</summary>
    private readonly Dictionary<string, ImageSource> _championPortraits = new(StringComparer.Ordinal);

    /// <summary>The user's in-progress "hypothetical build" item selection (loop 38 pending item 2),
    /// persisted per champion via <see cref="ItemBuildStore"/> (loaded on champion select in
    /// <see cref="SelectChampionAsync"/>, saved on add/remove) — the same store
    /// <c>ComboRunner.BuildAttacker</c> now reads to add this build's AD/AP additively on top of
    /// the attacker's live stats. See M04_COMBO_EDITOR.md's changelog.</summary>
    private readonly List<string> _buildItemIds = new();

    /// <summary>Item id → square icon, loaded lazily via <see cref="DDragonIconProvider"/>.</summary>
    private readonly Dictionary<string, ImageSource> _itemIcons = new(StringComparer.Ordinal);

    /// <summary>The currently displayed palette chips (P/Q/W/E/R/AA for the selected champion),
    /// kept so the palette can be re-rendered when the async ability icons arrive.</summary>
    private readonly List<Chip> _palette = new();

    /// <summary>Slot → real ability icon for the selected champion (task 6). Empty until
    /// <see cref="AbilityIconProvider"/> resolves them; slots missing here fall back to a letter
    /// badge. AA is never present (it uses a sword glyph).</summary>
    private IReadOnlyDictionary<string, ImageSource> _icons =
        new Dictionary<string, ImageSource>(StringComparer.Ordinal);

    /// <summary>The champion whose icons the latest async load is for, so a stale load that finishes
    /// after the user switched champions is ignored.</summary>
    private string? _iconChampionId;

    /// <summary>Semi-transparent backing for the small key-letter badge overlaid on each chip.</summary>
    private static readonly Brush KeyBadgeBrush = Freeze(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>The combo being built, in order. Each palette node is cloned with a unique id
    /// before it enters this list (the palette reuses slot ids P/Q/W/E/R/AA and the engine
    /// rejects duplicate node ids, so cloning lets a skill appear more than once).</summary>
    private readonly List<Chip> _sequence = new();
    private int _nodeCounter;

    // Drag state
    private Point _pressPoint;
    private bool _dragging;
    private Popup? _ghostPopup;
    private Border? _insertIndicator;
    private bool _droppedOnSequence;

    // Item drag-and-hold attach (M04 item 3, item half): a separate press/move pair (distinct from
    // the skill chips' _pressPoint/_dragging above) so dragging a build-item chip doesn't interfere
    // with in-progress skill-chip drag state, plus the hover-hold timer that performs the attach.
    private Point _itemPressPoint;
    private bool _itemDragging;
    private DispatcherTimer? _itemAttachTimer;

    /// <summary>~1 second hold, per the user's gesture spec in M04_COMBO_EDITOR.md item 3.</summary>
    private static readonly TimeSpan ItemAttachHoldDuration = TimeSpan.FromSeconds(1);

    // Rune drag-and-hold attach (M04 item 3, rune half): a separate press/move pair (distinct from the
    // skill and item chips' state above) so dragging a rune chip doesn't interfere with those, plus the
    // hover-hold timer that performs the attach. Reuses ItemAttachHoldDuration (same ~1s gesture).
    private Point _runePressPoint;
    private bool _runeDragging;
    private DispatcherTimer? _runeAttachTimer;

    /// <summary>The runes offered as drag-attach chips: the 14 modeled runes = the 8 non-API-trackable
    /// ("manual") runes plus the 6 auto-triggered runes the combo engine force-applies. Built from the
    /// two authoritative sets (as their raw Data Dragon perk-id strings) so this palette never drifts
    /// from what the engine actually models.</summary>
    private static readonly IReadOnlyList<string> AttachableRuneIds =
        Overlay.Core.ChampionDb.RuneApiTrackability.NonTrackableRuneIds
            .Concat(Overlay.Core.Combo.ComboEngine.AutoTriggeredRuneIds)
            .Select(i => i.ToString())
            .ToList();

    /// <summary>Rune id → square icon, loaded lazily via <see cref="DDragonIconProvider"/>.</summary>
    private readonly Dictionary<string, ImageSource> _runeIcons = new(StringComparer.Ordinal);

    /// <summary>(loop 173) Ignite (SummonerDot) chip icon, loaded once lazily; null until the async
    /// fetch succeeds (chip shows a "점화" text fallback until then). <see cref="_igniteIconLoading"/>
    /// guards against firing the fetch more than once.</summary>
    private ImageSource? _igniteIcon;
    private bool _igniteIconLoading;

    /// <summary>(loop 174) Flash (SummonerFlash) chip icon — same lazy pattern as
    /// <see cref="_igniteIcon"/>. Flash deals no damage; its combo node exists only to satisfy the
    /// dash/blink trigger of runes like Sudden Impact (see ComboEngine.DashTriggeredManualRuneIds).</summary>
    private ImageSource? _flashIcon;
    private bool _flashIconLoading;

    /// <summary>(loop 173) When set, the editor was loaded from an existing saved combo via its
    /// "수정" (edit) button; on the next successful Save the ORIGINAL entry (this id) is removed so the
    /// edited version replaces it rather than saving a duplicate. Cleared after that Save (or when the
    /// user picks a different champion / clears the sequence).</summary>
    private string? _editingComboId;

    /// <summary>(loop 173) Guards the "auto-select the played champion on game start" behavior so it
    /// fires at most ONCE per game — after that the user is free to switch champions without being
    /// yanked back. Reset on GAME.CONNECTED/DISCONNECTED.</summary>
    private bool _autoSelectedThisGame;

    // Hotkey capture
    private bool _capturing;
    private string _capturedHotkey = string.Empty;

    // Second (optional) hotkey capture — M13 "Pending User-Reported Changes" (loop 38): a combo
    // may bind up to 2 independent chords, both triggering the same combo. The persisted mapping
    // (hotkeys.comboSlots, hotkey -> comboId) already supports N hotkeys per combo id structurally
    // (it is keyed by hotkey, not by combo), so no schema change was needed — only this second
    // capture control plus reverse-map/display/cleanup updates below to manage a second entry.
    private bool _capturing2;
    private string _capturedHotkey2 = string.Empty;

    public ComboSettingsView()
    {
        InitializeComponent();
        Localization.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
        // Refresh the target dropdown each time the view is shown so the living-enemy list reflects
        // the current game (the view is created once at startup, before any game exists). Also try to
        // auto-select the champion being played (loop 173) when opened mid-game.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) { PopulateTargets(); TryAutoSelectPlayedChampion(); } };

        // (loop 173) Auto-activate the played champion on GAME START (once per game), even when the
        // combo settings are already open. GAME.CONNECTED/DISCONNECTED reset the once-per-game flag; a
        // live in-game tick (GAME.GAME_TIME_UPDATED, published every poll once the snapshot exists)
        // drives the actual selection as soon as the active champion is known. Events arrive on the
        // poller thread, so hop to the UI thread before touching any view state.
        Overlay.Core.EventBus.EventBus.Subscribe("GAME.CONNECTED", _ => Dispatcher.BeginInvoke(() => _autoSelectedThisGame = false));
        Overlay.Core.EventBus.EventBus.Subscribe("GAME.DISCONNECTED", _ => Dispatcher.BeginInvoke(() => _autoSelectedThisGame = false));
        Overlay.Core.EventBus.EventBus.Subscribe("GAME.GAME_TIME_UPDATED", _ => Dispatcher.BeginInvoke(TryAutoSelectPlayedChampion));
    }

    // ── (loop 175) Collapsible 아이템/룬 sections — VSCode-style fold, default COLLAPSED ──
    private void ItemHeader_Click(object sender, MouseButtonEventArgs e) => ToggleSection(ItemSection, ItemChevron);
    private void RuneHeader_Click(object sender, MouseButtonEventArgs e) => ToggleSection(RuneSection, RuneChevron);

    /// <summary>Folds/expands a section and flips its chevron (▸ collapsed ↔ ▾ expanded).</summary>
    private static void ToggleSection(UIElement section, TextBlock chevron)
    {
        bool show = section.Visibility != Visibility.Visible;
        section.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        chevron.Text = show ? "▾" : "▸";
    }

    public void Attach(AppComposition composition)
    {
        _composition = composition;
        PopulateChampions();
        PopulateRunes(); // runes are not champion-specific — populate the static palette once
        PopulateTargets();
        PopulateEnemyDefensiveRunes(); // (M24 P5/P7) assumed-enemy-rune checkboxes -> config
        RefreshSavedList();

        // The M11 champion repository loads the full ~173-champion roster on a background task
        // that may still be running when Attach() runs (HomeWindow constructs this view immediately
        // after Start()-ing the composition root) — PopulateChampions() above can render only the
        // small fallback roster in that case. ChampionsReady fires once that load finishes (success
        // or degraded), on a background thread, so the re-render is marshaled to the UI thread here.
        // Attach() is only ever called once per process lifetime (HomeWindow constructs this view
        // once), so no double-subscription guard is needed.
        composition.ChampionsReady += () => Dispatcher.Invoke(PopulateChampions);
    }

    /// <summary>(M24 P5/P7) The enemy defensive runes the user can assume the target has — id + label,
    /// matching <see cref="Overlay.Core.Combo.ComboEngine.DefensiveRuneResists"/>. Checking one lowers
    /// the damage range's floor; the enemy's real runes aren't API-readable (P2), so this is honest
    /// user input.</summary>
    private static readonly (int Id, string Label)[] AssumableEnemyDefensiveRunes =
    {
        (8429, "단련 (+8 방어/마저)"),
        (8439, "여진 (CC 시 +45 방어/마저)"),
        (8465, "수호자 (실드)"),
    };

    /// <summary>Builds the assumed-enemy-rune checkboxes, reflecting the config-stored list and writing
    /// it back on toggle (see <see cref="ToggleEnemyRune"/>). No-op until <see cref="Attach"/> ran.</summary>
    private void PopulateEnemyDefensiveRunes()
    {
        EnemyDefensiveRunesPanel.Children.Clear();
        var current = ReadAssumedEnemyRuneIds();
        foreach (var (id, label) in AssumableEnemyDefensiveRunes)
        {
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = current.Contains(id),
                // Theme's primary text brush is keyed "Text" (Theme.xaml), not "TextPrimary".
                // TryFindResource never throws on a missing key (unlike FindResource, which was
                // crashing this panel every build with ResourceReferenceKeyNotFoundException).
                Foreground = Application.Current.TryFindResource("Text") as Brush,
                Margin = new Thickness(0, 2, 0, 2),
                FontSize = 12,
            };
            cb.Checked += (_, _) => ToggleEnemyRune(id, true);
            cb.Unchecked += (_, _) => ToggleEnemyRune(id, false);
            EnemyDefensiveRunesPanel.Children.Add(cb);
        }
    }

    private HashSet<int> ReadAssumedEnemyRuneIds()
    {
        var set = new HashSet<int>();
        if (_composition?.Config.Get("combo.assumedEnemyDefensiveRunes") is string raw)
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, out int id)) set.Add(id);
        return set;
    }

    private void ToggleEnemyRune(int id, bool on)
    {
        if (_composition is null) return;
        var set = ReadAssumedEnemyRuneIds();
        if (on) set.Add(id); else set.Remove(id);
        _composition.Config.Set("combo.assumedEnemyDefensiveRunes",
            string.Join(",", set.OrderBy(x => x)));
    }

    /// <summary>Loads the full champion roster id list (task 4/loop-38 item 4) and rebuilds the
    /// visible tile grid, preserving the current selection and search filter. Called on attach and
    /// on a language change (so tile display names refresh).</summary>
    private void PopulateChampions()
    {
        _allChampionIds.Clear();
        _allChampionIds.AddRange(AppComposition.ChampionIds);
        RebuildChampionTiles(ChampionSearchBox.Text);
    }

    /// <summary>Rebuilds the champion tile grid (loop 38 items 4/5: search-filtered, icon tile,
    /// internal-scroll-capped via the XAML ScrollViewer's MaxHeight) from <paramref name="filter"/>
    /// matched case-insensitively against both the localized display name and the raw id.</summary>
    private void ChampionSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => RebuildChampionTiles(ChampionSearchBox.Text);

    private void RebuildChampionTiles(string? filter)
    {
        ChampionPanel.Children.Clear();
        var needle = filter?.Trim() ?? string.Empty;

        foreach (var id in _allChampionIds)
        {
            var name = Localization.ChampionName(id);
            if (needle.Length > 0
                && name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                && id.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            ChampionPanel.Children.Add(BuildChampionTile(id, name));
        }
    }

    /// <summary>One champion's icon tile: a square portrait (async-loaded, letter fallback) over a
    /// name label, highlighted when it is the current selection. Clicking selects it.</summary>
    private StackPanel BuildChampionTile(string id, string name)
    {
        bool selected = string.Equals(id, _selectedChampionId, StringComparison.Ordinal);

        var image = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        var grid = new Grid { Width = ChipSize, Height = ChipSize };
        if (_championPortraits.TryGetValue(id, out var cached))
        {
            image.Source = cached;
            grid.Children.Add(image);
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = name.Length > 0 ? name[..1] : "?",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("TextDim"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            });
            _ = LoadChampionTilePortraitAsync(id, image, grid);
        }

        var column = new StackPanel { Margin = new Thickness(0, 0, 6, 6), Width = ChipSize };
        var tile = new Border
        {
            Width = ChipSize,
            Height = ChipSize,
            CornerRadius = new CornerRadius(0), // (loop 177) rectangular tile + selection border (user)
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = (Brush)Application.Current.FindResource(selected ? "Accent" : "Border"),
            Background = (Brush)Application.Current.FindResource("SurfaceHi"),
            Cursor = Cursors.Hand,
            ToolTip = name,
            Child = grid,
        };
        tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; _ = SelectChampionAsync(id); };
        column.Children.Add(tile);
        column.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 10,
            Foreground = (Brush)Application.Current.FindResource(selected ? "Text" : "TextDim"),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        return column;
    }

    /// <summary>Fetches and swaps in a champion tile's portrait once loaded; a stale/failed load
    /// leaves the letter fallback already in <paramref name="grid"/> untouched.</summary>
    private async Task LoadChampionTilePortraitAsync(string id, Image image, Grid grid)
    {
        var icon = await DDragonIconProvider.LoadChampionPortraitAsync(id);
        if (icon is null) return;
        _championPortraits[id] = icon;
        image.Source = icon;
        grid.Children.Clear();
        grid.Children.Add(image);
    }

    // ── Localization ────────────────────────────────────────────────────────

    private void ApplyLanguage()
    {
        TitleLabel.Text = Localization.L("combo.title");
        TargetCaption.Text = Localization.L("target.title");
        TargetHint.Text = Localization.L("target.hint");
        BuildCaption.Text = Localization.L("combo.build");
        ChampionCaption.Text = Localization.L("combo.champion");
        ChampionSearchBox.Tag = Localization.L("combo.championSearch");
        PaletteCaption.Text = Localization.L("combo.palette");
        PaletteSkillsLabel.Text = Localization.L("combo.paletteSkills");
        ItemCaption.Text = Localization.L("combo.paletteMore");
        ItemSearchBox.Tag = Localization.L("combo.itemSearch");
        ItemHint.Text = Localization.L("combo.itemHint");
        DragHint.Text = Localization.L("combo.dragHint");
        SequenceCaption.Text = Localization.L("combo.sequence");
        SequenceHint.Text = Localization.L("combo.sequenceEmpty");
        NameCaption.Text = Localization.L("combo.name");
        NameBox.Tag = Localization.L("combo.nameHint");
        HotkeyCaption.Text = Localization.L("combo.hotkey");
        ClearButton.Content = Localization.L("combo.clear");
        SaveButton.Content = Localization.L("combo.save");
        SavedCaption.Text = Localization.L("combo.saved");
        EmptySaved.Text = Localization.L("combo.emptySaved");
        SavedHint.Text = Localization.L("combo.savedHint");

        if (_selectedChampionId is null)
            PaletteHint.Text = Localization.L("combo.paletteHint");
        HotkeyButton.Content = _capturedHotkey.Length > 0 ? _capturedHotkey : Localization.L("combo.setHotkey");
        HotkeyButton2.Content = _capturedHotkey2.Length > 0 ? _capturedHotkey2 : "＋";
        HotkeyButton2.ToolTip = Localization.L("combo.hotkey2");

        // Rebuild the picker so champion display names follow the language (task 4). This also
        // refreshes the saved list (selection itself is unchanged — only display names/tiles refresh).
        PopulateChampions();
        PopulateTargets();
        RefreshSavedList();
        RebuildBuildItemsPanel(); // item tooltips are localized text
    }

    // ── Combo target selector ───────────────────────────────────────────────

    /// <summary>A target-picker entry: <see cref="ChampionId"/> null = Auto (same-position);
    /// otherwise the canonical champion id, displayed with its localized name.</summary>
    private sealed record TargetOption(string? ChampionId)
    {
        public override string ToString() =>
            ChampionId is null ? Localization.L("target.auto") : Localization.ChampionName(ChampionId);
    }

    /// <summary>Suppresses <see cref="TargetCombo_SelectionChanged"/> config writes while the items
    /// are being rebuilt (populate sets the selection programmatically).</summary>
    private bool _loadingTargets;

    /// <summary>Rebuilds the target dropdown: "Auto" plus each living enemy champion in the current
    /// game, and re-selects the persisted choice (targeting.mode/manualTarget). A configured manual
    /// target not currently in-game is added so the selection still shows.</summary>
    private void PopulateTargets()
    {
        if (TargetCombo is null || _composition is null) return;

        _loadingTargets = true;
        try
        {
            string manual = _composition.Config.Get("targeting.manualTarget") as string ?? string.Empty;
            bool isManual = string.Equals(
                _composition.Config.Get("targeting.mode") as string, "Manual", StringComparison.OrdinalIgnoreCase);

            TargetCombo.Items.Clear();
            TargetCombo.Items.Add(new TargetOption(null)); // Auto
            foreach (var champ in LivingEnemyChampions(_composition.LatestSnapshot))
                TargetCombo.Items.Add(new TargetOption(champ));

            var options = TargetCombo.Items.OfType<TargetOption>().ToList();
            if (isManual && manual.Length > 0
                && !options.Any(o => string.Equals(o.ChampionId, manual, StringComparison.OrdinalIgnoreCase)))
            {
                var extra = new TargetOption(manual);
                TargetCombo.Items.Add(extra);
                options.Add(extra);
            }

            TargetCombo.SelectedItem = isManual && manual.Length > 0
                ? options.FirstOrDefault(o => string.Equals(o.ChampionId, manual, StringComparison.OrdinalIgnoreCase))
                  ?? options[0]
                : options[0]; // Auto
        }
        finally
        {
            _loadingTargets = false;
        }
    }

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTargets || _composition is null) return;
        if (TargetCombo.SelectedItem is not TargetOption opt) return;

        if (opt.ChampionId is null)
        {
            _composition.Config.Set("targeting.mode", "Auto");
            _composition.Config.Set("targeting.manualTarget", string.Empty);
        }
        else
        {
            _composition.Config.Set("targeting.mode", "Manual");
            _composition.Config.Set("targeting.manualTarget", opt.ChampionId);
        }
    }

    /// <summary>Living ENEMY champion names in the snapshot (distinct). The enemy team is the one
    /// opposite the active player's row; when the active row can't be matched (identity unavailable)
    /// every living champion is listed so the picker is still usable.</summary>
    private static IEnumerable<string> LivingEnemyChampions(Overlay.Core.GameSnapshot? snap)
    {
        if (snap is null || !snap.HasData) yield break;

        string myTeam = ActiveTeam(snap);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (p.IsDead || string.IsNullOrEmpty(p.ChampionName)) continue;
            if (myTeam.Length > 0 && string.Equals(p.Team, myTeam, StringComparison.Ordinal)) continue;
            if (seen.Add(p.ChampionName)) yield return p.ChampionName;
        }
    }

    /// <summary>The active player's team, matched by riotId/summoner name (tolerant, like the core
    /// runner). Empty when no row matches — the caller then lists all living champions.</summary>
    private static string ActiveTeam(Overlay.Core.GameSnapshot snap)
    {
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (IdMatch(p.RiotId, snap.ActivePlayerRiotId) || IdMatch(p.SummonerName, snap.ActivePlayerSummonerName)
                || IdMatch(p.RiotId, snap.ActivePlayerSummonerName) || IdMatch(p.SummonerName, snap.ActivePlayerRiotId))
                return p.Team;
        }
        return string.Empty;
    }

    /// <summary>Case-insensitive identity match tolerating the API's "Name#TAG" vs bare-name
    /// inconsistency (mirrors ComboRunner.SamePlayer; empty strings never match).</summary>
    private static bool IdMatch(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        static string BaseName(string id) { int h = id.IndexOf('#'); return h < 0 ? id : id[..h]; }
        return string.Equals(BaseName(a), BaseName(b), StringComparison.OrdinalIgnoreCase);
    }

    // ── Champion select → palette ───────────────────────────────────────────

    /// <summary>(loop 173) Auto-selects the champion currently being played, at most once per game.
    /// No-op when there's no live game / active champion yet (retries on the next tick until it is
    /// known), or when that champion is already selected. After it fires once the user can freely
    /// switch to another champion — the once-flag stops it from switching back.</summary>
    private void TryAutoSelectPlayedChampion()
    {
        if (_autoSelectedThisGame) return;
        var id = _composition?.ActivePlayerChampionId;
        if (string.IsNullOrEmpty(id)) return; // no game / champion not resolved yet → try again next tick
        _autoSelectedThisGame = true;
        if (string.Equals(_selectedChampionId, id, StringComparison.Ordinal)) return; // already on it
        _ = SelectChampionAsync(id);
    }

    /// <summary>Selects a champion from a tile click (loop 38 items 4/5 replaced the ComboBox with
    /// a search-filtered icon-tile grid; this is the direct equivalent of the old
    /// SelectionChanged handler). Re-highlights the tile grid so the newly-selected tile shows
    /// the accent border.</summary>
    private async Task SelectChampionAsync(string? championId)
    {
        _selectedChampionId = championId;
        RebuildChampionTiles(ChampionSearchBox.Text); // refresh selection highlight

        _sequence.Clear();
        _editingComboId = null; // (loop 173) switching champions abandons any in-progress "수정" edit
        RebuildSequence();
        _palette.Clear();
        _icons = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
        PalettePanel.Children.Clear();

        if (championId is null)
        {
            _iconChampionId = null;
            RefreshSavedList(); // task 3: no champion → no combos shown
            LoadItemBuild(null); // no champion → no hypothetical build
            return;
        }
        LoadItemBuild(championId);

        NodePalette palette;
        try
        {
            palette = ComboEditor.LoadPalette(championId);
        }
        catch (Exception ex)
        {
            _iconChampionId = null;
            PaletteHint.Text = Localization.F("combo.skillLoadFailed", ex.Message);
            PaletteHint.Visibility = Visibility.Visible;
            RefreshSavedList();
            return;
        }

        PaletteHint.Visibility = Visibility.Collapsed;
        foreach (var node in palette.AvailableNodes)
            _palette.Add(new Chip(node, node.Id));
        // (loop 173) Ignite (점화): a UNIVERSAL summoner-spell chip, offered for every champion right
        // after the ability palette (next to AA). Saved as a ComboNodeType.Summoner node named "Ignite";
        // ComboRunner resolves its level-scaled TRUE damage from summoner_effects.json at trigger time
        // (engine already wired — CLAUDE_CODE_TODO §29). Dragged into the sequence like any other chip.
        _palette.Add(new Chip(
            new ComboNode(
                Id: "Ignite", NodeType: ComboNodeType.Summoner, Name: "Ignite",
                Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.True,
                RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0),
            "Ignite"));
        // (loop 174) Flash (점멸): a 0-damage summoner node. It carries no damage of its own — its only
        // purpose in a combo is to signal a dash/blink so dash-triggered runes (Sudden Impact / 돌발일격)
        // meet their condition and apply (see ComboEngine.DashTriggeredManualRuneIds).
        _palette.Add(new Chip(
            new ComboNode(
                Id: "Flash", NodeType: ComboNodeType.Summoner, Name: "Flash",
                Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.True,
                RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0),
            "Flash"));
        RebuildPalette();     // letter badges first (immediate)
        RefreshSavedList();   // task 3: filter saved combos to this champion

        // Task 6: fetch the real ability icons asynchronously; on success re-render the chips.
        // A stale load (user switched champions meanwhile) is ignored via the id guard.
        _iconChampionId = championId;
        try
        {
            var icons = await AbilityIconProvider.LoadIconsAsync(championId);
            if (_iconChampionId == championId)
            {
                _icons = icons;
                RebuildPalette();
                RebuildSequence();
                RefreshSavedList(); // loop 38 item 7: saved-combo preview icons use these same icons
            }
        }
        catch
        {
            // Any icon-load failure keeps the letter-badge fallback; nothing to do.
        }

        // (loop 175) Transform-form extra slots (Jayce QCannon/WCannon, Gnar QMega/WMega/EMega) aren't
        // in Data Dragon's base P/Q/W/E/R spell-icon set, so load their real ability icons from the
        // curation's CommunityDragon asset path into _icons here.
        await LoadExtraSlotIconsAsync(championId);
    }

    /// <summary>(loop 175) Loads the CommunityDragon icon for each EXTRA (transform-form) palette slot
    /// — those whose curation carries an icon path (<see cref="SkillDamageDb.GetSlotIcon"/>) — into
    /// <see cref="_icons"/> so <see cref="BuildChipContent"/> shows a real ability icon instead of the
    /// slot-key letter. Canonical P/Q/W/E/R (already handled by <see cref="AbilityIconProvider"/>), AA,
    /// and the summoner chips are skipped. Best-effort per slot; a stale champion switch is ignored.</summary>
    private async Task LoadExtraSlotIconsAsync(string championId)
    {
        foreach (var chip in _palette.ToList())
        {
            var slot = chip.Slot;
            if (slot is "AA" or "Ignite" or "Flash" || _icons.ContainsKey(slot)) continue;
            var iconPath = SkillDamageDb.GetSlotIcon(championId, slot);
            if (string.IsNullOrEmpty(iconPath)) continue;
            var icon = await DDragonIconProvider.LoadGameAssetIconAsync(iconPath);
            if (icon is null || _iconChampionId != championId) continue;
            var updated = new Dictionary<string, ImageSource>(_icons, StringComparer.Ordinal) { [slot] = icon };
            _icons = updated;
        }
        if (_iconChampionId == championId)
        {
            RebuildPalette();
            RebuildSequence();
        }
    }

    private void RebuildPalette()
    {
        PalettePanel.Children.Clear();
        foreach (var chip in _palette)
            PalettePanel.Children.Add(BuildChip(chip, inSequence: false));
    }

    // ── Item picker (loop 38 pending item 2: search-to-add, icon tiles) ─────
    //
    // Persisted "hypothetical build" (loop 38 continuation 18): _buildItemIds/RebuildBuildItemsPanel
    // render the chosen items as icon chips, persisted per champion via ItemBuildStore (mirroring
    // RuneSelectionStore's exact pattern), and now READ by ComboRunner.BuildAttacker, which adds
    // each item's AD/AP additively on top of the attacker's live stats — theory-crafting on top of
    // the caster's real current state, not a replacement for it (same "virtual model" design already
    // used for runes). ItemHint states this plainly to the user.
    //
    // NOT for reflecting real inventory stats — this list holds a HYPOTHETICAL build (capped at
    // MaxBuildItems, like a real inventory: 6 items + trinket) so the user can drag only the items
    // that have a manual-use effect (ItemData.IsActive, e.g. Zhonya's) onto a combo node. Passive/
    // stat-only items stay addable here (for the future stat-calc use above) but are never
    // draggable to a combo node — see RebuildBuildItemsPanel's IsActive gate below.

    private const int ItemSearchResultCap = 24;
    private const int MaxBuildItems = 7;

    /// <summary>Loads <paramref name="championId"/>'s persisted hypothetical build into
    /// <see cref="_buildItemIds"/> (empty when <paramref name="championId"/> is null or nothing was
    /// ever saved — never a fabricated default) and rebuilds the panel.</summary>
    private void LoadItemBuild(string? championId)
    {
        _buildItemIds.Clear();

        if (championId is not null && _composition is not null)
        {
            var build = ItemBuildStore.Load(_composition.Config, championId);
            if (build is not null)
                _buildItemIds.AddRange(build.ItemIds);
        }

        RebuildBuildItemsPanel();
    }

    /// <summary>Persists the current build for the selected champion. A no-op with no champion
    /// selected or before <see cref="Attach"/> (nothing to save against).</summary>
    private void SaveItemBuild()
    {
        if (_composition is null) return;
        if (_selectedChampionId is not string championId) return;

        ItemBuildStore.Save(_composition.Config, championId, _buildItemIds.ToArray());
    }

    private void ItemSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ItemResultsPanel.Children.Clear();
        var needle = ItemSearchBox.Text?.Trim() ?? string.Empty;

        if (needle.Length == 0 || !ItemRepository.IsInitialized)
        {
            ItemResultsScroll.Visibility = Visibility.Collapsed;
            return;
        }

        var matches = ItemRepository.GetAll()
            .Where(i => !string.IsNullOrWhiteSpace(i.Name)
                        && (i.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                            || (i.NameKo is not null
                                && i.NameKo.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                        && !_buildItemIds.Contains(i.Id)
                        && i.AvailableOnSummonersRift)
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(ItemSearchResultCap);

        int shown = 0;
        foreach (var item in matches)
        {
            ItemResultsPanel.Children.Add(BuildItemResultTile(item));
            shown++;
        }
        ItemResultsScroll.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One search-result item tile: icon (async-loaded, letter fallback) + name; clicking
    /// adds it to the build and clears the search.</summary>
    private Border BuildItemResultTile(ItemData item)
    {
        var tile = new Border
        {
            Width = ChipSize,
            Height = ChipSize,
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            Background = (Brush)Application.Current.FindResource("SurfaceHi"),
            Cursor = Cursors.Hand,
            ToolTip = Localization.ItemName(item),
            Margin = new Thickness(0, 0, 6, 6),
            Child = BuildItemIconContent(item.Id),
        };
        tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; AddBuildItem(item.Id); };
        return tile;
    }

    private void AddBuildItem(string itemId)
    {
        if (_buildItemIds.Contains(itemId)) return;
        if (_buildItemIds.Count >= MaxBuildItems) return; // build list is full (6 items + trinket)
        _buildItemIds.Add(itemId);
        ItemSearchBox.Clear(); // also collapses the results panel via the TextChanged handler
        RebuildBuildItemsPanel();
        SaveItemBuild();
    }

    private void RemoveBuildItem(string itemId)
    {
        _buildItemIds.Remove(itemId);
        RebuildBuildItemsPanel();
        SaveItemBuild();
    }

    private void RebuildBuildItemsPanel()
    {
        BuildItemsPanel.Children.Clear();
        foreach (var itemId in _buildItemIds)
        {
            var item = ItemRepository.Get(itemId);
            var name = item is not null ? Localization.ItemName(item) : itemId;
            bool isActive = item is not null && item.IsActive;
            var chip = new Border
            {
                Width = ChipSize,
                Height = ChipSize,
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                Background = (Brush)Application.Current.FindResource("SurfaceHi"),
                Opacity = isActive ? 1.0 : 0.6, // non-active items aren't combo-draggable; dim to hint why
                Cursor = Cursors.Hand,
                ToolTip = Localization.F("combo.itemRemove", name),
                Margin = new Thickness(0, 0, 6, 6),
                Child = BuildItemIconContent(itemId),
            };
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveBuildItem(itemId); };
            // Drag-and-hold attach source (M04 item 3, item half): dragging this chip onto a placed
            // node and holding ~1s attaches it there (see ItemAttach_DragEnter). A plain click below
            // the drag threshold still falls through to the MouseLeftButtonUp remove handler above,
            // same coexistence pattern TryStartDrag/BuildChip already uses for skill chips. Only
            // active-use items (ItemData.IsActive) are wired as a drag source at all — a passive/
            // stat-only item stays addable/removable but can never be dragged onto a combo node.
            if (isActive)
            {
                chip.PreviewMouseLeftButtonDown += (_, e) => { _itemPressPoint = e.GetPosition(this); _itemDragging = false; };
                chip.PreviewMouseMove += (_, e) => TryStartItemDrag(chip, itemId, e);
            }
            BuildItemsPanel.Children.Add(chip);
        }
    }

    /// <summary>An item's icon content (async-loaded, cached in <see cref="_itemIcons"/>; falls back
    /// to the item's first name letter until/unless the fetch succeeds).</summary>
    private UIElement BuildItemIconContent(string itemId)
    {
        var grid = new Grid { Width = ChipSize, Height = ChipSize };
        if (_itemIcons.TryGetValue(itemId, out var cached))
        {
            grid.Children.Add(new Image { Source = cached, Stretch = Stretch.UniformToFill, IsHitTestVisible = false });
            return grid;
        }

        var name = ItemRepository.Get(itemId)?.Name ?? itemId;
        var letter = new TextBlock
        {
            Text = name.Length > 0 ? name[..1] : "?",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        grid.Children.Add(letter);
        _ = LoadItemIconAsync(itemId, grid);
        return grid;
    }

    private async Task LoadItemIconAsync(string itemId, Grid grid)
    {
        var icon = await DDragonIconProvider.LoadItemIconAsync(itemId);
        if (icon is null) return;
        _itemIcons[itemId] = icon;
        grid.Children.Clear();
        grid.Children.Add(new Image { Source = icon, Stretch = Stretch.UniformToFill, IsHitTestVisible = false });
    }

    // ── Rune palette (M04 item 3, rune half: a static 14-rune drag-attach palette) ──────────
    //
    // Unlike the item picker there is no search box — only the 14 modeled runes (AttachableRuneIds)
    // are attachable, so the whole set is rendered as drag-source chips. Dragging one onto a placed
    // sequence node and holding ~1s force-attaches it there (see RuneAttach_DragEnter); the engine
    // then force-applies ComboNode.AttachedRuneId (Part 3). Populated once from Attach() since the
    // rune set is not champion-specific.

    /// <summary>Fills <see cref="RunesPanel"/> with one drag-source chip per <see cref="AttachableRuneIds"/>
    /// entry (icon async-loaded, letter fallback). A no-op-safe guard against a null panel keeps this
    /// callable before <see cref="InitializeComponent"/> would have created it.</summary>
    private void PopulateRunes()
    {
        if (RunesPanel is null) return;
        RunesPanel.Children.Clear();
        foreach (var id in AttachableRuneIds)
        {
            var name = RuneRepository.Get(id)?.Name ?? id;
            var chip = new Border
            {
                Width = ChipSize,
                Height = ChipSize,
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                Background = (Brush)Application.Current.FindResource("SurfaceHi"),
                Cursor = Cursors.Hand,
                ToolTip = name,
                Margin = new Thickness(0, 0, 6, 6),
                Child = BuildRuneIconContent(id),
            };
            chip.PreviewMouseLeftButtonDown += (_, e) => { _runePressPoint = e.GetPosition(this); _runeDragging = false; };
            chip.PreviewMouseMove += (_, e) => TryStartRuneDrag(chip, id, e);
            RunesPanel.Children.Add(chip);
        }
    }

    /// <summary>A rune's icon content (async-loaded, cached in <see cref="_runeIcons"/>; falls back to
    /// the rune's first name letter until/unless the fetch succeeds).</summary>
    private UIElement BuildRuneIconContent(string runeId)
    {
        var grid = new Grid { Width = ChipSize, Height = ChipSize };
        if (_runeIcons.TryGetValue(runeId, out var cached))
        {
            grid.Children.Add(new Image { Source = cached, Stretch = Stretch.UniformToFill, IsHitTestVisible = false });
            return grid;
        }

        var name = RuneRepository.Get(runeId)?.Name ?? runeId;
        var letter = new TextBlock
        {
            Text = name.Length > 0 ? name[..1] : "?",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        grid.Children.Add(letter);
        _ = LoadRuneIconAsync(runeId, grid);
        return grid;
    }

    /// <summary>Fetches a rune's icon by its <see cref="RuneData.Icon"/> path (NOT its id — unlike the
    /// item loader which takes an id) and swaps it into <paramref name="grid"/>; a null/empty path or a
    /// failed fetch leaves the letter fallback untouched.</summary>
    private async Task LoadRuneIconAsync(string runeId, Grid grid)
    {
        var iconPath = RuneRepository.Get(runeId)?.Icon;
        if (string.IsNullOrEmpty(iconPath)) return;
        var icon = await DDragonIconProvider.LoadRuneIconAsync(iconPath);
        if (icon is null) return;
        _runeIcons[runeId] = icon;
        grid.Children.Clear();
        grid.Children.Add(new Image { Source = icon, Stretch = Stretch.UniformToFill, IsHitTestVisible = false });
    }

    // ── Chip factory + drag-source wiring ───────────────────────────────────

    /// <summary>Compact chip size (task 5) — smaller than the old 52px badge, leaving room for
    /// future palette categories.</summary>
    private const double ChipSize = 44;

    /// <summary>A crisp, font-independent sword geometry for the auto-attack chip (task 6). Drawn as
    /// a vector <see cref="System.Windows.Shapes.Path"/> so no glyph font is required.</summary>
    private const string SwordGeometry =
        "M6.92 5H5l9 9 1-.94M19.96 19.12l-1.15 1.15c-.31.31-.81.31-1.11 0l-3.71-3.71-2.27 2.27" +
        "-1.42-1.42 1.42-1.41L3 5.09V3h2.09l7.91 7.91 1.41-1.42 1.42 1.42-2.27 2.27 3.71 3.71c.3.3.3.8-.02 1.11z";

    /// <summary>Accent brush for a node/effect slot key. Multiform slot keys (M22, e.g. "QCannon",
    /// "WMega", Hwei sub-spells) are NOT theme resources — they are normalized to the BASE ability's
    /// colour (Q/W/E/R/P/AA). <see cref="Application.TryFindResource"/> + an SlotAA fallback ensures an
    /// unknown key never throws, closing the same FindResource-throws crash class as the §9 loop-103
    /// startup crash (the theme defines SlotAA/P/Q/W/E/R only).</summary>
    private static Brush SlotAccentBrush(string? slot)
    {
        var app = Application.Current;
        if (!string.IsNullOrEmpty(slot))
        {
            string baseKey = slot.StartsWith("AA", StringComparison.OrdinalIgnoreCase)
                ? "SlotAA"
                : "Slot" + char.ToUpperInvariant(slot[0]);
            if (app.TryFindResource(baseKey) is Brush b) return b;
        }
        return app.TryFindResource("SlotAA") as Brush ?? Brushes.Gray;
    }

    private Border BuildChip(Chip data, bool inSequence)
    {
        var accent = SlotAccentBrush(data.Slot);
        var chip = new Border
        {
            Style = (Style)Application.Current.FindResource("SkillChip"),
            Width = ChipSize,
            Height = ChipSize,
            BorderBrush = accent,
            // No ClipToBounds here (loop 38 item 6 fix): it was clipping the real ability icon into
            // the chip's rounded corners, cutting into the square source image. The chip's Grid is
            // sized to exactly match ChipSize, so nothing actually needs clipping.
            Child = BuildChipContent(data, accent),
            ToolTip = data.Node.Name,
            Tag = data,
        };

        chip.PreviewMouseLeftButtonDown += (_, e) => { _pressPoint = e.GetPosition(this); _dragging = false; };
        chip.PreviewMouseMove += (_, e) => TryStartDrag(chip, data, inSequence, e);

        // Item drag-and-hold attach target (M04 item 3, item half only): only a chip already placed in
        // the sequence can receive an attached item — the palette isn't a valid attach target.
        if (inSequence)
        {
            chip.AllowDrop = true;
            chip.DragEnter += (_, e) => ItemAttach_DragEnter(data, e);
            chip.DragOver += (_, e) => ItemAttach_DragOver(e);
            chip.DragLeave += (_, e) => ItemAttach_DragLeave(e);
            chip.Drop += (_, e) => ItemAttach_Drop(e);
            // Rune drag-and-hold attach target (M04 item 3, rune half): same gesture as the item
            // handlers above but keyed on RuneDragPayload, so an item drag and a rune drag never
            // cross-fire (each handler ignores the other's payload type). AllowDrop already set above.
            chip.DragEnter += (_, e) => RuneAttach_DragEnter(data, e);
            chip.DragOver += (_, e) => RuneAttach_DragOver(e);
            chip.DragLeave += (_, e) => RuneAttach_DragLeave(e);
            chip.Drop += (_, e) => RuneAttach_Drop(e);
        }
        return chip;
    }

    // ── Item drag-and-hold attach (M04 "Pending User-Reported Changes" item 3, item half only —
    // see LEAD DECISION loop 38 continuation 12 / HOLD loop 38 continuation 14 in
    // docs/modules/M04_COMBO_EDITOR.md: UX/organization only, no engine change; rune half deferred). ──

    /// <summary>Hovering a dragged item over a placed node starts (or restarts) the ~1s hold timer;
    /// firing attaches the item to that node. A quick drag-through that leaves before the timer fires
    /// attaches nothing (see <see cref="ItemAttach_DragLeave"/>).</summary>
    private void ItemAttach_DragEnter(Chip targetChip, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ItemDragPayload)) is not ItemDragPayload payload) return;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;

        _itemAttachTimer?.Stop();
        var timer = new DispatcherTimer { Interval = ItemAttachHoldDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _itemAttachTimer = null;
            AttachItem(targetChip, payload.ItemId);
        };
        _itemAttachTimer = timer;
        timer.Start();
    }

    private void ItemAttach_DragOver(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ItemDragPayload))) return;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void ItemAttach_DragLeave(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ItemDragPayload))) return;
        _itemAttachTimer?.Stop();
        _itemAttachTimer = null;
        e.Handled = true;
    }

    /// <summary>A drop before the hold timer fires attaches nothing — the gesture is "hold", not
    /// "drop" (matches the ~1s hold behavior of <see cref="ItemAttach_DragEnter"/>).</summary>
    private void ItemAttach_Drop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ItemDragPayload))) return;
        _itemAttachTimer?.Stop();
        _itemAttachTimer = null;
        e.Handled = true;
    }

    /// <summary>Sets the node's <see cref="ComboNode.AttachedItemId"/> and re-renders. UX/organization
    /// only per the Lead decision — does not touch the item-proc pipeline.</summary>
    private void AttachItem(Chip chip, string itemId)
        => ReplaceChip(chip, chip with { Node = chip.Node with { AttachedItemId = itemId } });

    /// <summary>Clears the node's attached item (clicking its sub-icon badge) and re-renders.</summary>
    private void RemoveItem(Chip chip)
        => ReplaceChip(chip, chip with { Node = chip.Node with { AttachedItemId = null } });

    // ── Rune drag-and-hold attach (M04 item 3, rune half): mirrors the item handlers above exactly,
    // but keyed on RuneDragPayload and driving _runeAttachTimer, so a rune drag never triggers the
    // item-attach logic and vice-versa (each handler ignores the other's payload type). ──

    /// <summary>Hovering a dragged rune over a placed node starts (or restarts) the ~1s hold timer;
    /// firing attaches the rune to that node. A quick drag-through that leaves before the timer fires
    /// attaches nothing (see <see cref="RuneAttach_DragLeave"/>).</summary>
    private void RuneAttach_DragEnter(Chip targetChip, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RuneDragPayload)) is not RuneDragPayload payload) return;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;

        _runeAttachTimer?.Stop();
        var timer = new DispatcherTimer { Interval = ItemAttachHoldDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _runeAttachTimer = null;
            AttachRune(targetChip, payload.RuneId);
        };
        _runeAttachTimer = timer;
        timer.Start();
    }

    private void RuneAttach_DragOver(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(RuneDragPayload))) return;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void RuneAttach_DragLeave(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(RuneDragPayload))) return;
        _runeAttachTimer?.Stop();
        _runeAttachTimer = null;
        e.Handled = true;
    }

    /// <summary>A drop before the hold timer fires attaches nothing — the gesture is "hold", not
    /// "drop" (matches the ~1s hold behavior of <see cref="RuneAttach_DragEnter"/>).</summary>
    private void RuneAttach_Drop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(RuneDragPayload))) return;
        _runeAttachTimer?.Stop();
        _runeAttachTimer = null;
        e.Handled = true;
    }

    /// <summary>Sets the node's <see cref="ComboNode.AttachedRuneId"/> and re-renders; the combo
    /// engine force-applies it (Part 3). Same <see cref="ReplaceChip"/> path <see cref="AttachItem"/>
    /// uses.</summary>
    private void AttachRune(Chip chip, string runeId)
        => ReplaceChip(chip, chip with { Node = chip.Node with { AttachedRuneId = runeId } });

    /// <summary>Clears the node's attached rune (clicking its sub-icon badge) and re-renders.</summary>
    private void RemoveRune(Chip chip)
        => ReplaceChip(chip, chip with { Node = chip.Node with { AttachedRuneId = null } });

    /// <summary>(loop 173) Fetches the Ignite (SummonerDot) summoner icon once and re-renders the chips
    /// so both the palette and any sequence Ignite chip pick it up. Best-effort: a failed fetch leaves
    /// the "점화" text fallback. Guarded so it only ever fires one download.</summary>
    private async Task LoadIgniteIconAsync()
    {
        if (_igniteIcon is not null || _igniteIconLoading) return;
        _igniteIconLoading = true;
        var icon = await DDragonIconProvider.LoadSummonerIconAsync("SummonerDot");
        if (icon is null) { _igniteIconLoading = false; return; } // keep the text fallback; allow a later retry
        _igniteIcon = icon;
        RebuildPalette();
        RebuildSequence();
        RefreshSavedList(); // (loop 176) so saved-combo previews pick up the icon too
    }

    /// <summary>(loop 174) Fetches the Flash (SummonerFlash) summoner icon once and re-renders the chips.
    /// Best-effort — a failed fetch leaves the "점멸" text fallback. Mirrors <see cref="LoadIgniteIconAsync"/>.</summary>
    private async Task LoadFlashIconAsync()
    {
        if (_flashIcon is not null || _flashIconLoading) return;
        _flashIconLoading = true;
        var icon = await DDragonIconProvider.LoadSummonerIconAsync("SummonerFlash");
        if (icon is null) { _flashIconLoading = false; return; }
        _flashIcon = icon;
        RebuildPalette();
        RebuildSequence();
        RefreshSavedList(); // (loop 176) so saved-combo previews pick up the icon too
    }

    /// <summary>Builds a chip's inner visual: the champion's real ability icon (task 6) when loaded,
    /// a sword for auto-attack, or the letter badge as a graceful fallback — with the key letter
    /// (P/Q/W/E/R/A) as a small bottom-left overlay. A fresh visual tree per call (a UI element cannot
    /// have two parents), so the drag ghost can reuse it.</summary>
    /// <summary>Icon for a palette slot, falling back to the BASE ability when the slot is a
    /// variant cast without art of its own.
    ///
    /// <para>An extra slot is always a variant of one canonical ability — Irelia "RWall" is still R,
    /// Briar "WBite" is still W — so when CommunityDragon has no distinct asset for it (and several
    /// genuinely do not; Irelia ships one R icon), the right picture is the base ability's, not a
    /// letter badge. Before this, every extra slot without a curated icon fell through to the
    /// letter, which is what the user reported for WBite.</para></summary>
    private bool TryResolveSlotIcon(string slot, out ImageSource icon)
    {
        if (_icons.TryGetValue(slot, out icon!)) return true;
        // "QCalibrum" -> "Q", "RWall" -> "R". Canonical slots are single letters, so a longer key
        // whose first character is one of them is a variant of that ability.
        if (slot.Length > 1 && slot[0] is 'P' or 'Q' or 'W' or 'E' or 'R'
            && _icons.TryGetValue(slot[..1], out icon!)) return true;
        icon = null!;
        return false;
    }

    private UIElement BuildChipContent(Chip data, Brush accent)
    {
        string key = data.Slot == "AA" ? "A" : data.Slot == "Ignite" ? "점" : data.Slot == "Flash" ? "멸" : data.Slot;
        var grid = new Grid { Width = ChipSize, Height = ChipSize };

        if (data.Slot == "AA")
        {
            grid.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(SwordGeometry),
                Fill = accent,
                Stretch = Stretch.Uniform,
                Width = 24,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            });
        }
        else if (data.Slot == "Ignite")
        {
            // (loop 173) Ignite chip: the real DDragon summoner icon (SummonerDot) once loaded, else a
            // "점화" text fallback while the async fetch runs (mirrors the ability/rune icon pattern).
            if (_igniteIcon is not null)
            {
                grid.Children.Add(new Image { Source = _igniteIcon, Stretch = Stretch.UniformToFill, IsHitTestVisible = false });
            }
            else
            {
                grid.Children.Add(new TextBlock
                {
                    Text = "점화",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                });
                _ = LoadIgniteIconAsync();
            }
        }
        else if (data.Slot == "Flash")
        {
            // (loop 174) Flash chip: the real DDragon summoner icon (SummonerFlash) once loaded, else a
            // "점멸" text fallback while the async fetch runs.
            if (_flashIcon is not null)
            {
                grid.Children.Add(new Image { Source = _flashIcon, Stretch = Stretch.UniformToFill, IsHitTestVisible = false });
            }
            else
            {
                grid.Children.Add(new TextBlock
                {
                    Text = "점멸",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                });
                _ = LoadFlashIconAsync();
            }
        }
        else if (TryResolveSlotIcon(data.Slot, out var icon))
        {
            grid.Children.Add(new Image
            {
                Source = icon,
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false,
            });
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = key,
                FontSize = key.Length > 1 ? 15 : 18,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            });
        }

        // Key-letter overlay, bottom-left. (loop 177) Skipped for the summoner chips (Ignite/Flash):
        // they're identified purely by their icon, so the "점"/"멸" name badge is redundant clutter.
        if (data.Slot is not "Ignite" and not "Flash")
        {
            grid.Children.Add(new Border
            {
                Background = KeyBadgeBrush,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = key,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)Application.Current.FindResource("Text"),
                },
            });
        }

        return grid;
    }

    private void TryStartDrag(Border chip, Chip data, bool inSequence, MouseEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragging = true;
        var payload = new DragPayload(data, inSequence);
        ShowGhost(data);
        chip.GiveFeedback += OnGiveFeedback;

        _droppedOnSequence = false;
        try
        {
            DragDrop.DoDragDrop(chip, payload, DragDropEffects.Move);
        }
        finally
        {
            chip.GiveFeedback -= OnGiveFeedback;
            HideGhost();
            RemoveIndicator();
            _dragging = false;

            // Dragged a placed chip out of the sequence (up/anywhere non-sequence) → delete.
            if (inSequence && !_droppedOnSequence)
            {
                _sequence.RemoveAll(c => ReferenceEquals(c, data));
                RebuildSequence();
            }
        }
    }

    /// <summary>Drag-source counterpart of <see cref="TryStartDrag"/> for a build-item chip (M04 item
    /// 3, item half): past the drag threshold, shows an item ghost and starts a real
    /// <see cref="DragDrop.DoDragDrop"/> carrying <see cref="ItemDragPayload"/> — a placed sequence
    /// chip's <see cref="ItemAttach_DragEnter"/> handles the ~1s hold-to-attach while hovering.</summary>
    private void TryStartItemDrag(Border chip, string itemId, MouseEventArgs e)
    {
        if (_itemDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _itemPressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _itemPressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _itemDragging = true;
        ShowItemGhost(itemId);
        chip.GiveFeedback += OnGiveFeedback;
        try
        {
            DragDrop.DoDragDrop(chip, new ItemDragPayload(itemId), DragDropEffects.Link);
        }
        finally
        {
            chip.GiveFeedback -= OnGiveFeedback;
            HideGhost();
            _itemAttachTimer?.Stop();
            _itemAttachTimer = null;
            _itemDragging = false;
        }
    }

    /// <summary>Translucent ghost for an in-flight item drag — same <see cref="_ghostPopup"/>/
    /// <see cref="PositionGhost"/> plumbing <see cref="ShowGhost"/> uses for skill chips, just with the
    /// item's own icon instead of a slot-accented chip (items have no P/Q/W/E/R slot color).</summary>
    private void ShowItemGhost(string itemId)
    {
        var ghostChip = new Border
        {
            Width = ChipSize,
            Height = ChipSize,
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(2),
            BorderBrush = (Brush)Application.Current.FindResource("Accent"),
            Background = (Brush)Application.Current.FindResource("SurfaceHi"),
            IsHitTestVisible = false,
            Opacity = 0.7,
            Child = BuildItemIconContent(itemId),
        };

        _ghostPopup = new Popup
        {
            Child = ghostChip,
            Placement = PlacementMode.Absolute,
            AllowsTransparency = true,
            StaysOpen = true,
            PlacementTarget = this,
            IsOpen = true,
        };
        PositionGhost();
    }

    /// <summary>Drag-source counterpart of <see cref="TryStartItemDrag"/> for a rune palette chip (M04
    /// item 3, rune half): past the drag threshold, shows a rune ghost and starts a real
    /// <see cref="DragDrop.DoDragDrop"/> carrying <see cref="RuneDragPayload"/> — a placed sequence
    /// chip's <see cref="RuneAttach_DragEnter"/> handles the ~1s hold-to-attach while hovering.</summary>
    private void TryStartRuneDrag(Border chip, string runeId, MouseEventArgs e)
    {
        if (_runeDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _runePressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _runePressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _runeDragging = true;
        ShowRuneGhost(runeId);
        chip.GiveFeedback += OnGiveFeedback;
        try
        {
            DragDrop.DoDragDrop(chip, new RuneDragPayload(runeId), DragDropEffects.Link);
        }
        finally
        {
            chip.GiveFeedback -= OnGiveFeedback;
            HideGhost();
            _runeAttachTimer?.Stop();
            _runeAttachTimer = null;
            _runeDragging = false;
        }
    }

    /// <summary>Translucent ghost for an in-flight rune drag — same plumbing as
    /// <see cref="ShowItemGhost"/>, with the rune's own icon.</summary>
    private void ShowRuneGhost(string runeId)
    {
        var ghostChip = new Border
        {
            Width = ChipSize,
            Height = ChipSize,
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(2),
            BorderBrush = (Brush)Application.Current.FindResource("Accent"),
            Background = (Brush)Application.Current.FindResource("SurfaceHi"),
            IsHitTestVisible = false,
            Opacity = 0.7,
            Child = BuildRuneIconContent(runeId),
        };

        _ghostPopup = new Popup
        {
            Child = ghostChip,
            Placement = PlacementMode.Absolute,
            AllowsTransparency = true,
            StaysOpen = true,
            PlacementTarget = this,
            IsOpen = true,
        };
        PositionGhost();
    }

    // ── Translucent ghost following the cursor ──────────────────────────────

    private void ShowGhost(Chip data)
    {
        var accent = SlotAccentBrush(data.Slot);
        var ghostChip = new Border
        {
            Width = ChipSize,
            Height = ChipSize,
            // Square corners (not rounded) to match SkillChip's placed-chip border (Theme.xaml) —
            // the drag ghost should look identical to the chip it's dragging, not just the placed one.
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(2),
            BorderBrush = accent,
            Background = (Brush)Application.Current.FindResource("SurfaceHi"),
            IsHitTestVisible = false,
            Opacity = 0.7,
            // No ClipToBounds here either — see BuildChip's matching note (loop 38 item 6).
            Child = BuildChipContent(data, accent),
        };

        _ghostPopup = new Popup
        {
            Child = ghostChip,
            Placement = PlacementMode.Absolute,
            AllowsTransparency = true,
            StaysOpen = true,
            PlacementTarget = this,
            IsOpen = true,
        };
        PositionGhost();
    }

    private void HideGhost()
    {
        if (_ghostPopup is not null) _ghostPopup.IsOpen = false;
        _ghostPopup = null;
    }

    private void OnGiveFeedback(object sender, GiveFeedbackEventArgs e) => PositionGhost();

    private void PositionGhost()
    {
        if (_ghostPopup is null) return;
        if (!GetCursorPos(out var p)) return;

        var source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        // Absolute placement offsets are device-independent units; the cursor is physical pixels.
        _ghostPopup.HorizontalOffset = p.X / scaleX + 14;
        _ghostPopup.VerticalOffset = p.Y / scaleY + 8;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // ── Sequence drop zone (insert / reorder) ───────────────────────────────

    private void SequenceZone_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(DragPayload)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        ShowIndicatorAt(InsertIndex(e.GetPosition(SequencePanel)));
        e.Handled = true;
    }

    private void SequenceZone_DragLeave(object sender, DragEventArgs e) => RemoveIndicator();

    private void SequenceZone_Drop(object sender, DragEventArgs e)
    {
        RemoveIndicator();
        if (e.Data.GetData(typeof(DragPayload)) is not DragPayload payload) return;

        int index = InsertIndex(e.GetPosition(SequencePanel));
        _droppedOnSequence = true;

        if (payload.FromSequence)
        {
            // Reorder: pull the dragged chip out, then re-insert at the drop index.
            int old = _sequence.FindIndex(c => ReferenceEquals(c, payload.Chip));
            if (old < 0) return;
            _sequence.RemoveAt(old);
            if (old < index) index--; // account for the removed slot
            index = Math.Clamp(index, 0, _sequence.Count);
            _sequence.Insert(index, payload.Chip);
        }
        else
        {
            // Add: clone with a unique id so the same skill can chain more than once.
            var clone = payload.Chip.Node with { Id = $"{payload.Chip.Slot}_{_nodeCounter++}" };
            clone = AutoAttachOnAbilityBonusEffects(clone, payload.Chip.Slot);
            index = Math.Clamp(index, 0, _sequence.Count);
            _sequence.Insert(index, new Chip(clone, payload.Chip.Slot));
        }

        RebuildSequence();
        e.Handled = true;
    }

    // ── Palette zone as a delete target (drag a placed chip up here) ─────────

    private void PaletteZone_DragOver(object sender, DragEventArgs e)
    {
        bool deletable = e.Data.GetDataPresent(typeof(DragPayload))
                         && e.Data.GetData(typeof(DragPayload)) is DragPayload { FromSequence: true };
        e.Effects = deletable ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void PaletteZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DragPayload)) is DragPayload { FromSequence: true } payload)
        {
            _sequence.RemoveAll(c => ReferenceEquals(c, payload.Chip));
            _droppedOnSequence = true; // handled here; suppress the drag-out fallback delete
            RebuildSequence();
            e.Handled = true;
        }
    }

    // ── Sequence rendering + insertion indicator ────────────────────────────

    private void RebuildSequence()
    {
        SequencePanel.Children.Clear();
        _insertIndicator = null;
        var championId = _selectedChampionId;
        foreach (var chip in _sequence)
            SequencePanel.Children.Add(BuildSequenceEntry(chip, championId));
        SequenceHint.Visibility = _sequence.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPreview();
    }

    /// <summary>(loop 487) Recomputes the damage preview for the sequence as it currently stands.
    /// Called from <see cref="RebuildSequence"/>, which every edit already goes through — adding,
    /// removing, reordering, and every knob commit via <see cref="ReplaceChip"/> — so the number
    /// follows the checkbox that changed it.
    ///
    /// <para>The whole point is that a knob left alone shows a SPAN. Ticking it collapses the span to
    /// one number, which is the visible difference between "either could happen" and "I have
    /// decided" that the range machinery has encoded since loop 473 with nowhere to show it.</para>
    /// </summary>
    private void RefreshPreview()
    {
        if (_composition?.ComboRunner is not { } runner || _selectedChampionId is not string championId
            || _sequence.Count == 0)
        {
            PreviewCard.Visibility = Visibility.Collapsed;
            return;
        }

        var graph = new ComboGraph(_sequence.Select(c => c.Node).ToList(), Array.Empty<ComboEdge>());
        var preview = runner.ComputePreview(championId, graph);
        if (preview is null)
        {
            PreviewCard.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewCard.Visibility = Visibility.Visible;
        // A span only when the two ends genuinely differ — a combo with no unset knobs and no crit
        // reads as one number, and padding it with "412 ~ 412" would invent uncertainty.
        PreviewRange.Text = preview.Max - preview.Min >= 1.0
            ? $"{preview.Min:N0} ~ {preview.Max:N0}"
            : $"{preview.Resolved:N0}";
        PreviewBasis.Text = preview.IsLive
            ? $"기준: 현재 게임 · 대상 {(preview.TargetChampion.Length > 0 ? preview.TargetChampion : "미지정")}"
            : $"기준: 레벨 {preview.ReferenceLevel} · 스킬 만렙 · 아이템/룬 없음 · 더미(방어력 0, 체력 1000)"
              + " — 게임 중에는 실제 스탯과 대상으로 다시 계산됩니다";
    }

    /// <summary>Builds one sequence entry: the draggable skill chip on top, and below it the manual
    /// bonus-effect affordance (T3.3/T8) — a small "+추가효과" button plus a mini sub-icon per attached
    /// bonus effect (click a sub-icon to remove it). The chip alone is the drag source; the sub-row is
    /// static so clicking it never starts a drag.</summary>
    private FrameworkElement BuildSequenceEntry(Chip chip, string? championId)
    {
        var column = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 6, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        column.Children.Add(BuildChip(chip, inSequence: true));

        var subRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Width = ChipSize,
            Margin = new Thickness(0, 3, 0, 0),
        };

        var attached = chip.Node.UserBonusEffects;
        if (attached is not null)
            foreach (var eff in attached)
                subRow.Children.Add(BuildBonusBadge(chip, eff));

        // Attached item sub-icon (M04 item 3, item half): dragged-and-held onto this node from the
        // "hypothetical build" row above. Bottom-left of the node, matching the "+fx" badge pattern.
        if (chip.Node.AttachedItemId is { } attachedItemId)
            subRow.Children.Add(BuildAttachedItemBadge(chip, attachedItemId));

        // Attached rune sub-icon (M04 item 3, rune half): dragged-and-held onto this node from the
        // rune palette above. Same bottom-left badge pattern as the attached-item badge.
        if (chip.Node.AttachedRuneId is { } attachedRuneId)
            subRow.Children.Add(BuildAttachedRuneBadge(chip, attachedRuneId));

        // "적중시간" (hit duration) badge (M04 new node-option feature): offered only when this
        // node's curated skill has a duration-scaled hit (an escapable persistent zone/DoT, e.g.
        // Malzahar W "Null Zone" — see SkillHit.IsDurationScaled). Same "+fx" picker pattern:
        // always visible when applicable, opens a small slider popup on click.
        if (championId is not null && SkillDamageDb.GetDurationScaledHit(championId, chip.Slot) is { } durationHit)
            subRow.Children.Add(BuildHitDurationBadge(chip, durationHit));

        // "몇 대" (attack count) badge (M22 Phase 3): offered only when this node's curated skill has a
        // per-attack SUMMON hit (Annie R Tibbers, Ivern R Daisy, Illaoi R tentacles — see
        // SkillHit.PerAttackCalc). Same picker pattern as the hit-duration badge: the user states how
        // many summon hits land, and combo damage = per-hit BIN number × that count.
        if (championId is not null && SkillDamageDb.GetPerAttackHit(championId, chip.Slot) is not null)
            subRow.Children.Add(BuildAttackCountBadge(chip));

        // "거리" (distance/charge) knob (M24 P3/P7): offered when this node's curated skill is
        // distance/charge-scaled (Hecarim E, Fizz R, Nidalee Q — see SkillHit.IsDistanceScaled).
        // UserDistanceFraction 0-1 interpolates the min↔max-distance calcs; unset = the resolved
        // anchor. Same badge+slider picker pattern as the hit-duration/attack-count knobs.
        if (championId is not null && SkillDamageDb.GetDistanceScaledHit(championId, chip.Slot) is not null)
            subRow.Children.Add(BuildDistanceBadge(chip));

        // "몇 스택" (stack count) knob (M25 §11.G): offered when this node's curated skill is stack-scaled
        // (a BuffCounter per-stack term, e.g. Nasus Q Siphoning-Strike stacks — see SkillHit.StackScaled).
        // UserStackCount feeds the live stack count; unset = 0 = the un-stacked floor. Same badge+slider
        // picker pattern as the attack-count/distance knobs.
        if (championId is not null && SkillDamageDb.GetStackScaledHit(championId, chip.Slot) is { } stackHit)
            subRow.Children.Add(BuildStackCountBadge(chip, stackHit.MaxStackTier));

        // "최대 데미지" (max damage / 벽꿍-style) checkbox (M28 §1): offered when this node's curated
        // skill has a UserAssumed conditional-bonus hit — a wall/debuff/positional/terrain fact the
        // Live Client API cannot observe at all (e.g. K'Sante R's wall-impact bonus — see
        // SkillHit.IsConditional + ConditionType.HitsWall). Unlike the slider knobs above, this is a
        // plain boolean: a single click toggles ComboNode.UserConditionMet and commits immediately
        // (no popup). Default OFF is the P2 conservative floor.
        if (championId is not null && SkillDamageDb.GetConditionalHit(championId, chip.Slot) is { } conditionalHit)
            subRow.Children.Add(BuildConditionalBadge(chip, conditionalHit));

        // "+" affordance to attach a bonus effect. Disabled when the champion exposes none.
        var attachable = championId is null
            ? (IReadOnlyList<AttachableBonusEffect>)Array.Empty<AttachableBonusEffect>()
            : SkillDamageDb.GetAttachableBonusEffects(championId, chip.Slot);
        var addButton = new Button
        {
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Content = "＋fx",
            FontSize = 10,
            Padding = new Thickness(4, 1, 4, 1),
            MinWidth = 0,
            IsEnabled = attachable.Count > 0,
            ToolTip = Localization.L("combo.addFx"),
        };
        addButton.Click += (_, _) => OpenBonusPicker(addButton, chip, attachable);
        subRow.Children.Add(addButton);

        column.Children.Add(subRow);
        return column;
    }

    /// <summary>A mini sub-icon for one attached bonus effect: its slot letter on the slot's accent
    /// color; clicking it removes the effect from the node.</summary>
    private Border BuildBonusBadge(Chip chip, AttachableBonusEffect eff)
    {
        var accent = SlotAccentBrush(eff.Slot);
        var badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Background = KeyBadgeBrush,
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = Localization.F("combo.removeFx", BonusLabel(eff)),
            Child = new TextBlock
            {
                Text = eff.Slot,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveBonus(chip, eff); };
        return badge;
    }

    /// <summary>A mini sub-icon for the node's attached item (M04 item 3, item half): the item's real
    /// icon at badge size, reusing whatever <see cref="_itemIcons"/> already cached for its build-row
    /// chip; clicking it detaches the item. Same visual pattern as <see cref="BuildBonusBadge"/>.</summary>
    private Border BuildAttachedItemBadge(Chip chip, string itemId)
    {
        var name = ItemRepository.Get(itemId)?.Name ?? itemId;
        var badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.FindResource("Accent"),
            BorderThickness = new Thickness(1),
            Background = KeyBadgeBrush,
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = Localization.F("combo.itemDetach", name),
            Child = BuildSmallItemIcon(itemId),
        };
        badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveItem(chip); };
        return badge;
    }

    /// <summary>Badge-sized (18x18) item icon: the already-cached <see cref="_itemIcons"/> image, or a
    /// letter fallback (matches <see cref="BuildItemIconContent"/>'s fallback, at badge scale).</summary>
    private UIElement BuildSmallItemIcon(string itemId)
    {
        if (_itemIcons.TryGetValue(itemId, out var cached))
            return new Image { Source = cached, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };

        var name = ItemRepository.Get(itemId)?.Name ?? itemId;
        return new TextBlock
        {
            Text = name.Length > 0 ? name[..1] : "?",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }

    /// <summary>A mini sub-icon for the node's attached rune (M04 item 3, rune half): the rune's real
    /// icon at badge size, reusing whatever <see cref="_runeIcons"/> already cached for its palette
    /// chip; clicking it detaches the rune. Same visual pattern as <see cref="BuildAttachedItemBadge"/>.</summary>
    private Border BuildAttachedRuneBadge(Chip chip, string runeId)
    {
        var name = RuneRepository.Get(runeId)?.Name ?? runeId;
        var badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.FindResource("Accent"),
            BorderThickness = new Thickness(1),
            Background = KeyBadgeBrush,
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = $"룬 분리: {name}", // hardcoded (Localization.cs is out of edit scope; no combo.runeDetach key)
            Child = BuildSmallRuneIcon(runeId),
        };
        badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveRune(chip); };
        return badge;
    }

    /// <summary>Badge-sized (18x18) rune icon: the already-cached <see cref="_runeIcons"/> image, or a
    /// letter fallback (matches <see cref="BuildRuneIconContent"/>'s fallback, at badge scale).</summary>
    private UIElement BuildSmallRuneIcon(string runeId)
    {
        if (_runeIcons.TryGetValue(runeId, out var cached))
            return new Image { Source = cached, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };

        var name = RuneRepository.Get(runeId)?.Name ?? runeId;
        return new TextBlock
        {
            Text = name.Length > 0 ? name[..1] : "?",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }

    /// <summary>A small "적중시간" (hit duration) badge for a node whose curated skill has a
    /// duration-scaled hit (M04 new node-option feature — see <see cref="SkillHit.IsDurationScaled"/>):
    /// shows the currently-set seconds (or "0s" when unset, matching the honest 0-damage default),
    /// and opens a slider popup (<see cref="OpenHitDurationPicker"/>) to set it. Same visual pattern
    /// as <see cref="BuildBonusBadge"/>/<see cref="BuildAttachedItemBadge"/>, always shown (not
    /// conditional on being "attached") since a duration is a plain editable node field, not an
    /// attach/detach toggle.</summary>
    private Border BuildHitDurationBadge(Chip chip, SkillHit hit)
    {
        double max = hit.MaxDurationSeconds ?? 0;
        double current = Math.Clamp(chip.Node.UserHitDurationSeconds ?? 0, 0, max);
        var badge = new Border
        {
            Width = 30,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.FindResource("Accent"),
            BorderThickness = new Thickness(1),
            Background = KeyBadgeBrush,
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = Localization.F("combo.hitDuration", max.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)),
            Child = new TextBlock
            {
                Text = current.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenHitDurationPicker(badge, chip, hit); };
        return badge;
    }

    /// <summary>Opens a small popup under the duration badge with a Slider (0 to the hit's real
    /// <see cref="SkillHit.MaxDurationSeconds"/>) and a text label showing the live value / max. The
    /// slider only updates its own label while dragging (rebuilding the whole sequence panel on every
    /// drag tick would tear down this popup's own anchor); the node's
    /// <see cref="ComboNode.UserHitDurationSeconds"/> is committed ONCE, on popup close, through
    /// <see cref="ReplaceChip"/> — the same re-render path every other node edit (bonus effects,
    /// attached items) already uses.</summary>
    private void OpenHitDurationPicker(Border anchor, Chip chip, SkillHit hit)
    {
        double max = hit.MaxDurationSeconds ?? 0;
        double initial = Math.Clamp(chip.Node.UserHitDurationSeconds ?? 0, 0, max);

        var label = new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
        };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = max,
            Value = initial,
            Width = 120,
            SmallChange = 0.1,
            LargeChange = 0.5,
        };
        void UpdateLabel(double v) => label.Text =
            $"{v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s / {max.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}s";
        UpdateLabel(initial);
        slider.ValueChanged += (_, e) => UpdateLabel(e.NewValue);

        var panel = new StackPanel { Margin = new Thickness(6) };
        panel.Children.Add(label);
        panel.Children.Add(slider);

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            Child = new Border
            {
                Background = (Brush)Application.Current.FindResource("Surface"),
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = panel,
            },
        };
        popup.Closed += (_, _) =>
        {
            double committed = Math.Round(slider.Value, 1);
            if (Math.Abs(committed - initial) > 0.001)
                ReplaceChip(chip, chip with { Node = chip.Node with { UserHitDurationSeconds = committed } });
        };
        popup.IsOpen = true;
    }

    /// <summary>(M24 P3/P7) A small "거리" (distance/charge) badge for a distance-scaled node
    /// (Hecarim E, Fizz R, Nidalee Q). Shows the assumed distance % (or "자동" when unset = the
    /// resolved anchor) and opens <see cref="OpenDistancePicker"/>. Same pattern as
    /// <see cref="BuildHitDurationBadge"/>.</summary>
    private Border BuildDistanceBadge(Chip chip)
    {
        double? f = chip.Node.UserDistanceFraction;
        string text = f is { } v ? ((int)Math.Round(Math.Clamp(v, 0, 1) * 100)) + "%" : "자동";
        var badge = new Border
        {
            Width = 36,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.FindResource("Accent"),
            BorderThickness = new Thickness(1),
            Background = KeyBadgeBrush,
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = "거리/충전 — 0%=근접, 100%=최대 사거리 (미설정=자동)",
            Child = new TextBlock
            {
                Text = "↔" + text,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenDistancePicker(badge, chip); };
        return badge;
    }

    /// <summary>Opens a popup with a 0-100% slider under the distance badge; commits
    /// <see cref="ComboNode.UserDistanceFraction"/> (0-1) ONCE on close via <see cref="ReplaceChip"/> —
    /// same re-render path as the duration/attack-count pickers.</summary>
    private void OpenDistancePicker(Border anchor, Chip chip)
    {
        double initial = Math.Clamp(chip.Node.UserDistanceFraction ?? 1.0, 0, 1);
        var label = new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
        };
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = initial * 100, Width = 120, SmallChange = 5, LargeChange = 25 };
        void UpdateLabel(double pct) => label.Text =
            $"거리 {pct.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}% (0=근접 ~ 100=최대)";
        UpdateLabel(slider.Value);
        slider.ValueChanged += (_, e) => UpdateLabel(e.NewValue);

        var panel = new StackPanel { Margin = new Thickness(6) };
        panel.Children.Add(label);
        panel.Children.Add(slider);

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            Child = new Border
            {
                Background = (Brush)Application.Current.FindResource("Surface"),
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = panel,
            },
        };
        popup.Closed += (_, _) =>
        {
            double committed = Math.Round(slider.Value / 100.0, 2);
            if (Math.Abs(committed - initial) > 0.001)
                ReplaceChip(chip, chip with { Node = chip.Node with { UserDistanceFraction = committed } });
        };
        popup.IsOpen = true;
    }

    /// <summary>A small "몇 대" (attack count) badge for a node whose curated skill has a per-attack
    /// SUMMON hit (M22 Phase 3 — see <see cref="SkillHit.PerAttackCalc"/>, e.g. Annie R Tibbers, Ivern
    /// R Daisy, Illaoi R tentacles): shows the currently-assumed hit count (or "0타" when unset,
    /// matching the honest 0-damage default) and opens a picker (<see cref="OpenAttackCountPicker"/>)
    /// to set it. Same visual pattern as <see cref="BuildHitDurationBadge"/>.</summary>
    private Border BuildAttackCountBadge(Chip chip)
    {
        int current = Math.Max(0, chip.Node.UserAttackCount ?? 0);
        var badge = new Border
        {
            Width = 30,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.FindResource("Accent"),
            BorderThickness = new Thickness(1),
            Background = KeyBadgeBrush,
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = "소환 히트수 — 소환물이 몇 대 맞출지 가정 (× 히트당 데미지)",
            Child = new TextBlock
            {
                Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture) + "타",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenAttackCountPicker(badge, chip); };
        return badge;
    }

    /// <summary>Opens a small popup under the attack-count badge with an integer Slider (0..20) and a
    /// live "N타" label. Mirrors <see cref="OpenHitDurationPicker"/> exactly: the node's
    /// <see cref="ComboNode.UserAttackCount"/> is committed ONCE, on popup close, via
    /// <see cref="ReplaceChip"/>. Unset/0 → 0 damage from the per-attack hit (honest default; a
    /// summon's total is player-uptime dependent, not a fixed cast number).</summary>
    private void OpenAttackCountPicker(Border anchor, Chip chip)
    {
        const int maxAttacks = 20;
        int initial = Math.Clamp(chip.Node.UserAttackCount ?? 0, 0, maxAttacks);

        var label = new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
        };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = maxAttacks,
            Value = initial,
            Width = 120,
            SmallChange = 1,
            LargeChange = 5,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
        };
        void UpdateLabel(double v) => label.Text =
            $"{(int)Math.Round(v)}타 / {maxAttacks}타";
        UpdateLabel(initial);
        slider.ValueChanged += (_, e) => UpdateLabel(e.NewValue);

        var panel = new StackPanel { Margin = new Thickness(6) };
        panel.Children.Add(label);
        panel.Children.Add(slider);

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            Child = new Border
            {
                Background = (Brush)Application.Current.FindResource("Surface"),
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = panel,
            },
        };
        popup.Closed += (_, _) =>
        {
            int committed = (int)Math.Round(slider.Value);
            if (committed != initial)
                ReplaceChip(chip, chip with { Node = chip.Node with { UserAttackCount = committed } });
        };
        popup.IsOpen = true;
    }

    /// <summary>(M28 §1 "binary conditional hit" — docs/modules/M28_NODE_OPTION_UX.md) A "최대 데미지"
    /// checkbox badge for a node whose curated skill has a UserAssumed conditional-bonus hit (a wall/
    /// debuff/positional/terrain fact the Live Client API cannot observe — e.g. K'Sante R's wall-hit,
    /// <paramref name="conditionalHit"/>). Unlike the slider badges above, this is a plain boolean: ON
    /// (gold, checked) means the user asserts the condition held (the hit's MetCalc contributes); OFF
    /// (dim, unchecked — the default/floor) means it's excluded entirely. A single click TOGGLES
    /// <see cref="ComboNode.UserConditionMet"/> and commits IMMEDIATELY via <see cref="ReplaceChip"/>
    /// — no popup, since there is nothing to range-pick.</summary>
    /// <summary>What the checkbox is asserting, in the user's terms. A resource condition reads
    /// differently from a terrain one: for fury/ferocity the box is an ASSUMPTION that overrides the
    /// live bar, which is the whole reason it is offered outside a game.</summary>
    private static string ConditionBadgeTooltip(SkillHit hit)
    {
        bool parsed = Enum.TryParse<Overlay.Core.Combo.ConditionType>(hit.ConditionType, out var t);
        // (loop 479) An Upgraded condition is not a "maximum damage" assumption — it is which FORM of
        // the ability this is (빅토르 진화 E, 우디르 각성, 신드라 초월 W, 진 4번째 탄). Saying "최대
        // 데미지" there reads as a worst-case knob rather than the toggle the user actually wants.
        if (parsed && t == Overlay.Core.Combo.ConditionType.FromBehind)
            return "뒤에서 적중 가정 — 샤코 백스탭처럼 대상 뒤에서 맞혀야 붙는 추가 피해입니다. "
                   + "꺼짐이 정면 적중(보수적 기본값).";
        if (parsed && t == Overlay.Core.Combo.ConditionType.SweetSpot)
            return "정타(스윗스팟) 가정 — 아트록스 Q 바깥날, 제라스 W 중앙, 릴리아 W 안쪽 원처럼 "
                   + "맞히는 위치로 배수가 붙는 스킬입니다. 꺼짐이 일반 적중(보수적 기본값).";
        if (parsed && t == Overlay.Core.Combo.ConditionType.Upgraded)
            return "강화/진화 형태 — 켜면 강화된 형태로 계산합니다. 꺼짐이 기본 형태(보수적 기본값)이고, "
                   + "건드리지 않으면 두 형태를 [기본~강화] 범위로 함께 보여줍니다.";
        bool auto = parsed && !Overlay.Core.Combo.ConditionResolution.IsUserAssumed(t);
        return auto
            ? $"강화 스킬 가정 ({hit.ConditionType} {hit.ConditionValue:0.#}) — 켜면 자원이 찼다고 보고 강화 수치로 계산합니다. "
              + "건드리지 않으면 게임 중 실제 자원값을 그대로 씁니다."
            : $"최대 데미지 가정 ({hit.ConditionType}) — 켜면 조건이 충족됐다고 가정(예: 벽꿍), 꺼짐이 보수적 기본값";
    }

    /// <summary>(loop 479) The two words on the badge itself. Everything else is a "이만큼까지 나올 수
    /// 있다" assumption, so "최대" is right; an Upgraded condition is a FORM, so it says 강화.</summary>
    private static string ConditionBadgeLabel(SkillHit hit) =>
        Enum.TryParse<Overlay.Core.Combo.ConditionType>(hit.ConditionType, out var t)
        ? t switch
        {
            Overlay.Core.Combo.ConditionType.Upgraded => "강화",
            Overlay.Core.Combo.ConditionType.SweetSpot => "정타",
            Overlay.Core.Combo.ConditionType.FromBehind => "뒤",
            _ => "최대",
        }
        : "최대";

    private Border BuildConditionalBadge(Chip chip, SkillHit conditionalHit)
    {
        bool isOn = chip.Node.UserConditionMet == true;
        string resourceKey = isOn ? "Accent" : "TextDim";
        var badge = new Border
        {
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.FindResource(resourceKey),
            BorderThickness = new Thickness(1),
            Background = isOn ? KeyBadgeBrush : Brushes.Transparent,
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = ConditionBadgeTooltip(conditionalHit),
            Child = new TextBlock
            {
                Text = (isOn ? "☑ " : "☐ ") + ConditionBadgeLabel(conditionalHit),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource(resourceKey),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // (loop 473) Two states, not three: UNSET (range spans [base, met]) and ON (fixed at the
            // met value). Clicking an ON badge returns to UNSET rather than pinning the base, so the
            // range the user was shown before their first click is always one click away again.
            ReplaceChip(chip, chip with { Node = chip.Node with { UserConditionMet = isOn ? null : true } });
        };
        return badge;
    }

    /// <summary>(M25 §11.G) A small "몇 스택" (stack count) badge for a node whose curated skill is
    /// stack-scaled (<see cref="SkillHit.StackScaled"/>, e.g. Nasus Q Siphoning-Strike stacks): shows the
    /// currently-assumed stack count (or "0스택" when unset, matching the honest un-stacked floor) and
    /// opens a picker (<see cref="OpenStackCountPicker"/>) to set it. Same visual pattern as
    /// <see cref="BuildAttackCountBadge"/>.</summary>
    private Border BuildStackCountBadge(Chip chip, int maxStacks)
    {
        int current = Math.Max(0, chip.Node.UserStackCount ?? 0);
        var badge = new Border
        {
            Width = 42,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.FindResource("Accent"),
            BorderThickness = new Thickness(1),
            Background = KeyBadgeBrush,
            Margin = new Thickness(0, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = "스택 수 — 버프 스택(예: 나수스 Q 흡수의 일격)당 추가 데미지 (미설정=0=미스택)",
            Child = new TextBlock
            {
                Text = current.ToString(System.Globalization.CultureInfo.InvariantCulture) + "스택",
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenStackCountPicker(badge, chip, maxStacks); };
        return badge;
    }

    /// <summary>Opens a small popup under the stack-count badge with an integer Slider (0..600) and a
    /// live "N스택" label. Mirrors <see cref="OpenAttackCountPicker"/>: the node's
    /// <see cref="ComboNode.UserStackCount"/> is committed ONCE, on popup close, via
    /// <see cref="ReplaceChip"/>. Unset/0 → the un-stacked floor (honest default; the in-game stack
    /// count isn't observable, so it stays a user knob).</summary>
    /// <param name="maxTier">(loop 484) The cap a TIERED hit states through its own tier list (Locke
    /// has three nails, so 3). 0 for an ordinary BuffCounter stack, which has no natural cap and keeps
    /// the old 600 — dragging a 600-wide slider to pick 1 of 3 nails would be unusable.</param>
    private void OpenStackCountPicker(Border anchor, Chip chip, int maxTier)
    {
        int maxStacks = maxTier > 0 ? maxTier : 600;
        int initial = Math.Clamp(chip.Node.UserStackCount ?? 0, 0, maxStacks);

        var label = new TextBlock
        {
            FontSize = 10,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
        };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = maxStacks,
            Value = initial,
            Width = 140,
            SmallChange = 5,
            LargeChange = 50,
            IsSnapToTickEnabled = true,
            TickFrequency = 5,
        };
        void UpdateLabel(double v) => label.Text =
            $"{(int)Math.Round(v)}스택 / {maxStacks}";
        UpdateLabel(initial);
        slider.ValueChanged += (_, e) => UpdateLabel(e.NewValue);

        var panel = new StackPanel { Margin = new Thickness(6) };
        panel.Children.Add(label);
        panel.Children.Add(slider);

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            Child = new Border
            {
                Background = (Brush)Application.Current.FindResource("Surface"),
                BorderBrush = (Brush)Application.Current.FindResource("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = panel,
            },
        };
        popup.Closed += (_, _) =>
        {
            int committed = (int)Math.Round(slider.Value);
            if (committed != initial)
                ReplaceChip(chip, chip with { Node = chip.Node with { UserStackCount = committed } });
        };
        popup.IsOpen = true;
    }

    /// <summary>Opens a context menu of the champion's attachable bonus effects under the "+" button;
    /// choosing one attaches it to the chip's node.</summary>
    private void OpenBonusPicker(Button anchor, Chip chip, IReadOnlyList<AttachableBonusEffect> attachable)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var already = chip.Node.UserBonusEffects ?? (IReadOnlyList<AttachableBonusEffect>)Array.Empty<AttachableBonusEffect>();
        foreach (var eff in attachable)
        {
            var item = new MenuItem
            {
                Header = BonusLabel(eff),
                IsChecked = already.Contains(eff),
            };
            var captured = eff;
            item.Click += (_, _) => AttachBonus(chip, captured);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Human-readable label for a bonus effect: slot + trigger + first hit's damage type,
    /// e.g. "P · 온히트 · Magic". Localized trigger text; type stays the canonical name.</summary>
    private static string BonusLabel(AttachableBonusEffect eff)
    {
        string trigger = eff.Effect.Trigger switch
        {
            BonusTrigger.OnHit => Localization.L("combo.fxOnHit"),
            BonusTrigger.OnAbility => Localization.L("combo.fxOnAbility"),
            _ => Localization.L("combo.fxSelf"),
        };
        string type = eff.Effect.Hits.Length > 0 ? eff.Effect.Hits[0].Type.ToString() : string.Empty;
        return $"{eff.Slot} · {trigger}" + (type.Length > 0 ? $" · {type}" : string.Empty);
    }

    /// <summary>Auto-populates a newly-added Q/W/E/R skill node's <see cref="ComboNode.UserBonusEffects"/>
    /// with the champion's <see cref="BonusTrigger.OnAbility"/> bonus effect(s) (an on-ability-cast
    /// passive proc, e.g. Warwick's Eternal Hunger-style on-hit passive if it were OnAbility instead —
    /// today's concrete OnAbility example is any curated passive whose <c>bonusEffects</c> trigger is
    /// "onAbility"), so the common case needs no manual "+fx" pick on every node. Called once, at the
    /// moment a palette chip is dropped into the sequence (<see cref="SequenceZone_Drop"/>'s "Add"
    /// branch) — NOT on reorder, so an already-placed node's manual edits are never touched. The
    /// existing "+fx" picker (<see cref="OpenBonusPicker"/>) remains fully functional afterward: the
    /// user can remove this auto-attached effect (its sub-icon badge has the same click-to-remove
    /// behavior as a manually attached one, see <see cref="BuildBonusBadge"/>) or attach additional
    /// OnHit/Self effects on top.
    ///
    /// <b>Judgment call:</b> if the champion has MORE THAN ONE OnAbility effect, ALL are attached (not
    /// just the first) — <see cref="ComboNode.UserBonusEffects"/> already supports a list, and an
    /// OnAbility passive is defined to fire on every Q/W/E/R cast, so there is no principled way to
    /// prefer one over another; omitting any of them would silently under-represent real damage.
    ///
    /// AA and P palette chips (and drops with no selected champion) are left untouched — this feature
    /// is scoped to Q/W/E/R exactly as specified.</summary>
    private ComboNode AutoAttachOnAbilityBonusEffects(ComboNode node, string slot)
    {
        if (_selectedChampionId is not { } championId) return node;
        if (slot is not ("Q" or "W" or "E" or "R")) return node;

        var onAbility = SkillDamageDb.GetAttachableBonusEffects(championId, slot)
            .Where(eff => eff.Effect.Trigger == BonusTrigger.OnAbility)
            .ToList();
        if (onAbility.Count == 0) return node;

        return node with { UserBonusEffects = onAbility };
    }

    /// <summary>Attaches a bonus effect to the chip's node (no duplicates) and re-renders.</summary>
    private void AttachBonus(Chip chip, AttachableBonusEffect eff)
    {
        var current = chip.Node.UserBonusEffects?.ToList() ?? new List<AttachableBonusEffect>();
        if (current.Contains(eff)) return; // already attached
        current.Add(eff);
        ReplaceChip(chip, chip with { Node = chip.Node with { UserBonusEffects = current } });
    }

    /// <summary>Removes a bonus effect from the chip's node and re-renders.</summary>
    private void RemoveBonus(Chip chip, AttachableBonusEffect eff)
    {
        var current = chip.Node.UserBonusEffects?.ToList() ?? new List<AttachableBonusEffect>();
        current.Remove(eff);
        ReplaceChip(chip, chip with { Node = chip.Node with { UserBonusEffects = current.Count > 0 ? current : null } });
    }

    /// <summary>Swaps <paramref name="oldChip"/> for <paramref name="newChip"/> in the sequence (by
    /// reference) and rebuilds. The immutable records mean a node edit produces a new chip instance.</summary>
    private void ReplaceChip(Chip oldChip, Chip newChip)
    {
        int index = _sequence.FindIndex(c => ReferenceEquals(c, oldChip));
        if (index < 0) return;
        _sequence[index] = newChip;
        RebuildSequence();
    }

    /// <summary>Insertion index for a cursor position over the sequence panel: the first chip
    /// whose row contains the cursor and whose horizontal midpoint is right of it; otherwise
    /// the end.</summary>
    private int InsertIndex(Point pos)
    {
        int logical = 0;
        foreach (UIElement child in SequencePanel.Children)
        {
            if (ReferenceEquals(child, _insertIndicator)) continue;
            if (child is not FrameworkElement fe) { logical++; continue; }

            var tl = fe.TranslatePoint(new Point(0, 0), SequencePanel);
            double midX = tl.X + fe.ActualWidth / 2;
            double bottom = tl.Y + fe.ActualHeight;
            if (pos.Y < bottom && pos.X < midX)
                return logical;
            logical++;
        }
        return logical;
    }

    private void ShowIndicatorAt(int logicalIndex)
    {
        RemoveIndicator();
        _insertIndicator = new Border
        {
            Width = 4,
            Height = 52,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)Application.Current.FindResource("Accent"),
            Margin = new Thickness(0, 0, 6, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        int childIndex = Math.Clamp(logicalIndex, 0, SequencePanel.Children.Count);
        SequencePanel.Children.Insert(childIndex, _insertIndicator);
    }

    private void RemoveIndicator()
    {
        if (_insertIndicator is not null)
        {
            SequencePanel.Children.Remove(_insertIndicator);
            _insertIndicator = null;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _sequence.Clear();
        RebuildSequence();
    }

    // ── Hotkey capture ──────────────────────────────────────────────────────

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        HotkeyButton.Content = Localization.L("combo.pressKey");
        Keyboard.Focus(HotkeyButton);
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore lone modifier presses — wait for the first non-modifier key.
        if (IsModifierKey(key)) { e.Handled = true; return; }

        var token = KeyToToken(key);
        if (token is null) { e.Handled = true; return; } // unmappable; keep waiting

        var parts = new List<string>(5);
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("CTRL");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("ALT");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("SHIFT");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("WIN");
        parts.Add(token);

        _capturedHotkey = string.Join("+", parts);
        _capturing = false;
        HotkeyButton.Content = _capturedHotkey;
        e.Handled = true;
    }

    private void HotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturing) return;
        _capturing = false;
        HotkeyButton.Content = _capturedHotkey.Length > 0 ? _capturedHotkey : Localization.L("combo.setHotkey");
    }

    // Second (optional) hotkey capture — same capture logic as the first control, independent
    // state, so a combo can bind two chords that both trigger it (M13 loop-38 request).
    private void HotkeyButton2_Click(object sender, RoutedEventArgs e)
    {
        _capturing2 = true;
        HotkeyButton2.Content = Localization.L("combo.pressKey");
        Keyboard.Focus(HotkeyButton2);
    }

    private void HotkeyButton2_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing2) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (IsModifierKey(key)) { e.Handled = true; return; }

        var token = KeyToToken(key);
        if (token is null) { e.Handled = true; return; }

        var parts = new List<string>(5);
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("CTRL");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("ALT");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("SHIFT");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("WIN");
        parts.Add(token);

        _capturedHotkey2 = string.Join("+", parts);
        _capturing2 = false;
        HotkeyButton2.Content = _capturedHotkey2;
        e.Handled = true;
    }

    private void HotkeyButton2_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_capturing2) return;
        _capturing2 = false;
        HotkeyButton2.Content = _capturedHotkey2.Length > 0 ? _capturedHotkey2 : "＋";
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin or Key.System;

    /// <summary>Normalizes a WPF <see cref="Key"/> to the token M13's HotkeyCombo parser
    /// understands (digits, letters, F-keys, common named keys). Returns null for keys that
    /// have no sensible token so capture keeps waiting.</summary>
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
            Key.Back => "BACKSPACE",
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

    // ── Save ────────────────────────────────────────────────────────────────

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var editor = _composition?.ComboEditor;
        if (editor is null)
        {
            ShowMessage(Localization.L("combo.initializing"), isError: true);
            return;
        }
        if (_selectedChampionId is not string championId)
        {
            ShowMessage(Localization.L("combo.selectChampion"), isError: true);
            return;
        }
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ShowMessage(Localization.L("combo.enterName"), isError: true);
            return;
        }
        if (_sequence.Count == 0)
        {
            ShowMessage(Localization.L("combo.addSkill"), isError: true);
            return;
        }

        try
        {
            var draft = editor.CreateCombo(championId, name);
            foreach (var chip in _sequence)
                editor.AddNode(draft.Id, chip.Node);
            editor.SaveCombo(draft.Id);
            if (_capturedHotkey.Length > 0)
                editor.BindHotkey(draft.Id, _capturedHotkey);
            // Second, independent chord (M13 loop-38): skip if identical to the first — that
            // would just rewrite the same hotkeys.comboSlots entry, not add a second one.
            if (_capturedHotkey2.Length > 0
                && !string.Equals(_capturedHotkey2, _capturedHotkey, StringComparison.OrdinalIgnoreCase))
                editor.BindHotkey(draft.Id, _capturedHotkey2);

            // (loop 173) If this Save came from editing an existing combo (수정), drop the ORIGINAL
            // saved entry now that the edited version is persisted under a new id — so it replaces it
            // instead of leaving a duplicate. Only combos.saved is removed, NOT the hotkey mapping: the
            // BindHotkey calls above already re-pointed the same hotkey(s) to this new combo, so deleting
            // the mapping here would unbind what we just bound.
            if (_editingComboId is { } oldId && !string.Equals(oldId, draft.Id, StringComparison.Ordinal))
                _composition?.Config.Set("combos.saved." + oldId, null);
            _editingComboId = null;
        }
        catch (Exception ex)
        {
            ShowMessage(Localization.F("combo.saveFailed", ex.Message), isError: true);
            return;
        }

        ShowMessage(Localization.F("combo.saveOk", name), isError: false);
        NameBox.Clear();
        _capturedHotkey = string.Empty;
        HotkeyButton.Content = Localization.L("combo.setHotkey");
        _capturedHotkey2 = string.Empty;
        HotkeyButton2.Content = "＋";
        _sequence.Clear();
        RebuildSequence();
        RefreshSavedList();

        // Re-register combo hotkeys so a combo bound AFTER the overlay was first wired
        // actually fires in-game (otherwise its hotkey was never registered).
        _composition?.RefreshComboHotkeys();
    }

    private void ShowMessage(string text, bool isError)
    {
        SaveMessage.Text = text;
        SaveMessage.Foreground = (Brush)Application.Current.FindResource(isError ? "Danger" : "Success");
        SaveMessage.Visibility = Visibility.Visible;
    }

    // ── Saved list ──────────────────────────────────────────────────────────

    /// <summary>Reads combos.saved (id → serialized SavedCombo) from the shared config and lists
    /// each with its bound hotkey(s) (reverse-mapped from hotkeys.comboSlots; up to 2 per combo,
    /// see <see cref="BuildHotkeyReverseMap"/>).</summary>
    public void RefreshSavedList()
    {
        if (SavedList is null) return; // guard calls before InitializeComponent finishes
        SavedList.Items.Clear();
        if (_composition is null) { EmptySaved.Visibility = Visibility.Visible; return; }

        // Task 3: only show combos for the currently-selected champion. With no champion picked the
        // list is empty (there is no champion to scope it to).
        var selectedChampionId = _selectedChampionId;

        var hotkeysByCombo = BuildHotkeyReverseMap();
        int count = 0;

        if (selectedChampionId is not null
            && _composition.Config.Get("combos.saved") is IDictionary<string, object?> saved)
        {
            foreach (var (id, value) in saved)
            {
                var combo = TryDeserialize(value?.ToString());
                if (combo is null) continue;
                if (!string.Equals(combo.ChampionId, selectedChampionId, StringComparison.Ordinal)) continue;

                var hotkeys = hotkeysByCombo.TryGetValue(id, out var hk) ? hk : new List<string>();
                SavedList.Items.Add(BuildSavedCard(combo, hotkeys));
                count++;
            }
        }

        EmptySaved.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Deletes a saved combo (task 7): removes <c>combos.saved.{id}</c> and every bound
    /// <c>hotkeys.comboSlots.{hotkey}::{championId}</c> mapping (up to 2, set to null so each entry
    /// is dropped), then refreshes the filtered list and re-registers combo hotkeys so the deleted
    /// combo's key(s) stop firing. <paramref name="championId"/> is needed to reconstruct the
    /// composite slot key (see <see cref="ComboEditor.ComposeSlotKey"/>) — hotkeys are now scoped
    /// per champion, not global (loop 44 bug 1/2 fix).</summary>
    private void DeleteCombo(string comboId, string championId, IReadOnlyList<string> hotkeys)
    {
        var config = _composition?.Config;
        if (config is null) return;

        config.Set("combos.saved." + comboId, null);
        foreach (var hotkey in hotkeys)
            if (!string.IsNullOrEmpty(hotkey))
                config.Set("hotkeys.comboSlots." + ComboEditor.ComposeSlotKey(hotkey, championId), null);

        RefreshSavedList();
        _composition?.RefreshComboHotkeys();
    }

    /// <summary>(loop 173) "수정" (edit): loads an existing saved combo back into the sequence editor —
    /// its nodes, name, and bound hotkey(s) — and marks it so the next Save REPLACES the original
    /// (see <see cref="_editingComboId"/>) instead of creating a duplicate. The saved list is already
    /// scoped to the selected champion, so the palette/icons are the right set; no champion switch is
    /// needed. Best-effort: a corrupt/missing combo just shows an error and leaves the editor as-is.</summary>
    private void LoadComboForEdit(SavedCombo combo, IReadOnlyList<string> hotkeys)
    {
        var editor = _composition?.ComboEditor;
        if (editor is null) { ShowMessage(Localization.L("combo.initializing"), isError: true); return; }

        List<ComboNode> nodes;
        try
        {
            nodes = editor.LoadCombo(combo.Id).Graph.Nodes.ToList();
        }
        catch
        {
            ShowMessage(Localization.F("combo.saveFailed", combo.Id), isError: true);
            return;
        }

        _sequence.Clear();
        foreach (var node in nodes)
        {
            int u = node.Id.IndexOf('_');
            _sequence.Add(new Chip(node, u < 0 ? node.Id : node.Id[..u]));
        }
        RebuildSequence();

        NameBox.Text = combo.Name;
        _capturedHotkey = hotkeys.Count > 0 ? hotkeys[0] : string.Empty;
        _capturedHotkey2 = hotkeys.Count > 1 ? hotkeys[1] : string.Empty;
        HotkeyButton.Content = _capturedHotkey.Length > 0 ? _capturedHotkey : Localization.L("combo.setHotkey");
        HotkeyButton2.Content = _capturedHotkey2.Length > 0 ? _capturedHotkey2 : "＋";
        _editingComboId = combo.Id;

        // Literal (not a Localization key) to avoid depending on a new resource string in this batch.
        var editingName = string.IsNullOrEmpty(combo.Name) ? Localization.L("combo.noName") : combo.Name;
        ShowMessage($"수정 중: {editingName} — 저장하면 기존 콤보를 대체합니다.", isError: false);
    }

    /// <summary>Reverse-maps <c>hotkeys.comboSlots</c> (composite <c>{hotkey}::{championId}</c> key
    /// -&gt; comboId — see <see cref="ComboEditor.ComposeSlotKey"/>) into comboId -&gt; its bound RAW
    /// hotkeys (in encounter order; normally at most 2, since the UI only ever binds 2 chords per
    /// combo — see <see cref="HotkeyButton2_Click"/>), for display (the UI shows e.g. "A", never the
    /// "A::Ahri" composite form). The forward map is naturally N:1 already (keyed by hotkey), so no
    /// M14 schema change was needed to support a second chord.</summary>
    private Dictionary<string, List<string>> BuildHotkeyReverseMap()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (_composition?.Config.Get("hotkeys.comboSlots") is IDictionary<string, object?> slots)
        {
            foreach (var (compositeKey, comboId) in slots)
            {
                var id = comboId?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var (rawHotkey, _) = ComboEditor.SplitSlotKey(compositeKey);
                if (string.IsNullOrWhiteSpace(rawHotkey)) continue;
                if (!map.TryGetValue(id, out var list))
                    map[id] = list = new List<string>(2);
                list.Add(rawHotkey);
            }
        }
        return map;
    }

    private static SavedCombo? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<SavedCombo>(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Extracts each node's slot letter (P/Q/W/E/R/AA), in sequence order, from a saved
    /// combo's serialized <c>ComboGraph</c> JSON — for the loop 38 item 7 preview row. Reads the
    /// "Id" field directly (a slot's node id is always "{slot}" or, once cloned into a sequence,
    /// "{slot}_{n}" — see <see cref="SequenceZone_Drop"/>) rather than pulling in
    /// <see cref="ComboEngine"/> just to deserialize a graph this view never otherwise needs.
    /// Malformed JSON yields an empty list (the card just omits the preview row).</summary>
    private static List<string> ExtractNodeSlots(string graphJson)
    {
        var slots = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(graphJson);
            if (!doc.RootElement.TryGetProperty("Nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return slots;
            foreach (var node in nodes.EnumerateArray())
            {
                if (!node.TryGetProperty("Id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString() ?? string.Empty;
                var underscore = id.IndexOf('_');
                slots.Add(underscore < 0 ? id : id[..underscore]);
            }
        }
        catch (JsonException)
        {
            // Corrupt saved combo — TryDeserialize already handles the outer SavedCombo shape;
            // here we just skip the preview row rather than crash the saved-combo list.
        }
        return slots;
    }

    /// <summary>A small (20px) read-only preview icon for one sequence slot: the champion's real
    /// ability icon when <see cref="_icons"/> already has it (loaded for the currently-selected
    /// champion), a sword for AA, or the slot letter as a fallback. No drag/click wiring — this is
    /// display-only, unlike the palette/sequence chips <see cref="BuildChip"/> builds.</summary>
    private UIElement BuildPreviewIcon(string slot)
    {
        var accent = SlotAccentBrush(slot);
        var box = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(0),
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.FindResource("Surface"),
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = slot,
        };

        if (slot == "AA")
        {
            box.Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(SwordGeometry),
                Fill = accent,
                Stretch = Stretch.Uniform,
                Width = 10,
                Height = 10,
            };
        }
        else if (slot == "Ignite" && _igniteIcon is not null)
        {
            box.Child = new Image { Source = _igniteIcon, Stretch = Stretch.UniformToFill };
        }
        else if (slot == "Flash" && _flashIcon is not null)
        {
            box.Child = new Image { Source = _flashIcon, Stretch = Stretch.UniformToFill };
        }
        else if (TryResolveSlotIcon(slot, out var icon))
        {
            // TryResolveSlotIcon (not a plain _icons lookup) so a multi-cast sub-slot / variant —
            // "E2" (Akali E), "Q2"/"Q3" (Aatrox Q), "RWall" — falls back to its base ability's art
            // instead of a letter badge, matching the palette/sequence chips (BuildChipContent).
            box.Child = new Image { Source = icon, Stretch = Stretch.UniformToFill };
        }
        else
        {
            // (loop 176) Kick the summoner-icon load so the preview fills in on the next refresh.
            if (slot == "Ignite") _ = LoadIgniteIconAsync();
            else if (slot == "Flash") _ = LoadFlashIconAsync();
            box.Child = new TextBlock
            {
                Text = slot == "Ignite" ? "점" : slot == "Flash" ? "멸" : slot.Length > 0 ? slot[..1] : "?",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return box;
    }

    /// <summary>Honest relative-time label ("captured X ago") for a
    /// <see cref="TargetSnapshot.CapturedAtUtcMs"/> timestamp — never implies the snapshot is
    /// live/current (CLAUDE.md Policy P2/UX-honesty).</summary>
    private static string FormatAgo(long capturedAtUtcMs)
    {
        var capturedAt = DateTimeOffset.FromUnixTimeMilliseconds(capturedAtUtcMs);
        var elapsed = DateTimeOffset.UtcNow - capturedAt;
        if (elapsed.TotalSeconds < 60) return $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h";
        return $"{(int)elapsed.TotalDays}d";
    }

    private UIElement BuildSavedCard(SavedCombo combo, IReadOnlyList<string> hotkeys)
    {
        var textBrush = (Brush)Application.Current.FindResource("Text");
        var dimBrush = (Brush)Application.Current.FindResource("TextDim");
        var accentBrush = (Brush)Application.Current.FindResource("Accent");

        // Up to 2 bound chords, joined for display (e.g. "ALT+1, ALT+2").
        string hotkeyText = string.Join(", ", hotkeys);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(combo.Name) ? Localization.L("combo.noName") : combo.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = Localization.ChampionName(combo.ChampionId), // task 4: localized display name
            FontSize = 12,
            Foreground = dimBrush,
            Margin = new Thickness(0, 2, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = hotkeyText.Length == 0 ? Localization.L("combo.noHotkey") : Localization.F("combo.hotkeyLabel", hotkeyText),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = hotkeyText.Length == 0 ? dimBrush : accentBrush,
            Margin = new Thickness(0, 8, 0, 0),
        });

        // Loop 38 item 7: preview the combo's actual node sequence as small icons instead of just
        // its name. Reuses whatever ability icons are already loaded for the currently-selected
        // champion (RefreshSavedList already scopes this list to that same champion, so _icons is
        // always the right set here); a slot with no loaded icon falls back to its letter badge.
        var slots = ExtractNodeSlots(combo.GraphJson);
        if (slots.Count > 0)
        {
            var preview = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var slot in slots)
                preview.Children.Add(BuildPreviewIcon(slot));
            stack.Children.Add(preview);
        }

        // Loop 38 continuation 19: defender-side "virtual model" — capture the currently-resolved
        // LIVE target's stats for THIS combo (TargetSnapshotStore is comboId-scoped, unlike the
        // per-champion item/rune stores, since a combo is tested against one specific hypothetical
        // target) plus the explicit per-combo opt-in toggle. Default OFF / no snapshot => unchanged
        // live behavior (CLAUDE.md Policy P2) — the label below always states which mode is active.
        if (_composition is not null)
        {
            var snapshotPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            var snapshot = TargetSnapshotStore.Load(_composition.Config, combo.Id);
            snapshotPanel.Children.Add(new TextBlock
            {
                FontSize = 11,
                Foreground = dimBrush,
                TextWrapping = TextWrapping.Wrap,
                Text = snapshot is null
                    ? Localization.L("combo.snapshotNone")
                    : Localization.F("combo.snapshotCaptured",
                        Localization.ChampionName(snapshot.ChampionName),
                        snapshot.Armor.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                        snapshot.Mr.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                        FormatAgo(snapshot.CapturedAtUtcMs)),
            });

            var snapshotRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var captureButton = new Button
            {
                Style = (Style)Application.Current.FindResource("GhostButton"),
                Content = Localization.L("combo.copyTargetStats"),
                FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
            };
            string comboIdForCapture = combo.Id; // captured by value, not by the loop/field
            captureButton.Click += (_, _) =>
            {
                bool ok = _composition?.ComboRunner?.CaptureTargetSnapshot(comboIdForCapture) ?? false;
                ShowMessage(Localization.L(ok ? "combo.snapshotCaptureOk" : "combo.snapshotCaptureFailed"), isError: !ok);
                RefreshSavedList();
            };
            snapshotRow.Children.Add(captureButton);

            var useSnapshotToggle = new CheckBox
            {
                Content = Localization.L("combo.useSnapshotTarget"),
                FontSize = 11,
                Foreground = dimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                IsChecked = TargetSnapshotStore.GetUseSnapshot(_composition.Config, comboIdForCapture),
            };
            useSnapshotToggle.Checked += (_, _) => TargetSnapshotStore.SetUseSnapshot(_composition.Config, comboIdForCapture, true);
            useSnapshotToggle.Unchecked += (_, _) => TargetSnapshotStore.SetUseSnapshot(_composition.Config, comboIdForCapture, false);
            snapshotRow.Children.Add(useSnapshotToggle);

            snapshotPanel.Children.Add(snapshotRow);
            stack.Children.Add(snapshotPanel);
        }

        // Task 7: per-row delete button (✕). Left column holds the details, right column the button.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(stack, 0);
        grid.Children.Add(stack);

        // (loop 173) Edit (✎) loads this combo back into the sequence editor; the next Save replaces
        // it. Stacked above the existing delete (✕) button in the right column.
        var editButton = new Button
        {
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Content = "✎",
            FontSize = 13,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "수정",
        };
        editButton.Click += (_, _) => LoadComboForEdit(combo, hotkeys);

        var deleteButton = new Button
        {
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Content = "✕", // ✕
            FontSize = 13,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = Localization.L("combo.delete"),
        };
        deleteButton.Click += (_, _) => DeleteCombo(combo.Id, combo.ChampionId, hotkeys);

        var buttonStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttonStack.Children.Add(editButton);
        buttonStack.Children.Add(deleteButton);
        Grid.SetColumn(buttonStack, 1);
        grid.Children.Add(buttonStack);

        return new Border
        {
            Background = (Brush)Application.Current.FindResource("SurfaceHi"),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid,
        };
    }
}
