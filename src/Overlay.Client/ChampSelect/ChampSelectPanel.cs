using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Overlay.Core.ChampionDb;
using Overlay.Core.ChampSelect;
using Overlay.Core.Lcu;

namespace Overlay.Client.ChampSelect;

/// <summary>
/// M33 champ-select assistant, hosted at the top of the HOME dashboard content (collapsed
/// outside champ select): title strip + preset dropdown + status, the pick/ban comp board, the
/// editable rune page with a spells column, and the recommendation rail. 2026-07-26 contract:
/// there are no action buttons — SELECTING is APPLYING (dropdown choice, rec-card click, rune
/// click via debounced live-apply, spell pick/swap), and champ-select entry loads the client's
/// current page. All LCU work is async off the UI thread; every failure collapses to a status
/// line (M33 failure posture — no dialogs).
///
/// <para>P4: every apply is an explicit user interaction. The only non-interactive path is the
/// standing opt-in <c>champSelect.autoApply</c> via <see cref="AutoApplyGate"/>'s
/// once-per-session / lock-only / local-only rules. D2: a user-built page is never overwritten
/// silently — a slot shortage asks for a repeat click as consent.</para>
/// </summary>
public sealed class ChampSelectPanel : UserControl
{
    private readonly LcuConnector _lcu;
    private readonly ChampSelectPresets _presets;
    private readonly IRunePresetSource? _recommendations;
    private readonly Func<bool> _autoApplyOptedIn;
    private readonly AutoApplyGate _gate = new();

    /// <summary>Presets currently shown in the dropdown (local first, then recommendations);
    /// selection is by INDEX into this list so identically-named entries can't collide.</summary>
    private List<RunePreset> _currentList = new();

    private readonly Border _root;
    private readonly TextBlock _title;
    private readonly ComboBox _presetBox;
    private readonly TextBlock _status;

    /// <summary>Spells beside the rune page (2026-07-26 redesign): click an icon to change that
    /// spell via a picker, the swap glyph to exchange D/F; every change applies immediately.</summary>
    private readonly Image _spell1Icon = new() { Width = 28, Height = 28 };
    private readonly Image _spell2Icon = new() { Width = 28, Height = 28 };
    private (int Spell1, int Spell2)? _spells;
    private readonly System.Windows.Controls.Primitives.Popup _spellPopup =
        new() { StaysOpen = false, AllowsTransparency = true };

    /// <summary>champSelect.flashKey accessors ("D"/"F"; null = not chosen yet — the first-run
    /// chooser strip shows until the user picks, 2026-07-25 request).</summary>
    private readonly Func<string?> _getFlashKey;
    private readonly Action<string> _setFlashKey;

    /// <summary>League-style editable rune page under the strip (dashboard reorganization,
    /// 2026-07-25): renders the SELECTED preset (local or [추천]) and live-applies each click.</summary>
    private readonly RunePageView _runeView = new();

    /// <summary>Debounces click-storms into one LCU write: each complete edit restarts the timer,
    /// the tick applies the latest page.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _liveApplyTimer = new()
    { Interval = TimeSpan.FromMilliseconds(300) }; // 500 -> 300: user latency feedback 2026-07-25
    private RunePage? _pendingLivePage;

    /// <summary>True while RefreshPresetList sets the dropdown programmatically — the rune view
    /// must only follow USER-initiated selection (2026-07-25 feedback: entering champ select
    /// shows the CURRENT page; recommendations are looked at by explicit choice, never pushed).</summary>
    private bool _suppressPresetRender;

    /// <summary>Edge detector for champ-select entry (current-page auto-load fires once).</summary>
    private bool _wasInChampSelect;

