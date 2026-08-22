using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Overlay.Core.Stats;

namespace Overlay.Client.Views;

/// <summary>
/// Dashboard statistics tab (통계): the champion tier list built from the local aggregation
/// pipeline — grade, win/pick/ban rates and the game-duration win curve (&lt;25 / 25–32 / &gt;32
/// min) for the collected KR sample.
///
/// <para>Laid out (loop 466) the way the public Korean stats sites present a tier list, at the
/// user's request: lane as a tab strip rather than a dropdown, a header line naming exactly what
/// the numbers describe (when, which patch, how big), a stated pick-rate floor, and a rank number
/// per row. The grade is the visual anchor — a coloured badge next to the rank, where the eye
/// lands first.</para>
///
/// <para>Two columns those sites carry are deliberately absent. A "꿀챔" ease score would need
/// per-player mastery data the pipeline does not collect, and an ARAM tab would need queue 450,
/// which the collector does not fetch. Neither is worth inventing.</para>
///
/// <para>Grades come from <see cref="ChampionGrade"/> and are ABSOLUTE — measured cutoffs on the
/// champion's own win-rate edge, so a lane can hold no S+ at all or several, and no filter on this
/// screen can change a letter. The score the cutoffs are applied to is printed beside it.</para>
/// </summary>
public sealed class StatsView : UserControl
{
    /// <summary>Pick-rate floors, as fractions. The default matches what the reference layout
    /// states on its own face: off-meta noise is excluded, and the view says so out loud.</summary>
    private static readonly double[] PickFloors = { 0.005, 0.01, 0.02, 0 };
    private const int DefaultPickFloorIndex = 0;

    /// <summary>Config key holding the browsing bracket. Separate from the recommendation
    /// bracket: reading Iron statistics out of curiosity must not change what champ select
    /// recommends.</summary>
    private const string BracketKey = "stats.bracket";

    private const double SmoothK = 10;

    private FileTierStatsSource? _source;
    private FileMatchupSource? _matchups;
    private AppComposition? _composition;

    private readonly StackPanel _list = new() { Margin = new Thickness(0, 0, 16, 24) };
    private readonly TextBlock _meta = new() { FontSize = 12, Margin = new Thickness(0, 3, 0, 10) };
    private readonly TextBlock _note = new() { FontSize = 11, Margin = new Thickness(2, 8, 0, 8) };
    /// <summary>One width for every control on the filter row — the three dropdowns and the search
    /// box — so the row reads as a set rather than four different sizes.</summary>
    private const double FilterWidth = 120;

    private readonly StackPanel _laneTabs = new() { Orientation = Orientation.Horizontal };
    private readonly ComboBox _bracketBox = new() { MinWidth = FilterWidth };
    private readonly ComboBox _sortBox = new() { MinWidth = FilterWidth };
    private readonly ComboBox _pickBox = new() { MinWidth = FilterWidth };
    private readonly TextBox _searchBox = new() { Width = FilterWidth };

    private List<string> _brackets = new();
    private readonly HashSet<string> _thinBrackets = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _lanes = new();
    private int _laneIndex;
    private bool _built;
    private bool _populating;

    /// <summary>(sortKey, comparer) — index-aligned with the dropdown items. Grade first: a tier
    /// list is read top-down, so the default order is the one the grades describe.</summary>
    private static readonly (string LKey, Comparison<TierRow> Cmp)[] Sorts =
    {
        // Grade first, then score — the same order ChampionGrade.Rank produces, so this entry
        // and the default view can never disagree about what "by grade" means.
        ("stats.sort.grade", (a, b) =>
        {
            int ba = ChampionGrade.BandIndex(ChampionGrade.Of(a));
            int bb = ChampionGrade.BandIndex(ChampionGrade.Of(b));
            return ba != bb ? ba.CompareTo(bb)
                            : ChampionGrade.Score(b).CompareTo(ChampionGrade.Score(a));
        }),
        ("stats.sort.win", (a, b) => Smoothed(b).CompareTo(Smoothed(a))),
        ("stats.sort.pick", (a, b) => b.PickRate.CompareTo(a.PickRate)),
        ("stats.sort.ban", (a, b) => b.BanRate.CompareTo(a.BanRate)),
        ("stats.sort.games", (a, b) => b.Games.CompareTo(a.Games)),
    };

    private static double Smoothed(TierRow r)
        => (r.WinRate * r.Games + SmoothK * 0.5) / (r.Games + SmoothK);

