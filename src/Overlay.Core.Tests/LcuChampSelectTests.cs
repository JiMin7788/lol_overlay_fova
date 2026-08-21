using System.Net;
using System.Text;
using System.Text.Json;
using Overlay.Core.ChampSelect;
using Overlay.Core.Config;
using Overlay.Core.Lcu;

namespace Overlay.Core.Tests;

/// <summary>
/// M33 acceptance criteria (headless half): lockfile parsing, champ-select snapshot extraction
/// from recorded session JSON, the D2 rune-page write fencing against a stubbed LCU handler,
/// config-backed preset round-trip, and the P4 auto-apply gate rules.
/// </summary>
public class LcuChampSelectTests : IDisposable
{
    private readonly string _dir;

    public LcuChampSelectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LcuChampSelectTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── lockfile parsing ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("LeagueClient:22488:52104:secretpw:https", 52104, "secretpw")]
    [InlineData("LeagueClient:1:443:p:https\n", 443, "p")] // trailing newline tolerated
    public void Lockfile_Parses_ValidShapes(string content, int port, string password)
    {
        var lf = LcuLockfile.Parse(content);
        Assert.NotNull(lf);
        Assert.Equal(port, lf!.Port);
        Assert.Equal(password, lf.Password);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LeagueClient:22488:52104")]         // too few parts (mid-write read)
    [InlineData("LeagueClient:22488:notaport:pw:https")]
    [InlineData("LeagueClient:22488:52104::https")]  // empty password
    public void Lockfile_RejectsMalformed(string? content)
        => Assert.Null(LcuLockfile.Parse(content));

    // ── champ-select snapshot extraction ────────────────────────────────────────

    private static JsonElement Session(string myTeamJson, long localCell = 2)
        => JsonDocument.Parse($$"""
           { "localPlayerCellId": {{localCell}}, "myTeam": {{myTeamJson}} }
           """).RootElement.Clone();

    [Fact]
    public void Snapshot_LockedChampion_WinsOverIntent()
    {
        var snap = LcuConnector.ExtractSnapshot(Session(
            """[ {"cellId":2, "championId":266, "championPickIntent":103} ]"""));
        Assert.Equal(new ChampSelectSnapshot(true, 266, true), snap);
    }

    [Fact]
    public void Snapshot_HoverOnly_IsUnlockedIntent()
    {
        var snap = LcuConnector.ExtractSnapshot(Session(
            """[ {"cellId":2, "championId":0, "championPickIntent":103} ]"""));
        Assert.Equal(new ChampSelectSnapshot(true, 103, false), snap);
    }

    [Fact]
    public void Snapshot_NothingPicked_IsZero()
    {
        var snap = LcuConnector.ExtractSnapshot(Session(
            """[ {"cellId":2, "championId":0, "championPickIntent":0} ]"""));
        Assert.Equal(new ChampSelectSnapshot(true, 0, false), snap);
    }

    [Fact]
    public void Snapshot_OtherCellsIgnored()
    {
        var snap = LcuConnector.ExtractSnapshot(Session(
            """[ {"cellId":0, "championId":55}, {"cellId":2, "championId":0, "championPickIntent":0} ]"""));
        Assert.Equal(new ChampSelectSnapshot(true, 0, false), snap);
    }

    [Fact]
    public void Snapshot_MissingLocalCellId_IsDefault()
    {
        var el = JsonDocument.Parse("""{ "myTeam": [] }""").RootElement.Clone();
        Assert.Equal(default, LcuConnector.ExtractSnapshot(el));
    }

    // ── D2 rune-page write fencing ──────────────────────────────────────────────

    private sealed class LcuStub : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Writes = new();
        public string PagesJson = "[]";
        public string InventoryJson = """{"ownedPageCount": 2}""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            string path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get)
            {
                string json = path switch
                {
                    "/lol-perks/v1/pages" => PagesJson,
                    "/lol-perks/v1/inventory" => InventoryJson,
                    _ => "null",
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            string body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            Writes.Add((req.Method.Method, path, body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static LcuConnector Connector(LcuStub stub)
    {
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://127.0.0.1:1") };
        return new LcuConnector(http);
    }

    private static readonly RunePage Page = new()
    {
        PrimaryStyleId = 8100,
        SubStyleId = 8300,
        PerkIds = { 8112, 8143, 8138, 8135, 8352, 8345, 5008, 5008, 5001 },
    };

    [Fact]
    public async Task Fencing_ExistingFovaPage_IsOverwrittenInPlace()
    {
        var stub = new LcuStub
        {
            PagesJson = """
                [ {"id": 7, "name": "내 페이지", "isEditable": true, "isActive": true},
                  {"id": 9, "name": "Fova · Ahri", "isEditable": true, "isActive": false} ]
                """,
        };
        using var lcu = Connector(stub);

        var result = await lcu.ApplyRunePageAsync(Page, "Zed");

        Assert.Equal(ApplyRunesResult.Applied, result);
        // Two writes since 2026-07-26: the page update AND the explicit currentpage activation
        // (the client honors current:true only on POST, so a PUT alone left the old page shown).
        Assert.Equal(2, stub.Writes.Count);
        var write = stub.Writes[0];
        Assert.Equal(("PUT", "/lol-perks/v1/pages/9"), (write.Method, write.Path));
        using var body = JsonDocument.Parse(write.Body); // serializer unicode-escapes the middle dot
        Assert.Equal("Fova · Zed", body.RootElement.GetProperty("name").GetString()); // renamed to the new champion
        Assert.Equal(("PUT", "/lol-perks/v1/currentpage", "9"),
            (stub.Writes[1].Method, stub.Writes[1].Path, stub.Writes[1].Body));
        Assert.DoesNotContain("/lol-perks/v1/pages/7", stub.Writes.Select(w => w.Path));
    }

    [Fact]
    public async Task Fencing_NoFovaPage_FreeSlot_CreatesNewPage()
    {
        var stub = new LcuStub
        {
            PagesJson = """[ {"id": 7, "name": "내 페이지", "isEditable": true, "isActive": true} ]""",
            InventoryJson = """{"ownedPageCount": 2}""", // 1 used of 2 -> free slot
        };
        using var lcu = Connector(stub);

        var result = await lcu.ApplyRunePageAsync(Page, "Ahri");

        Assert.Equal(ApplyRunesResult.Applied, result);
        var write = Assert.Single(stub.Writes);
        Assert.Equal(("POST", "/lol-perks/v1/pages"), (write.Method, write.Path));
    }

    [Fact]
    public async Task Fencing_SlotsFull_WithoutConfirmation_WritesNothing()
    {
        var stub = new LcuStub
        {
            PagesJson = """
                [ {"id": 7, "name": "내 페이지 1", "isEditable": true, "isActive": true},
                  {"id": 8, "name": "내 페이지 2", "isEditable": true, "isActive": false} ]
                """,
            InventoryJson = """{"ownedPageCount": 2}""",
        };
        using var lcu = Connector(stub);

        var result = await lcu.ApplyRunePageAsync(Page, "Ahri");

        Assert.Equal(ApplyRunesResult.NeedsOverwriteConfirmation, result);
        Assert.Empty(stub.Writes); // D2: a user-built page is NEVER touched silently
    }

    [Fact]
    public async Task Fencing_SlotsFull_WithConfirmation_OverwritesCurrentPageOnly()
    {
        var stub = new LcuStub
        {
            PagesJson = """
                [ {"id": 7, "name": "내 페이지 1", "isEditable": true, "isActive": false},
                  {"id": 8, "name": "내 페이지 2", "isEditable": true, "isActive": true} ]
                """,
            InventoryJson = """{"ownedPageCount": 2}""",
        };
        using var lcu = Connector(stub);

        var result = await lcu.ApplyRunePageAsync(Page, "Ahri", confirmOverwriteCurrent: true);

        Assert.Equal(ApplyRunesResult.Applied, result);
        Assert.Equal(2, stub.Writes.Count); // page update + explicit activation (2026-07-26)
        Assert.Equal(("PUT", "/lol-perks/v1/pages/8"), (stub.Writes[0].Method, stub.Writes[0].Path)); // the ACTIVE page
        Assert.Equal(("PUT", "/lol-perks/v1/currentpage", "8"),
            (stub.Writes[1].Method, stub.Writes[1].Path, stub.Writes[1].Body));
    }

    [Fact]
    public async Task Spells_PatchMySelection_PreservesOrder()
    {
        var stub = new LcuStub();
        using var lcu = Connector(stub);

        Assert.True(await lcu.ApplySpellsAsync(spell1Id: 4, spell2Id: 14)); // Flash on D

        var write = Assert.Single(stub.Writes);
        Assert.Equal(("PATCH", "/lol-champ-select/v1/session/my-selection"), (write.Method, write.Path));
        using var doc = JsonDocument.Parse(write.Body);
        Assert.Equal(4, doc.RootElement.GetProperty("spell1Id").GetInt32());
        Assert.Equal(14, doc.RootElement.GetProperty("spell2Id").GetInt32());
    }

    // ── preset store round-trip ─────────────────────────────────────────────────

    [Fact]
    public void Presets_RoundTrip_SaveListDeleteSurvivesReload()
    {
        string configPath = Path.Combine(_dir, "user_config.json");
        var preset = new RunePreset
        {
            Name = "미드 기본",
            ChampionKey = 103,
            Page = Page,
            Spell1Id = 4,
            Spell2Id = 14,
            SavedAt = "2026-07-23",
        };

        using (var config = new ConfigManager(configPath))
        {
            var store = new ChampSelectPresets(config);
            store.Save(preset);
            store.Save(new RunePreset { Name = "포킹", ChampionKey = 103, Page = Page });
            store.Save(preset); // same-name save replaces, not duplicates
            Assert.Equal(2, store.List(103).Count);
        } // Dispose flushes the debounced write synchronously

        using (var config = new ConfigManager(configPath)) // reload from disk: typed round-trip
        {
            var store = new ChampSelectPresets(config);
            var list = store.List(103);
            Assert.Equal(2, list.Count);
            Assert.Equal("미드 기본", list[0].Name);
            Assert.Equal(103, list[0].ChampionKey);
            Assert.Equal(Page.PerkIds, list[0].Page.PerkIds);
            Assert.Equal(4, list[0].Spell1Id);

            store.Delete(103, "포킹");
            Assert.Single(store.List(103));
            Assert.Empty(store.List(999)); // unknown champion -> empty, never null/throw
        }
    }

    // ── D4 phase-2: file recommendation source ──────────────────────────────────

    [Fact]
    public void RecSource_PicksNumericallyLatestPatch_NotStringOrder()
    {
        string root = Path.Combine(_dir, "rec");
        foreach (var patch in new[] { "16.9", "16.14", "16.2", "junk", "16.x" })
            Directory.CreateDirectory(Path.Combine(root, patch));

        // string sort would pick "16.9"; numeric compare must pick 16.14
        Assert.EndsWith("16.14", FileRecommendationSource.ResolveLatestPatchDir(root)!);
    }

    [Fact]
    public void RecSource_KeepsMaturePatch_WhenTheNewerOneIsStillThin()
    {
        string root = Path.Combine(_dir, "rec");
        // Measured on 2026-07-31, one day into patch 16.15: the mature patch had aggregated 166
        // champions, the fresh one only 43. Preferring "newest" alone cost 74% of the coverage.
        Seed(root, "16.14", 166);
        Seed(root, "16.15", 43);

        Assert.EndsWith("16.14", FileRecommendationSource.ResolveLatestPatchDir(root)!);

        // Once collection catches up, the newer patch must take over on its own.
        Seed(root, "16.15", 160);
        Assert.EndsWith("16.15", FileRecommendationSource.ResolveLatestPatchDir(root)!);

        static void Seed(string root, string patch, int champions)
        {
            string dir = Path.Combine(root, patch);
            Directory.CreateDirectory(dir);
            for (int i = 1; i <= champions; i++)
                File.WriteAllText(Path.Combine(dir, $"{i}.json"), "[]");
        }
    }

    [Fact]
    public void RecSource_ReadsChampionFile_AndForcesRemoteSource()
    {
        string root = Path.Combine(_dir, "rec");
        Directory.CreateDirectory(Path.Combine(root, "16.14"));
        File.WriteAllText(Path.Combine(root, "16.14", "64.json"), """
            [ { "name": "JUNGLE 추천 12판 58%", "championKey": 64,
                "page": { "primaryStyleId": 8000, "subStyleId": 8300,
                          "perkIds": [8010,9111,9104,8014,8304,8347,5005,5008,5001] },
                "spell1Id": 4, "spell2Id": 11, "source": "local" } ]
            """); // source lies as "local" — the reader must force "remote" (P4 gate safety)

        var source = new FileRecommendationSource(root);
        var list = source.List(64);

        var p = Assert.Single(list);
        Assert.Equal(8000, p.Page.PrimaryStyleId);
        Assert.Equal("remote", p.Source); // never auto-apply eligible
        Assert.Empty(source.List(999));   // unknown champion -> empty
    }

    [Fact]
    public void RecSource_MissingDirOrCorruptJson_ListsNothing()
    {
        Assert.Empty(new FileRecommendationSource(Path.Combine(_dir, "nope")).List(64));
        Assert.Empty(new FileRecommendationSource("").List(64));

        string root = Path.Combine(_dir, "rec2");
        Directory.CreateDirectory(Path.Combine(root, "16.14"));
        File.WriteAllText(Path.Combine(root, "16.14", "64.json"), "{not json");
        Assert.Empty(new FileRecommendationSource(root).List(64));
    }

    // ── P4 auto-apply gate ──────────────────────────────────────────────────────

    private sealed class FixedSource : IRunePresetSource
    {
        private readonly IReadOnlyList<RunePreset> _presets;
        public FixedSource(params RunePreset[] presets) => _presets = presets;
        public IReadOnlyList<RunePreset> List(int championKey) => _presets;
    }

    [Fact]
    public void AutoApply_FiresOncePerSession_OnLockOnly_WhenOptedIn()
    {
        var gate = new AutoApplyGate();
        var source = new FixedSource(new RunePreset { Name = "기본", ChampionKey = 266 });

        // not opted in -> never
        Assert.Null(gate.OnSnapshot(new(true, 266, true), optedIn: false, source));
        // hover only -> never
        Assert.Null(gate.OnSnapshot(new(true, 266, false), optedIn: true, source));
        // locked + opted in -> fires once
        Assert.NotNull(gate.OnSnapshot(new(true, 266, true), optedIn: true, source));
        // same session -> never again
        Assert.Null(gate.OnSnapshot(new(true, 266, true), optedIn: true, source));
        // session ends -> re-arms for the next one
        Assert.Null(gate.OnSnapshot(default, optedIn: true, source));
        Assert.NotNull(gate.OnSnapshot(new(true, 266, true), optedIn: true, source));
    }

    [Fact]
    public void AutoApply_NeverFiresForRemoteSources()
    {
        var gate = new AutoApplyGate();
        var remoteOnly = new FixedSource(new RunePreset { Name = "추천", ChampionKey = 266, Source = "remote" });
        Assert.Null(gate.OnSnapshot(new(true, 266, true), optedIn: true, remoteOnly));
    }
}
