using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Overlay.Core.Telemetry;

/// <summary>
/// M39 anonymous telemetry + crash reporting — the non-UI half. Sends exactly two kinds of
/// payload to OUR endpoint (a self-hosted Cloudflare Worker, config <c>telemetry.endpoint</c>):
///
/// <list type="bullet">
/// <item><b>POST {endpoint}/ping</b> — once per app launch: anonymous install id, app version,
/// OS version. The server dedupes per (install, day), which is what makes the count a DAU.</item>
/// <item><b>POST {endpoint}/crash</b> — queued crash reports: the same three fields plus the
/// crash source and the exception text (user paths scrubbed, size-capped).</item>
/// </list>
///
/// <para><b>What is deliberately NOT collected</b> (M29 §3 spirit, P1 extension): no Riot account
/// data, no game data, no hardware fingerprint, no third-party SDK. The install id is a random
/// GUID minted locally on first use; it identifies an INSTALL, never a person.</para>
///
/// <para><b>Durability.</b> A crash is first written to a small on-disk queue
/// (<c>%LocalAppData%/Fova/telemetry/crash-queue/</c>) and only then sent best-effort — a process
/// dying mid-crash still gets its report delivered by the next launch's
/// <see cref="StartupAsync"/> flush. The queue is capped at <see cref="MaxQueuedCrashes"/>
/// (oldest dropped) so a crash loop can never fill a disk.</para>
///
/// <para><b>Failure posture</b> — identical to <see cref="Ads.AdSlotService"/>: every error path
/// returns quietly, nothing throws past the public surface, nothing retries in a storm (after
/// <see cref="MaxSessionFailures"/> failed requests the service stays silent for the session).
/// Telemetry must never produce a visible error or affect the app.</para>
/// </summary>
public sealed class TelemetryService : IDisposable
{
    /// <summary>Cap on a single crash report's exception text (post-scrub, UTF-16 chars).</summary>
    public const int MaxCrashChars = 16 * 1024;

    /// <summary>Max crash files retained on disk; oldest are dropped first.</summary>
    public const int MaxQueuedCrashes = 20;

    /// <summary>Failed requests allowed per session before going quiet (M29 posture).</summary>
    public const int MaxSessionFailures = 2;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly bool _enabled;
    private readonly string? _endpoint;
    private readonly string _dataDir;
    private readonly string _queueDir;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _appVersion;
    private readonly string _osVersion;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _gate = new();
    private int _failures;
    private bool _flushing;
    private string? _installId;
    private bool _disposed;

    /// <summary>Default data dir: <c>%LocalAppData%/Fova/telemetry</c> — sibling of the M29 ad
    /// cache, so an app reinstall keeps the same install id (counts stay honest).</summary>
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fova", "telemetry");

    /// <param name="enabled">Config <c>telemetry.enabled</c>; false disables every code path.</param>
    /// <param name="endpoint">Config <c>telemetry.endpoint</c>: base HTTPS URL of the Worker
    /// (no trailing path). Empty/invalid = not configured — everything is silently off.</param>
    /// <param name="dataDir">Install-id + crash-queue directory (tests inject a temp dir).</param>
    /// <param name="httpClient">Injected for tests; otherwise a short-timeout client is owned here.</param>
    /// <param name="appVersion">Informational app version stamped on every payload.</param>
    /// <param name="osVersion">OS version string stamped on every payload.</param>
    public TelemetryService(bool enabled, string? endpoint, string? dataDir = null,
        HttpClient? httpClient = null, string? appVersion = null, string? osVersion = null)
    {
        _enabled = enabled;
        // Same fetch-URL rule as M29: absolute HTTPS, or plain HTTP to loopback for a local test
        // server. An invalid endpoint degrades to "not configured", never an error.
        _endpoint = Ads.AdSlotService.IsAllowedFetchUrl(endpoint) ? endpoint!.TrimEnd('/') : null;
        _dataDir = dataDir ?? DefaultDataDirectory;
        _queueDir = Path.Combine(_dataDir, "crash-queue");
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _appVersion = appVersion ?? "unknown";
        _osVersion = osVersion ?? Environment.OSVersion.VersionString;
    }

    /// <summary>True when anything can ever be sent (enabled AND a valid endpoint). The HOME
    /// window's one-time disclosure banner shows only while this is true — a notice about
    /// sending nothing would be false.</summary>
    public bool IsConfigured => _enabled && _endpoint is not null;

    /// <summary>Anonymous install id — a random GUID persisted on first use. Never derived from
    /// hardware, account, or anything identifying.</summary>
    public string InstallId
    {
        get
        {
            lock (_gate)
            {
                if (_installId is not null) return _installId;
                string path = Path.Combine(_dataDir, "install_id");
                try
                {
                    if (File.Exists(path))
                    {
                        var existing = File.ReadAllText(path).Trim();
                        if (Guid.TryParse(existing, out var parsed))
                            return _installId = parsed.ToString("N");
                    }
                    _installId = Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(_dataDir);
                    File.WriteAllText(path, _installId);
                    return _installId;
                }
                catch (Exception)
                {
                    // Unreadable/unwritable data dir: a per-session id keeps payloads well-formed.
                    return _installId = Guid.NewGuid().ToString("N");
                }
            }
        }
    }

