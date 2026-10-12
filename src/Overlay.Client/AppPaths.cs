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

    /// <summary>Downloaded icons (champion portraits, ability / item / spell / rune icons). Installed:
    /// <c>%LocalAppData%\Fova\cache</c> — under the app folder they were deleted by every update and
    /// downloaded again. Elsewhere: <c>data\</c> next to the exe, where they always were. Only images
    /// live here; the champion JSON the engine reads stays with the bundled data.</summary>
    public static string AssetCacheDir => AppUpdater.IsInstalled
        ? Path.Combine(InstalledDataDir, "cache")
        : Path.Combine(AppContext.BaseDirectory, "data");
}
