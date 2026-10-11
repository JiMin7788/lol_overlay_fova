using System.IO;

namespace Overlay.Client;

/// <summary>
/// Where user state (user_config.json, update log) lives. A Velopack install replaces the app
/// folder on every update, so there it must live outside it: <c>%LocalAppData%\Fova</c>, next to
/// the ad and flag caches. Everywhere else (Debug bundle, portable zip, <c>dotnet run</c>) it stays
/// next to the exe as before.
/// </summary>
internal static class AppPaths
{
    public static string InstalledDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fova");

    public static string UserDataDir => AppUpdater.IsInstalled ? InstalledDataDir : AppContext.BaseDirectory;

    public static string UserConfigPath => Path.Combine(UserDataDir, "config", "user_config.json");
}
