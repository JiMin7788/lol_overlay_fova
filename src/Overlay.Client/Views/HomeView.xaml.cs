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

        SavedCombosLine.Text = Localization.L("home.savedCombos") + " "
            + CountSavedCombos().ToString(CultureInfo.InvariantCulture);

        var snapshot = _composition.LatestSnapshot;
        bool inGame = snapshot is { HasData: true };

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

    /// <summary>Deleting a combo writes null under its id (ConfigManager keeps the key), so only
    /// non-empty entries count — counting keys showed deleted combos too (48 vs 29 real ones),
    /// which also disagreed with the Combos tab.</summary>
    private int CountSavedCombos()
    {
        if (_composition?.Config.Get("combos.saved") is not IDictionary<string, object?> saved) return 0;
        int n = 0;
        foreach (var value in saved.Values)
            if (!string.IsNullOrWhiteSpace(value?.ToString())) n++;
        return n;
    }
}
