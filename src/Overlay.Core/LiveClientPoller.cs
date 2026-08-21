using System.Buffers;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Overlay.Core;

/// <summary>
/// Local data core entry point. Polls the Riot Live Client Data API
/// (<c>allgamedata</c>) on a fixed interval, parses each response with zero
/// per-tick heap allocation, diffs it against the previous tick, and publishes
/// a typed <see cref="SnapshotDiff"/> stream.
///
/// Hard Rules honored:
///  #1 Anti-cheat: HTTP GET to 127.0.0.1:2999 only — no memory/screen access.
///  #2 Zero-alloc hot path: pooled receive buffer (ArrayPool), Utf8JsonReader
///     over ReadOnlySpan&lt;byte&gt;, reusable snapshot objects swapped each tick.
///  #3 Thread separation: the whole loop runs on a background async worker; it
///     never touches the UI thread. Consumers marshal to the Dispatcher.
///  #4 Config-driven: interval/url/timeout come from <see cref="PollerConfig"/>.
///
/// Consumption: subscribe to <see cref="DiffAvailable"/>, or read
/// <see cref="Diffs"/> (a bounded channel) from a consumer task.
/// </summary>
public sealed class LiveClientPoller : IAsyncDisposable, IDisposable
{
    private readonly PollerConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    // Two reusable snapshots, ping-ponged each tick (no per-tick snapshot alloc).
    private GameSnapshot _current = new();
    private GameSnapshot _previous = new();
    private bool _previousHadData;

    private readonly Channel<SnapshotDiff> _channel =
        Channel.CreateBounded<SnapshotDiff>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _disposed;

    /// <summary>M01 Internal Logic #3: consecutive failed polls before flipping to
    /// disconnected. A single blip (dropped frame, momentary loading-screen hiccup)
    /// must not fire GAME.DISCONNECTED — only 3 in a row does. (Transient in-game slowness
    /// is handled primarily by the generous per-request timeout the app configures — a slow
    /// /allgamedata response no longer counts as a failure — so this stays at 3.)</summary>
    private const int DisconnectFailureThreshold = 3;
    private int _consecutiveFailures;

    /// <summary>Raised on the background worker each time a diff with changes is
    /// produced. Handlers must marshal to the UI thread themselves; do not block.</summary>
    public event Action<SnapshotDiff>? DiffAvailable;

    /// <summary>Raised on every tick that successfully parses game data — regardless
    /// of whether <see cref="SnapshotDiff.HasChanges"/> is true. Gives consumers that
    /// need raw field values (not just the legacy deltas), such as the M01 EventBus
    /// publisher, the previous/current snapshot pair plus whether this is the first
    /// synced tick after (re)connecting. Handlers must marshal to the UI thread
    /// themselves; do not block, and do not mutate the snapshots (they are the
    /// poller's live reusable buffers).</summary>
    public event Action<GameSnapshot, GameSnapshot, bool>? SnapshotAvailable;

    /// <summary>Bounded channel of diffs for pull-based consumers. Oldest entries
    /// are dropped if a slow consumer falls behind, so the poller never stalls.</summary>
    public ChannelReader<SnapshotDiff> Diffs => _channel.Reader;

    /// <summary>True while the game is reachable and returning data.</summary>
    public bool GameConnected { get; private set; }

