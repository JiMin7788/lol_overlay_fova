using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Overlay.Core.Lcu;

/// <summary>
/// M33 D1 — League Client (LCU) connector: discovery, auth, champ-select session tracking, and
/// the two sanctioned writes (rune page, summoner spells). Everything runs against the client's
/// own local REST API using the credentials it publishes in its lockfile — no input synthesis,
/// no memory access (P3), own-session data only (P1).
///
/// <para><b>Discovery.</b> Poll (5 s) for the <c>LeagueClientUx</c> process, read the
/// <c>lockfile</c> next to its executable, then sanity-check with <c>GET /lol-gameflow/v1/phase</c>.
/// The client's self-signed certificate is accepted for this single localhost connection only —
/// the same pattern as <c>LiveClientPoller</c>.</para>
///
/// <para><b>Events.</b> MVP uses REST polling (2 s) of the gameflow phase + champ-select session
/// (the spec's WebSocket path is deferred; polling was its designed fallback and is fully
/// adequate at champ-select timescales). Consumers get <see cref="PhaseChanged"/> /
/// <see cref="ChampSelectChanged"/> plus the EventBus topics <c>SYSTEM.LCU_CONNECTED</c>,
/// <c>SYSTEM.LCU_DISCONNECTED</c>, <c>SYSTEM.CHAMPSELECT_ENTERED</c>, <c>SYSTEM.CHAMPSELECT_EXITED</c>,
/// <c>SYSTEM.CHAMPSELECT_CHAMPION_CHANGED</c>.</para>
///
/// <para><b>Failure posture</b> (M33): every error degrades to disconnected/hidden — no dialogs,
/// no throw to callers, reconnect backoff 5 s → 30 s cap.</para>
/// </summary>
public sealed class LcuConnector : IDisposable
{
    /// <summary>Fova's managed-page name prefix (M33 D2). Pages not starting with this are NEVER
    /// modified without the explicit overwrite confirmation.</summary>
    public const string ManagedPagePrefix = "Fova · ";

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(30);

    private HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<LcuLockfile?> _lockfileProvider;

    /// <summary>Lockfile the current <see cref="_http"/> is configured for. HttpClient forbids
    /// changing BaseAddress/auth after its first request (real-client finding 2026-07-24: doing
    /// so threw every loop iteration after the first, oscillating the connector) — so auth is
    /// applied once per credential set, and a CHANGED lockfile (client restart, new port)
    /// swaps in a fresh owned client instead of mutating the used one.</summary>
    private LcuLockfile? _appliedLockfile;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _phase = "";
    private ChampSelectSnapshot _lastSnapshot;
    private bool _disposed;

    public enum ConnectorState { Absent, Connected }

