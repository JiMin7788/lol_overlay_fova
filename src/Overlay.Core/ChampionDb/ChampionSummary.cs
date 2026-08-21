using System.Text.Json;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Lightweight, load-once base-stats table for ALL champions, parsed from the already-cached
/// Data Dragon summary <c>data/ddragon/{version}/champion.json</c> (the same file M11 already
/// bundles). Unlike <see cref="ChampionRepository"/> — which holds full BIN spell data for
/// only the 5 sampled champions — this covers every champion in the summary (all ~170+),
/// exposing just the fields the combo defender needs: base HP/armor/MR and their per-level
/// growth, plus the champion's display <c>name</c>.
///
/// Purpose (Part C of the combo-damage fix): the Live Client scoreboard reports enemies by
/// DISPLAY name (e.g. "Dr. Mundo"), so any enemy — not just the cached 5 — can be matched
/// here to real resistances, instead of falling back to a 0-armor/0-MR defender.
///
/// P1 compliance: the summary is Riot's own public Data Dragon file. No inference beyond the
/// published base + per-level values.
/// </summary>
public static class ChampionSummary
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static Dictionary<string, ChampionSummaryStats>? _byName;
    // M33: numeric Data Dragon key ("key": "266") -> (english id, display name). Champ select
    // reports champions by this numeric key; built in the same single summary parse.
    private static Dictionary<int, (string Id, string Name)>? _byKey;
    private static readonly object Gate = new();

    /// <summary>Returns the base/per-level resistance stats for a champion matched by its
    /// DISPLAY name (as the scoreboard reports it, e.g. "Ahri"/"Dr. Mundo") or its Data
    /// Dragon id (e.g. "DrMundo"), or <c>null</c> if unmatched or the summary can't be
    /// loaded. Never throws.</summary>
    public static ChampionSummaryStats? Get(string championName)
    {
        if (string.IsNullOrEmpty(championName)) return null;
        var map = Load();
        return map is not null && map.TryGetValue(championName, out var stats) ? stats : null;
    }

    private static Dictionary<string, ChampionSummaryStats>? Load()
    {
        lock (Gate)
        {
            if (_byName is not null) return _byName;

            try
            {
                var path = FindSummaryPath();
                if (path is null) { _byName = new(StringComparer.OrdinalIgnoreCase); return _byName; }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object)
                {
                    _byName = new(StringComparer.OrdinalIgnoreCase);
                    return _byName;
                }

                var map = new Dictionary<string, ChampionSummaryStats>(StringComparer.OrdinalIgnoreCase);
                var keyMap = new Dictionary<int, (string Id, string Name)>();
                foreach (var champ in data.EnumerateObject())
                {
                    var e = champ.Value;

                    // M33: numeric champion key mapping (champ select speaks numeric ids).
                    if (e.TryGetProperty("key", out var keyEl)
                        && int.TryParse(keyEl.GetString(), out int numericKey) && numericKey > 0)
                    {
                        string displayName = e.TryGetProperty("name", out var dn)
                            ? dn.GetString() ?? champ.Name : champ.Name;
                        keyMap[numericKey] = (champ.Name, displayName);
                    }

                    if (!e.TryGetProperty("stats", out var s)) continue;

                    var stats = new ChampionSummaryStats(
                        Hp: GetD(s, "hp"),
                        HpPerLevel: GetD(s, "hpperlevel"),
                        Armor: GetD(s, "armor"),
                        ArmorPerLevel: GetD(s, "armorperlevel"),
                        Mr: GetD(s, "spellblock"),
                        MrPerLevel: GetD(s, "spellblockperlevel"),
                        AttackRange: GetD(s, "attackrange"),
                        // Base attacks/sec — the divisor for the §48 bonus-AS ratio
                        // (bonusAS = live total / base − 1) in SkillDamage's mStat=4 mapping.
                        AttackSpeed: GetD(s, "attackspeed"));

                    // Key by both the Data Dragon id (the JSON property name, e.g. "DrMundo")
                    // and the display name (e.g. "Dr. Mundo"); the scoreboard uses the display
                    // name, but keying both makes the match robust to either source.
                    map[champ.Name] = stats;
                    if (e.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { Length: > 0 } display)
                        map[display] = stats;
                }
                _byName = map;
                _byKey = keyMap;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _byName = new(StringComparer.OrdinalIgnoreCase);
                _byKey = new();
            }
            return _byName;
        }
    }

    /// <summary>Locates the bundled <c>champion.json</c> under <c>data/ddragon/{version}/</c>.
    /// The version dir is discovered rather than hardcoded so a patch bump needs no code
    /// change; the newest (last) matching file wins if more than one version is cached.</summary>
    private static string? FindSummaryPath()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "data", "ddragon");
        if (!Directory.Exists(root)) return null;
        var files = Directory.GetFiles(root, "champion.json", SearchOption.AllDirectories);
        if (files.Length == 0) return null;
        Array.Sort(files, StringComparer.Ordinal);
        return files[^1];
    }

    private static double GetD(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

    /// <summary>Loop-38 investigation: a live combo trigger against a visible, named target (e.g. an
    /// "Ahri" bot) reported <c>ChampionRepository.LoadedIds.Count == 173</c> (the FULL expected roster
    /// loaded, nothing skipped) yet <c>ChampionRepository.Get</c>/<c>ChampionSummary.Get</c> both still
    /// failed to match the target's resolved name — only explainable if the resolved name string
    /// itself isn't the English Data Dragon id both tables are keyed by. Leading hypothesis: the Live
    /// Client API returned the champion's KOREAN client-language display name (e.g. "아리") rather than
    /// the English id ("Ahri") for this row.
    ///
    /// Loop-43 continuation (confirmed, not just hypothesized): a real-game retest reported EVERY
    /// enemy champion failing armor/MR lookup, with the on-screen diagnostic showing genuine Korean
    /// names (e.g. "베인"/Vayne, "마오카이"/Maokai) — names far outside this original 5-entry table,
    /// proving the Korean-name issue affects the full roster, not just Ahri. This 5-entry table is now
    /// a last-resort fallback only; the primary path is the dynamic, full-173-champion reverse lookup
    /// via <see cref="ChampionLocalizationRepository.GetEnglishId"/>, sourced from the SAME
    /// CommunityDragon ko_kr champion-summary data already fetched for M04 display localization (no
    /// new network fetch needed) — see <see cref="ResolveKoreanName"/>.</summary>
    private static readonly Dictionary<string, string> KoreanNameToId = new(StringComparer.Ordinal)
    {
        ["아트록스"] = "Aatrox",
        ["아리"] = "Ahri",
        ["애니"] = "Annie",
        ["제드"] = "Zed",
        ["징크스"] = "Jinx",
    };

    /// <summary>Returns the English Data Dragon id for a known Korean champion display name, or null
    /// if <paramref name="name"/> isn't a recognized Korean name (including when it's already an
    /// English id — this is a targeted reverse-translation step, not a general name resolver).
    /// Checks the dynamic, full-roster <see cref="ChampionLocalizationRepository"/> first (covers all
    /// ~173 champions once initialized); falls back to the tiny static <see cref="KoreanNameToId"/>
    /// table only when that repository isn't initialized yet (e.g. very early during startup).</summary>
    public static string? ResolveKoreanName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (ChampionLocalizationRepository.IsInitialized
            && ChampionLocalizationRepository.GetEnglishId(name) is { } dynamicId)
            return dynamicId;
        return KoreanNameToId.TryGetValue(name, out var id) ? id : null;
    }

    /// <summary>M33: resolves a numeric Data Dragon champion key (the id champ select reports,
    /// e.g. 266) to the English id + display name, or null when unknown / summary unavailable.
    /// Never throws.</summary>
    public static (string Id, string Name)? GetByNumericKey(int championKey)
    {
        if (championKey <= 0) return null;
        Load();
        lock (Gate)
            return _byKey is not null && _byKey.TryGetValue(championKey, out var entry) ? entry : null;
    }

    /// <summary>Test-only reset for the lazy cache.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) { _byName = null; _byKey = null; }
    }
}

