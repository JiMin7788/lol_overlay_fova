using System.Net;
using System.Text.Json;
using Overlay.Core.Telemetry;

namespace Overlay.Core.Tests;

/// <summary>
/// M39 acceptance criteria for the telemetry service: consent gates (disabled / no endpoint =
/// zero network AND zero disk queue), the launch ping's payload, install-id persistence, the
/// durable crash queue (send-then-delete, keep-on-failure, oldest-first cap), the session
/// failure cap (M29 posture), and user-path scrubbing. Every case runs against a stubbed
/// <see cref="HttpMessageHandler"/> and a temp data dir — no network, no disk outside temp.
/// </summary>
public class TelemetryServiceTests : IDisposable
{
    private const string Endpoint = "https://telemetry.example";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "fova-telemetry-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private TelemetryService Create(StubHandler handler, bool enabled = true, string? endpoint = Endpoint)
        => new(enabled, endpoint, _dataDir, new HttpClient(handler),
            appVersion: "1.2.3-test", osVersion: "TestOS 10.0");

    [Fact]
    public async Task Disabled_SendsNothing_AndQueuesNothing()
    {
        var handler = new StubHandler(_ => Ok());
        using var service = Create(handler, enabled: false);

        service.RecordCrash("Test", "boom");
        await service.StartupAsync();

        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, service.PendingCrashes);
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public async Task NoEndpoint_SendsNothing_AndQueuesNothing()
    {
        var handler = new StubHandler(_ => Ok());
        using var service = Create(handler, endpoint: "");

        service.RecordCrash("Test", "boom");
        await service.StartupAsync();

        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, service.PendingCrashes);
    }

    [Fact]
    public async Task Startup_PostsLaunchPing_WithAnonymousIdentityOnly()
    {
        var handler = new StubHandler(_ => Ok());
        using var service = Create(handler);

        await service.StartupAsync();

        var (uri, body) = Assert.Single(handler.Requests);
        Assert.Equal(Endpoint + "/ping", uri);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(service.InstallId, doc.RootElement.GetProperty("InstallId").GetString());
        Assert.Equal("1.2.3-test", doc.RootElement.GetProperty("Version").GetString());
        Assert.Equal("TestOS 10.0", doc.RootElement.GetProperty("Os").GetString());
        // The whole payload is these three fields — nothing else ever rides along.
        Assert.Equal(3, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void InstallId_IsARandomGuid_PersistedAcrossInstances()
    {
        var handler = new StubHandler(_ => Ok());
        string first;
        using (var a = Create(handler)) first = a.InstallId;
        using var b = Create(handler);

        Assert.Equal(first, b.InstallId);
        Assert.Matches("^[0-9a-f]{32}$", first);
    }

    [Fact]
    public async Task RecordCrash_QueuesDurably_ThenFlushDeliversAndDeletes()
    {
        var handler = new StubHandler(_ => Ok());
        using var service = Create(handler);

        service.RecordCrash("UI-thread (Dispatcher)", "System.NullReferenceException: boom");
        await WaitFor(() => service.PendingCrashes == 0);

        var (uri, body) = Assert.Single(handler.Requests);
        Assert.Equal(Endpoint + "/crash", uri);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("UI-thread (Dispatcher)", doc.RootElement.GetProperty("Source").GetString());
        Assert.Contains("NullReferenceException", doc.RootElement.GetProperty("Details").GetString());
    }

    [Fact]
    public async Task UnreachableEndpoint_KeepsQueue_ForNextLaunch()
    {
        var failing = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using (var service = Create(failing))
        {
            service.RecordCrash("Test", "boom");
            await WaitFor(() => failing.Calls >= 1);
            Assert.Equal(1, service.PendingCrashes); // kept, not dropped
        }

        // "Next launch": a fresh instance against a healthy endpoint delivers the queued report.
        var healthy = new StubHandler(_ => Ok());
        using var next = Create(healthy);
        await next.StartupAsync();

        Assert.Equal(0, next.PendingCrashes);
        Assert.Contains(healthy.Requests, r => r.Uri == Endpoint + "/crash");
    }

    [Fact]
    public async Task SessionFailureCap_StopsRequests_QueueingContinues()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var service = Create(handler);

        for (int i = 0; i < 5; i++)
        {
            service.RecordCrash("Test", $"boom {i}");
            await WaitFor(() => !service.IsFlushing);
        }

        Assert.True(handler.Calls <= TelemetryService.MaxSessionFailures,
            $"expected at most {TelemetryService.MaxSessionFailures} attempts, saw {handler.Calls}");
        Assert.Equal(5, service.PendingCrashes); // the disk queue is unaffected by the network cap
    }

    [Fact]
    public void QueueCap_DropsOldestFirst()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var service = Create(handler);

        for (int i = 0; i < TelemetryService.MaxQueuedCrashes + 5; i++)
            service.RecordCrash("Test", $"boom {i}");

        Assert.Equal(TelemetryService.MaxQueuedCrashes, service.PendingCrashes);
    }

    [Theory]
    [InlineData(@"at C:\Users\pjm\Desktop\app.dll", "at ~\\Desktop\\app.dll")]
    [InlineData(@"path 'D:/Users/Some One/x.json' missing", "path '~ One/x.json' missing")]
    [InlineData("no user path here", "no user path here")]
    public void ScrubUserPaths_RemovesProfileNames(string input, string expected)
        => Assert.Equal(expected, TelemetryService.ScrubUserPaths(input));

    // ── Plumbing ────────────────────────────────────────────────────────────────

    private static HttpResponseMessage Ok() => new(HttpStatusCode.NoContent);

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
        private readonly List<(string Uri, string Body)> _requests = new();
        private int _calls;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<(string Uri, string Body)> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (_requests) _requests.Add((request.RequestUri!.ToString(), body));
            return _responder(request);
        }
    }
}
