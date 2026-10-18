using System.Globalization;
using System.IO;
using System.Text.Json;
using Overlay.Core.Combo;

namespace Overlay.Client;

/// <summary>
/// Downloads every icon the app can show, once, in the background — champion portraits, P/Q/W/E/R
/// ability icons (Data Dragon), the CommunityDragon icons curated for transform-form slots, item
/// icons and summoner-spell icons — so the combo editor and the overlay never wait on a CDN or show
/// letter badges the first time a champion comes up. Rune icons are warmed separately at startup
/// (<c>RunePageView.PrefetchIconsAsync</c>).
///
/// <para>Writes exactly the files the icon providers read (same directories via
/// <see cref="DDragonIconProvider.ImgDir"/>), download-only — nothing is decoded, so ~1,500 icons
/// cost disk, not memory. Skips files already present, and once a run finishes with no failures it
/// leaves a marker for the Data Dragon version, so later starts do nothing. A failed or offline run
/// simply tries again next start. Never throws.</para>
/// </summary>
internal static class AssetPrefetcher
{
    private const int Parallelism = 16;

    public static async Task RunAsync(CancellationToken ct)
    {
        var marker = Path.Combine(AppPaths.AssetCacheDir, $"prefetch-{DDragonIconProvider.Version}.done");
        if (File.Exists(marker)) return;
        try
        {
            // Let startup (repositories, first paint, update check) go first.
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

            var jobs = new List<Func<Task<bool>>>();
            var champions = ReadKeys(BundledDDragonFile("champion.json"));
            foreach (var id in champions)
            {
                var champ = id;
                jobs.Add(() => Download(DDragonIconProvider.ImgDir("champion"), champ + ".png",
                    $"img/champion/{champ}.png", ct));
                jobs.Add(() => AbilityIconsAsync(champ, ct));
                foreach (var slot in SkillDamageDb.GetCuratedSlotKeys(champ))
                    if (SkillDamageDb.GetSlotIcon(champ, slot) is { Length: > 0 } asset)
                        jobs.Add(() => DDragonIconProvider.DownloadIfMissingAsync(DDragonIconProvider.GameAssetDir,
                            DDragonIconProvider.GameAssetFileName(asset),
                            $"{DDragonIconProvider.CDragonGameRoot}/{asset}", ct));
            }
            foreach (var item in ReadKeys(BundledDDragonFile("item.json")))
            {
                var file = item + ".png";
                jobs.Add(() => Download(DDragonIconProvider.ImgDir("item"), file, $"img/item/{file}", ct));
            }
            foreach (var spellImage in await SummonerSpellImagesAsync(ct).ConfigureAwait(false))
            {
                var file = spellImage;
                jobs.Add(() => Download(DDragonIconProvider.ImgDir("spell"), file, $"img/spell/{file}", ct));
            }

            int failed = 0;
            using var gate = new SemaphoreSlim(Parallelism);
            await Task.WhenAll(jobs.Select(async job =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { if (!await job().ConfigureAwait(false)) Interlocked.Increment(ref failed); }
                finally { gate.Release(); }
            })).ConfigureAwait(false);

            Log($"{jobs.Count} icon jobs for {champions.Count} champions, {failed} failed");
            if (failed == 0 && champions.Count > 0)
                await File.WriteAllTextAsync(marker, DateTime.Now.ToString("O", CultureInfo.InvariantCulture), ct)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("prefetch failed: " + ex.Message); }
    }

    /// <summary>The champion's passive + Q/W/E/R icons. Resolving the file names fetches the
    /// champion's Data Dragon JSON when it is not cached yet (shared with the editor's path).</summary>
    private static async Task<bool> AbilityIconsAsync(string championId, CancellationToken ct)
    {
        var fulls = await AbilityIconProvider.ResolveSlotFullsAsync(championId, ct).ConfigureAwait(false);
        if (fulls.Count == 0) return false;
        bool ok = true;
        foreach (var (slot, full) in fulls)
        {
            var kind = slot == "P" ? "passive" : "spell";
            ok &= await Download(DDragonIconProvider.ImgDir(kind), full, $"img/{kind}/{full}", ct).ConfigureAwait(false);
        }
        return ok;
    }

    private static Task<bool> Download(string dir, string file, string cdnPath, CancellationToken ct)
        => DDragonIconProvider.DownloadIfMissingAsync(dir, file,
            $"{DDragonIconProvider.DDragonRoot}/cdn/{DDragonIconProvider.Version}/{cdnPath}", ct);

    /// <summary>Summoner-spell image names from Data Dragon's list (not bundled; small).</summary>
    private static async Task<List<string>> SummonerSpellImagesAsync(CancellationToken ct)
    {
        var json = await DDragonIconProvider.GetStringOrNullAsync(
            $"{DDragonIconProvider.DDragonRoot}/cdn/{DDragonIconProvider.Version}/data/en_US/summoner.json", ct)
            .ConfigureAwait(false);
        var result = new List<string>();
        if (json is null) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var spell in doc.RootElement.GetProperty("data").EnumerateObject())
                if (spell.Value.TryGetProperty("image", out var img) && img.GetProperty("full").GetString() is { Length: > 0 } full)
                    result.Add(full);
        }
        catch { /* malformed list: no spell icons this run */ }
        return result;
    }

    private static string BundledDDragonFile(string name)
        => Path.Combine(Overlay.Core.ChampionDb.DDragonData.CurrentDir ?? "", name);

    /// <summary>The keys of a Data Dragon list file's <c>data</c> object (champion ids, item ids).</summary>
    private static List<string> ReadKeys(string path)
    {
        var keys = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in doc.RootElement.GetProperty("data").EnumerateObject()) keys.Add(entry.Name);
        }
        catch { /* missing/malformed bundle file: nothing to prefetch from it */ }
        return keys;
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