/// <summary>Base + per-level resistance/health stats for one champion from the Data Dragon
/// summary. Resistances at a given level use Riot's real super-linear growth curve
/// (<see cref="LevelGrowth.Stat"/>), NOT naive linear interpolation — a user-reported overlay-
/// vs-real-game gap (armor 101 shown vs 97 actual at the same build) traced to this class (and
/// <see cref="Combo.ComboRunner.TryResolveBase"/>'s parallel inline copy) using plain
/// <c>base + perLevel*(level-1)</c> instead. <see cref="AttackRange"/> is not per-level (Data
/// Dragon publishes one constant value per champion) — used for melee/ranged item-proc
/// classification (e.g. Blade of the Ruined King, Titanic Hydra), never for damage math.</summary>
public sealed record ChampionSummaryStats(
    double Hp,
    double HpPerLevel,
    double Armor,
    double ArmorPerLevel,
    double Mr,
    double MrPerLevel,
    double AttackRange = 0,
    double AttackSpeed = 0)
{
    public double HpAt(int level) => LevelGrowth.Stat(Hp, HpPerLevel, level);
    public double ArmorAt(int level) => LevelGrowth.Stat(Armor, ArmorPerLevel, level);
    public double MrAt(int level) => LevelGrowth.Stat(Mr, MrPerLevel, level);
}
