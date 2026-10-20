using System.Net;
using System.Text;
using Overlay.Core.Flags;

namespace Overlay.Core.Tests;

/// <summary>
/// Remote kill switch for the gated features: it can only force features off, it survives an
/// offline launch through its disk cache, and a bad fetch never loosens the current set.
/// Stubbed <see cref="HttpMessageHandler"/> + temp cache file — no network.
/// </summary>
public class RemoteFlagsTests : IDisposable
{
    private const string Endpoint = "https://flags.example/flags.json";

    private readonly string _cachePath =
        Path.Combine(Path.GetTempPath(), "fova-flag-tests", Guid.NewGuid().ToString("N"), "flags.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_cachePath)!, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Refresh_ForcesListedFeaturesOff_AndRaisesChanged()
    {
        using var http = new HttpClient(new StubHandler(_ => Ok("{\"forceOff\":[\"enemyItemAlert\",\"laneReturn\"]}")));
        using var flags = new RemoteFlags(Endpoint, _cachePath, http);
        int changed = 0;
        flags.Changed += () => changed++;

        Assert.False(flags.IsForcedOff("enemyItemAlert"));
        await flags.RefreshAsync();

        Assert.True(flags.IsForcedOff("enemyItemAlert"));
        Assert.True(flags.IsForcedOff("LANERETURN")); // names are case-insensitive
        Assert.False(flags.IsForcedOff("recallTimer"));
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task CachedDocument_IsInForce_OnTheNextOfflineLaunch()
    {
        using (var http = new HttpClient(new StubHandler(_ => Ok("{\"forceOff\":[\"recallTimer\"]}"))))
        using (var first = new RemoteFlags(Endpoint, _cachePath, http))
            await first.RefreshAsync();

        using var offline = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        using var second = new RemoteFlags(Endpoint, _cachePath, offline);
        Assert.True(second.IsForcedOff("recallTimer")); // before any fetch
        await second.RefreshAsync();
        Assert.True(second.IsForcedOff("recallTimer")); // failed fetch keeps it
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"forceOff\":\"recallTimer\"}")]
    public async Task MalformedDocument_KeepsTheCurrentSet(string body)
    {
        File.WriteAllText(EnsureDir(_cachePath), "{\"forceOff\":[\"recallTimer\"]}");
        using var http = new HttpClient(new StubHandler(_ => Ok(body)));
        using var flags = new RemoteFlags(Endpoint, _cachePath, http);

        await flags.RefreshAsync();

        Assert.True(flags.IsForcedOff("recallTimer"));
    }

    [Fact]
    public async Task OversizedDocument_IsIgnored()
    {
        var huge = "{\"forceOff\":[\"recallTimer\"],\"pad\":\"" + new string('x', RemoteFlags.MaxBytes) + "\"}";
        using var http = new HttpClient(new StubHandler(_ => Ok(huge)));
        using var flags = new RemoteFlags(Endpoint, _cachePath, http);

        await flags.RefreshAsync();

        Assert.False(flags.IsForcedOff("recallTimer"));
    }

    [Fact]
    public async Task EmptyList_UnforcesEverything()
    {
        File.WriteAllText(EnsureDir(_cachePath), "{\"forceOff\":[\"recallTimer\"]}");
        using var http = new HttpClient(new StubHandler(_ => Ok("{\"forceOff\":[]}")));
        using var flags = new RemoteFlags(Endpoint, _cachePath, http);

        await flags.RefreshAsync();

        Assert.False(flags.IsForcedOff("recallTimer"));
    }

    [Fact]
    public async Task NonHttpsEndpoint_NeverFetches()
    {
        var handler = new StubHandler(_ => Ok("{\"forceOff\":[\"recallTimer\"]}"));
        using var http = new HttpClient(handler);
        using var flags = new RemoteFlags("http://flags.example/flags.json", _cachePath, http);

        await flags.RefreshAsync();

        Assert.Equal(0, handler.Calls);
        Assert.False(flags.IsForcedOff("recallTimer"));
    }

    [Fact]
    public void PublishedDocument_ForcesTheThreeGatedFeaturesOff()
    {
        // The repo-root flags.json is what the app fetches from the public branch.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "flags.json"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var set = RemoteFlags.Parse(File.ReadAllBytes(Path.Combine(dir!.FullName, "flags.json")));

        Assert.NotNull(set);
        Assert.Contains("recallTimer", set!);
        Assert.DoesNotContain("laneReturn", set!); // (2026-10-20) back on by the user's decision
        Assert.Contains("enemyJunglerSpotted", set!);
        Assert.Contains("enemyItemAlert", set!);
    }

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

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
}
