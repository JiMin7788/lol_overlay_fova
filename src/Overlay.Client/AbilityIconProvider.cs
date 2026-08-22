using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overlay.Client;

/// <summary>
/// Fetches a champion's real ability icons from Riot Data Dragon (P1 — public, official,
/// patch-versioned CDN) for the combo-editor palette. Given a champion id it reads the cached
/// <c>champion/{id}.json</c> to resolve each spell's and the passive's image filename, then loads
/// each icon PNG from <c>cdn/{ver}/img/spell|passive/{full}</c>, caching the bytes on disk under
/// <c>data/ddragon/{ver}/img/spell|passive/</c> so subsequent runs never re-download.
///
/// <para><b>Slot mapping.</b> P = the passive icon; Q/W/E/R = <c>spells[0..3]</c> in slot order.
/// Auto-attack (AA) has no ability icon (the view draws a sword glyph) and is intentionally absent
/// from the returned map.</para>
///
/// <para><b>Graceful + non-blocking.</b> Everything is async and fully best-effort: a missing cache
/// file, an offline first run, a 403, or a malformed JSON never throws out of
/// <see cref="LoadIconsAsync"/> — the affected slot is simply omitted and the caller falls back to
/// its letter badge. Returned <see cref="ImageSource"/>s are frozen, so they are safe to hand to the
/// UI thread. The CDN 403s without a <c>User-Agent</c> header (same as <c>DDragonClient</c>), so one
/// is always set.</para>
/// </summary>
public static class AbilityIconProvider
{
    /// <summary>Cached Data Dragon patch version — matches <c>AppComposition.DataDragonVersion</c>
    /// and the on-disk offline cache the app ships with.</summary>
    private const string Version = "16.13.1";

