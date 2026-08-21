using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Overlay.Core.EventBus;

namespace Overlay.Core.Ads;

/// <summary>
/// M29 Ad Module — the non-UI half of the single HOME banner slot: creative sourcing, disk cache,
/// size guards, dormancy, and impression accounting. Rendering (decode + WPF <c>Image</c>) lives in
/// the client's AdBanner; this class never touches a UI type, so it is unit-testable on plain
/// net8.0 with a stubbed <see cref="HttpMessageHandler"/>.
///
/// <para><b>D1 — static images only.</b> The endpoint returns an <see cref="AdManifest"/> of image
/// URL + click URL. No WebView, no JS SDK, no video/animated creatives.</para>
///
/// <para><b>D2 — dormant in game.</b> <c>GAME.CONNECTED</c> sets <see cref="IsDormant"/>, cancels any
/// in-flight fetch, and makes <see cref="NextAsync"/> a no-op that returns null, so the live-game
/// path executes zero ad code and issues zero requests. <c>GAME.DISCONNECTED</c> clears it; the
/// client resumes lazily on the next HOME activation.</para>
///
/// <para><b>Failure posture</b> (§2): every error path returns null — the slot collapses. Nothing
/// throws, nothing retries in a storm: after <see cref="MaxSessionFailures"/> failures the service
/// stays quiet for the rest of the session. Offline with a warm disk cache still serves the last
/// creative (ETag revalidation, 304 → cached bytes).</para>
/// </summary>
public sealed class AdSlotService : IDisposable
{
    /// <summary>§2 defensive cap: a creative larger than this is rejected, not decoded.</summary>
    public const int MaxCreativeBytes = 300 * 1024;

    /// <summary>§D3 network budget: the client's rotation timer must not tick faster than this.</summary>
    public static readonly TimeSpan MinRotationInterval = TimeSpan.FromSeconds(60);

    /// <summary>§2 "no retry storm": fetch attempts allowed per session before going quiet.</summary>
    public const int MaxSessionFailures = 2;

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly bool _enabled;
    private readonly string? _endpoint;
    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    private readonly string _connSub;
    private readonly string _discSub;

    private readonly object _gate = new();
    private readonly List<string> _beacons = new();

    private AdManifest? _manifest;
    private int _rotation;
    private int _failures;
    private CancellationTokenSource? _inFlight;
    private bool _disposed;

    /// <summary>Default disk cache: <c>%LocalAppData%/Fova/ads</c> (§2).</summary>
    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fova", "ads");

    /// <summary>True while a game is live — no timer, no fetch, no bitmaps (D2).</summary>
    public bool IsDormant { get; private set; }

    /// <summary>Raised when <see cref="IsDormant"/> flips, on the bus thread. The client marshals
    /// to the UI thread to stop/restart its rotation timer and release/reload the bitmap.</summary>
    public event Action? DormancyChanged;

    /// <param name="enabled">Config <c>ads.enabled</c>; false disables every code path here.</param>
    /// <param name="endpoint">Config <c>ads.endpoint</c>: HTTPS URL returning an
    /// <see cref="AdManifest"/>. Empty/null/invalid (see <see cref="IsAllowedFetchUrl"/>) = no ads
    /// (the slot simply stays collapsed).</param>
    /// <param name="cacheRoot">Disk cache dir (tests inject a temp dir).</param>
    /// <param name="httpClient">Injected for tests; otherwise a short-timeout client is owned here.</param>
    public AdSlotService(bool enabled, string? endpoint, string? cacheRoot = null, HttpClient? httpClient = null)
    {
        _enabled = enabled;
        // (loop 514) The endpoint must satisfy the same fetch-URL rule as the creatives it names:
        // an invalid one degrades to "no endpoint" — the slot stays collapsed, nothing throws.
        _endpoint = IsAllowedFetchUrl(endpoint) ? endpoint : null;
        _cacheDir = cacheRoot ?? DefaultCacheDirectory;
        _ownsHttp = httpClient is null;
        // No auto-redirect on the owned client (loop 514): a redirecting manifest/creative URL is a
        // non-success response and falls back to the cache, instead of letting the remote endpoint
        // bounce requests to arbitrary hosts through up to 50 hops.
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        _connSub = EventBus.EventBus.Subscribe("GAME.CONNECTED", _ => SetDormant(true));
        _discSub = EventBus.EventBus.Subscribe("GAME.DISCONNECTED", _ => SetDormant(false));
    }

