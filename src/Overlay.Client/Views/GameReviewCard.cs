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
            // (design pass) An empty state instead of no card: hidden until the first recorded
            // game, new users never learned the review existed.
            _content.Children.Add(Line("지난 게임", "CardTitle"));
            _content.Children.Add(Line("게임을 한 판 하면 여기에 지난 게임 리뷰(사망·킬 돌아보기)가 나와요.", "HintText", top: 4));
            return;
        }
        var r = all[0];

        _content.Children.Add(Line(
            $"지난 게임 · {Localization.ChampionName(r.Champion)} · {ResultText(r.Result)} · "
            + $"{r.Kills}/{r.Deaths}/{r.Assists} · {Minutes(r.LengthSeconds)}",
            "CardTitle"));

        if (r.DeathEntries.Count > 0)
            _content.Children.Add(Line(
                $"사망 {r.DeathEntries.Count}번 중 {r.DeathsInDanger}번은 상대 풀콤 위험선 아래에서 "
                + $"(위험선 아래로 내려간 횟수 {r.DangerWindows.Count}번)", "BodyText", top: 8));
        else if (r.DangerWindows.Count > 0)
            _content.Children.Add(Line($"위험선 아래로 {r.DangerWindows.Count}번 내려갔지만 죽지 않음", "BodyText", top: 8));
        if (r.KillEntries.Count > 0)
            _content.Children.Add(Line(
                $"킬 {r.KillEntries.Count}번 중 {r.CheckedKills}번은 킬 전 {KillReviewRecorder.CheckWindowSeconds:0}초 안에 콤보 단축키로 데미지를 확인함",
                "BodyText", top: 4));

        foreach (var d in r.DeathEntries.Take(6))
            _content.Children.Add(Line(
                $"  {Clock(d.Time)}  {(d.KillerChampion.Length > 0 ? Localization.ChampionName(d.KillerChampion) : "?")}에게 사망"
                + (d.InDanger ? " — 위험선 아래" : ""), "HintText", top: 2));

        if (all.Count > 1)
        {
            _content.Children.Add(Line("최근 게임", "CaptionText", top: 12));
            foreach (var g in all.Skip(1).Take(RecentShown))
                _content.Children.Add(Line(
                    $"  {g.StartedAt.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)}  "
                    + $"{Localization.ChampionName(g.Champion)}  {ResultText(g.Result)}  {g.Kills}/{g.Deaths}/{g.Assists}"
                    + (g.DeathEntries.Count > 0 ? $"  · 위험선 사망 {g.DeathsInDanger}/{g.DeathEntries.Count}" : ""),
                    "HintText", top: 2));
        }

        _content.Children.Add(Line("위험선 = 상대가 모든 스킬로 한 번에 줄 수 있는 피해(추정). 내 체력이 이보다 낮으면 위험합니다. 기록은 이 PC에만 저장됩니다.",
            "HintText", top: 8));
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

    /// <param name="styleKey">A Theme.xaml text style (CardTitle / BodyText / HintText / CaptionText).</param>
    private static TextBlock Line(string text, string styleKey, double top = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, top, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(StyleProperty, styleKey);
        return block;
    }
}
