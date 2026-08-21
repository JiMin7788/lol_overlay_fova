using System.Net;
using System.Text;
using Overlay.Core.Ads;
using Overlay.Core.EventBus;

namespace Overlay.Core.Tests;

/// <summary>
/// M29 §5 acceptance criteria for the ad slot's non-UI half: in-game dormancy (D2), the size cap
/// and failure posture (§2), and the kill switch. Every case runs against a stubbed
/// <see cref="HttpMessageHandler"/> and a temp cache dir — no network, no disk outside temp.
/// </summary>
public class AdSlotServiceTests : IDisposable
{
    private const string Endpoint = "https://ads.example/manifest.json";
    private const string ImageUrl = "https://ads.example/creative-a.png";

    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "fova-ad-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task GameConnected_MakesSlotDormant_AndIssuesNoRequests()
    {
        var handler = new StubHandler(_ => Ok(ManifestJson()));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        EventBus.EventBus.Publish("GAME.CONNECTED", null, source: nameof(AdSlotServiceTests));
        await WaitFor(() => service.IsDormant);

        var ad = await service.NextAsync();

        Assert.Null(ad);
        Assert.Equal(0, handler.Calls); // D2: zero network while a game is live

        EventBus.EventBus.Publish("GAME.DISCONNECTED", null, source: nameof(AdSlotServiceTests));
    }

    [Fact]
    public async Task GameDisconnected_ResumesServingCreatives()
    {
        var handler = new StubHandler(req => req.RequestUri!.ToString() == Endpoint
            ? Ok(ManifestJson())
            : Ok(new byte[64]));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        EventBus.EventBus.Publish("GAME.CONNECTED", null, source: nameof(AdSlotServiceTests));
        await WaitFor(() => service.IsDormant);
        EventBus.EventBus.Publish("GAME.DISCONNECTED", null, source: nameof(AdSlotServiceTests));
        await WaitFor(() => !service.IsDormant);

        var ad = await service.NextAsync();

        Assert.NotNull(ad);
        Assert.Equal("a", ad!.Creative.Id);
    }

    [Fact]
    public async Task Disabled_FetchesNothing()
    {
        var handler = new StubHandler(_ => Ok(ManifestJson()));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(false, Endpoint, _cacheDir, http);

        Assert.Null(await service.NextAsync());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NoEndpoint_FetchesNothing()
    {
        var handler = new StubHandler(_ => Ok(ManifestJson()));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, "", _cacheDir, http);

        Assert.Null(await service.NextAsync());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OversizedCreative_IsRejected()
    {
        var oversized = new byte[AdSlotService.MaxCreativeBytes + 1];
        var handler = new StubHandler(req => req.RequestUri!.ToString() == Endpoint
            ? Ok(ManifestJson())
            : Ok(oversized));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        Assert.Null(await service.NextAsync());
    }

    [Fact]
    public async Task UnreachableEndpoint_CollapsesAndStopsRetryingThisSession()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("simulated: server down"));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        for (int i = 0; i < 5; i++) Assert.Null(await service.NextAsync());

        // §2 "no retry storm": at most MaxSessionFailures attempts, however often we ask.
        Assert.True(handler.Calls <= AdSlotService.MaxSessionFailures,
                    $"expected <= {AdSlotService.MaxSessionFailures} attempts, saw {handler.Calls}");
    }

    [Fact]
    public async Task GameStartMidFetch_IsNotCountedAsASessionFailure()
    {
        // Regression (review F1-1): a dormancy flip used to cancel the in-flight fetch AND count
        // the resulting OperationCanceledException toward MaxSessionFailures (2) — two well-timed
        // game starts would permanently silence ads for the session. Cancellation is not a failure.
        using var entered = new SemaphoreSlim(0);
        int calls = 0;
        var handler = new AsyncStubHandler(async (req, ct) =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
            {
                entered.Release();
                await Task.Delay(Timeout.Infinite, ct); // hangs until the dormancy cancel aborts it
            }
            return req.RequestUri!.ToString() == Endpoint ? Ok(ManifestJson()) : Ok(new byte[64]);
        });
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        for (int i = 0; i < 2; i++)
        {
            var pending = service.NextAsync();
            Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(2)), "fetch never started");
            EventBus.EventBus.Publish("GAME.CONNECTED", null, source: nameof(AdSlotServiceTests));
            Assert.Null(await pending); // cancelled by the dormancy flip
            EventBus.EventBus.Publish("GAME.DISCONNECTED", null, source: nameof(AdSlotServiceTests));
            await WaitFor(() => !service.IsDormant);
        }

        // With the bug, _failures == 2 here and this returns null without touching the network.
        var ad = await service.NextAsync();
        Assert.NotNull(ad);
        Assert.Equal("a", ad!.Creative.Id);
    }

    [Fact]
    public async Task Creatives_RotateInOrder()
    {
        var handler = new StubHandler(req => req.RequestUri!.ToString() == Endpoint
            ? Ok(ManifestJson(twoCreatives: true))
            : Ok(new byte[64]));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        var first = await service.NextAsync();
        var second = await service.NextAsync();
        var third = await service.NextAsync();

        Assert.Equal("a", first!.Creative.Id);
        Assert.Equal("b", second!.Creative.Id);
        Assert.Equal("a", third!.Creative.Id); // wraps, and the manifest is fetched only once
        Assert.Equal(4, handler.Calls);        // 1 manifest + 3 images
    }

    [Fact]
    public async Task Impressions_AreBuffered_NotPingedImmediately()
    {
        var handler = new StubHandler(req => req.RequestUri!.ToString() == Endpoint
            ? Ok(ManifestJson())
            : Ok(new byte[64]));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        var ad = await service.NextAsync();
        service.RecordImpression(ad!.Creative);

        Assert.Equal(1, service.PendingBeacons);
        Assert.Equal(2, handler.Calls); // manifest + image only — the beacon waits for shutdown
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static byte[] ManifestJson(bool twoCreatives = false)
    {
        string creatives = $$"""
            {"id":"a","image":"{{ImageUrl}}","click":"https://ads.example/a","impression":"https://ads.example/i/a"}
            """;
        if (twoCreatives)
            creatives += ",{\"id\":\"b\",\"image\":\"https://ads.example/creative-b.png\",\"click\":\"https://ads.example/b\"}";

        return Encoding.UTF8.GetBytes($"{{\"creatives\":[{creatives}]}}");
    }

    private static HttpResponseMessage Ok(byte[] body)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    /// <summary>GAME.* events are delivered on the bus's dispatch thread, so dormancy flips
    /// asynchronously; poll briefly instead of sleeping a fixed amount.</summary>
    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 10)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition not met within timeout");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private int _calls;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(_responder(request));
        }
    }

    /// <summary>Async variant for responders that must observe the request's CancellationToken
    /// (e.g. hang until the service cancels the fetch).</summary>
    private sealed class AsyncStubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public AsyncStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => _responder(request, ct);
    }
}