    /// <summary>Pick/ban + AD/AP composition section (2026-07-25 request), refreshed from the
    /// champ-select session at most every 2s.</summary>
    private readonly StackPanel _compSection = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };

    /// <summary>Champ-select poll timer (2026-07-26 fix): comp refresh + reverse sync used to
    /// piggyback on ChampSelectChanged, which fires only when the SNAPSHOT changes (hover/lock)
    /// — so client-side rune edits never synced until the user locked. A real 2s timer runs
    /// while in champ select instead.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _pollTimer = new()
    { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>Reverse sync: changes made INSIDE the League client (rune edits, spell swaps)
    /// are polled back into the panel. A recent LOCAL interaction
    /// (<see cref="LocalActionGraceSeconds"/>) pauses the pull so the poller never clobbers a
    /// user mid-edit or races our own debounced write.</summary>
    private DateTime _lastLocalActionUtc = DateTime.MinValue;
    private const double LocalActionGraceSeconds = 3.0;

    /// <summary>Right-hand recommendation rail next to the rune page (2026-07-25 feedback):
    /// top rec presets as one-click loads + a hint line derived from the ENEMY comp analysis.</summary>
    private readonly StackPanel _recRail = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(24, 10, 0, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _compHint = new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Margin = new Thickness(0, 8, 0, 0) };

    /// <summary>(item recs, loop 459) Item-build section under the rune rec cards: top co-completed
    /// core trios + boots for the hovered/locked champion's most-played role, from the same
    /// aggregation pipeline (and, by construction, the same patch) as the rune recommendations.
    /// Display-only — nothing here writes to the LCU. Collapsed when the source has no data.</summary>
    private readonly FileItemRecommendationSource? _itemRecs;
    private readonly StackPanel _itemSection = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };

    /// <summary>(brackets, loop 463) Which tier band the recommendations are drawn from. The
    /// aggregation writes one directory per cumulative bracket, so switching is a re-read of a
    /// different sample — the rune source and the item source are moved together, because a rail
    /// showing Platinum+ runes beside Diamond+ items would be two answers wearing one label.</summary>
    private readonly List<string> _recBrackets;
    private readonly HashSet<string> _thinRecBrackets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _setRecBracket;
    private string _recBracket;

    private ChampSelectSnapshot _snapshot;

    /// <summary>Armed by a NeedsOverwriteConfirmation result: the NEXT [적용] click carries the
    /// explicit current-page-overwrite consent (D2). Reset on every snapshot change.</summary>
    private bool _confirmOverwriteArmed;

    public ChampSelectPanel(LcuConnector lcu, ChampSelectPresets presets, Func<bool> autoApplyOptedIn,
        IRunePresetSource? recommendations = null,
        Func<string?>? getFlashKey = null, Action<string>? setFlashKey = null,
        FileItemRecommendationSource? itemRecommendations = null,
        string recDir = "", Action<string>? setRecBracket = null, string recBracket = "")
    {
        _lcu = lcu;
        _presets = presets;
        _recommendations = recommendations;
        _itemRecs = itemRecommendations;
        _autoApplyOptedIn = autoApplyOptedIn;
        _getFlashKey = getFlashKey ?? (() => null);
        _setFlashKey = setFlashKey ?? (_ => { });
        _setRecBracket = setRecBracket ?? (_ => { });
        _recBracket = recBracket.Length > 0 ? recBracket : Overlay.Core.Stats.RecBrackets.Default;
        _recBrackets = new List<string>();
        if (recDir.Length > 0)
            foreach (var (slug, thin) in
                     Overlay.Core.ChampSelect.FileRecommendationSource.AvailableBrackets(recDir))
            {
                _recBrackets.Add(slug);
                if (thin) _thinRecBrackets.Add(slug);
            }

        // Leading rune glyph (Segoe MDL2 "Contact"/shield-like) in the accent color, so the strip
        // reads as the champ-select assistant at a glance — same icon-then-label idiom as the nav.
        var icon = new TextBlock
        {
            Text = "", // MDL2 "Repair" glyph — the rune/setup mark
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = (Brush)Application.Current.FindResource("Accent"),
        };

        _title = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("Text"),
        };
        _presetBox = new ComboBox
        {
            MinWidth = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("ThemedComboBox"),
        };
        // 2026-07-26 redesign: no action buttons. Selecting a preset/card applies it, rune
        // clicks live-apply, spells apply on pick - the strip is title + dropdown + status.
        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        };

        // Grid strip: [icon+title][preset][status*] - the status column stretches so its text
        // is right-aligned and ellipsized rather than pushing the dropdown around.
        var grid = new Grid();
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleGroup.Children.Add(icon);
        titleGroup.Children.Add(_title);
        Grid.SetColumn(titleGroup, 0);

        _presetBox.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(_presetBox, 1);
        _status.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(_status, 2);

        grid.Children.Add(titleGroup);
        grid.Children.Add(_presetBox);
        grid.Children.Add(_status);

        // Two rows: the original strip, and the editable rune page under it (collapsed until a
        // preset is shown). The page renders whichever preset the dropdown selects — including
        // [추천] entries — and every rune click live-applies through the LCU (debounced).
        _runeView.Margin = new Thickness(0, 10, 0, 0);
        _runeView.Visibility = Visibility.Collapsed;
        var rows = new StackPanel();
        rows.Children.Add(grid);
        rows.Children.Add(_compSection);
        var pageArea = new StackPanel { Orientation = Orientation.Horizontal };
        pageArea.Children.Add(_runeView);
        pageArea.Children.Add(BuildSpellsColumn());
        pageArea.Children.Add(_recRail);
        rows.Children.Add(pageArea);

        _root = new Border
        {
            Background = (Brush)Application.Current.FindResource("Surface"),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10),
            Visibility = Visibility.Collapsed, // auto-height row: hidden = zero height
            Child = rows,
        };
        Content = _root;

        _presetBox.SelectionChanged += async (_, _) =>
        {
            BuildRecRail(); // keep the selected rec card highlight in sync
            if (_suppressPresetRender) return;
            ShowSelectedPresetPage();
            await ApplySelectedPresetAsync(); // 2026-07-26: selecting IS applying
        };
        _runeView.PageEdited += page =>
        {
            _lastLocalActionUtc = DateTime.UtcNow;
            _pendingLivePage = page;
            _liveApplyTimer.Stop();
            _liveApplyTimer.Start();
        };
        _liveApplyTimer.Tick += async (_, _) =>
        {
            _liveApplyTimer.Stop();
            if (_pendingLivePage is { } page) await LiveApplyAsync(page);
        };
        _pollTimer.Tick += (_, _) =>
        {
            if (!_snapshot.InChampSelect) return;
            _ = RefreshCompAsync();
            _ = SyncFromClientAsync();
        };

        _lcu.ChampSelectChanged += snap => Dispatcher.BeginInvoke(new Action(() => OnSnapshot(snap)));
    }

    // ── State ───────────────────────────────────────────────────────────────────

    private void OnSnapshot(ChampSelectSnapshot snap)
    {
        _snapshot = snap;
        _confirmOverwriteArmed = false; // consent never outlives the state it was shown for

        if (!snap.InChampSelect)
        {
            _wasInChampSelect = false;
            _pollTimer.Stop();
            _root.Visibility = Visibility.Collapsed;
            _gate.OnSnapshot(snap, false, _presets); // re-arm the once-per-session gate
            return;
        }

        _root.Visibility = Visibility.Visible;

        // 2026-07-25 feedback: entering champ select starts from the CLIENT's current page —
        // what the player is actually running — not from a preset. Recommendations stay in the
        // dropdown for the user to open deliberately.
        if (!_wasInChampSelect)
        {
            _wasInChampSelect = true;
            _pollTimer.Start();
            _ = LoadCurrentPageIntoViewAsync();
            _ = RefreshSpellsAsync();
            _ = RefreshCompAsync();
        }

        var champ = ChampionSummary.GetByNumericKey(snap.ChampionKey);
        _title.Text = snap.ChampionKey <= 0
            ? "픽창 · 챔피언 선택 대기"
            : $"픽창 · {champ?.Name ?? $"#{snap.ChampionKey}"}{(snap.Locked ? " (확정)" : " (호버)")}";

        RefreshPresetList(snap.ChampionKey);

        // P4 auto-apply: standing opt-in only, once per session, lock only, local presets only.
        if (_gate.OnSnapshot(snap, _autoApplyOptedIn(), _presets) is { } auto)
        {
            _status.Text = $"자동 적용 중: {auto.Name}";
            _ = ApplyPresetAsync(auto, confirmOverwrite: false);
        }
    }

    private void RefreshPresetList(int championKey)
    {
        string? keep = _presetBox.SelectedItem as string;

        // D4: local presets first (auto-apply eligible, user-owned), then recommendations.
        _currentList = new List<RunePreset>(_presets.List(championKey));
        if (_recommendations is not null)
            _currentList.AddRange(_recommendations.List(championKey));

        _suppressPresetRender = true;
        try
        {
            _presetBox.Items.Clear();
            foreach (var p in _currentList)
                _presetBox.Items.Add(p.Source == "remote" ? $"[추천] {p.Name}" : p.Name);
            if (_presetBox.Items.Count > 0)
                _presetBox.SelectedIndex = keep is not null && _presetBox.Items.Contains(keep)
                    ? _presetBox.Items.IndexOf(keep) : 0;
        }
        finally { _suppressPresetRender = false; }
        BuildItemSection(championKey);
        BuildRecRail();
    }

    /// <summary>Right-hand rail: up to three [추천] presets as one-click loads (clicking selects
    /// them in the dropdown, which renders the page — the user still applies/edits explicitly)
    /// plus the enemy-comp hint line. Hidden when there are no recommendations.</summary>
    private void BuildRecRail()
    {
        _recRail.Children.Clear();
        var recs = new List<(int Index, RunePreset Preset)>();
        for (int i = 0; i < _currentList.Count && recs.Count < 3; i++)
            if (_currentList[i].Source == "remote") recs.Add((i, _currentList[i]));

        if (_recBrackets.Count > 0) _recRail.Children.Add(BracketPicker());

        if (recs.Count > 0)
        {
            _recRail.Children.Add(new TextBlock
            {
                Text = Localization.L("rec.title"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = (Brush)Application.Current.FindResource("Text"),
            });
            foreach (var (index, preset) in recs)
                _recRail.Children.Add(RecCard(index, preset));
        }
        _recRail.Children.Add(_itemSection);
        _recRail.Children.Add(_compHint);
        _recRail.Visibility = recs.Count > 0 || _itemSection.Visibility == Visibility.Visible
            || _compHint.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Localized bracket name, falling back to the slug for one this build does not know.</summary>
    private static string BracketLabel(string slug)
    {
        string key = "stats.bracket." + slug;
        string text = Localization.L(key);
        return text == key ? slug : text;
    }

    /// <summary>The rail's tier-band picker. Only brackets the aggregation actually wrote are
    /// listed, so a band that was never collected is not offered as an empty answer.</summary>
    private UIElement BracketPicker()
    {
        var box = new ComboBox { MinWidth = 116, Margin = new Thickness(0, 0, 0, 8) };
        box.SetResourceReference(StyleProperty, "ThemedComboBox");
        box.ToolTip = Localization.L("rec.bracket.tip");
        foreach (var slug in _recBrackets)
            box.Items.Add(_thinRecBrackets.Contains(slug)
                ? Localization.F("stats.bracket.thin", BracketLabel(slug))
                : BracketLabel(slug));

        int idx = _recBrackets.IndexOf(_recBracket);
        if (idx < 0) idx = _recBrackets.IndexOf(Overlay.Core.Stats.RecBrackets.Default);
        box.SelectedIndex = Math.Max(idx, 0);
        _recBracket = _recBrackets[box.SelectedIndex];

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0 || box.SelectedIndex >= _recBrackets.Count) return;
            string slug = _recBrackets[box.SelectedIndex];
            if (slug == _recBracket) return;
            _recBracket = slug;
            if (_recommendations is Overlay.Core.ChampSelect.FileRecommendationSource runes)
                runes.Bracket = slug;
            if (_itemRecs is not null) _itemRecs.Bracket = slug;
            _setRecBracket(slug);
            // RefreshPresetList re-reads both sources and rebuilds the item section and the rail;
            // going through OnSnapshot instead would re-run the P4 auto-apply gate for a change
            // that is only about which sample is displayed.
            RefreshPresetList(_snapshot.ChampionKey);
        };
        return box;
    }

    /// <summary>(item recs) Rebuilds the item section for the champion: the most-played role's top
    /// core trios (3 item icons + 표본/승률) and its boots pick. Honesty rule carried from the
    /// aggregation: these are CO-COMPLETED sets (final inventories; Match-V5 has no purchase order
    /// without the timeline endpoint), so the header says 조합 and never claims a build ORDER.</summary>
    private void BuildItemSection(int championKey)
    {
        _itemSection.Children.Clear();
        var roles = _itemRecs?.List(championKey) ?? Array.Empty<Overlay.Core.ChampSelect.ItemRoleRecs>();
        var role = roles.FirstOrDefault(r => r.CoreSets.Count > 0 || r.Boots.Count > 0
                                             || r.Items.Count > 0);
        if (role is null)
        {
            _itemSection.Visibility = Visibility.Collapsed;
            return;
        }

        _itemSection.Children.Add(new TextBlock
        {
            Text = Localization.F(role.CoreSets.Count > 0 ? "rec.items.title" : "rec.items.title.single",
                                  BracketLabel(_recBracket)) + $" · {role.Role}",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)Application.Current.FindResource("Text"),
        });

        foreach (var set in role.CoreSets.Take(3))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            foreach (int itemId in set.Items)
                row.Children.Add(ItemIcon(itemId, 26));
            row.Children.Add(new TextBlock
            {
                Text = $"{Localization.L("rec.samples")} {set.Games} · {Localization.L("rec.winRate")} {set.WinRate:P1}",
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("TextDim"),
            });
            _itemSection.Children.Add(row);
        }

        // Fallback: no core trio cleared the sample floor, so recommend single items instead.
        // Structural, not incidental — UTILITY completes 3+ legendaries in only 21.9% of games
        // (JUNGLE 60.6%, BOTTOM 63.9%), so supports would otherwise render as a boots line alone.
        // Still a co-completion claim (final inventories), never an order — header says 아이템, not 순서.
        if (role.CoreSets.Count == 0)
        {
            foreach (var item in role.Items.Take(4))
            {
                var itemRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                itemRow.Children.Add(ItemIcon(item.ItemId, 22));
                itemRow.Children.Add(new TextBlock
                {
                    Text = $"{Localization.L("rec.pickRate")} {item.PickRate:P0} · {Localization.L("rec.winRate")} {item.WinRate:P1} · {Localization.L("rec.samples")} {item.Games}",
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.FindResource("TextDim"),
                });
                _itemSection.Children.Add(itemRow);
            }
        }

        if (role.Boots.Count > 0)
        {
            var boots = role.Boots[0];
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ItemIcon(boots.ItemId, 22));
            row.Children.Add(new TextBlock
            {
                Text = $"{Localization.L("rec.items.boots")} · {Localization.L("rec.pickRate")} {boots.PickRate:P0} · {Localization.L("rec.winRate")} {boots.WinRate:P1}",
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("TextDim"),
            });
            _itemSection.Children.Add(row);
        }

        _itemSection.Visibility = Visibility.Visible;
    }

    private static Image ItemIcon(int itemId, double size)
    {
        var img = new Image { Width = size, Height = size, Margin = new Thickness(0, 0, 3, 0) };
        _ = SetItemIconAsync(img, itemId);
        return img;
    }

    private static async Task SetItemIconAsync(Image image, int itemId)
    {
        var src = await DDragonIconProvider.LoadItemIconAsync(itemId.ToString());
        if (src is not null) image.Source = src;
    }

    /// <summary>One recommendation card (2026-07-26 redesign, op.gg-style): keystone icon with a
    /// sub-style badge, the keystone's localized description, and a 표본/픽률/승률 stat line.
    /// The dropdown-selected card is highlighted; clicking selects it (user path → page render).</summary>
    private UIElement RecCard(int index, RunePreset preset)
    {
        bool selected = _presetBox.SelectedIndex == index;

        // Keystone (first perk id) + sub-style badge bottom-right.
        int keystoneId = preset.Page.PerkIds.Count > 0 ? preset.Page.PerkIds[0] : 0;
        var iconGrid = new Grid { Width = 42, Height = 42, VerticalAlignment = VerticalAlignment.Top };
        var keystone = new System.Windows.Controls.Image { Width = 38, Height = 38,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        if (Overlay.Core.Runes.RuneCatalog.GetPerk(keystoneId) is { } kp)
            _ = SetRuneIconAsync(keystone, kp.IconPath);
        iconGrid.Children.Add(keystone);
        var sub = new System.Windows.Controls.Image { Width = 18, Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        if (Overlay.Core.Runes.RuneCatalog.GetStyle(preset.Page.SubStyleId) is { } ss)
            _ = SetRuneIconAsync(sub, ss.IconPath);
        iconGrid.Children.Add(sub);

        // 2026-07-26: description removed from the card (icons + stats only); the keystone's
        // full numeric tooltip lives on the rune page itself.
        var text = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            Width = 230,
            VerticalAlignment = VerticalAlignment.Center,
        };
        string stats = preset.Games is int g
            ? $"{Localization.L("rec.samples")} {g} · " +
              (preset.PickRate is double pr ? $"{Localization.L("rec.pickRate")} {pr:P1} · " : "") +
              $"{Localization.L("rec.winRate")} {preset.WinRate ?? 0:P1}"
            : preset.Name;
        text.Children.Add(new TextBlock
        {
            Text = (string.IsNullOrEmpty(preset.Role) ? "" : preset.Role + "  ") + stats,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("Text"),
        });

        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal };
        rowPanel.Children.Add(iconGrid);
        rowPanel.Children.Add(text);

        var card = new Border
        {
            Child = rowPanel,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.FindResource(selected ? "SurfaceHi" : "Surface"),
            BorderBrush = selected
                ? (Brush)Application.Current.FindResource("Accent")
                : (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        int idx = index;
        card.MouseLeftButtonUp += async (_, _) =>
        {
            if (_presetBox.SelectedIndex == idx)
            {
                // Already selected (e.g. the FIRST card with no local presets): SelectionChanged
                // will not fire, so render + apply directly — this was the "first recommendation
                // never applies" bug (2026-07-26).
                ShowSelectedPresetPage();
                await ApplySelectedPresetAsync();
            }
            else
            {
                _presetBox.SelectedIndex = idx; // selection handler renders + applies
            }
        };
        return card;
    }

    private static async Task SetRuneIconAsync(System.Windows.Controls.Image image, string iconPath)
    {
        var src = await DDragonIconProvider.LoadRuneIconAsync(iconPath);
        if (src is not null) image.Source = src;
    }

    /// <summary>Renders the dropdown's current preset in the rune editor (both rows of the
    /// dashboard stay in sync; no selection collapses the page).</summary>
    private void ShowSelectedPresetPage()
    {
        int i = _presetBox.SelectedIndex;
        _runeView.ShowPage(i >= 0 && i < _currentList.Count ? _currentList[i].Page : null);
    }

    /// <summary>Rebuilds the pick/ban + composition section from the live session: one line per
    /// team (portraits + Riot info-score AD/AP shares + curated true-damage count) and a ban
    /// line. Enemy hovers stay hidden until lock (the session exposes 0), honest by design.</summary>
    private async Task RefreshCompAsync()
    {
        var board = await _lcu.GetChampSelectBoardAsync();
        if (board is null) { _compSection.Visibility = Visibility.Collapsed; return; }

        var mine = TeamCompAnalyzer.Analyze(board.MyTeam);
        var theirs = TeamCompAnalyzer.Analyze(board.TheirTeam);

        _compSection.Children.Clear();
        if (mine.Rows.Count > 0) _compSection.Children.Add(TeamLine("아군", mine));
        if (theirs.Rows.Count > 0) _compSection.Children.Add(TeamLine("적군", theirs));
        if (board.MyBans.Count > 0 || board.TheirBans.Count > 0)
            _compSection.Children.Add(BanLine(board));
        _compSection.Visibility = _compSection.Children.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Enemy-comp shard hint (advice text only — nothing is auto-changed, P4): worth showing
        // once at least three enemy picks are visible.
        if (theirs.Rows.Count >= 3)
        {
            // Armor/MR shards no longer exist (removed from the shard system in patch 14.19) —
            // the current defensive shards are HP / tenacity only, so the advice points there
            // and at itemization instead (2026-07-25 user report: "물마방 파편이 삭제됨").
            string? hint =
                theirs.ApShare >= 0.60 ? $"적 조합 마법 성향 {theirs.ApShare:P0} — 체력 파편 · 마법저항 아이템 고려"
                : theirs.AdShare >= 0.60 ? $"적 조합 물리 성향 {theirs.AdShare:P0} — 체력 파편 · 방어구 아이템 고려"
                : theirs.TrueCount >= 2 ? $"적 트루딤 {theirs.TrueCount}명 — 저항 무효, 체력 파편 우선"
                : null;
            _compHint.Text = hint ?? "";
            _compHint.Foreground = (Brush)Application.Current.FindResource("TextDim");
            _compHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
            if (hint is not null) _recRail.Visibility = Visibility.Visible;
        }
    }

    private static UIElement TeamLine(string label, TeamCompAnalyzer.Comp comp)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        line.Children.Add(new TextBlock
        {
            Text = label,
            Width = 34,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        });
        foreach (var row in comp.Rows)
            line.Children.Add(PortraitIcon(row.Id, 26, row.Name +
                (row.HasTrueDamage ? " · 트루딤" : "") + $" (물리 {row.Attack}/마법 {row.Magic})"));
        line.Children.Add(TypeBar(comp));
        line.Children.Add(new TextBlock
        {
            Text = $"물 {comp.PhysShare:P0} · 마 {comp.MagicShare:P0} · 고정 {comp.TrueShare:P0}",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("Text"),
        });
        return line;
    }

    private static readonly Brush PhysBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x72, 0x2A));
    private static readonly Brush MagicBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush TrueBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));

    /// <summary>Stacked phys/magic/true tendency bar (2026-07-25 feedback: text percentages
    /// were hard to scan). Widths are proportional; the tooltip carries the exact split.</summary>
    private static UIElement TypeBar(TeamCompAnalyzer.Comp comp)
    {
        const double totalWidth = 120, height = 10;
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Seg(double share, Brush brush)
        {
            if (share <= 0) return;
            bar.Children.Add(new Border
            {
                Width = Math.Max(1, totalWidth * share),
                Height = height,
                Background = brush,
            });
        }
        Seg(comp.PhysShare, PhysBrush);
        Seg(comp.MagicShare, MagicBrush);
        Seg(comp.TrueShare, TrueBrush);
        var holder = new Border
        {
            Child = bar,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(holder,
            $"물리 {comp.PhysShare:P0} · 마법 {comp.MagicShare:P0} · 고정 {comp.TrueShare:P0} (스킬 구성 기반 성향)");
        return holder;
    }

    private static UIElement BanLine(ChampSelectBoard board)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        line.Children.Add(new TextBlock
        {
            Text = "밴",
            Width = 34,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        });
        void AddBans(IEnumerable<int> keys)
        {
            foreach (int key in keys)
                if (ChampionSummary.GetByNumericKey(key) is { } c)
                    line.Children.Add(PortraitIcon(c.Id, 20, $"{c.Name} (밴)", dim: true));
        }
        AddBans(board.MyBans);
        line.Children.Add(new TextBlock
        {
            Text = "·",
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
        });
        AddBans(board.TheirBans);
        return line;
    }

    private static UIElement PortraitIcon(string championId, double size, string tooltip, bool dim = false)
    {
        var image = new System.Windows.Controls.Image { Width = size, Height = size, Opacity = dim ? 0.45 : 1.0 };
        _ = SetPortraitAsync(image, championId);
        var holder = new Border { Margin = new Thickness(2, 0, 2, 0), Child = image };
        ToolTipService.SetToolTip(holder, tooltip);
        ToolTipService.SetInitialShowDelay(holder, 300);
        return holder;
    }

    private static async Task SetPortraitAsync(System.Windows.Controls.Image image, string championId)
    {
        var src = await DDragonIconProvider.LoadChampionPortraitAsync(championId);
        if (src is not null) image.Source = src;
    }

    /// <summary>Loads the client's CURRENT rune page into the editor (champ-select entry and
    /// the [현재 룬 불러오기] button share this) — edits then live-apply like any page.</summary>
    private async Task LoadCurrentPageIntoViewAsync()
    {
        _status.Text = "현재 룬 읽는 중…";
        var page = await _lcu.GetCurrentRunePageAsync();
        if (page is null) { _status.Text = "현재 룬 페이지를 읽지 못함"; return; }
        _runeView.ShowPage(page);
        _status.Text = "현재 룬 표시 중 — 클릭 수정 시 실시간 적용";
    }

    /// <summary>Debounced live apply for rune-view clicks: writes the edited page to the client
    /// (runes only — spells are untouched by an edit). The D2 overwrite consent is never
    /// auto-granted here; a slot shortage just reports and defers to the [적용] button flow.</summary>
    private async Task LiveApplyAsync(RunePage page)
    {
        if (_snapshot.ChampionKey <= 0) return;
        var champ = ChampionSummary.GetByNumericKey(_snapshot.ChampionKey);
        var result = await _lcu.ApplyRunePageAsync(page, champ?.Name ?? _snapshot.ChampionKey.ToString(),
            confirmOverwriteCurrent: false);
        _status.Text = result switch
        {
            ApplyRunesResult.Applied => "룬 실시간 적용 ✓",
            ApplyRunesResult.NeedsOverwriteConfirmation => "룬 페이지 슬롯 부족 — 추천 카드를 다시 클릭하면 현재 페이지를 덮어씀",
            _ => "실시간 적용 실패 (클라이언트 상태 확인)",
        };
    }

    // ── Actions ─────────────────────────────────────────────────────────────────

    /// <summary>Applies the dropdown's current preset (selection IS application since the
    /// 2026-07-26 redesign); a repeat interaction carries the armed overwrite consent (D2).</summary>
    private async Task ApplySelectedPresetAsync()
    {
        int i = _presetBox.SelectedIndex;
        if (_snapshot.ChampionKey <= 0 || i < 0 || i >= _currentList.Count) return;
        bool confirm = _confirmOverwriteArmed;
        _confirmOverwriteArmed = false;
        await ApplyPresetAsync(_currentList[i], confirm);
    }

    private async Task ApplyPresetAsync(RunePreset preset, bool confirmOverwrite)
    {
        _lastLocalActionUtc = DateTime.UtcNow;
        _status.Text = "적용 중…";
        var champ = ChampionSummary.GetByNumericKey(preset.ChampionKey);
        var result = await _lcu.ApplyRunePageAsync(preset.Page, champ?.Name ?? preset.ChampionKey.ToString(),
            confirmOverwrite);

        bool spellsOk = true;
        if (result == ApplyRunesResult.Applied && preset is { Spell1Id: int s1, Spell2Id: int s2 })
        {
            // Land Flash on the user's key (champSelect.flashKey; unset = leave preset order).
            var (n1, n2) = _getFlashKey() is { Length: > 0 } fk
                ? SpellOrder.Normalize(s1, s2, flashOnF: fk.Equals("F", StringComparison.OrdinalIgnoreCase))
                : (s1, s2);
            spellsOk = await _lcu.ApplySpellsAsync(n1, n2);
        }

        _status.Text = result switch
        {
            ApplyRunesResult.Applied when spellsOk => "적용 완료 ✓",
            ApplyRunesResult.Applied => "룬 적용 완료 · 스펠 실패",
            // D2: a user-built page is never overwritten silently — one explicit second click.
            ApplyRunesResult.NeedsOverwriteConfirmation => "룬 페이지 슬롯 부족 — 같은 선택을 다시 클릭하면 현재 페이지를 덮어씀",
            _ => "적용 실패 (클라이언트 상태 확인)",
        };
        _confirmOverwriteArmed = result == ApplyRunesResult.NeedsOverwriteConfirmation;
        if (result == ApplyRunesResult.Applied) _ = RefreshSpellsAsync();
    }

    // ── Spells (2026-07-26 redesign) ─────────────────────────────────────────────

    /// <summary>The champ-select-pickable summoner spells (API-contract ids → DDragon icon
    /// keys). Smite last — jungle-only relevance.</summary>
    private static readonly (int Id, string Key)[] PickableSpells =
    {
        (4, "SummonerFlash"), (14, "SummonerDot"), (12, "SummonerTeleport"),
        (7, "SummonerHeal"), (21, "SummonerBarrier"), (3, "SummonerExhaust"),
        (1, "SummonerBoost"), (6, "SummonerHaste"), (11, "SummonerSmite"),
    };

    private static string? SpellKey(int id)
    {
        foreach (var (spellId, key) in PickableSpells)
            if (spellId == id) return key;
        return null;
    }

    private StackPanel BuildSpellsColumn()
    {
        var col = new StackPanel
        {
            Margin = new Thickness(16, 34, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        col.Children.Add(SpellButton(_spell1Icon, slot: 1, keyLabel: "D"));
        var swap = new TextBlock
        {
            Text = "⇄",
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4),
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = (Brush)Application.Current.FindResource("TextDim"),
            ToolTip = Localization.L("spell.swap"),
        };
        swap.MouseLeftButtonUp += async (_, e) => { e.Handled = true; await SwapSpellsAsync(); };
        col.Children.Add(swap);
        col.Children.Add(SpellButton(_spell2Icon, slot: 2, keyLabel: "F"));
        return col;
    }

    private Border SpellButton(Image img, int slot, string keyLabel)
    {
        var b = new Border
        {
            Child = img,
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = keyLabel + " — " + Localization.L("spell.change"),
        };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenSpellPicker(slot, b); };
        return b;
    }

    private async Task RefreshSpellsAsync()
    {
        _spells = await _lcu.GetMySpellsAsync();
        if (_spells is { } s)
        {
            _ = SetSpellImageAsync(_spell1Icon, s.Spell1);
            _ = SetSpellImageAsync(_spell2Icon, s.Spell2);
        }
    }

    /// <summary>Pulls the client's CURRENT page + spells into the panel when they differ from
    /// what is shown — the reverse of every apply path, so edits made directly in the League
    /// client stay in sync (2026-07-26 request). Skipped while a local interaction is fresh.</summary>
    private async Task SyncFromClientAsync()
    {
        if ((DateTime.UtcNow - _lastLocalActionUtc).TotalSeconds < LocalActionGraceSeconds) return;

        var clientPage = await _lcu.GetCurrentRunePageAsync();
        if (clientPage is not null && !SamePage(clientPage, _runeView.CurrentPage))
        {
            _runeView.ShowPage(clientPage);
            _status.Text = "클라이언트 변경 동기화 ✓";
        }

        var clientSpells = await _lcu.GetMySpellsAsync();
        if (clientSpells is { } cs && (_spells is not { } s || cs.Spell1 != s.Spell1 || cs.Spell2 != s.Spell2))
        {
            _spells = cs;
            _ = SetSpellImageAsync(_spell1Icon, cs.Spell1);
            _ = SetSpellImageAsync(_spell2Icon, cs.Spell2);
        }
    }

    private static bool SamePage(RunePage a, RunePage? b)
        => b is not null
           && a.PrimaryStyleId == b.PrimaryStyleId
           && a.SubStyleId == b.SubStyleId
           && a.PerkIds.SequenceEqual(b.PerkIds);

    private static async Task SetSpellImageAsync(Image img, int spellId)
    {
        if (SpellKey(spellId) is not { } key) { img.Source = null; return; }
        var src = await DDragonIconProvider.LoadSummonerIconAsync(key);
        if (src is not null) img.Source = src;
    }

    private async Task SwapSpellsAsync()
    {
        _lastLocalActionUtc = DateTime.UtcNow;
        if (_spells is not { } s) { _status.Text = "스펠을 읽지 못함"; return; }
        _status.Text = await _lcu.ApplySpellsAsync(s.Spell2, s.Spell1)
            ? "스펠 위치 교체 ✓" : "스펠 교체 실패";
        await RefreshSpellsAsync();
    }

    /// <summary>Small popup under the clicked spell slot listing the pickable spells; choosing
    /// one applies immediately (picking the OTHER slot's spell swaps instead — the client
    /// rejects duplicates).</summary>
    private void OpenSpellPicker(int slot, UIElement anchor)
    {
        var wrap = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (id, key) in PickableSpells)
        {
            int spellId = id;
            var img = new Image { Width = 26, Height = 26 };
            _ = SetSpellImageAsync(img, spellId);
            var cell = new Border
            {
                Child = img,
                Margin = new Thickness(2),
                Padding = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = Localization.L("spell." + spellId),
            };
            cell.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                _spellPopup.IsOpen = false;
                await SetSpellAsync(slot, spellId);
            };
            wrap.Children.Add(cell);
        }
        _spellPopup.Child = new Border
        {
            Child = wrap,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.FindResource("Surface"),
            BorderBrush = (Brush)Application.Current.FindResource("Border"),
            BorderThickness = new Thickness(1),
        };
        _spellPopup.PlacementTarget = anchor;
        _spellPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        _spellPopup.IsOpen = true;
    }

    private async Task SetSpellAsync(int slot, int spellId)
    {
        _lastLocalActionUtc = DateTime.UtcNow;
        if (_spells is not { } s) { _status.Text = "스펠을 읽지 못함"; return; }
        (int n1, int n2) = slot == 1 ? (spellId, s.Spell2) : (s.Spell1, spellId);
        // Picking the other slot's spell = swap (the client rejects duplicate spells).
        if (n1 == n2) (n1, n2) = (s.Spell2, s.Spell1);
        _status.Text = await _lcu.ApplySpellsAsync(n1, n2) ? "스펠 변경 ✓" : "스펠 변경 실패";
        await RefreshSpellsAsync();
    }
}
