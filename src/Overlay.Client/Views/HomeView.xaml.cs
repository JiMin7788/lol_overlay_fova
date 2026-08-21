using System.Collections.Generic;
using System.Globalization;
using System.Windows.Controls;
using Overlay.Core;

namespace Overlay.Client.Views;

/// <summary>
/// Home/stats dashboard view. Pure presentation: it reads state from the shared
/// <see cref="AppComposition"/> on demand (base counts always, live player cards only when
/// in a game) and never produces data itself. <see cref="Refresh"/> is called by HomeWindow
/// on the UI thread — on load and on every GAME.CONNECTED/DISCONNECTED transition.
/// </summary>
public partial class HomeView : UserControl
{
    private AppComposition? _composition;

    public HomeView()
    {
        InitializeComponent();
        Localization.LanguageChanged += ApplyLanguage;
        ApplyLanguage();
    }

    /// <summary>Injects the shared composition and renders the first frame.</summary>
    public void Attach(AppComposition composition)
    {
        _composition = composition;
        Refresh();
    }

    /// <summary>Re-applies every static label from the current language table. Called on
    /// construction and whenever the language changes; live values are set by <see cref="Refresh"/>.</summary>
    private void ApplyLanguage()
    {
        TitleLabel.Text = Localization.L("home.title");
        SubtitleLabel.Text = Localization.L("home.subtitle");
        GameStateCaption.Text = Localization.L("home.gameState");
        SavedCombosCaption.Text = Localization.L("home.savedCombos");
        LiveStatsCaption.Text = Localization.L("home.liveStats");
        EmptyStateLabel.Text = Localization.L("home.emptyState");
        ChampionCaption.Text = Localization.L("home.champion");
        LevelCaption.Text = Localization.L("home.level");
        GoldCaption.Text = Localization.L("home.gold");
        Refresh();
    }

    /// <summary>Re-reads the shared state and updates every card. Safe to call repeatedly;
    /// degrades to placeholders when data is missing.</summary>
    public void Refresh()
    {
        if (_composition is null) return;

        ComboCountValue.Text = CountSavedCombos().ToString(CultureInfo.InvariantCulture);

        var snapshot = _composition.LatestSnapshot;
        bool inGame = snapshot is { HasData: true };

        GameStateValue.Text = inGame ? Localization.L("home.detected") : Localization.L("home.idle");
        InGamePanel.Visibility = inGame ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        EmptyState.Visibility = inGame ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        if (inGame && snapshot is not null)
        {
            ChampionValue.Text = ResolveActiveChampion(snapshot);
            LevelValue.Text = snapshot.Level.ToString(CultureInfo.InvariantCulture);
            GoldValue.Text = ((int)snapshot.CurrentGold).ToString("N0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Resolves the active player's champion name from the scoreboard by matching the
    /// active summoner name; falls back to the summoner name when no scoreboard row matches.</summary>
    private static string ResolveActiveChampion(GameSnapshot snapshot)
    {
        for (int i = 0; i < snapshot.PlayerCount; i++)
        {
            var p = snapshot.Players[i];
            if (p.SummonerName == snapshot.ActivePlayerSummonerName && !string.IsNullOrEmpty(p.ChampionName))
                return Localization.ChampionName(p.ChampionName);
        }
        return string.IsNullOrEmpty(snapshot.ActivePlayerSummonerName) ? "-" : snapshot.ActivePlayerSummonerName;
    }

    private int CountSavedCombos()
    {
        return _composition?.Config.Get("combos.saved") is IDictionary<string, object?> saved ? saved.Count : 0;
    }
}
