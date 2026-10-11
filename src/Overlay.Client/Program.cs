using Velopack;

namespace Overlay.Client;

/// <summary>Entry point (csproj StartupObject) so Velopack runs before WPF starts.</summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Must come first: during install/update/uninstall Velopack launches the exe with hook
        // arguments, and this call handles them and exits. It also applies an update that was
        // downloaded but not applied (the app died before a clean exit).
        VelopackApp.Build().Run();

        AppUpdater.Initialize();
        // Every start, not an OnFirstRun hook: a silent install never delivers the first-run
        // signal. Cheap when there is nothing to do (one registry lookup).
        if (AppUpdater.IsInstalled) LegacyInstall.Migrate();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
