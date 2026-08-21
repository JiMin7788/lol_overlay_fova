using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overlay.Client;

/// <summary>
/// Small square-icon loader for the M04 icon-tile pickers (loop 38 pending change item 5):
/// champion portraits, item icons, and rune icons, all from Riot Data Dragon (P1 — public,
/// official, patch-versioned CDN). Mirrors <see cref="AbilityIconProvider"/>'s contract exactly
/// (download once, cache bytes on disk, decode to a frozen <see cref="ImageSource"/>, never throw
/// out of a public method) but is generic over the three icon kinds this module needs instead of
/// one champion's P/Q/W/E/R set.
/// </summary>
public static class DDragonIconProvider
{
    /// <summary>Cached Data Dragon patch version — matches <see cref="AbilityIconProvider"/> and
    /// <see cref="ChampionIconProvider"/>.</summary>
    private const string Version = "16.13.1";

    private const string BaseUrl = "https://ddragon.leagueoflegends.com";

    /// <summary>CommunityDragon's un-versioned ("latest") game-asset root — used for icons Data Dragon
    /// doesn't serve, e.g. transform-form ability icons (Jayce Cannon, Gnar Mega). See
    /// <see cref="LoadGameAssetIconAsync"/>.</summary>
    private const string CDragonGameUrl = "https://raw.communitydragon.org/latest/game";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>In-memory decode cache so a champion/item/rune already shown once in a session is
    /// never re-decoded from disk. Keyed by the same string used to build the on-disk path.</summary>
    private static readonly ConcurrentDictionary<string, ImageSource> MemoryCache = new(StringComparer.Ordinal);