    /// <summary>Diagnostic sink (same convention as minimap-vision.log): state transitions,
    /// discovery results, and loop errors land in <c>logs/lcu.log</c> so a silent panel can be
    /// root-caused from a real client session. Message-only, never credentials.</summary>
    private static void Log(string message)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "logs", "lcu.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* diagnostics must never take down the connector */ }
    }

    public ConnectorState State { get; private set; } = ConnectorState.Absent;

    /// <summary>Raised on the connector's worker thread when the gameflow phase string changes.</summary>
    public event Action<string>? PhaseChanged;

    /// <summary>Raised on the worker thread when the own-player champ-select snapshot changes
    /// (entered/exited, champion hover/lock changes).</summary>
    public event Action<ChampSelectSnapshot>? ChampSelectChanged;

    /// <param name="httpClient">Injected for tests (stubbed handler); otherwise an own client
    /// accepting the localhost self-signed cert is created per connection.</param>
    /// <param name="lockfileProvider">Injected for tests; defaults to the process-probe +
    /// lockfile-read described in M33 D1.</param>
    public LcuConnector(HttpClient? httpClient = null, Func<LcuLockfile?>? lockfileProvider = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? NewOwnedClient();
        _lockfileProvider = lockfileProvider ?? ReadLockfileFromProcess;
    }

    /// <summary>Begin the background probe/poll loop. Idempotent-safe to call once.</summary>
    public void Start(CancellationToken externalToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) return;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    // ── Discovery (M33 D1) ──────────────────────────────────────────────────────

    /// <summary>Default lockfile provider: locate the running <c>LeagueClientUx</c> process and
    /// read the <c>lockfile</c> beside its executable. Returns null when the client is not
    /// running or the file is unreadable (mid-write) — the probe loop just retries.</summary>
    private static LcuLockfile? ReadLockfileFromProcess()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("LeagueClientUx"))
            {
                try
                {
                    string? exe = p.MainModule?.FileName;
                    if (exe is null) continue;
                    string path = Path.Combine(Path.GetDirectoryName(exe)!, "lockfile");
                    if (!File.Exists(path)) continue;
                    // The client keeps the lockfile open for write — share-friendly read.
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    return LcuLockfile.Parse(reader.ReadToEnd());
                }
                catch (Exception) { /* access denied / exited mid-probe — try next / retry later */ }
                finally { p.Dispose(); }
            }
        }
        catch (Exception) { /* process enumeration failure — retry later */ }
        return null;
    }

    private static HttpClient NewOwnedClient() => new(new SocketsHttpHandler
    {
        // Riot's self-signed cert — accepted for loopback targets only, never remote hosts.
        SslOptions = { RemoteCertificateValidationCallback = LoopbackServerCertificate.Validate },
        ConnectTimeout = TimeSpan.FromSeconds(2),
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Configures the HTTP client for <paramref name="lf"/> exactly once per credential
    /// set (see <see cref="_appliedLockfile"/>). An injected test client keeps its own
    /// BaseAddress; auth is still attached once.</summary>
    private void EnsureAuth(LcuLockfile lf)
    {
        if (lf == _appliedLockfile) return;

        if (_ownsHttp)
        {
            if (_appliedLockfile is not null)
            {
                _http.Dispose();
                _http = NewOwnedClient();
            }
            _http.BaseAddress = new Uri($"https://127.0.0.1:{lf.Port}");
        }
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{lf.Password}")));
        _appliedLockfile = lf;
    }

    // ── Main loop ───────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        TimeSpan backoff = ProbeInterval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var lf = _lockfileProvider();
                if (lf is null)
                {
                    if (State != ConnectorState.Absent) Log("lockfile gone -> Absent");
                    await SetDisconnectedAsync(ct, ProbeInterval).ConfigureAwait(false);
                    continue;
                }

                EnsureAuth(lf);
                // Real-client verification (2026-07-24): the phase endpoint is
                // /lol-gameflow/v1/gameflow-phase — /lol-gameflow/v1/phase 404s on the live LCU.
                string? phase = await GetStringField("/lol-gameflow/v1/gameflow-phase", ct).ConfigureAwait(false);
                if (phase is null)
                {
                    Log($"phase probe failed (port {lf.Port}) -> backoff {backoff.TotalSeconds:F0}s");
                    await SetDisconnectedAsync(ct, backoff).ConfigureAwait(false);
                    backoff = backoff >= BackoffCap ? BackoffCap : backoff + ProbeInterval;
                    continue;
                }

                backoff = ProbeInterval;
                if (State != ConnectorState.Connected)
                {
                    State = ConnectorState.Connected;
                    Log($"Connected (port {lf.Port}), phase={phase}");
                    EventBus.EventBus.Publish("SYSTEM.LCU_CONNECTED", null, source: nameof(LcuConnector));
                }
                await TickAsync(phase, ct).ConfigureAwait(false);
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // M33 failure posture: any surprise degrades to disconnected + retry.
                Log($"loop error: {ex.GetType().Name}: {ex.Message}");
                try { await SetDisconnectedAsync(ct, backoff).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                backoff = backoff >= BackoffCap ? BackoffCap : backoff + ProbeInterval;
            }
        }
    }

    private async Task SetDisconnectedAsync(CancellationToken ct, TimeSpan delay)
    {
        if (State != ConnectorState.Absent)
        {
            State = ConnectorState.Absent;
            PushSnapshot(default);
            EventBus.EventBus.Publish("SYSTEM.LCU_DISCONNECTED", null, source: nameof(LcuConnector));
        }
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private async Task TickAsync(string phase, CancellationToken ct)
    {
        if (phase != _phase)
        {
            _phase = phase;
            PhaseChanged?.Invoke(phase);
        }

        if (phase == "ChampSelect")
        {
            using var doc = await GetJson("/lol-champ-select/v1/session", ct).ConfigureAwait(false);
            PushSnapshot(doc is null ? default : ExtractSnapshot(doc.RootElement));
        }
        else
        {
            PushSnapshot(default);
        }
    }

    private void PushSnapshot(ChampSelectSnapshot snap)
    {
        if (snap == _lastSnapshot) return;
        var prev = _lastSnapshot;
        _lastSnapshot = snap;

        Log($"snapshot: inCS={snap.InChampSelect} champ={snap.ChampionKey} locked={snap.Locked}");
        if (snap.InChampSelect && !prev.InChampSelect)
            EventBus.EventBus.Publish("SYSTEM.CHAMPSELECT_ENTERED", null, source: nameof(LcuConnector));
        if (!snap.InChampSelect && prev.InChampSelect)
            EventBus.EventBus.Publish("SYSTEM.CHAMPSELECT_EXITED", null, source: nameof(LcuConnector));
        if (snap.InChampSelect && (snap.ChampionKey != prev.ChampionKey || snap.Locked != prev.Locked))
            EventBus.EventBus.Publish("SYSTEM.CHAMPSELECT_CHAMPION_CHANGED", snap, source: nameof(LcuConnector));

        ChampSelectChanged?.Invoke(snap);
    }

    /// <summary>Both teams' picks and bans from the live champ-select session (2026-07-25 comp
    /// analysis): championId per cell (0 = not picked yet; hovering enemies stay 0 until lock)
    /// and the two ban lists. Null outside champ select / on any error.</summary>
    public async Task<ChampSelectBoard?> GetChampSelectBoardAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJson("/lol-champ-select/v1/session", ct).ConfigureAwait(false);
            if (doc is null) return null;
            return ExtractBoard(doc.RootElement);
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    /// <summary>Pure/static board extraction (unit-testable against recorded session JSON) —
    /// see <see cref="GetChampSelectBoardAsync"/>.</summary>
    public static ChampSelectBoard ExtractBoard(JsonElement session)
    {
        static List<int> TeamPicks(JsonElement s, string prop)
        {
            var list = new List<int>();
            if (s.TryGetProperty(prop, out var team) && team.ValueKind == JsonValueKind.Array)
                foreach (var m in team.EnumerateArray())
                {
                    int id = m.TryGetProperty("championId", out var c)
                             && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                    // Pre-lock the LOCAL player's own cell (and ally hovers) carry the pick as
                    // championPickIntent while championId stays 0 — without this fallback the
                    // user's own champion was missing from the ally comp bar (2026-07-26).
                    if (id == 0 && m.TryGetProperty("championPickIntent", out var intent)
                        && intent.ValueKind == JsonValueKind.Number)
                        id = intent.GetInt32();
                    list.Add(id);
                }
            return list;
        }

        static List<int> BanList(JsonElement s, string prop)
        {
            var list = new List<int>();
            if (s.TryGetProperty("bans", out var bans)
                && bans.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var b in arr.EnumerateArray())
                    if (b.ValueKind == JsonValueKind.Number && b.GetInt32() > 0)
                        list.Add(b.GetInt32());
            return list;
        }

        return new ChampSelectBoard(
            TeamPicks(session, "myTeam"), TeamPicks(session, "theirTeam"),
            BanList(session, "myTeamBans"), BanList(session, "theirTeamBans"));
    }

    /// <summary>Extracts the OWN player's pick state from a champ-select session document
    /// (M33: <c>localPlayerCellId</c> → own myTeam row → <c>championId</c> when locked, else
    /// <c>championPickIntent</c> when hovering). Pure/static for unit tests against recorded
    /// session JSON.</summary>
    public static ChampSelectSnapshot ExtractSnapshot(JsonElement session)
    {
        if (!session.TryGetProperty("localPlayerCellId", out var cellEl)) return default;
        long cellId = cellEl.ValueKind == JsonValueKind.Number ? cellEl.GetInt64() : -1;
        if (cellId < 0 || !session.TryGetProperty("myTeam", out var team)
            || team.ValueKind != JsonValueKind.Array)
            return new ChampSelectSnapshot(true, 0, false);

        foreach (var member in team.EnumerateArray())
        {
            if (!member.TryGetProperty("cellId", out var idEl) || idEl.GetInt64() != cellId) continue;

            int locked = member.TryGetProperty("championId", out var champEl)
                && champEl.ValueKind == JsonValueKind.Number ? champEl.GetInt32() : 0;
            if (locked > 0) return new ChampSelectSnapshot(true, locked, true);

            int intent = member.TryGetProperty("championPickIntent", out var intentEl)
                && intentEl.ValueKind == JsonValueKind.Number ? intentEl.GetInt32() : 0;
            return new ChampSelectSnapshot(true, intent, false);
        }
        return new ChampSelectSnapshot(true, 0, false);
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>Current rune page in LCU shape, or null on any failure. Reading any page —
    /// user-built or Fova-managed — is safe; only WRITES are fenced (M33 D2).</summary>
    public async Task<RunePage?> GetCurrentRunePageAsync(CancellationToken ct = default)
    {
        using var doc = await GetJson("/lol-perks/v1/currentpage", ct).ConfigureAwait(false);
        if (doc is null) return null;
        return ParsePage(doc.RootElement);
    }

    private static RunePage? ParsePage(JsonElement el)
    {
        try
        {
            var page = new RunePage
            {
                PrimaryStyleId = el.GetProperty("primaryStyleId").GetInt32(),
                SubStyleId = el.GetProperty("subStyleId").GetInt32(),
            };
            foreach (var id in el.GetProperty("selectedPerkIds").EnumerateArray())
                page.PerkIds.Add(id.GetInt32());
            return page.PerkIds.Count > 0 ? page : null;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Own current summoner spells from the champ-select session (for preset capture),
    /// or null outside champ select / on failure.</summary>
    public async Task<(int Spell1, int Spell2)?> GetMySpellsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJson("/lol-champ-select/v1/session", ct).ConfigureAwait(false);
        if (doc is null) return null;
        var session = doc.RootElement;
        if (!session.TryGetProperty("localPlayerCellId", out var cellEl)
            || !session.TryGetProperty("myTeam", out var team)
            || team.ValueKind != JsonValueKind.Array) return null;
        long cellId = cellEl.GetInt64();
        foreach (var member in team.EnumerateArray())
        {
            if (!member.TryGetProperty("cellId", out var idEl) || idEl.GetInt64() != cellId) continue;
            int s1 = member.TryGetProperty("spell1Id", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
            int s2 = member.TryGetProperty("spell2Id", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
            return s1 > 0 && s2 > 0 ? (s1, s2) : null;
        }
        return null;
    }

    // ── Writes (M33 D2 fencing) ─────────────────────────────────────────────────

    /// <summary>Applies <paramref name="page"/> into the single Fova-managed page slot and makes
    /// it current. Fencing: overwrite only a page named <see cref="ManagedPagePrefix"/>*, else
    /// create a new page when a slot is free, else — only with
    /// <paramref name="confirmOverwriteCurrent"/> — overwrite the client's CURRENT page.
    /// Never deletes anything.</summary>
    public async Task<ApplyRunesResult> ApplyRunePageAsync(
        RunePage page, string championName, bool confirmOverwriteCurrent = false,
        CancellationToken ct = default)
    {
        try
        {
            using var pagesDoc = await GetJson("/lol-perks/v1/pages", ct).ConfigureAwait(false);
            if (pagesDoc is null) return ApplyRunesResult.Failed;

            long fovaPageId = -1, currentPageId = -1;
            int editableCount = 0;
            foreach (var p in pagesDoc.RootElement.EnumerateArray())
            {
                bool editable = p.TryGetProperty("isEditable", out var e) && e.GetBoolean();
                if (!editable) continue;
                editableCount++;
                long id = p.GetProperty("id").GetInt64();
                if (p.TryGetProperty("isActive", out var a) && a.GetBoolean()) currentPageId = id;
                string name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.StartsWith(ManagedPagePrefix, StringComparison.Ordinal)) fovaPageId = id;
            }

            using var invDoc = await GetJson("/lol-perks/v1/inventory", ct).ConfigureAwait(false);
            int ownedSlots = invDoc is not null
                && invDoc.RootElement.TryGetProperty("ownedPageCount", out var owned)
                ? owned.GetInt32() : editableCount; // fallback: assume full

            string body = JsonSerializer.Serialize(new
            {
                name = ManagedPagePrefix + championName,
                primaryStyleId = page.PrimaryStyleId,
                subStyleId = page.SubStyleId,
                selectedPerkIds = page.PerkIds,
                current = true,
            });

            if (fovaPageId >= 0)
            {
                bool ok = await Send(HttpMethod.Put, $"/lol-perks/v1/pages/{fovaPageId}", body, ct)
                    .ConfigureAwait(false);
                // (2026-07-26 live finding) The client honors `current:true` only on POST — a PUT
                // updates the page but leaves the PREVIOUSLY selected page active, so the user
                // kept seeing their old runes. Select the Fova page explicitly.
                if (ok) await Send(HttpMethod.Put, "/lol-perks/v1/currentpage",
                    fovaPageId.ToString(), ct).ConfigureAwait(false);
                return ok ? ApplyRunesResult.Applied : ApplyRunesResult.Failed;
            }

            if (editableCount < ownedSlots)
            {
                bool ok = await Send(HttpMethod.Post, "/lol-perks/v1/pages", body, ct)
                    .ConfigureAwait(false);
                if (ok)
                {
                    // Belt-and-braces activation: re-list to find the fresh page's id.
                    using var after = await GetJson("/lol-perks/v1/pages", ct).ConfigureAwait(false);
                    if (after is not null)
                        foreach (var p in after.RootElement.EnumerateArray())
                            if ((p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                                .StartsWith(ManagedPagePrefix, StringComparison.Ordinal))
                            {
                                await Send(HttpMethod.Put, "/lol-perks/v1/currentpage",
                                    p.GetProperty("id").GetInt64().ToString(), ct).ConfigureAwait(false);
                                break;
                            }
                }
                return ok ? ApplyRunesResult.Applied : ApplyRunesResult.Failed;
            }

            if (!confirmOverwriteCurrent || currentPageId < 0)
                return ApplyRunesResult.NeedsOverwriteConfirmation;

            {
                bool ok = await Send(HttpMethod.Put, $"/lol-perks/v1/pages/{currentPageId}", body, ct)
                    .ConfigureAwait(false);
                if (ok) await Send(HttpMethod.Put, "/lol-perks/v1/currentpage",
                    currentPageId.ToString(), ct).ConfigureAwait(false);
                return ok ? ApplyRunesResult.Applied : ApplyRunesResult.Failed;
            }
        }
        catch (OperationCanceledException) { return ApplyRunesResult.Failed; }
        catch (Exception) { return ApplyRunesResult.Failed; }
    }

    /// <summary>Sets the own summoner spells for the current champ select. Order is preserved
    /// (spell1 = D, spell2 = F).</summary>
    public async Task<bool> ApplySpellsAsync(int spell1Id, int spell2Id, CancellationToken ct = default)
    {
        string body = JsonSerializer.Serialize(new { spell1Id, spell2Id });
        return await Send(HttpMethod.Patch, "/lol-champ-select/v1/session/my-selection", body, ct)
            .ConfigureAwait(false);
    }

    // ── HTTP helpers ────────────────────────────────────────────────────────────

    private async Task<JsonDocument?> GetJson(string path, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return JsonDocument.Parse(bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    private async Task<string?> GetStringField(string path, CancellationToken ct)
    {
        using var doc = await GetJson(path, ct).ConfigureAwait(false);
        return doc?.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
    }

    private async Task<bool> Send(HttpMethod method, string path, string jsonBody, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, path)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
