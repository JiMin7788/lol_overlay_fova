using System.Net;
using System.Net.Security;
using System.Text;
using Overlay.Core.Ads;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 514) Regression guards for the three ad-slot security fixes from the loop-512 review:
/// the chunked-response size-cap bypass (a null ContentLength passed the <c>&gt; cap</c> pre-check
/// and the whole body was buffered), unvalidated manifest URLs (any scheme, any host was fetched
/// verbatim), and the payload being handed to the client-side decoder without a format check.
/// Plus the loopback scoping of the two Riot self-signed-certificate callbacks, whose "localhost
/// only" claim used to live in a comment while the code accepted every host.
/// </summary>
public class AdSlotHardeningTests : IDisposable
{
    private const string Endpoint = "https://ads.example/manifest.json";

    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), "fova-ad-hardening", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch (IOException) { }
    }

    // ── size cap vs chunked transfer ────────────────────────────────────────────

    [Fact]
    public async Task EndlessChunkedBody_IsRejectedAtTheCap_NotBufferedForever()
    {
        // No Content-Length and a stream that NEVER ends: the old ReadAsByteArrayAsync path would
        // buffer toward 2 GB. The capped read must give up at MaxCreativeBytes and return null.
        var handler = new StubHandler(req => req.RequestUri!.ToString() == Endpoint
            ? Ok(ManifestJson("https://ads.example/a.png"))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new EndlessContent() });
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        var ad = await service.NextAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(ad);
    }

    [Fact]
    public async Task ChunkedBodyUnderTheCap_IsStillServed()
    {
        // Chunked per se is fine — only crossing the cap is not.
        var handler = new StubHandler(req => req.RequestUri!.ToString() == Endpoint
            ? Ok(ManifestJson("https://ads.example/a.png"))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new NoLengthContent(Png()) });
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        var ad = await service.NextAsync();

        Assert.NotNull(ad);
        Assert.Equal("a", ad!.Creative.Id);
    }

    // ── URL validation at the manifest boundary ─────────────────────────────────

    [Fact]
    public async Task RemoteHttpImage_DropsTheCreative_AndNeverRequestsIt()
    {
        string manifest = """
            {"creatives":[
              {"id":"evil","image":"http://evil.example/a.png","click":"https://ads.example/a"},
              {"id":"good","image":"https://ads.example/b.png","click":"https://ads.example/b"}
            ]}
            """;
        var handler = new StubHandler(req => req.RequestUri!.ToString() == Endpoint
            ? Ok(Encoding.UTF8.GetBytes(manifest))
            : Ok(Png()));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        var ad = await service.NextAsync();

        Assert.Equal("good", ad!.Creative.Id);
        Assert.DoesNotContain(handler.Requests, u => u.Contains("evil.example"));
    }

    [Fact]
    public async Task LoopbackHttpUrls_AreAllowed_SoTheLocalTestServerKeepsWorking()
    {
        const string localEndpoint = "http://127.0.0.1:8099/ads.json";
        var handler = new StubHandler(req => req.RequestUri!.ToString() == localEndpoint
            ? Ok(ManifestJson("http://localhost:8099/a.png"))
            : Ok(Png()));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, localEndpoint, _cacheDir, http);

        Assert.True(service.IsConfigured);
        var ad = await service.NextAsync();
        Assert.NotNull(ad);
    }

    [Fact]
    public async Task RemoteHttpEndpoint_CollapsesTheSlot()
    {
        var handler = new StubHandler(_ => Ok(ManifestJson("https://ads.example/a.png")));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, "http://ads.example/manifest.json", _cacheDir, http);

        Assert.False(service.IsConfigured);
        Assert.Null(await service.NextAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BadImpressionUrl_LosesOnlyTheBeacon()
    {
        string manifest = """
            {"creatives":[{"id":"a","image":"https://ads.example/a.png",
                           "click":"https://ads.example/a","impression":"file:///C:/x"}]}
            """;
        var handler = new StubHandler(req => req.RequestUri!.ToString() == Endpoint
            ? Ok(Encoding.UTF8.GetBytes(manifest))
            : Ok(Png()));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        var ad = await service.NextAsync();
        Assert.NotNull(ad);            // the creative survives...
        service.RecordImpression(ad!.Creative);
        Assert.Equal(0, service.PendingBeacons);   // ...its beacon does not
    }

    // ── payload format check ────────────────────────────────────────────────────

    [Fact]
    public async Task NonImagePayload_IsRejectedBeforeTheDecoder()
    {
        var handler = new StubHandler(req => req.RequestUri!.ToString() == Endpoint
            ? Ok(ManifestJson("https://ads.example/a.png"))
            : Ok(Encoding.UTF8.GetBytes("<html>not an image</html>")));
        using var http = new HttpClient(handler);
        using var service = new AdSlotService(true, Endpoint, _cacheDir, http);

        Assert.Null(await service.NextAsync());
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, true)]  // PNG
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]              // JPEG
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, true)]  // WebP
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0, 0, 0, 0 }, false)]       // GIF (animated risk)
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]             // "<svg"
    public void LooksLikeStaticImage_AcceptsOnlyTheContractFormats(byte[] bytes, bool expected)
        => Assert.Equal(expected, AdSlotService.LooksLikeStaticImage(bytes));

    // ── loopback-scoped certificate policy (LiveClientPoller / LcuConnector) ────

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData(null, true)]
    public void ValidChain_IsAcceptedForAnyHost(string? host, bool expected)
        => Assert.Equal(expected, LoopbackServerCertificate.Accept(host, SslPolicyErrors.None));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("::1", true)]
    [InlineData("example.com", false)]       // a remote host gets full validation again
    [InlineData("127.0.0.1.evil.com", false)]
    [InlineData(null, false)]
    public void SelfSignedChain_IsAcceptedForLoopbackTargetsOnly(string? host, bool expected)
        => Assert.Equal(expected,
            LoopbackServerCertificate.Accept(host, SslPolicyErrors.RemoteCertificateChainErrors));

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static byte[] ManifestJson(string imageUrl) => Encoding.UTF8.GetBytes(
        $"{{\"creatives\":[{{\"id\":\"a\",\"image\":\"{imageUrl}\",\"click\":\"https://ads.example/a\"}}]}}");

    private static byte[] Png(int length = 64)
    {
        var bytes = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static HttpResponseMessage Ok(byte[] body)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly List<string> _requests = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public IReadOnlyList<string> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_requests) _requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(_responder(request));
        }
    }

    /// <summary>No Content-Length (TryComputeLength false = the chunked shape) over a fixed body.</summary>
    private class NoLengthContent : HttpContent
    {
        private readonly byte[] _data;
        public NoLengthContent(byte[] data) => _data = data;

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(_data, writable: false));

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(_data, 0, _data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>No Content-Length AND a stream that never reaches EOF — the worst case the capped
    /// read must survive. If the service ever regresses to whole-body buffering, the test hangs
    /// toward OOM instead of failing an assert, hence the WaitAsync in the caller.</summary>
    private sealed class EndlessContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new EndlessStream());

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => throw new NotSupportedException("read via CreateContentReadStreamAsync only");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class EndlessStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => count; // zeros, forever
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