    // ── Dormancy (D2) ───────────────────────────────────────────────────────────

    private void SetDormant(bool dormant)
    {
        lock (_gate)
        {
            if (IsDormant == dormant) return;
            IsDormant = dormant;
            if (dormant)
            {
                // Cancel whatever was in flight so a game start never leaves a download running.
                try { _inFlight?.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
        DormancyChanged?.Invoke();
    }

    /// <summary>True when the slot can ever fill (enabled AND an endpoint is configured). The
    /// HOME window reads this to decide whether reserving the 130px row is meaningful: with no
    /// endpoint the reservation is a permanent empty band, not a reflow guard.</summary>
    public bool IsConfigured => _enabled && _endpoint is not null;

    // ── Creative rotation ───────────────────────────────────────────────────────

    /// <summary>Next creative to display, or null when there is nothing to show (disabled, dormant,
    /// no endpoint, fetch/size failure). Never throws.</summary>
    public async Task<AdImage?> NextAsync(CancellationToken ct = default)
    {
        if (_disposed || !_enabled || _endpoint is null) return null;

        CancellationTokenSource linked;
        lock (_gate)
        {
            if (IsDormant || _failures >= MaxSessionFailures) return null;
            _inFlight = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked = _inFlight;
        }

        try
        {
            var manifest = _manifest ?? await LoadManifestAsync(linked.Token).ConfigureAwait(false);
            if (manifest is null || manifest.Creatives.Count == 0) return null;
            _manifest = manifest;

            var creative = manifest.Creatives[_rotation++ % manifest.Creatives.Count];
            if (string.IsNullOrWhiteSpace(creative.Image)) return null;

            var bytes = await FetchWithCacheAsync(creative.Image, linked.Token).ConfigureAwait(false);
            if (bytes is null || !LooksLikeStaticImage(bytes))
            {
                // D1 says static JPEG/PNG/WebP; anything else (HTML error page, SVG, a video the
                // endpoint mislabelled) must never reach the client-side decoder. (loop 514)
                Fail();
                return null;
            }

            return new AdImage(creative, bytes);
        }
        catch (OperationCanceledException)
        {
            // A dormancy flip (game start) or caller cancellation aborted the fetch. That is the
            // system working as designed, not a failure — counting it against MaxSessionFailures
            // could permanently silence ads after two well-timed game starts.
            return null;
        }
        catch (Exception)
        {
            // §2: an ad error is never visible, never logged loudly, never rethrown.
            Fail();
            return null;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, linked)) _inFlight = null;
            }
            linked.Dispose();
        }
    }

    private void Fail()
    {
        lock (_gate) _failures++;
    }

    private async Task<AdManifest?> LoadManifestAsync(CancellationToken ct)
    {
        var bytes = await FetchWithCacheAsync(_endpoint!, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            Fail();
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<AdManifest>(bytes, ManifestOptions);
            if (manifest is null) return null;

            // (loop 514) The manifest is the one remote input this app consumes: nothing in it is
            // trusted until its URLs pass the scheme rules. A creative with a bad image or click
            // URL is dropped whole; a bad impression URL only loses its beacon.
            manifest.Creatives.RemoveAll(c => !IsAllowedFetchUrl(c.Image) || !IsAllowedClickUrl(c.Click));
            foreach (var c in manifest.Creatives)
                if (c.Impression is not null && !IsAllowedFetchUrl(c.Impression))
                    c.Impression = null;
            return manifest;
        }
        catch (JsonException)
        {
            Fail();
            return null;
        }
    }

    /// <summary>Rule for every URL this service itself fetches (endpoint, image, impression):
    /// absolute HTTPS — or plain HTTP to a loopback host only, so <c>tools/ad_test_server.py</c>
    /// keeps working. Everything else (other schemes, remote http, relative) is rejected.</summary>
    internal static bool IsAllowedFetchUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>Click URLs open in the system browser, never in-process — absolute http(s) only
    /// (matching the client-side allow-list in AdBanner, so a creative that would be unclickable
    /// there is dropped here instead of rendering dead).</summary>
    internal static bool IsAllowedClickUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>D1 magic-byte check: PNG, JPEG, or (lossy/lossless/extended) WebP.</summary>
    internal static bool LooksLikeStaticImage(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
        return b.Length >= 12
            && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';
    }

