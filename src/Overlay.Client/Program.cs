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

        // Before anything logs: Core's file logs follow the app's log dir (moved in test mode).
        Overlay.Core.Logging.LogDirectory.Current = AppPaths.LogDir;
        if (AppPaths.TestDataDir is { } testDir)
        {
            // AI-NOTE: a test dir that cannot be created must not fall back to the user's real
            // state; exit code 2 tells the UI test harness why.
            try { System.IO.Directory.CreateDirectory(testDir); }
            catch { Environment.Exit(2); }
        }

        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalFirstInstance();
            return;
        }
        // First app.log message after the build-stamp header, so a log shows it came from a test run.
        if (AppPaths.TestDataDir is { } dir) AppComposition.Log($"test mode: user data dir = {dir}");
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
