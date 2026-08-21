using System.Text.Json;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Fetches Riot Data Dragon static data (P1 — public, official, patch-versioned) and
/// caches the raw JSON on disk under data/ddragon/{version}/ so subsequent app starts
/// do not re-download (M11 Internal Logic step 1).
///
/// Data Dragon requires no API key and serves purely static, publicly-hosted JSON —
/// there is no per-request rate limit concern like the Riot personal-key APIs.
/// A User-Agent header is set because ddragon.leagueoflegends.com returns 403 for
/// requests with no User-Agent at all.
/// </summary>
public sealed class DDragonClient
{
    private const string BaseUrl = "https://ddragon.leagueoflegends.com";
    private static readonly string[] SampleChampionIds =
    {
        "Aatrox", "Ahri", "Annie", "Zed", "Jinx",
    };

    private readonly HttpClient _http;
    private readonly string _cacheRoot;

    public DDragonClient(string? cacheRoot = null, HttpClient? httpClient = null)
    {
        _cacheRoot = cacheRoot ?? Path.Combine(AppContext.BaseDirectory, "data", "ddragon");
        _http = httpClient ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("LolOverlay-M11/1.0");
        }
    }

    /// <summary>Resolves the latest published patch version (e.g. "16.13.1") from
    /// Data Dragon's versions.json. Not cached to disk — this single small request is
    /// the version-change detection step (M11 Internal Logic step 3).</summary>
    public async Task<string> GetLatestVersionAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync($"{BaseUrl}/api/versions.json", ct).ConfigureAwait(false);
        var versions = JsonSerializer.Deserialize<List<string>>(json)
            ?? throw new InvalidDataException("versions.json deserialized to null");
        if (versions.Count == 0)
            throw new InvalidDataException("versions.json contained no versions");
        return versions[0];
    }

    /// <summary>
    /// Ensures champion.json, item.json, item.ko_KR.json, runesReforged.json, and per-champion
    /// detail files for <see cref="SampleChampionIds"/> are present in the on-disk cache for
    /// <paramref name="version"/>, downloading any that are missing. Returns the
    /// directory containing the cached files.
    /// </summary>
    public async Task<string> EnsureCachedAsync(string version, CancellationToken ct = default)
    {
        var dir = Path.Combine(_cacheRoot, version);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "champion"));

        await EnsureFileAsync(Path.Combine(dir, "champion.json"),
            $"{BaseUrl}/cdn/{version}/data/en_US/champion.json", ct).ConfigureAwait(false);
        await EnsureFileAsync(Path.Combine(dir, "item.json"),
            $"{BaseUrl}/cdn/{version}/data/en_US/item.json", ct).ConfigureAwait(false);
        // Korean locale variant of item.json — same shape, localized `name` fields only. Used to
        // populate ItemData.NameKo for the M04 item picker's dual-language search/display (Part 3).
        await EnsureFileAsync(Path.Combine(dir, "item.ko_KR.json"),
            $"{BaseUrl}/cdn/{version}/data/ko_KR/item.json", ct).ConfigureAwait(false);
        await EnsureFileAsync(Path.Combine(dir, "runesReforged.json"),
            $"{BaseUrl}/cdn/{version}/data/en_US/runesReforged.json", ct).ConfigureAwait(false);

        foreach (var championId in SampleChampionIds)
        {
            await EnsureFileAsync(
                Path.Combine(dir, "champion", $"{championId}.json"),
                $"{BaseUrl}/cdn/{version}/data/en_US/champion/{championId}.json",
                ct).ConfigureAwait(false);
        }

        return dir;
    }

    private async Task EnsureFileAsync(string path, string url, CancellationToken ct)
    {
        if (File.Exists(path)) return;
        var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }
}