    // ── HTTPS + disk cache with ETag revalidation (§2) ──────────────────────────

    /// <summary>GET with If-None-Match against the on-disk copy. 304 or any failure with a warm
    /// cache serves the cached bytes (offline = last creative, never a spinner). Returns null when
    /// there is neither a usable response nor a cached copy, or the payload is over the size cap.</summary>
    private async Task<byte[]?> FetchWithCacheAsync(string url, CancellationToken ct)
    {
        string blobPath = Path.Combine(_cacheDir, CacheKey(url) + ".bin");
        string etagPath = blobPath + ".etag";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (File.Exists(blobPath) && File.Exists(etagPath))
                request.Headers.TryAddWithoutValidation("If-None-Match", File.ReadAllText(etagPath));

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return ReadCached(blobPath);

            if (!response.IsSuccessStatusCode) return ReadCached(blobPath);
            if (response.Content.Headers.ContentLength > MaxCreativeBytes) return null;

            var bytes = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            if (bytes is null) return null;

            WriteCache(blobPath, etagPath, bytes, response.Headers.ETag?.Tag);
            return bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A real cancellation (dormancy flip / caller) must reach NextAsync's own handler so it
            // is NOT counted as a session failure. An HttpClient TIMEOUT also surfaces as a
            // TaskCanceledException but with ct un-cancelled — the `when` guard keeps that on the
            // failure path below.
            throw;
        }
        catch (Exception)
        {
            return ReadCached(blobPath);
        }
    }

    /// <summary>Reads at most <see cref="MaxCreativeBytes"/>; null once the cap is crossed.
    /// (loop 514) The ContentLength pre-check above is only a fast reject: it is <c>long?</c>, and
    /// for a chunked response it is null — <c>null &gt; cap</c> is false — so the old
    /// <c>ReadAsByteArrayAsync</c> path buffered the ENTIRE body (framework limit 2 GB;
    /// MaxResponseContentBufferSize does not apply under ResponseHeadersRead) before the post-hoc
    /// length check. A hostile endpoint could stream hundreds of MB into this "never visible,
    /// never logged" code path. Reading through a cap+1 buffer rejects at 300 KB instead.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxCreativeBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total > MaxCreativeBytes ? null : buffer.AsSpan(0, total).ToArray();
    }

    private static byte[]? ReadCached(string blobPath)
    {
        try
        {
            return File.Exists(blobPath) ? File.ReadAllBytes(blobPath) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void WriteCache(string blobPath, string etagPath, byte[] bytes, string? etag)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllBytes(blobPath, bytes);
            if (etag is null) File.Delete(etagPath);
            else File.WriteAllText(etagPath, etag);
        }
        catch (IOException)
        {
            // A cache we cannot write is not a reason to skip the ad we already have.
        }
    }

    private static string CacheKey(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    // ── Impression / click accounting (§2: buffered, flushed batched on close) ──

    /// <summary>Buffer a beacon. Nothing goes out on the wire here — see <see cref="Dispose"/>.</summary>
    public void RecordImpression(AdCreative creative)
    {
        if (string.IsNullOrWhiteSpace(creative.Impression)) return;
        lock (_gate) _beacons.Add(creative.Impression!);
    }

    /// <summary>Buffered beacons awaiting the batched flush (exposed for the dormancy test).</summary>
    public int PendingBeacons
    {
        get { lock (_gate) return _beacons.Count; }
    }

    // ── Teardown ────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        EventBus.EventBus.Unsubscribe(_connSub);
        EventBus.EventBus.Unsubscribe(_discSub);

        string[] beacons;
        lock (_gate)
        {
            try { _inFlight?.Cancel(); } catch (ObjectDisposedException) { }
            beacons = _beacons.ToArray();
            _beacons.Clear();
        }

        // Batched, best-effort, bounded: shutdown must not wait on the network.
        if (beacons.Length > 0 && _enabled)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var pings = beacons.Select(b => _http.GetAsync(b, cts.Token));
                Task.WaitAll(pings.ToArray<Task>(), TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Losing impression counts is strictly better than delaying app exit.
            }
        }

        if (_ownsHttp) _http.Dispose();
    }
}
