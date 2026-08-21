namespace Overlay.Core.ChampionDb;

/// <summary>
/// Fetches CommunityDragon's Champion Gameplay Data (<c>{champion}.bin.json</c>) — a
/// mirror of the static game-data files already shipped inside the Riot client, not a
/// live-memory read or reverse-engineered protocol — and caches the raw JSON on disk
/// under data/communitydragon/{championId}.bin.json (M11 Internal Logic step 1,
/// "데이터 소스 분리 원칙": BIN is the only source for spell AD/AP coefficients, since
/// Data Dragon's `spells[].vars` is empty — see M11 spec Changelog v2.0).
/// </summary>
public sealed class CommunityDragonClient
{
    private const string BaseUrl = "https://raw.communitydragon.org/latest/game/data/characters";

    /// <summary>Bulk per-locale file listing every champion's id/name/alias (Korean display
    /// names for the M04 combo editor's champion picker — see
    /// <see cref="EnsureChampionSummaryCachedAsync"/>). Not champion-specific, so it lives
    /// under a different CommunityDragon path than <see cref="BaseUrl"/>.</summary>
    private const string ChampionSummaryUrl =
        "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/ko_kr/v1/champion-summary.json";

    private readonly HttpClient _http;
    private readonly string _cacheRoot;

    public CommunityDragonClient(string? cacheRoot = null, HttpClient? httpClient = null)
    {
        _cacheRoot = cacheRoot ?? Path.Combine(AppContext.BaseDirectory, "data", "communitydragon");
        _http = httpClient ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("LolOverlay-M11/1.0");
        }
    }

    /// <summary>Ensures <c>{championId}.bin.json</c> is cached on disk for the given
    /// champion (CommunityDragon's directory/file names are the champion id lower-cased,
    /// e.g. "Aatrox" -&gt; "aatrox/aatrox.bin.json") and returns the cached file path.</summary>
    public async Task<string> EnsureCachedAsync(string championId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_cacheRoot);
        var lower = championId.ToLowerInvariant();
        var path = Path.Combine(_cacheRoot, $"{lower}.bin.json");
        if (!File.Exists(path))
        {
            var bytes = await _http.GetByteArrayAsync($"{BaseUrl}/{lower}/{lower}.bin.json", ct)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }
        return path;
    }

    /// <summary>Ensures the Korean champion-summary file (id/name/alias for the whole roster,
    /// used only for display-name localization — never gameplay data) is cached on disk and
    /// returns the cached file path. Mirrors <see cref="EnsureCachedAsync"/>'s
    /// check-exists/GetByteArrayAsync/WriteAllBytesAsync pattern.</summary>
    public async Task<string> EnsureChampionSummaryCachedAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_cacheRoot);
        var path = Path.Combine(_cacheRoot, "champion-summary-ko_kr.json");
        if (!File.Exists(path))
        {
            var bytes = await _http.GetByteArrayAsync(ChampionSummaryUrl, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }
        return path;
    }
}
