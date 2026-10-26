using System.Text.Json;
using System.Text.Json.Serialization;
using Overlay.Core.Ads;

namespace Overlay.Core.Flags;

/// <summary>
/// Remote kill switch for gated features (1.0 plan, "Gated" tier). Reads a tiny JSON document from
/// the public repo, the same way the ad manifest is read:
///
/// <code>{ "forceOff": ["recallTimer", "enemyJunglerSpotted", "enemyItemAlert"], "beta_kill": [] }</code>
///
/// <para><b>Force-OFF only.</b> The document has no way to say "on": a listed feature is off
/// whatever the local config says, an unlisted one follows the local config. Removing a name
/// from the list is how a feature comes back — no app release needed.</para>
///
/// <para><b>Two lists, one per build (lolF05).</b> Non-beta builds read only <c>forceOff</c>
/// (required); beta builds read only <c>beta_kill</c> (optional, absent/null = empty) — see
/// <see cref="IsOff"/>. Both come from the same document and are swapped together, so a reader
/// never sees one list from the old document and the other from the new. Released 0.4.0 ignores
/// <c>beta_kill</c> (unknown members are skipped).</para>
///
/// <para><b>Failure posture.</b> The last good document is cached on disk and loaded in the
/// constructor, so an offline launch keeps honouring it. A failed, oversized, or malformed fetch
/// keeps whatever set is current; nothing here throws.</para>
/// </summary>
public sealed class RemoteFlags : IDisposable
{
    /// <summary>The document lists a handful of names; anything larger is not ours.</summary>
    public const int MaxBytes = 16 * 1024;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly string? _endpoint;
    private readonly string _cachePath;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    // AI-NOTE: one reference for both lists — two volatile fields could be read across a refresh
    // and mix the old forceOff with the new beta_kill.
    private volatile FlagSets _sets = FlagSets.Empty;

    /// <summary>Default cache: <c>%LocalAppData%/Fova/flags.json</c>.</summary>
    public static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fova", "flags.json");

    /// <summary>Raised (on the fetching thread) after a refresh replaced the forceOff and beta_kill sets.</summary>
    public event Action? Changed;

    /// <param name="endpoint">HTTPS URL of the flags document; invalid/empty = cache only.</param>
    /// <param name="cachePath">Cache file (tests inject a temp path).</param>
    /// <param name="httpClient">Injected for tests; otherwise a short-timeout client is owned here.</param>
    public RemoteFlags(string? endpoint, string? cachePath = null, HttpClient? httpClient = null)
    {
        _endpoint = AdSlotService.IsAllowedFetchUrl(endpoint) ? endpoint : null;
        _cachePath = cachePath ?? DefaultCachePath;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        try
        {
            if (File.Exists(_cachePath) && Parse(File.ReadAllBytes(_cachePath)) is { } cached)
                _sets = cached;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>True when the remote document forces <paramref name="feature"/> off.</summary>
    public bool IsForcedOff(string feature) => _sets.ForceOff.Contains(feature);

    /// <summary>True when the remote document's <c>beta_kill</c> list names <paramref name="feature"/>.</summary>
    public bool IsBetaKilled(string feature) => _sets.BetaKill.Contains(feature);

    /// <summary>The kill rule of the running build: beta reads only <c>beta_kill</c>, non-beta only <c>forceOff</c>.</summary>
    public bool IsOff(string feature, bool isBeta) => isBeta ? IsBetaKilled(feature) : IsForcedOff(feature);

    /// <summary>The current <c>beta_kill</c> names.</summary>
    public IReadOnlyCollection<string> BetaKillNames => _sets.BetaKill;

    /// <summary>Fetches the document once. On success replaces the set, rewrites the cache and
    /// raises <see cref="Changed"/>; on any failure keeps the current set. Never throws.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_endpoint is null) return;
        try
        {
            using var response = await _http
                .GetAsync(_endpoint, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[MaxBytes + 1];
            int total = 0, read;
            while (total < buffer.Length
                   && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
                total += read;
            if (total > MaxBytes) return;

            var bytes = buffer.AsSpan(0, total).ToArray();
            if (Parse(bytes) is not { } sets) return;

            _sets = sets;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                File.WriteAllBytes(_cachePath, bytes);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            Changed?.Invoke();
        }
        catch (Exception)
        {
            // Offline, timeout, cancellation: the cached set stays in force.
        }
    }

    /// <summary>The forceOff and beta_kill names, or null when the bytes are not a valid flags
    /// document (no forceOff list, or either list not an array of strings).</summary>
    internal static FlagSets? Parse(byte[] bytes)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<FlagsDocument>(bytes, Options);
            if (doc?.ForceOff is null) return null;
            return new FlagSets(ToSet(doc.ForceOff), ToSet(doc.BetaKill ?? []));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HashSet<string> ToSet(IEnumerable<string> names) => new(
        names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
        StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    /// <summary>Both lists of one document; replaced as a whole, never mutated.</summary>
    internal sealed record FlagSets(HashSet<string> ForceOff, HashSet<string> BetaKill)
    {
        public static readonly FlagSets Empty = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class FlagsDocument
    {
        public List<string>? ForceOff { get; set; }

        [JsonPropertyName("beta_kill")]
        public List<string>? BetaKill { get; set; }
    }
}
