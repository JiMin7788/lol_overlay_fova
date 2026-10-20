using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Overlay.Core.ChampSelect;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Client.ChampSelect;

/// <summary>
/// (M41) 킬각 타이밍 — in champ select, both sides' kill thresholds by level for me vs my lane
/// opponent: blue = the enemy HP% below which my full combo kills, red = my HP% below which theirs
/// kills me (<see cref="ComboRunner.ComputeKillTiming"/>). The opponent is guessed
/// (<see cref="LaneOpponent"/>) and any enemy portrait can be clicked to switch. Computed once per
/// pair off the UI thread; hidden until both my champion and an enemy are known.
/// </summary>
internal sealed class KillTimingCard : Border
{
    private const double ChartW = 380, ChartH = 120, PadL = 30, PadB = 16;

    private readonly Func<string, string, IReadOnlyList<KillTimingRow>> _compute;
    private readonly StackPanel _enemyRow = new() { Orientation = Orientation.Horizontal };
    private readonly Canvas _chart = new() { Width = ChartW, Height = ChartH, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

    private string? _me;
    private IReadOnlyList<string> _enemies = Array.Empty<string>();
    private string? _opponent;
    private bool _userPicked;
    private (string Me, string Enemy)? _shownPair;
    private int _generation;

    public KillTimingCard(Func<string, string, IReadOnlyList<KillTimingRow>> compute)
    {
        _compute = compute;
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(0, 8, 0, 0);
        SetResourceReference(StyleProperty, "CardInset");

        var title = new TextBlock { Text = "킬각 타이밍 · 상대", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        title.SetResourceReference(StyleProperty, "FieldLabel");
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(title);
        header.Children.Add(_enemyRow);

        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var caption = new TextBlock
        {
            Text = "파랑 = 상대 체력이 이 % 아래면 내 풀콤으로 킬 · 빨강 = 내 체력이 이 % 아래면 상대 풀콤에 죽음\n"
                 + "아이템·룬 없음, 점멸만 있음, 스킬은 일반적인 선마 순서 기준 추정",
            Margin = new Thickness(0, 4, 0, 0),
        };
        caption.SetResourceReference(StyleProperty, "HintText");

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_chart);
        stack.Children.Add(_summary);
        stack.Children.Add(caption);
        Child = stack;
    }

    /// <summary>Called on every champ-select refresh. Recomputes only when the pair changes.</summary>
    public void Update(string? myChampionId, IReadOnlyList<string> enemyChampionIds, string myPosition)
    {
        if (myChampionId is null || enemyChampionIds.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            _shownPair = null;
            return;
        }

        if (_me != myChampionId) _userPicked = false; // a new champion of mine: guess again
        _me = myChampionId;
        if (!_enemies.SequenceEqual(enemyChampionIds))
        {
            _enemies = enemyChampionIds.ToList();
            if (_opponent is not null && !_enemies.Contains(_opponent)) _userPicked = false;
        }
        if (!_userPicked)
            _opponent = LaneOpponent.Guess(LaneOpponent.MyRole(myPosition, myChampionId), _enemies);

        BuildEnemyRow();
        Visibility = Visibility.Visible;
        Recompute();
    }

    private void BuildEnemyRow()
    {
        _enemyRow.Children.Clear();
        foreach (var id in _enemies)
        {
            bool selected = id == _opponent;
            var image = new Image { Width = 24, Height = 24, Opacity = selected ? 1.0 : 0.45 };
            _ = SetPortraitAsync(image, id);
            var holder = new Border
            {
                Child = image,
                Margin = new Thickness(2, 0, 2, 0),
                BorderThickness = new Thickness(selected ? 2 : 0),
                CornerRadius = new CornerRadius(3),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            holder.SetResourceReference(BorderBrushProperty, "Accent");
            ToolTipService.SetToolTip(holder, Localization.ChampionName(id) + (selected ? " (선택됨)" : " — 클릭해서 상대로"));
            string pick = id;
            holder.MouseLeftButtonUp += (_, _) =>
            {
                _opponent = pick;
                _userPicked = true;
                BuildEnemyRow();
                Recompute();
            };
            _enemyRow.Children.Add(holder);
        }
    }

    private void Recompute()
    {
        if (_me is null || _opponent is null) return;
        var pair = (_me, _opponent);
        if (_shownPair == pair) return;
        _shownPair = pair;
        int generation = ++_generation;
        _chart.Children.Clear();
        _summary.Text = "계산 중…";
        Task.Run(() => _compute(pair.Item1, pair.Item2)).ContinueWith(t =>
        {
            if (generation != _generation) return; // a newer pair was requested meanwhile
            Draw(t.IsCompletedSuccessfully ? t.Result : Array.Empty<KillTimingRow>());
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Draw(IReadOnlyList<KillTimingRow> rows)
    {
        _chart.Children.Clear();
        if (rows.Count == 0)
        {
            _summary.Text = "이 조합은 계산할 수 없어요. 한쪽 챔피언의 추천 콤보나 스탯 데이터가 아직 없습니다.";
            return;
        }

        double plotW = ChartW - PadL, plotH = ChartH - PadB;
        double X(int level) => PadL + (level - 1) / 17.0 * plotW;
        double Y(double percent) => plotH - percent / 100.0 * plotH;

        // Grid: 0 / 50 / 100% lines, level labels, and a marker at level 6.
        foreach (var pct in new[] { 0, 50, 100 })
        {
            _chart.Children.Add(Line(PadL, Y(pct), ChartW, Y(pct), "Border", 1));
            _chart.Children.Add(Label($"{pct}%", 0, Y(pct) - 7));
        }
        foreach (var lv in new[] { 1, 6, 11, 16, 18 })
            _chart.Children.Add(Label(lv.ToString(), X(lv) - 4, plotH + 1));
        _chart.Children.Add(Line(X(6), 0, X(6), plotH, "TextDim", 1, dashed: true));

        _chart.Children.Add(Series(rows.Select(r => new Point(X(r.Level), Y(r.MyKillPercent))), MineBrush));
        _chart.Children.Add(Series(rows.Select(r => new Point(X(r.Level), Y(r.EnemyKillPercent))), TheirsBrush));

        // One invisible hover column per level carrying the exact numbers.
        foreach (var r in rows)
        {
            var hit = new Rectangle { Width = plotW / 17.0, Height = plotH, Fill = Brushes.Transparent };
            Canvas.SetLeft(hit, X(r.Level) - plotW / 34.0);
            ToolTipService.SetToolTip(hit,
                $"{r.Level}레벨\n내 풀콤 {r.MyDamage:N0} → 상대 체력 {r.MyKillPercent:0}% 이하면 킬\n"
                + $"상대 풀콤 {r.EnemyDamage:N0} → 내 체력 {r.EnemyKillPercent:0}% 이하면 죽음");
            ToolTipService.SetInitialShowDelay(hit, 100);
            _chart.Children.Add(hit);
        }

        string FirstFull(Func<KillTimingRow, double> pct)
            => rows.FirstOrDefault(r => pct(r) >= 100) is { } r ? $"{r.Level}레벨부터" : "없음";
        string name = Localization.ChampionName(_opponent!);
        _summary.Text = $"풀피에서 원콤 — 나: {FirstFull(r => r.MyKillPercent)} · {name}: {FirstFull(r => r.EnemyKillPercent)}";
    }

    private static readonly Brush MineBrush = (Brush)Application.Current.FindResource("Ally");
    private static readonly Brush TheirsBrush = (Brush)Application.Current.FindResource("Enemy");

    private static Polyline Series(IEnumerable<Point> points, Brush brush)
        => new() { Points = new PointCollection(points), Stroke = brush, StrokeThickness = 2 };

    private static Line Line(double x1, double y1, double x2, double y2, string brushKey, double thickness, bool dashed = false)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = thickness, Opacity = 0.6 };
        if (dashed) line.StrokeDashArray = new DoubleCollection { 3, 3 };
        line.SetResourceReference(Shape.StrokeProperty, brushKey);
        return line;
    }

    private static TextBlock Label(string text, double x, double y)
    {
        var label = new TextBlock { Text = text };
        label.SetResourceReference(TextBlock.FontSizeProperty, "FontMicro");
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        return label;
    }

    private static async Task SetPortraitAsync(Image image, string championId)
    {
        var src = await DDragonIconProvider.LoadChampionPortraitAsync(championId);
        if (src is not null) image.Source = src;
    }
}
