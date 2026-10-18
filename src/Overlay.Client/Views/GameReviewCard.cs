using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Overlay.Core.Review;

namespace Overlay.Client.Views;

/// <summary>
/// (M42) 지난 게임 — the latest <see cref="GameReview"/> on HOME: result and K/D/A, how many deaths
/// came while my HP was under an enemy's full-combo line, how many kills followed a combo check,
/// and the deaths in order; then a short list of earlier games. Refreshes when a review is saved.
/// Hidden until there is one.
/// </summary>
internal sealed class GameReviewCard : Border
{
    private const int RecentShown = 5;
    private readonly KillReviewStore _store;
    private readonly StackPanel _content = new();

    public GameReviewCard(KillReviewStore store)
    {
        _store = store;
        SetResourceReference(StyleProperty, "Card");
        Child = _content;
        Rebuild();
        _store.Saved += _ => Dispatcher.BeginInvoke(new Action(Rebuild));
    }

    private void Rebuild()
    {
        _content.Children.Clear();
        var all = _store.LoadAll();
        if (all.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        var r = all[0];

        _content.Children.Add(Line(
            $"지난 게임 · {Localization.ChampionName(r.Champion)} · {ResultText(r.Result)} · "
            + $"{r.Kills}/{r.Deaths}/{r.Assists} · {Minutes(r.LengthSeconds)}",
            "Text", 15, bold: true));

        if (r.DeathEntries.Count > 0)
            _content.Children.Add(Line(
                $"사망 {r.DeathEntries.Count}번 중 {r.DeathsInDanger}번은 상대 풀콤 위험선 아래에서 "
                + $"(위험선 아래로 내려간 횟수 {r.DangerWindows.Count}번)", "Text", 13, top: 8));
        else if (r.DangerWindows.Count > 0)
            _content.Children.Add(Line($"위험선 아래로 {r.DangerWindows.Count}번 내려갔지만 죽지 않음", "Text", 13, top: 8));
        if (r.KillEntries.Count > 0)
            _content.Children.Add(Line(
                $"킬 {r.KillEntries.Count}번 중 {r.CheckedKills}번은 콤보 확인(단축키) 후 {KillReviewRecorder.CheckWindowSeconds:0}초 안에",
                "Text", 13, top: 4));

        foreach (var d in r.DeathEntries.Take(6))
            _content.Children.Add(Line(
                $"  {Clock(d.Time)}  {(d.KillerChampion.Length > 0 ? Localization.ChampionName(d.KillerChampion) : "?")}에게 사망"
                + (d.InDanger ? " — 위험선 아래" : ""), "TextDim", 12, top: 2));

        if (all.Count > 1)
        {
            _content.Children.Add(Line("최근 게임", "TextDim", 12, top: 10));
            foreach (var g in all.Skip(1).Take(RecentShown))
                _content.Children.Add(Line(
                    $"  {g.StartedAt.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)}  "
                    + $"{Localization.ChampionName(g.Champion)}  {ResultText(g.Result)}  {g.Kills}/{g.Deaths}/{g.Assists}"
                    + (g.DeathEntries.Count > 0 ? $"  · 위험선 사망 {g.DeathsInDanger}/{g.DeathEntries.Count}" : ""),
                    "TextDim", 12, top: 2));
        }

        _content.Children.Add(Line("위험선 = 상대 풀콤 추정치(모든 스킬 준비 기준). 기록은 이 PC에만 저장됩니다.",
            "TextDim", 11, top: 8));
    }

    private static string ResultText(string result) => result switch
    {
        "Win" => "승리",
        "Lose" => "패배",
        _ => "결과 없음",
    };

    private static string Minutes(double seconds) => $"{(int)(seconds / 60)}분";

    private static string Clock(double seconds)
        => $"{(int)(seconds / 60)}:{((int)seconds % 60).ToString("00", CultureInfo.InvariantCulture)}";

    private static TextBlock Line(string text, string brushKey, double size, double top = 0, bool bold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Margin = new Thickness(0, top, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }
}