    // ── Crash queue ─────────────────────────────────────────────────────────────

    /// <summary>Durably queues a crash report (never throws, never blocks on the network), then
    /// kicks a best-effort background flush. Safe to call from any thread, including the global
    /// crash handlers — a report the process dies before sending goes out on the next launch.</summary>
    public void RecordCrash(string source, string details)
    {
        if (_disposed || !IsConfigured) return;
        try
        {
            var report = new CrashReport(
                InstallId, _appVersion, _osVersion, source ?? "",
                Truncate(ScrubUserPaths(details ?? "")),
                DateTime.UtcNow.ToString("O"));

            Directory.CreateDirectory(_queueDir);
            // Ticks-prefixed name keeps the queue sortable oldest-first for the cap below.
            string file = Path.Combine(_queueDir,
                $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(report, JsonOptions));
            EnforceQueueCap();
        }
        catch (Exception)
        {
            // The crash reporter must never become a second crash.
            return;
        }

        _ = FlushQueueAsync(_cts.Token);
    }

    /// <summary>Send anything the queue holds, then the launch ping. Called fire-and-forget once
    /// at composition time; never throws.</summary>
    public async Task StartupAsync(CancellationToken ct = default)
    {
        if (_disposed || !IsConfigured) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await FlushQueueAsync(linked.Token).ConfigureAwait(false);
        await PostAsync("/ping", JsonSerializer.Serialize(
            new Ping(InstallId, _appVersion, _osVersion), JsonOptions), linked.Token)
            .ConfigureAwait(false);
    }

    /// <summary>True while a background queue flush is running (exposed for tests).</summary>
    internal bool IsFlushing
    {
        get { lock (_gate) return _flushing; }
    }

    /// <summary>Queued crash files awaiting delivery (exposed for tests).</summary>
    public int PendingCrashes
    {
        get
        {
            try
            {
                return Directory.Exists(_queueDir) ? Directory.GetFiles(_queueDir, "*.json").Length : 0;
            }
            catch (IOException) { return 0; }
        }
    }

    private async Task FlushQueueAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_flushing || _failures >= MaxSessionFailures) return;
            _flushing = true;
        }
        try
        {
            string[] files;
            try
            {
                files = Directory.Exists(_queueDir)
                    ? Directory.GetFiles(_queueDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>();
            }
            catch (IOException) { return; }

            foreach (var file in files)
            {
                string body;
                try { body = File.ReadAllText(file); }
                catch (IOException) { continue; }

                if (!await PostAsync("/crash", body, ct).ConfigureAwait(false))
                    return; // endpoint unreachable — keep the rest for next launch, no storm

                try { File.Delete(file); } catch (IOException) { /* resent next launch, deduped server-side by installId+at */ }
            }
        }
        finally
        {
            lock (_gate) _flushing = false;
        }
    }

    /// <summary>Oldest-first trim to <see cref="MaxQueuedCrashes"/>.</summary>
    private void EnforceQueueCap()
    {
        var files = Directory.GetFiles(_queueDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < files.Length - MaxQueuedCrashes; i++)
        {
            try { File.Delete(files[i]); } catch (IOException) { }
        }
    }

    // ── HTTP ────────────────────────────────────────────────────────────────────

    /// <summary>POST JSON; true on 2xx. Every failure counts toward the session cap.</summary>
    private async Task<bool> PostAsync(string path, string json, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_failures >= MaxSessionFailures) return false;
        }
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_endpoint + path, content, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return true;
            lock (_gate) _failures++;
            return false;
        }
        catch (OperationCanceledException)
        {
            return false; // shutdown/caller cancel — not a failure, not retried
        }
        catch (Exception)
        {
            lock (_gate) _failures++;
            return false;
        }
    }

    // ── Payload hygiene ─────────────────────────────────────────────────────────

    /// <summary>Windows user-profile paths inside exception text (messages routinely embed
    /// <c>C:\Users\{name}\...</c>) are the one place a crash report could pick up something
    /// personal — replaced with <c>~</c> before anything leaves the machine.</summary>
    internal static string ScrubUserPaths(string text)
        => Regex.Replace(text, @"[A-Za-z]:[\\/]Users[\\/][^\\/\s]+", "~", RegexOptions.IgnoreCase);

    private static string Truncate(string text)
        => text.Length <= MaxCrashChars ? text : text[..MaxCrashChars];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    // ── Wire shapes (server contract, see telemetry-worker/) ────────────────────

    private sealed record Ping(string InstallId, string Version, string Os);

    private sealed record CrashReport(
        string InstallId, string Version, string Os, string Source, string Details, string At);
}