    /// <summary>Localized label for a tier/lane code, falling back to the code itself when the
    /// pipeline reports one the string table has never heard of.</summary>
    private static string Label(string prefix, string code)
    {
        string key = prefix + code;
        string text = Localization.L(key);
        return text == key ? code : text;
    }

    private static string BracketLabel(string slug) => Label("stats.bracket.", slug);

    private static string LaneLabel(string lane)
        => lane.Length == 0 ? Localization.L("stats.filter.all") : Label("stats.role.", lane);

    public StatsView()
    {
        var head = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };

        var title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        title.Text = Localization.L("stats.title");
        head.Children.Add(title);

        _meta.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        head.Children.Add(_meta);

        _laneTabs.Margin = new Thickness(0, 0, 0, 10);
        head.Children.Add(_laneTabs);

        head.Children.Add(FilterBar());

        _note.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        head.Children.Add(_note);

        var page = new StackPanel();
        page.Children.Add(head);
        page.Children.Add(_list);
        // Theme convention (Theme.xaml "HiddenScroll"): every view in the home app scrolls with
        // the wheel and draws no raw WPF scrollbar.
        var scroll = new ScrollViewer { Content = page };
        scroll.SetResourceReference(StyleProperty, "HiddenScroll");
        Content = scroll;
    }

    private UIElement FilterBar()
    {
        var bar = new WrapPanel();

        _bracketBox.ToolTip = Localization.L("stats.filter.tierTip");
        _pickBox.ToolTip = Localization.L("stats.filter.pickTip");
        _searchBox.ToolTip = Localization.L("stats.filter.search");

        _bracketBox.SelectionChanged += (_, _) =>
        {
            if (_populating) return;
            _composition?.Config.Set(BracketKey, SelectedBracket);
            PopulateLanes();
            RebuildRows();
        };
        foreach (var box in new[] { _sortBox, _pickBox })
            box.SelectionChanged += (_, _) => { if (!_populating) RebuildRows(); };
        _searchBox.TextChanged += (_, _) => { if (!_populating) RebuildRows(); };

        foreach (var box in new[] { _bracketBox, _sortBox, _pickBox })
        {
            box.SetResourceReference(StyleProperty, "ThemedComboBox");
            box.Margin = new Thickness(0, 0, 8, 0);
            bar.Children.Add(box);
        }
        _searchBox.SetResourceReference(StyleProperty, "ThemedTextBox");
        bar.Children.Add(SearchField());
        return bar;
    }

    /// <summary>The search box with a "검색" watermark behind it, shown only while the box is empty,
    /// so the field is recognisable as a search at a glance. The hint takes no hits, so typing and
    /// focus are unaffected.</summary>
    private UIElement SearchField()
    {
        var hint = new TextBlock
        {
            Text = Localization.L("stats.filter.searchHint"),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        void Sync() => hint.Visibility =
            _searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Sync();
        _searchBox.TextChanged += (_, _) => Sync();

        var host = new Grid { Width = FilterWidth };
        host.Children.Add(_searchBox);
        host.Children.Add(hint);
        return host;
    }

    /// <summary>Wires the data source from config (same rec dir as every other rec consumer).
    /// Rows are built lazily on the first <see cref="Refresh"/> so app startup never pays for
    /// parsing a table the user may not open.</summary>
    public void Attach(AppComposition composition)
    {
        _composition = composition;
        if (composition.Config.Get("champSelect.recDir") is string recDir
            && !string.IsNullOrWhiteSpace(recDir))
        {
            _source = new FileTierStatsSource(recDir);
            _matchups = new FileMatchupSource(recDir);
        }
    }

    /// <summary>Called on tab entry (HomeWindow nav). First call builds the table.</summary>
    public void Refresh()
    {
        if (_built) return;
        _built = true;

        _populating = true;
        _brackets.Clear();
        _thinBrackets.Clear();
        foreach (var (slug, thin) in _source?.AvailableBrackets()
                                     ?? Array.Empty<(string, bool)>())
        {
            _brackets.Add(slug);
            if (thin) _thinBrackets.Add(slug);
            // Marked in the list itself, so a band is chosen knowing what it holds.
            _bracketBox.Items.Add(thin
                ? Localization.F("stats.bracket.thin", BracketLabel(slug))
                : BracketLabel(slug));
        }
        // Remembered choice, else the shared default — but only if this sample can answer it.
        string wanted = _composition?.Config.Get(BracketKey) as string ?? RecBrackets.Default;
        int idx = _brackets.IndexOf(wanted);
        if (idx < 0) idx = _brackets.IndexOf(RecBrackets.Default);
        _bracketBox.SelectedIndex = _brackets.Count > 0 ? Math.Max(idx, 0) : -1;
        _bracketBox.Visibility = _brackets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (lkey, _) in Sorts) _sortBox.Items.Add(Localization.L(lkey));
        _sortBox.SelectedIndex = 0;

        foreach (double floor in PickFloors)
            _pickBox.Items.Add(floor <= 0
                ? Localization.L("stats.filter.all")
                : Localization.F("stats.filter.pick", PercentText(floor)));
        _pickBox.SelectedIndex = DefaultPickFloorIndex;

        PopulateLanes();
        _populating = false;

        RebuildRows();
    }

    /// <summary>"0.5%" — one decimal, trailing ".0" trimmed, so 1% does not read as 1.0%.</summary>
    private static string PercentText(double fraction)
    {
        string text = (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture);
        if (text.EndsWith(".0", StringComparison.Ordinal)) text = text[..^2];
        return text + "%";
    }

    /// <summary>Lane tabs for the selected bracket: the positions it holds, then "all". Built from
    /// the sample, so a lane the data does not contain is never a tab.</summary>
    private void PopulateLanes()
    {
        bool outer = _populating;
        _populating = true;

        _lanes = new List<string>(_source?.Roles(SelectedBracket) ?? Array.Empty<string>());
        _lanes.Add(FileTierStatsSource.AllRoles);   // "전체" sits at the end, as on the reference
        if (_laneIndex >= _lanes.Count) _laneIndex = Math.Max(_lanes.Count - 1, 0);

        BuildLaneTabs();
        _populating = outer;
    }

    private void BuildLaneTabs()
    {
        _laneTabs.Children.Clear();
        for (int i = 0; i < _lanes.Count; i++)
        {
            int index = i;
            bool active = i == _laneIndex;

            var text = new TextBlock
            {
                Text = LaneLabel(_lanes[i]),
                FontSize = 13,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(10, 5, 10, 5),
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, active ? "Text" : "TextDim");

            var tab = new Border
            {
                Child = text,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Margin = new Thickness(0, 0, 2, 0),
            };
            tab.SetResourceReference(Border.BorderBrushProperty, active ? "Accent" : "Bg");
            tab.MouseLeftButtonUp += (_, _) =>
            {
                if (_laneIndex == index) return;
                _laneIndex = index;
                BuildLaneTabs();
                RebuildRows();
            };
            _laneTabs.Children.Add(tab);
        }
    }

    private string SelectedBracket
        => _bracketBox.SelectedIndex >= 0 && _bracketBox.SelectedIndex < _brackets.Count
            ? _brackets[_bracketBox.SelectedIndex]
            : RecBrackets.Default;

    private string SelectedLane
        => _laneIndex >= 0 && _laneIndex < _lanes.Count
            ? _lanes[_laneIndex]
            : FileTierStatsSource.AllRoles;

    private double SelectedPickFloor
        => _pickBox.SelectedIndex >= 0 && _pickBox.SelectedIndex < PickFloors.Length
            ? PickFloors[_pickBox.SelectedIndex]
            : 0;

    private void RebuildRows()
    {
        _list.Children.Clear();

        string bracket = SelectedBracket, lane = SelectedLane;
        var (patch, totalMatches) = _source?.SampleInfo ?? ("", 0);
        // "no data at all" is a different message from "no row survived the filters", so it is
        // decided on the unfiltered sample, not on this render's rows.
        if (totalMatches == 0)
        {
            _meta.Text = "";
            _note.Text = "";
            _list.Children.Add(Note("stats.empty"));
            return;
        }

        var updated = _source?.UpdatedAt;
        _meta.Text = Localization.F("stats.meta",
            updated is { } t ? t.ToString(Localization.L("stats.meta.time"), CultureInfo.CurrentCulture) : "",
            patch, _source?.Matches(bracket) ?? totalMatches);

        double pickFloor = SelectedPickFloor;
        _note.Text = _thinBrackets.Contains(bracket)
            ? Localization.F("stats.note.thin", (_source?.Matches(bracket) ?? 0).ToString("N0",
                  CultureInfo.CurrentCulture), _source?.Covered(bracket) ?? 0)
            : pickFloor > 0 ? Localization.F("stats.note.pick", PercentText(pickFloor)) : "";

        // Grades are absolute, so filtering cannot change a letter. The search is still applied
        // after ranking, because the RANK NUMBER is positional: a search should show a champion's
        // place in the lane, not renumber it to 1.
        var peers = new List<TierRow>();
        foreach (var row in RowsFor(bracket, lane))
            if (row.PickRate >= pickFloor) peers.Add(row);

        var ranked = ChampionGrade.Rank(peers);

        string needle = _searchBox.Text.Trim();
        var shown = new List<GradedRow>(ranked.Count);
        foreach (var graded in ranked)
            if (needle.Length == 0 || Matches(graded.Row, needle)) shown.Add(graded);

        if (shown.Count == 0)
        {
            _list.Children.Add(Note("stats.none"));
            return;
        }

        // Rank() already returns score order, which is what the grade sort means; the other
        // orders re-sort the same graded rows.
        int sortIdx = Math.Clamp(_sortBox.SelectedIndex, 0, Sorts.Length - 1);
        if (sortIdx != 0) shown.Sort((a, b) => Sorts[sortIdx].Cmp(a.Row, b.Row));

        _list.Children.Add(HeaderRow());
        for (int i = 0; i < shown.Count; i++)
            _list.Children.Add(ChampionRow(i + 1, shown[i]));
    }

    /// <summary>Rows for the current view. A lane tab shows that lane's rows; "전체" is the UNION of
    /// every lane's rows — each champion appears once per lane it plays, and a "전체" row is
    /// literally the same row its lane tab shows, never a separately pooled number.</summary>
    private IEnumerable<TierRow> RowsFor(string bracket, string lane)
    {
        if (_source is null) return Array.Empty<TierRow>();
        if (lane.Length != 0) return _source.All(bracket, lane);
        var union = new List<TierRow>();
        foreach (var role in _source.Roles(bracket)) union.AddRange(_source.All(bracket, role));
        return union;
    }

    /// <summary>Search matches three ways, so a Korean player never has to switch IME to find a
    /// champion: the localized display name ("가렌"), the canonical id ("Garen"), and the Korean
    /// name typed on a QWERTY keyboard with the IME still off — "잭스" comes out as "wortm", which
    /// this maps back by rendering the Korean name to its 2-beolsik keystrokes and matching that.</summary>
    private static bool Matches(TierRow row, string needle)
    {
        string ko = Localization.ChampionName(row.Name);
        if (ko.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return QwertyFromHangul(ko).Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    // 2-beolsik (두벌식) keystrokes for each jamo, in Unicode composition order. Doubled jamo use
    // their shifted key (ㄲ→R); matching is case-insensitive so an un-shifted "r" still finds it.
    private const string Cho = "r,R,s,e,E,f,a,q,Q,t,T,d,w,W,c,z,x,v,g";
    private static readonly string[] Jung =
        "k,o,i,O,j,p,u,P,h,hk,ho,hl,y,n,nj,np,nl,b,m,ml,l".Split(',');
    private static readonly string[] Jong =
        (",r,R,rt,s,sw,sg,e,f,fr,fa,fq,ft,fx,fv,fg,a,q,qt,t,T,d,w,c,z,x,v,g").Split(',');
    private static readonly string[] ChoKeys = Cho.Split(',');

    /// <summary>Renders a Korean string to the QWERTY keys a two-set keyboard would produce for it,
    /// leaving non-Hangul characters as-is. Used only to make the "IME-off" search spelling
    /// matchable; it is a fuzzy aid, not a reversible transliteration.</summary>
    private static string QwertyFromHangul(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length * 2);
        foreach (char ch in text)
        {
            int i = ch - 0xAC00;
            if (i is >= 0 and < 11172)
            {
                sb.Append(ChoKeys[i / 588]).Append(Jung[(i % 588) / 28]).Append(Jong[i % 28]);
            }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static TextBlock Note(string lkey)
    {
        var note = new TextBlock
        {
            Text = Localization.L(lkey),
            Margin = new Thickness(0, 24, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        return note;
    }

    private static readonly GridLength[] ColWidths =
    {
        new(28),                          // rank
        new(46),                          // grade badge
        new(34),                          // icon
        new(1, GridUnitType.Star),        // name
        new(54),                          // score
        new(62), new(62), new(62),        // win / pick / ban
        new(66),                          // sample
        new(92),                          // duration win-rate graph (3 buckets in one cell)
        new(60), new(60),                 // favourable / unfavourable lane opponents
    };

    private static Grid Row()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var w in ColWidths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }

    private static TextBlock Cell(string text, int col, string colorKey = "Text", bool bold = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        Grid.SetColumn(tb, col);
        return tb;
    }

    private static UIElement HeaderRow()
    {
        var g = Row();
        g.Margin = new Thickness(8, 0, 8, 6);
        // Every displayed row is now lane-scoped (a lane tab, or one lane's row inside "전체"), so
        // the pick rate is always per-lane-slot and the ban rate is always the champion-wide value
        // (bans precede positions); the headers say so on one consistent basis.
        string[] keys = { "stats.col.rank", "stats.col.grade", "", "stats.col.champion",
                          "stats.col.score", "stats.col.win",
                          "stats.col.pick.role",
                          "stats.col.ban.all",
                          "stats.col.sample",
                          "stats.col.durgraph",
                          "stats.col.favor", "stats.col.unfavor" };
        for (int c = 0; c < keys.Length; c++)
        {
            if (keys[c].Length == 0) continue;
            var cell = Cell(Localization.L(keys[c]), c, "TextDim", bold: true);
            if (c is 1 or 4) cell.ToolTip = Localization.L("stats.grade.tip");
            else if (c is 10 or 11) cell.ToolTip = Localization.L("stats.counter.headerTip");
            g.Children.Add(cell);
        }
        return g;
    }

    private UIElement ChampionRow(int rank, GradedRow graded)
    {
        var row = graded.Row;
        var g = Row();

        g.Children.Add(Cell(rank.ToString(CultureInfo.InvariantCulture), 0, "TextDim"));

        var badge = GradeBadge(graded.Grade);
        Grid.SetColumn(badge, 1);
        g.Children.Add(badge);

        var icon = new Image { Width = 26, Height = 26, Margin = new Thickness(0, 1, 0, 1) };
        _ = SetPortraitAsync(icon, row.Name);
        Grid.SetColumn(icon, 2);
        g.Children.Add(icon);

        // In "전체" the same champion appears once per lane, so each row names its lane; a lane tab
        // needs no such tag because the tab already says which lane it is.
        string name = Localization.ChampionName(row.Name);
        if (SelectedLane.Length == 0 && row.Role.Length != 0)
            name += "  " + LaneLabel(row.Role);
        g.Children.Add(Cell(name, 3, bold: true));
        // A gated row shows a score its letter does not match — that is the whole point of the
        // gate — so it is marked and says why on hover rather than looking like a sorting bug.
        var score = Cell(graded.Score.ToString("0.0", CultureInfo.InvariantCulture)
                         + (graded.Gated ? "*" : ""), 4, "TextDim");
        if (graded.Gated)
            score.ToolTip = Localization.F("stats.grade.gated",
                graded.LowerEdge.ToString("0.0", CultureInfo.InvariantCulture));
        g.Children.Add(score);
        g.Children.Add(Cell($"{row.WinRate:P2}", 5, RateColorKey(row.WinRate, row.Games)));
        g.Children.Add(Cell($"{row.PickRate:P2}", 6, "TextDim"));
        g.Children.Add(Cell($"{row.BanRate:P2}", 7, "TextDim"));
        g.Children.Add(Cell(row.Games.ToString("N0", CultureInfo.CurrentCulture), 8, "TextDim"));
        g.Children.Add(DurationGraph(row, 9));

        // Counters are per-lane. Each row carries its own lane (its Role, or the selected lane),
        // so a "전체" row shows that lane's counters. Both cells are blank when the pooled sample
        // held no qualifying matchup — a common, honest outcome on a thin patch.
        string counterLane = row.Role.Length != 0 ? row.Role : SelectedLane;
        var set = counterLane.Length != 0 ? _matchups?.Get(counterLane, row.ChampionKey) : null;
        g.Children.Add(CounterCell(set?.Best, 10));
        g.Children.Add(CounterCell(set?.Worst, 11));

        var host = new Border
        {
            Child = g,
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(4),
        };
        host.SetResourceReference(Border.BackgroundProperty, "Surface");
        host.Margin = new Thickness(0, 0, 0, 2);
        return host;
    }

    /// <summary>The grade, as the row's visual anchor: an outlined badge in the grade's colour,
    /// sitting immediately after the rank. Outlined rather than filled so it reads at the same
    /// strength whatever the surface behind it is.</summary>
    private static UIElement GradeBadge(string grade)
    {
        // Empty grade = the peer group was too small to rank; a dash, never an invented letter.
        if (grade.Length == 0) return Cell("–", 1, "TextDim");

        var text = new TextBlock
        {
            Text = grade,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, GradeColorKey(grade));

        var badge = new Border
        {
            Child = text,
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0, 1, 0, 2),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.SetResourceReference(Border.BorderBrushProperty, GradeColorKey(grade));
        return badge;
    }

    /// <summary>A descending colour ladder rather than a green/red pair: six grades need six
    /// distinguishable steps, and gold reads as "best" in this app's palette.</summary>
    private static string GradeColorKey(string grade) => grade switch
    {
        "S+" => "Accent",
        "S" => "AccentBlue",
        "A" => "Success",
        "B" => "Text",
        "C" => "TextDim",
        "D" => "Danger",
        _ => "TextDim",
    };

    /// <summary>The three game-length buckets and how to pull each from a row, left-to-right in
    /// ascending game length — the reading order of the mini bar chart.</summary>
    private static readonly (string LabelKey, Func<TierRow, DurationBucket> Pick)[] DurBuckets =
    {
        ("stats.col.lt25", r => r.Under25),
        ("stats.col.mid",  r => r.From25To32),
        ("stats.col.gt32", r => r.Over32),
    };

    private const double BarBand = 26, BarWidth = 16, BarMin = 3;

    /// <summary>The game-length win curve as a compact three-bar chart (the reference sites' shape),
    /// replacing three text columns: each bar's height maps the bucket's win rate across the 40–60%
    /// band the tier list actually lives in, coloured by the same real-deviation rule as the win
    /// column, and the exact rate + sample is on hover. Empty buckets are a dim stub, never 0%.</summary>
    private static UIElement DurationGraph(TierRow row, int col)
    {
        var bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = BarBand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (labelKey, pick) in DurBuckets)
            bars.Children.Add(Bar(pick(row), Localization.L(labelKey)));
        Grid.SetColumn(bars, col);
        return bars;
    }

    private static UIElement Bar(DurationBucket b, string window)
    {
        bool empty = b.Games == 0;
        // 50% sits mid-band; the bar saturates by 60% and bottoms out by 40%, so the visible
        // range is the one that separates a strong bucket from a weak one.
        double h = empty ? BarMin
            : Math.Clamp((b.WinRate - 0.40) / 0.20, 0, 1) * (BarBand - BarMin) + BarMin;
        var bar = new Border
        {
            Width = BarWidth,
            Height = h,
            CornerRadius = new CornerRadius(2, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = empty
                ? Localization.F("stats.dur.empty", window)
                : Localization.F("stats.dur.tip", window,
                    b.WinRate.ToString("P2", CultureInfo.CurrentCulture),
                    b.Games.ToString("N0", CultureInfo.CurrentCulture)),
        };
        bar.SetResourceReference(Border.BackgroundProperty,
            empty ? "TextDim" : RateColorKey(b.WinRate, b.Games));
        return bar;
    }

    private const int MaxCounters = 3;

    /// <summary>Up to <see cref="MaxCounters"/> lane opponents as small portraits, most extreme
    /// matchup first, each naming its win rate and sample on hover. A null or empty list is a blank
    /// cell — the champion simply has no counter on record for this lane and patch.</summary>
    private UIElement CounterCell(IReadOnlyList<Matchup>? opponents, int col)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        int n = opponents is null ? 0 : Math.Min(opponents.Count, MaxCounters);
        for (int i = 0; i < n; i++)
        {
            var m = opponents![i];
            var img = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 2, 0) };
            img.ToolTip = Localization.F("stats.counter.tip",
                Localization.ChampionName(m.Name),
                m.WinRate.ToString("P0", CultureInfo.CurrentCulture),
                m.Games.ToString("N0", CultureInfo.CurrentCulture));
            _ = SetPortraitAsync(img, m.Name);
            panel.Children.Add(img);
        }
        Grid.SetColumn(panel, col);
        return panel;
    }

    /// <summary>Green/red only when the deviation is real: thin samples (&lt;30) stay neutral so a
    /// 12-game 66% bucket doesn't light up like a signal.</summary>
    private static string RateColorKey(double rate, int games)
    {
        if (games < 30) return "TextDim";
        if (rate >= 0.52) return "Success";
        if (rate <= 0.48) return "Danger";
        return "Text";
    }

    private static async System.Threading.Tasks.Task SetPortraitAsync(Image image, string championId)
    {
        if (string.IsNullOrEmpty(championId)) return;
        var src = await DDragonIconProvider.LoadChampionPortraitAsync(championId);
        if (src is not null) image.Source = src;
    }
}
