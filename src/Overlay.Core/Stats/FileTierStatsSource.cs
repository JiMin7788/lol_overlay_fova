using System.Text.Json;
using System.Text.Json.Serialization;
using Overlay.Core.ChampSelect;

namespace Overlay.Core.Stats;

/// <summary>Win rate of one game-duration bucket for a champion: sample size + rate. Zero games
/// means the bucket is empty (renderers should show a dash, not 0%).</summary>
public readonly record struct DurationBucket(int Games, double WinRate);

/// <summary>One row of the champion tier table (one patch, one tier bracket, optionally one
/// role). Rates are raw measured fractions (0–1); this type assigns no grade — the tier list's
/// letters are relative to the peer group on screen, so they are computed at display time by
/// <see cref="ChampionGrade"/> rather than baked into a row here.
///
/// <para>Under a role filter <see cref="Games"/>, <see cref="WinRate"/>, <see cref="PickRate"/>
/// and the duration buckets describe THAT role only, and <see cref="PickRate"/> switches
/// denominator from "matches" to "slots at this position" — the two are different questions and
/// the view labels which one it is showing. <see cref="BanRate"/> never does: bans are declared
/// before positions exist, so it stays the champion-wide rate under every filter.</para></summary>
public sealed record TierRow(
    int ChampionKey,
    string Name,
    int Games,
    double WinRate,
    double PickRate,
    double BanRate,
    DurationBucket Under25,
    DurationBucket From25To32,
    DurationBucket Over32);

/// <summary>
/// The dashboard statistics view's data: champion win/pick/ban rates + duration-bucket win
/// curves read from <c>rec/{patch}/stats/tiers.json</c> (<c>tools/aggregate_tiers.py</c>).
/// Same patch resolution as runes/items/benchmarks
/// (<see cref="FileRecommendationSource.ResolveLatestPatchDir"/>) and the same failure posture
/// (missing/corrupt → an empty table, never an error).
///
/// <para>The file stores COUNTS per seed tier and no rates, which is what lets this class compose
/// any cumulative bracket (<see cref="RecBrackets"/>) exactly: counts add across tiers, rates do
/// not. Unlike the rune/item/benchmark files there is no per-bracket copy on disk — one patch-level
/// file answers every bracket.</para>
///
/// <para>Seed tiers, not match tiers: MATCH-V5 carries no rank, so the pipeline records the tier
/// of the player each match was discovered through. <see cref="SeedTiers"/> lists what the file
/// actually holds, and the view offers only brackets those tiers can answer.</para>
/// </summary>
public sealed class FileTierStatsSource
{
    /// <summary>Passed as the role argument to mean "every position".</summary>
    public const string AllRoles = "";

    /// <summary>Display order for positions; anything else (e.g. UNKNOWN) sorts after these.</summary>
    private static readonly string[] RoleOrder = { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" };

    private readonly string _recRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, Block> _composed = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Block> _byTier = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _seedTiers = Array.Empty<string>();
    private string _patch = "";
    private int _totalMatches;
    private DateTime? _updatedAt;
    private bool _loaded;

    public FileTierStatsSource(string recRoot) => _recRoot = recRoot ?? "";

    /// <summary>The patch the table describes ("" when nothing loaded) and the whole collected
    /// sample across every seed tier.</summary>
    public (string Patch, int Matches) SampleInfo
    {
        get { Load(); return (_patch, _totalMatches); }
    }

    /// <summary>Seed tiers present in the file, in file order. Empty when nothing loaded.</summary>
    public IReadOnlyList<string> SeedTiers { get { Load(); return _seedTiers; } }

    /// <summary>When the aggregation last wrote this table, or null when nothing loaded. This is
    /// the file's own write time rather than "now": a reader has to be able to tell a live table
    /// from one frozen by a collector outage, and the outages in this project's history are exactly
    /// why that distinction is worth a line on screen.</summary>
    public DateTime? UpdatedAt { get { Load(); return _updatedAt; } }

    /// <summary>Games a champion needs before it counts toward a bracket's roster coverage. Fixed
    /// rather than taken from the view's minimum-sample filter, so which brackets exist does not
    /// flicker as the reader moves that filter.</summary>
    private const int CoverageReferenceGames = 30;

    /// <summary>Brackets this sample holds, highest first (<see cref="RecBrackets"/>), each flagged
    /// as thin or not.
    ///
    /// <para>Every bracket with any data is offered; the thin ones are marked rather than hidden, so
    /// a band the user asked for is present and honest instead of silently missing. Thin-ness is
    /// ROSTER COVERAGE, against the same fraction
    /// (<see cref="FileRecommendationSource.CoverageFloorRatio"/>) the rune side uses. Coverage
    /// saturates — every bracket reaches the full roster once its sample is real — so a narrow
    /// bracket is not punished for holding fewer tiers. Comparing match counts would be, because
    /// the brackets are nested: platinum_plus holds six tenths of "all" by construction and would
    /// fail such a test permanently.</para></summary>
    public IReadOnlyList<(string Slug, bool Thin)> AvailableBrackets()
    {
        Load();
        var covered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var slug in RecBrackets.Available(_seedTiers))
            covered[slug] = Covered(slug);

        int best = 0;
        foreach (int n in covered.Values) if (n > best) best = n;
        double floor = best * FileRecommendationSource.CoverageFloorRatio;

        // Offered on having ANY sample, not on coverage: the user asked for these bands by name,
        // and a band that was collected but is still thin should say so rather than vanish.
        var offered = new List<(string, bool)>();
        foreach (var slug in RecBrackets.Available(_seedTiers))
            if (Matches(slug) > 0) offered.Add((slug, covered[slug] < floor));
        return offered;
    }

