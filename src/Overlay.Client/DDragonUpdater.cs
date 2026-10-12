using System.Globalization;
using System.IO;
using System.Text.Json;
using Overlay.Core.ChampionDb;

namespace Overlay.Client;

/// <summary>
/// Keeps the app on the live Data Dragon patch (user decision 2026-10-12: "use the latest version
/// on every launch"). At startup it asks Data Dragon for the newest version; when that is newer
/// than the one this session runs on, it downloads the four list files the engine reads
/// (champion, item, Korean item names, runes) into <see cref="DDragonData.DownloadRoot"/>. The next
/// launch picks the folder up (<see cref="DDragonData"/> resolves the version once per process),
/// and the icon prefetcher then fills that version's icons. Per-champion JSON is fetched on demand
/// as before. Offline or on any failure nothing changes. Never throws.
///
/// <para>Unlike the CommunityDragon BINs (a frozen, audited snapshot) these values reach users
/// unreviewed: item stats and champion base stats follow the live patch. That was the explicit
/// trade-off chosen.</para>
/// </summary>
internal static class DDragonUpdater
{
    private static readonly (string File, string Locale, string Name)[] Files =
    {
        ("item.json", "en_US", "item.json"),
        ("item.ko_KR.json", "ko_KR", "item.json"),
        ("runesReforged.json", "en_US", "runesReforged.json"),
        // Last: its presence is what marks the folder complete (DDragonData.IsComplete).
        ("champion.json", "en_US", "champion.json"),
    };

    public static async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var root = DDragonIconProvider.DDragonRoot;
            var versionsJson = await DDragonIconProvider.GetStringOrNullAsync($"{root}/api/versions.json", ct)
                .ConfigureAwait(false);
            if (versionsJson is null) return;
            var latest = JsonSerializer.Deserialize<List<string>>(versionsJson)?.FirstOrDefault();
            if (latest is null || DDragonData.ParseVersion(latest) is not { } latestVersion) return;

            var current = DDragonData.ParseVersion(DDragonData.CurrentVersion);
            if (current is not null && latestVersion <= current)
            {
                Log($"Data Dragon {DDragonData.CurrentVersion} is the live patch");
                PruneOld();
                return;
            }

            var dir = Path.Combine(DDragonData.DownloadRoot, latest);
            if (DDragonData.IsComplete(dir)) return; // already downloaded, waiting for the next launch

            foreach (var (file, locale, name) in Files)
                if (!await DDragonIconProvider.DownloadIfMissingAsync(dir, file,
                        $"{root}/cdn/{latest}/data/{locale}/{name}", ct).ConfigureAwait(false))
                {
                    Log($"download of {latest}/{file} failed; retry next launch");
                    return;
                }
            Log($"downloaded Data Dragon {latest} (running {DDragonData.CurrentVersion}); used from next launch");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("Data Dragon refresh failed: " + ex.Message); }
    }

    /// <summary>Deletes downloaded version folders older than the one in use (their data and
    /// ~12 MB of icons each). Only in the download root, and only when that root is not the
    /// bundled data folder — the shipped version is never touched.</summary>
    private static void PruneOld()
    {
        try
        {
            var bundledRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "ddragon"));
            if (string.Equals(Path.GetFullPath(DDragonData.DownloadRoot), bundledRoot, StringComparison.OrdinalIgnoreCase))
                return;
            if (DDragonData.ParseVersion(DDragonData.CurrentVersion) is not { } current) return;
            foreach (var dir in Directory.GetDirectories(DDragonData.DownloadRoot))
                if (DDragonData.ParseVersion(Path.GetFileName(dir)) is { } v && v < current)
                {
                    Directory.Delete(dir, recursive: true);
                    Log($"removed old Data Dragon {Path.GetFileName(dir)}");
                }
        }
        catch { /* a locked file only delays the cleanup */ }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(AppPaths.UserDataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "assets.log"),
                "[" + DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + "] " + message + "\n");
        }
        catch { /* logging must never affect the app */ }
    }
}