    /// <summary>(loop 515) Failure memory + in-flight dedupe for every loader here. The
    /// <c>*PathOrNull</c> members run inside the 60fps HUD render loop; without these, a miss
    /// (offline, CDN 403, an id the pinned patch has no asset for) launched a fresh HTTP attempt
    /// EVERY FRAME — ~240 requests over one 4s enemy-item toast. A failure is remembered for
    /// <see cref="FailureRetryMs"/> and then retried once, so going back online recovers without a
    /// storm. <see cref="ChampionIconProvider"/> already did this (it caches null); this brings the
    /// rest of the loaders to the same contract. Cancellation is never treated as a failure.</summary>
    private static readonly ConcurrentDictionary<string, long> FailedUntil = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> InFlight = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task> EnsureInFlight = new(StringComparer.Ordinal);
    private const int FailureRetryMs = 60_000;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient();
        // ddragon.leagueoflegends.com returns 403 for requests with no User-Agent (see DDragonClient).
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LolOverlay-M19/1.0");
        return http;
    }

    /// <summary>Loads a champion's square portrait (<c>cdn/{ver}/img/champion/{id}.png</c>).</summary>
    public static Task<ImageSource?> LoadChampionPortraitAsync(string championId, CancellationToken ct = default)
        => LoadAsync(
            memoryKey: "champion:" + championId,
            diskDir: Path.Combine(AppContext.BaseDirectory, "data", "ddragon", Version, "img", "champion"),
            fileName: championId + ".png",
            url: $"{BaseUrl}/cdn/{Version}/img/champion/{championId}.png",
            ct);

    /// <summary>The on-disk PATH of an item icon if already cached (a valid image reference for the
    /// overlay's <c>Icon</c> draw command), else null — and, when absent, kicks off a background
    /// download so it's ready on a later frame. Synchronous + non-blocking, mirroring
    /// <see cref="SummonerIconPathOrNull"/> / <see cref="ChampionIconProvider.GetPortraitReference"/>'s
    /// contract for the HUD render loop (used by the enemy-item alert card).</summary>
    public static string? ItemIconPathOrNull(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        var path = Path.Combine(AppContext.BaseDirectory, "data", "ddragon", Version, "img", "item", itemId + ".png");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        _ = LoadItemIconAsync(itemId); // background fetch; the icon appears on a subsequent frame
        return null;
    }

    /// <summary>Loads an item's square icon (<c>cdn/{ver}/img/item/{itemId}.png</c>).</summary>
    public static Task<ImageSource?> LoadItemIconAsync(string itemId, CancellationToken ct = default)
        => LoadAsync(
            memoryKey: "item:" + itemId,
            diskDir: Path.Combine(AppContext.BaseDirectory, "data", "ddragon", Version, "img", "item"),
            fileName: itemId + ".png",
            url: $"{BaseUrl}/cdn/{Version}/img/item/{itemId}.png",
            ct);

    /// <summary>(loop 176) The on-disk PATH of a summoner-spell icon if already cached (a valid image
    /// reference for the overlay's <c>Icon</c> draw command), else null — and, when absent, kicks off a
    /// background download so it's ready on a later frame. Synchronous + non-blocking, mirroring
    /// <see cref="ChampionIconProvider.GetPortraitReference"/>'s contract for the HUD render loop.</summary>
    public static string? SummonerIconPathOrNull(string spellId)
    {
        if (string.IsNullOrWhiteSpace(spellId)) return null;
        var path = Path.Combine(AppContext.BaseDirectory, "data", "ddragon", Version, "img", "spell", spellId + ".png");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        _ = LoadSummonerIconAsync(spellId); // background fetch; the icon appears on a subsequent frame
        return null;
    }

    /// <summary>Loads a summoner-spell icon (<c>cdn/{ver}/img/spell/{spellId}.png</c>, e.g.
    /// <c>SummonerDot</c> for Ignite) — the same versioned spell endpoint
    /// <see cref="AbilityIconProvider"/> uses for champion abilities.</summary>
    public static Task<ImageSource?> LoadSummonerIconAsync(string spellId, CancellationToken ct = default)
        => LoadAsync(
            memoryKey: "summoner:" + spellId,
            diskDir: Path.Combine(AppContext.BaseDirectory, "data", "ddragon", Version, "img", "spell"),
            fileName: spellId + ".png",
            url: $"{BaseUrl}/cdn/{Version}/img/spell/{spellId}.png",
            ct);

    /// <summary>Loads a CommunityDragon game-asset icon from a curated relative path (e.g.
    /// "assets/characters/jayce/hud/icons2d/jayceq_ranged.png"), served at
    /// <c>raw.communitydragon.org/latest/game/{path}</c>. Used for transform-form ability icons that
    /// Data Dragon's base P/Q/W/E/R spell-icon set doesn't include (Jayce Cannon, Gnar Mega). A
    /// null/empty path or failed fetch returns null (caller keeps the letter badge).</summary>
    public static Task<ImageSource?> LoadGameAssetIconAsync(string relativePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Task.FromResult<ImageSource?>(null);
        var safeFileName = relativePath.Replace('/', '_');
        return LoadAsync(
            memoryKey: "cdasset:" + relativePath,
            diskDir: Path.Combine(AppContext.BaseDirectory, "data", "cdragon", "img"),
            fileName: safeFileName,
            url: $"{CDragonGameUrl}/{relativePath}",
            ct);
    }

    /// <summary>Loads a rune's icon from its Data Dragon-relative <paramref name="iconPath"/> (e.g.
    /// "perk-images/Styles/Domination/CheapShot/CheapShot.png"), served UN-versioned at
    /// <c>cdn/img/{iconPath}</c> (rune icons have no per-patch path segment, unlike champion/item/
    /// spell icons).</summary>
    public static Task<ImageSource?> LoadRuneIconAsync(string iconPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return Task.FromResult<ImageSource?>(null);
        var safeFileName = iconPath.Replace('/', '_');
        return LoadAsync(
            memoryKey: "rune:" + iconPath,
            diskDir: Path.Combine(AppContext.BaseDirectory, "data", "ddragon", "img", "perk"),
            fileName: safeFileName,
            url: $"{BaseUrl}/cdn/img/{iconPath}",
            ct);
    }

    /// <summary>Directory holding the minimap circle icons (see
    /// <see cref="ChampionCircleIconPathOrNull"/>). Separate from <c>cdragon/img</c> so the minimap's
    /// template set can be inspected — or deleted to force a refetch — on its own.</summary>
    private static string CircleIconDir =>
        Path.Combine(AppContext.BaseDirectory, "data", "cdragon", "minimap");

    /// <summary>Champions whose minimap icon is filed under something other than
    /// <c>{name}_circle_0</c> or <c>{name}_circle</c> — mostly the champion's ORIGINAL internal
    /// codename from before it was renamed, which the asset never followed.
    ///
    /// <para>Found by listing all 172 champions' asset folders rather than by guessing: exactly these
    /// ten resolve to neither standard spelling, and without them each would silently keep the Data
    /// Dragon square and stay in the hard-to-identify regime §43-AP describes.</para></summary>
    private static readonly Dictionary<string, string> LegacyCircleFileNames = new(StringComparer.Ordinal)
    {
        ["Chogath"] = "greenterror_circle.png",
        ["Rammus"] = "armordillo_circle.png",
        ["Anivia"] = "cryophoenix_circle.png",
        ["Shaco"] = "jester_circle.png",
        ["Zilean"] = "chronokeeper_circle.png",
        ["Blitzcrank"] = "steamgolem_circle.png",
        ["Orianna"] = "oriana_circle.png",           // asset kept the misspelling
        ["XinZhao"] = "xinzhaorework_circle_0.png",
        ["Shyvana"] = "shyvana_circle_0.shyvana_rework.png",
        ["Locke"] = "locke_circle_0.locke.png",
    };

    /// <summary>The on-disk PATH of a champion's MINIMAP icon if already cached, else null — kicking
    /// off a background download so it is ready on a later call. Same synchronous, non-blocking
    /// contract as <see cref="ItemIconPathOrNull"/>.
    ///
    /// <para>(§43-AP) This is the image the game actually draws on the minimap, and it is NOT Data
    /// Dragon's square portrait — that one is framed and zoomed differently. Replaying 608 real
    /// captured frames through the detector, switching the templates to this asset moved the same
    /// icons from similarity 0.75-0.78 (runner-up within 0.03, so refused as ambiguous) to 0.87-0.91
    /// with a 0.12-0.16 margin, taking identified sightings from 696 to 1126 with no identity flips.
    /// One enemy — Sett — went from 3 sightings in a whole game to 402: he had been scoring just
    /// under the ambiguity bar every single frame, so afterimage, disappear and voice never fired for
    /// him at all.</para></summary>
    public static string? ChampionCircleIconPathOrNull(string championId)
    {
        if (string.IsNullOrWhiteSpace(championId)) return null;
        var path = Path.Combine(CircleIconDir, championId + ".png");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        _ = EnsureChampionCircleIconAsync(championId); // background fetch; ready on a later call
        return null;
    }

    /// <summary>Downloads a champion's minimap icon if not already cached.
    ///
    /// <para>Two spellings are in use and there is no way to tell which from the id alone: most
    /// champions publish <c>{name}_circle_0.png</c>, while some (Wukong, Kayn) publish an
    /// un-suffixed <c>{name}_circle.png</c>. Both are tried before giving up, and giving up is not an
    /// error — that champion simply keeps the Data Dragon square as its template.</para>
    ///
    /// <para>Skins are deliberately NOT followed: the minimap draws the base icon whatever skin is
    /// worn. Two champions do change icon with FORM — Kayn
    /// (<c>kayn_ass_circle</c>/<c>kayn_slay_circle</c>) and Gnar (<c>gnarbig/gnarbig_circle</c>) —
    /// and neither is handled here, because nothing in this codebase can currently observe which
    /// form they are in. Both fall back to their base icon. Surveying all 172 champions' asset
    /// folders, those are the only two: Elise, Nidalee, Jayce, Rek'Sai, Shyvana and Kled publish no
    /// alternate circle icon at all, so their minimap icon does not change.</para></summary>
    public static Task EnsureChampionCircleIconAsync(string championId, CancellationToken ct = default)
    {
        string key = "circle:" + championId;
        if (FailedUntil.TryGetValue(key, out var until) && Environment.TickCount64 < until)
            return Task.CompletedTask;
        return EnsureInFlight.GetOrAdd(key, _ => EnsureChampionCircleIconCoreAsync(key, championId, ct));
    }

    private static async Task EnsureChampionCircleIconCoreAsync(
        string key, string championId, CancellationToken ct)
    {
        var path = Path.Combine(CircleIconDir, championId + ".png");
        try
        {
            Directory.CreateDirectory(CircleIconDir);
            if (File.Exists(path) && new FileInfo(path).Length == 0) File.Delete(path);
            if (File.Exists(path)) return;

            var lower = championId.ToLowerInvariant();
            var names = new List<string> { $"{lower}_circle_0.png", $"{lower}_circle.png" };
            if (LegacyCircleFileNames.TryGetValue(championId, out var legacy)) names.Add(legacy);

            foreach (var name in names)
            {
                try
                {
                    var bytes = await Http
                        .GetByteArrayAsync($"{CDragonGameUrl}/assets/characters/{lower}/hud/{name}", ct)
                        .ConfigureAwait(false);
                    if (bytes.Length == 0) continue;
                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                    return;
                }
                catch (HttpRequestException) { /* try the next spelling */ }
            }
        }
        catch { /* best-effort, same as every other loader here */ }
        finally
        {
            // Giving up is still not an error — the champion keeps the Data Dragon square — but it
            // IS remembered, so the retry cadence is FailureRetryMs, not every caller.
            if (!ct.IsCancellationRequested && !File.Exists(path))
                FailedUntil[key] = Environment.TickCount64 + FailureRetryMs;
            EnsureInFlight.TryRemove(key, out _);
        }
    }

    private static Task<ImageSource?> LoadAsync(
        string memoryKey, string diskDir, string fileName, string url, CancellationToken ct)
    {
        if (MemoryCache.TryGetValue(memoryKey, out var cached)) return Task.FromResult<ImageSource?>(cached);
        if (FailedUntil.TryGetValue(memoryKey, out var until) && Environment.TickCount64 < until)
            return Task.FromResult<ImageSource?>(null);
        return InFlight.GetOrAdd(memoryKey, _ => LoadCoreAsync(memoryKey, diskDir, fileName, url, ct));
    }

    private static async Task<ImageSource?> LoadCoreAsync(
        string memoryKey, string diskDir, string fileName, string url, CancellationToken ct)
    {
        try
        {
            var bmp = await LoadUncachedAsync(memoryKey, diskDir, fileName, url, ct).ConfigureAwait(false);
            if (bmp is not null) FailedUntil.TryRemove(memoryKey, out _);
            else if (!ct.IsCancellationRequested)
                FailedUntil[memoryKey] = Environment.TickCount64 + FailureRetryMs;
            return bmp;
        }
        finally
        {
            InFlight.TryRemove(memoryKey, out _);
        }
    }

    private static async Task<ImageSource?> LoadUncachedAsync(
        string memoryKey, string diskDir, string fileName, string url, CancellationToken ct)
    {
        if (MemoryCache.TryGetValue(memoryKey, out var cached)) return cached;

        try
        {
            Directory.CreateDirectory(diskDir);
            var path = Path.Combine(diskDir, fileName);

            // (loop 172) A cached file of 0 bytes — e.g. an interrupted write from a prior run — passes
            // File.Exists but decodes to nothing, permanently pinning the letter fallback for that icon
            // (the download branch never re-runs while the file exists). Treat an empty file as absent so
            // it re-downloads. This is the most likely cause of "some rune icons never show" (their paths
            // are all valid on the live CDN — verified — so a real 404 isn't the culprit for those).
            if (File.Exists(path) && new FileInfo(path).Length == 0)
                File.Delete(path);

            if (!File.Exists(path))
            {
                var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            }

            var data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            BitmapImage bmp;
            try
            {
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze(); // cross-thread safe: constructed off the UI thread, consumed on it.
            }
            catch
            {
                // Corrupt cached bytes (partial download, wrong content): delete so the NEXT attempt
                // re-fetches a clean copy instead of decode-failing forever off the same bad file.
                try { File.Delete(path); } catch { /* best-effort */ }
                return null;
            }

            MemoryCache[memoryKey] = bmp;
            return bmp;
        }
        catch
        {
            // Offline / 403 / 404 (e.g. a modeled rune with no live CDN asset): best-effort, caller
            // falls back to a letter badge.
            return null;
        }
    }
}
