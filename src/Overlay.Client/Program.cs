using Velopack;

namespace Overlay.Client;

/// <summary>Entry point (csproj StartupObject) so Velopack runs before WPF starts.</summary>
internal static class Program
{
    /// <summary>Started by the autostart Run entry (<see cref="Autostart.BackgroundArg"/>): wait in
    /// the tray instead of opening the dashboard.</summary>
    internal static bool StartInBackground { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        // Must come first: during install/update/uninstall Velopack launches the exe with hook
        // arguments, and this call handles them and exits. It also applies an update that was
        // downloaded but not applied (the app died before a clean exit).
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Autostart.Remove())
            .Run();

        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalFirstInstance();
            return;
        }
        StartInBackground = args.Contains(Autostart.BackgroundArg);

        AppUpdater.Initialize();
        // Data Dragon versions: downloaded ones (kept across app updates) first, the bundled one
        // as the offline fallback. Before anything reads champion/item data.
        Overlay.Core.ChampionDb.DDragonData.SetRoots(
            System.IO.Path.Combine(AppPaths.AssetCacheDir, "ddragon"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "data", "ddragon"));
        // Every start, not an OnFirstRun hook: a silent install never delivers the first-run
        // signal. Cheap when there is nothing to do (one registry lookup).
        if (AppUpdater.IsInstalled) LegacyInstall.Migrate();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