    /// <summary>Champions in a bracket with enough games to count toward its coverage.</summary>
    public int Covered(string bracket)
    {
        Load();
        int n = 0;
        foreach (var row in All(bracket))
            if (row.Games >= CoverageReferenceGames) n++;
        return n;
    }

    /// <summary>Match sample behind one bracket.</summary>
    public int Matches(string bracket)
    {
        Load();
        return Compose(bracket)?.Matches ?? 0;
    }

    /// <summary>Positions the bracket's sample actually contains, in lane order.</summary>
    public IReadOnlyList<string> Roles(string bracket)
    {
        Load();
        var block = Compose(bracket);
        if (block is null) return Array.Empty<string>();
        var roles = new List<string>(block.RoleSlots.Keys);
        roles.Sort(static (a, b) =>
        {
            int ia = Array.IndexOf(RoleOrder, a), ib = Array.IndexOf(RoleOrder, b);
            if (ia < 0) ia = int.MaxValue;
            if (ib < 0) ib = int.MaxValue;
            return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a, b);
        });
        return roles;
    }

    /// <summary>All champion rows for one bracket, unsorted (the view owns ordering). With a role,
    /// every rate is recomputed for that role; champions that never played it are absent.</summary>
    public IReadOnlyList<TierRow> All(string bracket, string role = AllRoles)
    {
        Load();
        var block = Compose(bracket);
        if (block is null) return Array.Empty<TierRow>();

        var rows = new List<TierRow>(block.Champions.Count);
        foreach (var c in block.Champions)
        {
            double banRate = block.Matches > 0 ? (double)c.Bans / block.Matches : 0;
            if (role.Length == 0)
            {
                rows.Add(new TierRow(c.Key, c.Name, c.Games, (double)c.Wins / c.Games,
                                     block.Matches > 0 ? (double)c.Games / block.Matches : 0,
                                     banRate, c.Under25, c.From25To32, c.Over32));
                continue;
            }
            if (!c.Roles.TryGetValue(role, out var r) || r.Games <= 0) continue;
            // Role pick rate is measured against the slots at that position, not against matches:
            // "of every mid lane played, this share was this champion".
            double pick = block.RoleSlots.TryGetValue(role, out int slots) && slots > 0
                ? (double)r.Games / slots
                : 0;
            rows.Add(new TierRow(c.Key, c.Name, r.Games, (double)r.Wins / r.Games, pick, banRate,
                                 r.Under25, r.From25To32, r.Over32));
        }
        return rows;
    }

    // ── bracket composition ─────────────────────────────────────────────────────

    /// <summary>Sums the member tiers of a bracket into one block, cached per bracket. Summing is
    /// exact because the file stores counts; there is no averaging of rates anywhere.</summary>
    private Block? Compose(string bracket)
    {
        lock (_gate)
        {
            if (_composed.TryGetValue(bracket, out var cached)) return cached;

            var members = new List<Block>();
            foreach (var tier in RecBrackets.TiersOf(bracket))
                if (_byTier.TryGetValue(tier, out var block)) members.Add(block);
            // An unknown slug (a bracket this build does not define) answers nothing rather than
            // quietly falling back to some other sample.
            if (members.Count == 0) return null;

            var sum = members.Count == 1 ? members[0] : Sum(members);
            _composed[bracket] = sum;
            return sum;
        }
    }

    private static Block Sum(List<Block> blocks)
    {
        var sum = new Block();
        var champs = new Dictionary<int, Champ>();
        foreach (var b in blocks)
        {
            sum.Matches += b.Matches;
            foreach (var (role, n) in b.RoleSlots)
                sum.RoleSlots[role] = sum.RoleSlots.TryGetValue(role, out int have) ? have + n : n;
            foreach (var c in b.Champions)
            {
                if (!champs.TryGetValue(c.Key, out var acc))
                {
                    acc = new Champ { Key = c.Key, Name = c.Name };
                    champs[c.Key] = acc;
                }
                acc.Games += c.Games;
                acc.Wins += c.Wins;
                acc.Bans += c.Bans;
                Add(ref acc.Under25, c.Under25);
                Add(ref acc.From25To32, c.From25To32);
                Add(ref acc.Over32, c.Over32);
                foreach (var (role, r) in c.Roles)
                {
                    acc.Roles.TryGetValue(role, out var have);
                    var merged = new RoleStat
                    {
                        Games = have.Games + r.Games,
                        Wins = have.Wins + r.Wins,
                        Under25 = have.Under25,
                        From25To32 = have.From25To32,
                        Over32 = have.Over32,
                    };
                    Add(ref merged.Under25, r.Under25);
                    Add(ref merged.From25To32, r.From25To32);
                    Add(ref merged.Over32, r.Over32);
                    acc.Roles[role] = merged;
                }
            }
        }
        sum.Champions.AddRange(champs.Values);
        return sum;
    }

    /// <summary>Accumulates a duration bucket. Rates cannot be averaged, so the running bucket
    /// carries games+wins and the rate is re-derived from the totals.</summary>
    private static void Add(ref Counted target, Counted add)
    {
        target.Games += add.Games;
        target.Wins += add.Wins;
    }

    // ── in-memory model ─────────────────────────────────────────────────────────

    /// <summary>Games/wins pair; converts to the public <see cref="DurationBucket"/> on read.</summary>
    private struct Counted
    {
        public int Games;
        public int Wins;
        public static implicit operator DurationBucket(Counted c)
            => c.Games > 0 ? new DurationBucket(c.Games, (double)c.Wins / c.Games) : default;
    }

    private struct RoleStat
    {
        public int Games;
        public int Wins;
        public Counted Under25, From25To32, Over32;
    }

    private sealed class Champ
    {
        public int Key;
        public string Name = "";
        public int Games, Wins, Bans;
        public Counted Under25, From25To32, Over32;
        public Dictionary<string, RoleStat> Roles = new();
    }

    private sealed class Block
    {
        public int Matches;
        public Dictionary<string, int> RoleSlots = new();
        public List<Champ> Champions = new();
    }

    // ── file model ──────────────────────────────────────────────────────────────

    private sealed class ChampDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("games")] public int Games { get; set; }
        [JsonPropertyName("wins")] public int Wins { get; set; }
        [JsonPropertyName("bans")] public int Bans { get; set; }
        [JsonPropertyName("banRate")] public double BanRate { get; set; }  // v1 only
        [JsonPropertyName("duration")] public Dictionary<string, int[]>? Duration { get; set; }
        /// <summary>Left as raw JSON because two shapes exist in the wild: the current
        /// <c>{ROLE: {games, wins, duration}}</c> and pre-loop-462 files' <c>{ROLE: [games, wins]}</c>.
        /// A stale file must cost the role filter, not the whole table.</summary>
        [JsonPropertyName("roles")] public Dictionary<string, JsonElement>? Roles { get; set; }
    }

    private class BlockDto
    {
        [JsonPropertyName("matches")] public int Matches { get; set; }
        [JsonPropertyName("roleSlots")] public Dictionary<string, int>? RoleSlots { get; set; }
        [JsonPropertyName("champions")] public Dictionary<string, ChampDto>? Champions { get; set; }
    }

    private sealed class RootDto : BlockDto
    {
        [JsonPropertyName("patch")] public string? Patch { get; set; }
        [JsonPropertyName("seedTiers")] public List<string>? SeedTiers { get; set; }
        [JsonPropertyName("byTier")] public Dictionary<string, BlockDto>? ByTier { get; set; }
    }

    private static Counted Bucket(Dictionary<string, int[]>? duration, string key)
    {
        if (duration is null || !duration.TryGetValue(key, out var gw) || gw.Length != 2 || gw[0] <= 0)
            return default;
        return new Counted { Games = gw[0], Wins = gw[1] };
    }

    private static Dictionary<string, RoleStat> ParseRoles(Dictionary<string, JsonElement>? roles)
    {
        var parsed = new Dictionary<string, RoleStat>();
        if (roles is null) return parsed;
        foreach (var (role, el) in roles)
        {
            try
            {
                if (el.ValueKind == JsonValueKind.Array)
                {
                    // legacy [games, wins]: no per-role duration curve was stored
                    if (el.GetArrayLength() != 2) continue;
                    int lg = el[0].GetInt32(), lw = el[1].GetInt32();
                    if (lg > 0) parsed[role] = new RoleStat { Games = lg, Wins = lw };
                    continue;
                }
                if (el.ValueKind != JsonValueKind.Object) continue;
                int g = el.TryGetProperty("games", out var gp) ? gp.GetInt32() : 0;
                int w = el.TryGetProperty("wins", out var wp) ? wp.GetInt32() : 0;
                if (g <= 0) continue;
                Dictionary<string, int[]>? dur = null;
                if (el.TryGetProperty("duration", out var dp) && dp.ValueKind == JsonValueKind.Object)
                    dur = dp.Deserialize<Dictionary<string, int[]>>();
                parsed[role] = new RoleStat
                {
                    Games = g,
                    Wins = w,
                    Under25 = Bucket(dur, "lt25"),
                    From25To32 = Bucket(dur, "b25to32"),
                    Over32 = Bucket(dur, "gt32"),
                };
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                // one malformed role entry must not cost the champion its row
            }
        }
        return parsed;
    }

    private static Block ToBlock(BlockDto dto)
    {
        var block = new Block { Matches = dto.Matches, RoleSlots = dto.RoleSlots ?? new() };
        if (dto.Champions is null) return block;
        foreach (var (keyText, c) in dto.Champions)
        {
            if (!int.TryParse(keyText, out int key) || c.Games <= 0) continue;
            block.Champions.Add(new Champ
            {
                Key = key,
                Name = c.Name ?? "",
                Games = c.Games,
                Wins = c.Wins,
                // v1 files stored only a ban RATE; recovering the count keeps every bracket
                // summable, at the cost of the rounding v1 already baked in.
                Bans = c.Bans > 0 ? c.Bans : (int)Math.Round(c.BanRate * dto.Matches),
                Under25 = Bucket(c.Duration, "lt25"),
                From25To32 = Bucket(c.Duration, "b25to32"),
                Over32 = Bucket(c.Duration, "gt32"),
                Roles = ParseRoles(c.Roles),
            });
        }
        // roleSlots is absent from pre-loop-462 files; without it a role pick rate has no honest
        // denominator, so derive it from the rows we do have rather than printing a wrong one.
        if (block.RoleSlots.Count == 0)
        {
            var slots = new Dictionary<string, int>();
            foreach (var c in block.Champions)
                foreach (var (role, r) in c.Roles)
                    slots[role] = slots.TryGetValue(role, out int n) ? n + r.Games : r.Games;
            block.RoleSlots = slots;
        }
        return block;
    }

    private void Load()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (_recRoot.Length == 0) return;
                if (FileRecommendationSource.ResolveLatestPatchDir(_recRoot) is not { } patchDir) return;
                string path = Path.Combine(patchDir, "stats", "tiers.json");
                if (!File.Exists(path)) return;
                _updatedAt = File.GetLastWriteTime(path);

                var root = JsonSerializer.Deserialize<RootDto>(File.ReadAllText(path));
                if (root is null) return;
                _patch = root.Patch ?? "";

                var byTier = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
                if (root.ByTier is { Count: > 0 })
                {
                    foreach (var (tier, dto) in root.ByTier) byTier[tier] = ToBlock(dto);
                    _seedTiers = root.SeedTiers is { Count: > 0 }
                        ? root.SeedTiers.FindAll(byTier.ContainsKey)
                        : new List<string>(byTier.Keys);
                }
                else if (root.Champions is { Count: > 0 })
                {
                    // v1 layout: one unlabelled block at the root. It is attributable only if the
                    // file names its tier; otherwise it becomes an UNKNOWN bucket, which belongs
                    // to no bracket and so is shown by none of them.
                    string tier = root.SeedTiers is { Count: 1 } ? root.SeedTiers[0] : "UNKNOWN";
                    byTier[tier] = ToBlock(root);
                    _seedTiers = new[] { tier };
                }

                _byTier = byTier;
                foreach (var block in byTier.Values) _totalMatches += block.Matches;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // failure posture: table simply empty
            }
        }
    }
}
