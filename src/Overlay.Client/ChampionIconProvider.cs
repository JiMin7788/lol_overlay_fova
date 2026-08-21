using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Overlay.Client;

/// <summary>
/// Fetches an enemy champion's square PORTRAIT from Riot Data Dragon (P1 — public, official,
/// patch-versioned CDN) for the combo-result card's target header, mirroring
/// <see cref="AbilityIconProvider"/>: the PNG is downloaded once, cached on disk under
/// <c>data/ddragon/{ver}/img/champion/</c>, and its local path is returned as the string
/// reference the <see cref="Render.DrawCommandRenderer"/> loads <c>Icon</c> commands from.
///
/// <para><b>Name → id.</b> The Live Client scoreboard reports enemies by DISPLAY name
/// (e.g. "Wukong"), but the portrait URL is keyed by Data Dragon id (e.g. "MonkeyKing"). The
/// two are reconciled with a load-once map read from the bundled public <c>champion.json</c>
/// summary (the same file <see cref="Overlay.Core.ChampionDb.ChampionSummary"/> reads). Since
/// that summary keys every champion by both id and display name, any valid scoreboard name
/// resolves; when the summary is unavailable the raw name is used as a last resort.</para>
///
/// <para><b>Non-blocking + graceful.</b> <see cref="GetPortraitReference"/> is a synchronous,
/// per-frame call: on the first request for a champion it kicks off a background fetch and
/// returns null (the card omits the image that frame); once cached it returns the local path.
/// An offline first run, a 403, or an unknown champion resolves to null and is NOT retried, so
/// a missing portrait never blocks or spams the render loop. Same best-effort contract as
/// <see cref="AbilityIconProvider"/>.</para>
/// </summary>
public sealed class ChampionIconProvider
{
    /// <summary>Cached Data Dragon patch version — matches <see cref="AbilityIconProvider"/>
    /// and the on-disk offline cache the app ships with.</summary>
    private const string Version = "16.13.1";

    private const string BaseUrl = "https://ddragon.leagueoflegends.com";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Display-name/id → resolved local portrait path. A null value means
    /// "resolved as missing" (unknown champion / download failed) and is deliberately kept so
    /// the same name is not re-fetched every frame.</summary>
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names with a fetch currently in flight — guards against launching a second
    /// background download for the same champion before the first completes.</summary>
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string>? _nameToId;
    private static readonly object NameMapGate = new();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient();
        // ddragon.leagueoflegends.com returns 403 for requests with no User-Agent (see DDragonClient).
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LolOverlay-M19/1.0");
        return http;
    }

    /// <summary>Local portrait path for <paramref name="championName"/> (a Live Client display
    /// name) if already cached; otherwise null while a one-shot background fetch runs (or when
    /// the champion could not be resolved). Never throws, never blocks the caller.</summary>
    public string? GetPortraitReference(string championName)
    {
        if (string.IsNullOrWhiteSpace(championName)) return null;
        if (_cache.TryGetValue(championName, out var path)) return path;
        if (_inFlight.TryAdd(championName, 0))
            _ = Task.Run(() => LoadAsync(championName));
        return null;
    }

    private async Task LoadAsync(string championName)
    {
        try
        {
            var id = ResolveId(championName);
            if (id is null)
            {
                _cache[championName] = null; // unknown champion — resolved as missing (no retry).
                return;
            }

            var dir = Path.Combine(AppContext.BaseDirectory, "data", "ddragon", Version, "img", "champion");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, id + ".png");

            if (!File.Exists(path))
            {
                var url = $"{BaseUrl}/cdn/{Version}/img/champion/{id}.png";
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            }

            _cache[championName] = path;
        }
        catch
        {
            // Offline / 403 / IO failure: resolve as missing (null) so we neither retry every
            // frame nor crash the loop; the card simply falls back to no portrait.
            _cache[championName] = null;
        }
        finally
        {
            _inFlight.TryRemove(championName, out _);
        }
    }

    /// <summary>Maps a Live Client display name (or an id) to the Data Dragon champion id used in
    /// the portrait URL, via a load-once map from the bundled public champion.json summary.
    /// Returns null for a name unknown to a loaded summary; falls back to the raw name when no
    /// summary is available (so common id==name champions still resolve).</summary>
    private static string? ResolveId(string championName)
    {
        var map = LoadNameMap();
        if (map.TryGetValue(championName, out var id)) return id;

        // (loop 116) The Live Client returns KOREAN champion names on this user's Korean client
        // (loop 44 confirmed), but the summary map is keyed by English id + English display name only,
        // so a Korean scoreboard name ("제드") misses above → the portrait silently resolved to null
        // and NEVER drew in-game (while a previously-cached English name still showed with no game
        // running). Reverse-translate Korean → English id via ChampionSummary, mirroring
        // ComboRunner.ResolveChampionId / AppComposition.ResolveActiveChampionId, then retry the map.
        var englishId = Overlay.Core.ChampionDb.ChampionSummary.ResolveKoreanName(championName);
        if (englishId is not null)
            return map.TryGetValue(englishId, out var id2) ? id2 : englishId;

        return map.Count == 0 ? championName : null;
    }

    private static Dictionary<string, string> LoadNameMap()
    {
        lock (NameMapGate)
        {
            if (_nameToId is not null) return _nameToId;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = FindSummaryPath();
                if (path is not null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("data", out var data)
                        && data.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var champ in data.EnumerateObject())
                        {
                            string id = champ.Value.TryGetProperty("id", out var idEl)
                                        && idEl.GetString() is { Length: > 0 } s
                                ? s : champ.Name;
                            map[id] = id; // key by DDragon id
                            if (champ.Value.TryGetProperty("name", out var nameEl)
                                && nameEl.GetString() is { Length: > 0 } display)
                                map[display] = id; // and by display name (scoreboard reports this)
                        }
                    }
                }
            }
            catch
            {
                // Summary unavailable/malformed → empty map; ResolveId falls back to the raw name.
            }

            _nameToId = map;
            return _nameToId;
        }
    }

    /// <summary>Locates the bundled <c>champion.json</c> under <c>data/ddragon/{version}/</c>,
    /// discovering the version dir rather than hardcoding it (newest wins), mirroring
    /// <see cref="Overlay.Core.ChampionDb.ChampionSummary"/>.</summary>
    private static string? FindSummaryPath()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "data", "ddragon");
        if (!Directory.Exists(root)) return null;
        var files = Directory.GetFiles(root, "champion.json", SearchOption.AllDirectories);
        if (files.Length == 0) return null;
        Array.Sort(files, StringComparer.Ordinal);
        return files[^1];
    }
}
