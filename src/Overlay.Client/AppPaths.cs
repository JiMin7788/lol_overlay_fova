using System.IO;

namespace Overlay.Client;

/// <summary>
/// Where user state (user_config.json, update log) lives. A Velopack install replaces the app
/// folder on every update, so there it must live outside it: <c>%LocalAppData%\Fova</c>, next to
/// the ad and flag caches. Everywhere else (Debug bundle, portable zip, <c>dotnet run</c>) it stays
/// next to the exe as before. Test mode (<see cref="Overlay.Core.TestDataDir.EnvVar"/> set) puts
/// all of it, logs and the telemetry/ad/flag caches included, under that directory; an installed
/// test copy's icon cache too (a non-installed one keeps <c>data\</c> next to its exe).
/// </summary>
internal static class AppPaths
{
    /// <summary>The test data dir (full path), or null outside test mode.</summary>
    public static readonly string? TestDataDir = Overlay.Core.TestDataDir.Resolve(
        Environment.GetEnvironmentVariable(Overlay.Core.TestDataDir.EnvVar));

    public static bool IsTestMode => TestDataDir is not null;

    public static string InstalledDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fova");

    public static string UserDataDir => TestDataDir
        ?? (AppUpdater.IsInstalled ? InstalledDataDir : AppContext.BaseDirectory);

    public static string UserConfigPath => Path.Combine(UserDataDir, "config", "user_config.json");

    /// <summary>The app's file logs: <c>logs\</c> next to the exe, or under the test data dir.</summary>
    public static string LogDir => TestDataDir is { } d
        ? Path.Combine(d, "logs")
        : Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>Telemetry install id + crash queue in test mode; null = TelemetryService's default.</summary>
    public static string? TelemetryDir => TestDataDir is { } d ? Path.Combine(d, "telemetry") : null;

    /// <summary>Ad cache in test mode; null = AdSlotService's default.</summary>
    public static string? AdsCacheDir => TestDataDir is { } d ? Path.Combine(d, "ads") : null;

    /// <summary>Remote-flags cache in test mode; null = RemoteFlags' default.</summary>
    public static string? FlagsCachePath => TestDataDir is { } d ? Path.Combine(d, "flags.json") : null;

    /// <summary>Downloaded icons (champion portraits, ability / item / spell / rune icons). Installed:
    /// <c>%LocalAppData%\Fova\cache</c> — under the app folder they were deleted by every update and
    /// downloaded again — or <c>cache\</c> under the test data dir for an installed test copy.
    /// Elsewhere: <c>data\</c> next to the exe, where they always were. Only images live here; the
    /// champion JSON the engine reads stays with the bundled data.</summary>
    public static string AssetCacheDir => Overlay.Core.TestDataDir.AssetCacheDir(
        AppUpdater.IsInstalled, TestDataDir, InstalledDataDir, AppContext.BaseDirectory);
}