    public LiveClientPoller(PollerConfig? config = null, HttpClient? httpClient = null)
    {
        _config = config ?? new PollerConfig();

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            // The Live Client Data API serves over HTTPS with Riot's local
            // self-signed certificate. Accept it for this single localhost host
            // only — not a blanket disable.
            var handler = new SocketsHttpHandler
            {
                SslOptions =
                {
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                },
                ConnectTimeout = TimeSpan.FromSeconds(2),
            };
            _http = new HttpClient(handler) { Timeout = _config.RequestTimeout };
            _ownsHttp = true;
        }
    }

    /// <summary>Start the background polling loop. Idempotent-safe to call once.</summary>
    public void Start(CancellationToken externalToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_loop is not null) throw new InvalidOperationException("Poller already started.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Stop the loop and wait for it to wind down.</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_config.PollInterval);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_config.InitialReceiveBufferSize);
        try
        {
            // Fire one tick immediately, then on each timer interval.
            do
            {
                try
                {
                    buffer = await PollOnceAsync(buffer, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // One bad tick must never kill the loop (Hard Rule resilience).
                    // Any unclassified exception counts toward the same 3-consecutive-
                    // failure threshold as the classified ones handled inside
                    // PollOnceAsync (M01 Internal Logic #3).
                    RecordFailure();
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _channel.Writer.TryComplete();
        }
    }

    /// <summary>Runs one poll/parse/diff cycle. Returns the receive buffer (which
    /// may have been grown into a larger pooled rental) so the caller's loop keeps
    /// reusing a single buffer across ticks. Async methods can't take ref params,
    /// hence the return-the-buffer pattern.</summary>
    private async Task<byte[]> PollOnceAsync(byte[] buffer, CancellationToken token)
    {
        int length;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _config.AllGameDataUrl);
            using var resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                // 404 etc. — API up but no game data yet.
                RecordFailure();
                return buffer;
            }

            (buffer, length) = await ReadFullyAsync(resp, buffer, token).ConfigureAwait(false);
        }
        catch (HttpRequestException) { RecordFailure(); return buffer; } // connection refused: game not running
        catch (SocketException) { RecordFailure(); return buffer; }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { RecordFailure(); return buffer; } // per-request timeout

        // Parse straight off the pooled buffer span — no copy, no string of body.
        bool ok = LiveDataParser.Parse(buffer.AsSpan(0, length), _current);
        if (!ok)
        {
            RecordFailure();
            return buffer;
        }

        GameConnected = true;
        _consecutiveFailures = 0;

        bool wasInitialSync = !_previousHadData;
        // Raised before the swap below so "current"/"previous" mean exactly what
        // they say: this tick's freshly parsed data and the prior tick's data.
        SnapshotAvailable?.Invoke(_previous, _current, wasInitialSync);

        SnapshotDiff diff = SnapshotDiffer.Diff(_previous, _current, _previousHadData);

        // Swap current/previous so next tick reuses the now-stale buffer object.
        (_previous, _current) = (_current, _previous);
        _previousHadData = true;

        if (diff.HasChanges)
            Publish(diff);

        return buffer;
    }

    /// <summary>Reads the entire response body into the pooled buffer, growing it
    /// (back into the pool) if the body is larger than the current rental. Returns
    /// the (possibly grown) buffer and the number of bytes read.</summary>
    private static async Task<(byte[] Buffer, int Length)> ReadFullyAsync(
        HttpResponseMessage resp, byte[] buffer, CancellationToken token)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        int total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                Array.Copy(buffer, bigger, total);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = bigger;
            }

            int read = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return (buffer, total);
    }

    /// <summary>Counts a failed poll toward the M01 Internal Logic #3 threshold and
    /// only actually flips to disconnected once 3 failures land in a row. A
    /// successful tick (see <see cref="PollOnceAsync"/>) resets the counter, so a
    /// single dropped frame never trips GAME.DISCONNECTED.</summary>
    private void RecordFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures < DisconnectFailureThreshold)
            return;

        MarkDisconnected();
    }

    private void MarkDisconnected()
    {
        if (!_previousHadData && !GameConnected)
            return; // already disconnected — stay quiet (no log spam)

        GameConnected = false;
        // Reset diff baseline so the next successful poll is an initial sync.
        _current.Reset();
        bool wasConnected = _previousHadData;
        _previousHadData = false;

        if (wasConnected)
        {
            var diff = new SnapshotDiff { GameAvailabilityChanged = true, GameIsActive = false };
            Publish(diff);
        }
    }

    private void Publish(SnapshotDiff diff)
    {
        _channel.Writer.TryWrite(diff);
        DiffAvailable?.Invoke(diff);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        if (_ownsHttp) _http.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