    private const string BaseUrl = "https://ddragon.leagueoflegends.com";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient();
        // ddragon.leagueoflegends.com returns 403 for requests with no User-Agent (see DDragonClient).
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LolOverlay-M19/1.0");
        return http;
    }

    /// <summary>Resolves and loads the P/Q/W/E/R ability icons for <paramref name="championId"/>.
    /// Returns a slot → frozen <see cref="ImageSource"/> map containing only the slots that loaded
    /// successfully; on any failure the slot is omitted (never throws).</summary>
    public static async Task<IReadOnlyDictionary<string, ImageSource>> LoadIconsAsync(
        string championId, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(championId)) return result;

        var fulls = await ResolveSlotFullsAsync(championId, ct).ConfigureAwait(false);
        if (fulls.Count == 0) return result;

        if (fulls.TryGetValue("P", out var passiveFull))
            await AddSlotAsync(result, "P", passiveFull, "passive", ct).ConfigureAwait(false);
        foreach (var slot in new[] { "Q", "W", "E", "R" })
            if (fulls.TryGetValue(slot, out var spellFull))
                await AddSlotAsync(result, slot, spellFull, "spell", ct).ConfigureAwait(false);

        return result;
    }

    /// <summary>Reads (fetching + caching the champion JSON first if needed) the slot → Data Dragon
    /// "full" image filename map for a champion — <c>P</c> = passive, <c>Q/W/E/R</c> = spells[0..3].
    /// Best-effort: returns an empty map (never throws) on any failure. Split out of
    /// <see cref="LoadIconsAsync"/> so both the editor's ImageSource load AND the overlay's
    /// <see cref="AbilityIconPathOrNull"/> path resolver share one JSON-parse.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ResolveSlotFullsAsync(
        string championId, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(championId)) return map;

        var championJson = Path.Combine(
            AppContext.BaseDirectory, "data", "ddragon", Version, "champion", championId + ".json");
        if (!File.Exists(championJson) && !await TryFetchChampionJsonAsync(championJson, championId, ct).ConfigureAwait(false))
            return map;

        try
        {
            using var doc = JsonDocument.Parse(
                await File.ReadAllTextAsync(championJson, ct).ConfigureAwait(false));
            var champ = doc.RootElement.GetProperty("data").GetProperty(championId);
            var passiveFull = champ.GetProperty("passive").GetProperty("image").GetProperty("full").GetString();
            if (!string.IsNullOrEmpty(passiveFull)) map["P"] = passiveFull!;
            var slots = new[] { "Q", "W", "E", "R" };
            int i = 0;
            foreach (var spell in champ.GetProperty("spells").EnumerateArray())
            {
                if (i >= slots.Length) break;
                var full = spell.GetProperty("image").GetProperty("full").GetString();
                if (!string.IsNullOrEmpty(full)) map[slots[i]] = full!;
                i++;
            }
        }
        catch
        {
            // Malformed/unexpected champion JSON — no icons; caller falls back to letter badges.
        }

        return map;
    }

    /// <summary>Per-champion slot → "full" filename map, populated in the background by
    /// <see cref="EnsurePrepared"/> the first time a champion's icons are requested from the render
    /// loop. Keyed case-insensitively so "Aatrox"/"aatrox" share one entry.</summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> SlotFulls =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards against launching the same champion's background prepare more than once.</summary>
    private static readonly ConcurrentDictionary<string, byte> PrepareInFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>(loop 515) Earliest tick a champion's prepare may run again. PrepareInFlight only
    /// guards WHILE a task runs — a failed prepare was relaunched by the very next frame of the
    /// render loop, each attempt doing up to two CDN fetches, a continuous serial retry for as long
    /// as the icon stayed on screen. Every prepare now stamps this on completion, capping attempts
    /// at one per champion per <see cref="PrepareRetryMs"/> whatever the outcome (a fully successful
    /// prepare never re-enters — the resolved paths short-circuit before EnsurePrepared).</summary>
    private static readonly ConcurrentDictionary<string, long> NextPrepareAt =
        new(StringComparer.OrdinalIgnoreCase);

    private const int PrepareRetryMs = 60_000;

    /// <summary>The on-disk PATH of a champion ability icon (<paramref name="slot"/> = P/Q/W/E/R) if it
    /// is already resolved AND cached on disk (a valid image reference for the overlay's <c>Icon</c> draw
    /// command), else <see langword="null"/>. Synchronous + non-blocking, mirroring
    /// <c>DDragonIconProvider.SummonerIconPathOrNull</c>'s HUD-render-loop contract: on a miss it kicks off
    /// a background prepare (resolve the champion's slot filenames + download the icon PNGs) so the icon
    /// appears on a later frame, and the caller falls back to the key letter until then.</summary>
    /// <summary>The canonical ability letter (P/Q/W/E/R) that owns <paramref name="slot"/>'s icon, or
    /// null when the slot has no ability icon of its own (AA, Ignite, Flash, empty). A multi-cast
    /// sub-slot or named variant shares its base ability's art — "E2"/"Q3" (Akali E, Aatrox Q),
    /// "RWall"/"WBite" (Irelia/Briar) all resolve to R/W/E — so the overlay draws an icon there
    /// instead of nothing, matching the editor's <c>TryResolveSlotIcon</c> fallback.</summary>
    internal static string? CanonicalIconSlot(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot)) return null;
        slot = slot.ToUpperInvariant();
        if (slot is "P" or "Q" or "W" or "E" or "R") return slot;
        // Canonical slots are single letters, so a longer key starting with one is a variant of it.
        char b = slot[0];
        return b is 'P' or 'Q' or 'W' or 'E' or 'R' ? b.ToString() : null;
    }

    public static string? AbilityIconPathOrNull(string championId, string slot)
    {
        if (string.IsNullOrWhiteSpace(championId)) return null;
        if (CanonicalIconSlot(slot) is not { } canonical) return null;
        slot = canonical;

        if (!SlotFulls.TryGetValue(championId, out var fulls))
        {
            EnsurePrepared(championId);
            return null;
        }
        if (!fulls.TryGetValue(slot, out var full) || string.IsNullOrEmpty(full)) return null;

        var kind = slot == "P" ? "passive" : "spell";
        var path = Path.Combine(AppContext.BaseDirectory, "data", "ddragon", Version, "img", kind, full);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        // Filename known but the PNG isn't cached yet — kick off the icon download (idempotent, cached).
        EnsurePrepared(championId);
        return null;
    }

    /// <summary>Launches (at most once per champion) a background task that resolves the champion's
    /// slot → filename map and downloads its P/Q/W/E/R icon PNGs, so <see cref="AbilityIconPathOrNull"/>
    /// starts returning real paths on a later frame. Best-effort; never throws.</summary>
    private static void EnsurePrepared(string championId)
    {
        // No SlotFulls.ContainsKey short-circuit here (loop 515): it made the second call site in
        // AbilityIconPathOrNull — "filename known but the PNG isn't cached yet" — a permanent no-op,
        // so a champion whose slot map resolved but whose PNG download failed kept the letter badge
        // until app restart. The backoff below is what bounds re-entry instead.
        if (NextPrepareAt.TryGetValue(championId, out var at) && Environment.TickCount64 < at) return;
        if (!PrepareInFlight.TryAdd(championId, 0)) return;
        _ = PrepareAsync(championId);
    }

    private static async Task PrepareAsync(string championId)
    {
        try
        {
            var fulls = await ResolveSlotFullsAsync(championId, default).ConfigureAwait(false);
            if (fulls.Count > 0) SlotFulls[championId] = fulls;
            // Download the actual icon PNGs into the on-disk cache the path resolver reads from.
            await LoadIconsAsync(championId).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: offline/403/unknown id — the caller keeps the letter fallback.
        }
        finally
        {
            NextPrepareAt[championId] = Environment.TickCount64 + PrepareRetryMs;
            PrepareInFlight.TryRemove(championId, out _);
        }
    }

    /// <summary>Lazily fetches and caches the per-champion Data Dragon detail file (spell/passive
    /// icon filenames) for a champion not among the small startup-time sample set that
    /// <c>DDragonClient.EnsureCachedAsync</c> pre-fetches. On-demand, one champion at a time, mirroring
    /// <see cref="LoadIconAsync"/>'s existing download-then-cache pattern for icon PNGs below. Returns
    /// false (never throws) on any failure — offline, 403, unknown champion id, etc. — leaving the
    /// caller to fall back to letter badges exactly as when the file was simply missing.</summary>
    private static async Task<bool> TryFetchChampionJsonAsync(string championJson, string championId, CancellationToken ct)
    {
        try
        {
            var url = $"{BaseUrl}/cdn/{Version}/data/en_US/champion/{championId}.json";
            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            bytes = await RepairZeroedAttackDamagePerLevelAsync(bytes, championId, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(championJson)!);
            await File.WriteAllBytesAsync(championJson, bytes, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            // Offline first run, 403, unknown champion id, etc. — no icons; caller falls back to
            // letter badges, exactly as when the file was simply missing.
            return false;
        }
    }

    /// <summary>Data Dragon has been observed shipping <c>Version</c> with every champion's
    /// <c>stats.attackdamageperlevel</c> incorrectly zeroed (a real Data Dragon data bug, not
    /// something this app introduces). A freshly downloaded detail file with this bug would
    /// silently feed 0 AD growth into the combo damage engine. Rather than hardcode a patch
    /// number (forbidden — see M11's "no hardcoded patch-dependent values" rule), this walks
    /// backward through Data Dragon's own published version list (resolved live from
    /// <c>/api/versions.json</c>, cached for the process lifetime) until it finds a version
    /// that has this champion with a non-zero value, and patches only that one field into the
    /// bytes about to be cached. Best-effort: any failure — offline, malformed JSON, every
    /// older version also zeroed/missing the champion — simply leaves the original bytes
    /// untouched, exactly as Data Dragon actually returned them.</summary>
    private static async Task<byte[]> RepairZeroedAttackDamagePerLevelAsync(byte[] bytes, string championId, CancellationToken ct)
    {
        try
        {
            using (var doc = JsonDocument.Parse(bytes))
            {
                var stats = doc.RootElement.GetProperty("data").GetProperty(championId).GetProperty("stats");
                if (!stats.TryGetProperty("attackdamageperlevel", out var adpl) || adpl.GetDouble() != 0)
                    return bytes; // not affected — nothing to repair
            }

            foreach (var version in await GetLiveVersionsAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(version, Version, StringComparison.Ordinal)) continue;
                try
                {
                    var candidateUrl = $"{BaseUrl}/cdn/{version}/data/en_US/champion/{championId}.json";
                    var candidateJson = await Http.GetStringAsync(candidateUrl, ct).ConfigureAwait(false);
                    using var candidateDoc = JsonDocument.Parse(candidateJson);
                    var candidateValue = candidateDoc.RootElement.GetProperty("data").GetProperty(championId)
                        .GetProperty("stats").GetProperty("attackdamageperlevel").GetDouble();
                    if (candidateValue == 0) continue; // same bug on this version too — keep looking

                    // Schema-known-stable substitution: attackdamageperlevel is always followed
                    // by a comma before attackspeedperlevel, so a direct substring replace on the
                    // exact zeroed field is safe and avoids re-serializing the whole document.
                    var text = Encoding.UTF8.GetString(bytes);
                    var patchedText = text.Replace(
                        "\"attackdamageperlevel\":0,",
                        $"\"attackdamageperlevel\":{candidateValue.ToString(CultureInfo.InvariantCulture)},",
                        StringComparison.Ordinal);
                    if (!ReferenceEquals(text, patchedText))
                        return Encoding.UTF8.GetBytes(patchedText);
                }
                catch
                {
                    // this candidate version 404'd, or had an unexpected shape — try the next one
                }
            }
        }
        catch
        {
            // malformed/unexpected JSON — leave bytes untouched.
        }

        return bytes;
    }

    private static List<string>? _liveVersionsCache;

    /// <summary>Live, published Data Dragon version list (newest first), fetched once from
    /// <c>/api/versions.json</c> and cached for the process lifetime.</summary>
    private static async Task<List<string>> GetLiveVersionsAsync(CancellationToken ct)
    {
        if (_liveVersionsCache is not null) return _liveVersionsCache;
        var json = await Http.GetStringAsync($"{BaseUrl}/api/versions.json", ct).ConfigureAwait(false);
        _liveVersionsCache = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        return _liveVersionsCache;
    }

    private static async Task AddSlotAsync(
        IDictionary<string, ImageSource> map, string slot, string? full, string kind, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(full)) return;
        try
        {
            var icon = await LoadIconAsync(kind, full!, ct).ConfigureAwait(false);
            if (icon is not null) map[slot] = icon;
        }
        catch
        {
            // Per-slot best-effort: a single failed download/decode drops just this slot.
        }
    }

    /// <summary>Loads one icon PNG (<paramref name="kind"/> = "spell" or "passive"), downloading and
    /// caching it to disk on first use, and returns it as a frozen <see cref="ImageSource"/>.</summary>
    private static async Task<ImageSource?> LoadIconAsync(string kind, string full, CancellationToken ct)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data", "ddragon", Version, "img", kind);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, full);

        if (!File.Exists(path))
        {
            var url = $"{BaseUrl}/cdn/{Version}/img/{kind}/{full}";
            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }

        var data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(data);
        bmp.EndInit();
        bmp.Freeze(); // cross-thread safe: constructed off the UI thread, consumed on it.
        return bmp;
    }
}
