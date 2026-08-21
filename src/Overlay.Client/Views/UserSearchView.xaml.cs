using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Overlay.Core;

namespace Overlay.Client.Views;

/// <summary>
/// User-search view. Honest scope: external summoner lookup needs a Riot API key (out of
/// scope), so this searches the CURRENT live game's scoreboard by name and renders matching
/// players as result cards. When there is no active game (or no name match) it shows a clear
/// message instead of fabricating data. Static text is localized via <see cref="Localization"/>.
/// </summary>
public partial class UserSearchView : UserControl
{
    private AppComposition? _composition;

    public UserSearchView()
    {
        InitializeComponent();
        Localization.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
    }

    public void Attach(AppComposition composition) => _composition = composition;

    private void ApplyLanguage()
    {
        TitleLabel.Text = Localization.L("search.title");
        SearchBox.Tag = Localization.L("search.placeholder");
        SearchButton.Content = Localization.L("search.button");
        // Only reset the help text when no search result is currently shown.
        if (Results.Items.Count == 0)
            StatusMessage.Text = Localization.L("search.help");
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunSearch();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => RunSearch();

    private void RunSearch()
    {
        Results.Items.Clear();

        var query = SearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            StatusMessage.Text = Localization.L("search.enterName");
            return;
        }

        var snapshot = _composition?.LatestSnapshot;
        if (snapshot is not { HasData: true })
        {
            StatusMessage.Text = Localization.L("search.noGame") + Localization.L("search.help");
            return;
        }

        int matches = 0;
        for (int i = 0; i < snapshot.PlayerCount; i++)
        {
            var p = snapshot.Players[i];
            if (p.SummonerName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            Results.Items.Add(BuildCard(p));
            matches++;
        }

        StatusMessage.Text = matches > 0
            ? Localization.F("search.resultCount", query, matches)
            : Localization.F("search.noMatch", query);
    }

    private static UIElement BuildCard(ScoreboardEntry p)
    {
        bool isOrder = string.Equals(p.Team, "ORDER", StringComparison.OrdinalIgnoreCase);
        var teamBrush = (Brush)Application.Current.FindResource(isOrder ? "AccentBlue" : "Danger");
        var textBrush = (Brush)Application.Current.FindResource("Text");
        var dimBrush = (Brush)Application.Current.FindResource("TextDim");

        var stack = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Background = teamBrush,
            Margin = new Thickness(0, 2, 10, 2),
        });
        var nameStack = new StackPanel();
        nameStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(p.SummonerName) ? Localization.L("search.unknown") : p.SummonerName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush,
        });
        var team = isOrder ? Localization.L("search.blue") : Localization.L("search.red");
        nameStack.Children.Add(new TextBlock
        {
            Text = $"{Localization.ChampionName(p.ChampionName)}  ·  {team}  ·  Lv {p.Level}",
            FontSize = 12,
            Foreground = dimBrush,
            Margin = new Thickness(0, 2, 0, 0),
        });
        header.Children.Add(nameStack);
        stack.Children.Add(header);

        var kda = $"KDA {p.Kills}/{p.Deaths}/{p.Assists}   CS {p.CreepScore}";
        stack.Children.Add(new TextBlock
        {
            Text = p.IsDead ? $"{kda}   ({Localization.L("search.dead")})" : kda,
            FontSize = 13,
            Foreground = textBrush,
            Margin = new Thickness(14, 12, 0, 0),
        });

        return new Border
        {
            Style = (Style)Application.Current.FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack,
        };
    }
}
