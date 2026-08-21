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
    private AppComposition? _composition;

    private readonly StackPanel _list = new() { Margin = new Thickness(0, 0, 16, 24) };
    private readonly TextBlock _meta = new() { FontSize = 12, Margin = new Thickness(0, 3, 0, 10) };
    private readonly TextBlock _note = new() { FontSize = 11, Margin = new Thickness(2, 8, 0, 8) };
    private readonly StackPanel _laneTabs = new() { Orientation = Orientation.Horizontal };
    private readonly ComboBox _bracketBox = new() { MinWidth = 116 };
    private readonly ComboBox _sortBox = new() { MinWidth = 110 };
    private readonly ComboBox _pickBox = new() { MinWidth = 104 };
    private readonly TextBox _searchBox = new() { Width = 132 };

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
        bar.Children.Add(_searchBox);
        return bar;
    }

    /// <summary>Wires the data source from config (same rec dir as every other rec consumer).
    /// Rows are built lazily on the first <see cref="Refresh"/> so app startup never pays for
    /// parsing a table the user may not open.</summary>
    public void Attach(AppComposition composition)
    {
        _composition = composition;
        if (composition.Config.Get("champSelect.recDir") is string recDir
            && !string.IsNullOrWhiteSpace(recDir))
            _source = new FileTierStatsSource(recDir);
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
        foreach (var row in _source?.All(bracket, lane) ?? Array.Empty<TierRow>())
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

        _list.Children.Add(HeaderRow(lane));
        for (int i = 0; i < shown.Count; i++)
            _list.Children.Add(ChampionRow(i + 1, shown[i]));
    }

    /// <summary>Search matches either the localized display name or the canonical id, so "가렌"
    /// and "Garen" both find the same row whatever the UI language is.</summary>
    private static bool Matches(TierRow row, string needle)
        => Localization.ChampionName(row.Name).Contains(needle, StringComparison.OrdinalIgnoreCase)
           || row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);

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
        new(72), new(72), new(72),        // duration buckets
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

    private static UIElement HeaderRow(string lane)
    {
        var g = Row();
        g.Margin = new Thickness(8, 0, 8, 6);
        // Under a lane filter the pick rate is per-lane-slot and the ban rate is not per-lane at
        // all; the headers carry that difference so the two columns are not read on one basis.
        string[] keys = { "stats.col.rank", "stats.col.grade", "", "stats.col.champion",
                          "stats.col.score", "stats.col.win",
                          lane.Length == 0 ? "stats.col.pick" : "stats.col.pick.role",
                          lane.Length == 0 ? "stats.col.ban" : "stats.col.ban.all",
                          "stats.col.sample",
                          "stats.col.lt25", "stats.col.mid", "stats.col.gt32" };
        for (int c = 0; c < keys.Length; c++)
        {
            if (keys[c].Length == 0) continue;
            var cell = Cell(Localization.L(keys[c]), c, "TextDim", bold: true);
            if (c is 1 or 4) cell.ToolTip = Localization.L("stats.grade.tip");
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

        g.Children.Add(Cell(Localization.ChampionName(row.Name), 3, bold: true));
        // A gated row shows a score its letter does not match — that is the whole point of the
        // gate — so it is marked and says why on hover rather than looking like a sorting bug.
        var score = Cell(graded.Score.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)
                         + (graded.Gated ? "*" : ""), 4, "TextDim");
        if (graded.Gated)
            score.ToolTip = Localization.F("stats.grade.gated",
                graded.LowerEdge.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture));
        g.Children.Add(score);
        g.Children.Add(Cell($"{row.WinRate:P2}", 5, RateColorKey(row.WinRate, row.Games)));
        g.Children.Add(Cell($"{row.PickRate:P2}", 6, "TextDim"));
        g.Children.Add(Cell($"{row.BanRate:P2}", 7, "TextDim"));
        g.Children.Add(Cell(row.Games.ToString("N0", CultureInfo.CurrentCulture), 8, "TextDim"));
        g.Children.Add(DurationCell(row.Under25, 9));
        g.Children.Add(DurationCell(row.From25To32, 10));
        g.Children.Add(DurationCell(row.Over32, 11));

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

    private static TextBlock DurationCell(DurationBucket b, int col)
    {
        // Empty bucket shows a dash — "0%" would read as a measured catastrophic rate.
        if (b.Games == 0) return Cell("–", col, "TextDim");
        return Cell($"{b.WinRate:P0}", col, RateColorKey(b.WinRate, b.Games));
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
