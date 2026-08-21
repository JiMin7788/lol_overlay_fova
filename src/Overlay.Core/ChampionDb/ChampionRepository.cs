using System.Text.Json;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// M11 public API: ChampionRepository.get/getSkill/getSpecialProperty. Populated once
/// at startup via <see cref="Initialize"/> (or <see cref="InitializeAsync"/>), then
/// served entirely from the in-memory cache — no repeated I/O per M11 Agent
/// Implementation Notes ("앱 시작 시 1회 로드 후 메모리 캐싱, 이후 요청은 캐시에서 응답").
/// </summary>
public static class ChampionRepository
{
    private static Dictionary<string, ChampionData> _champions = new();
    private static bool _initialized;

    /// <summary>True once <see cref="Initialize"/>/<see cref="InitializeAsync"/> has
    /// populated the in-memory cache.</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>Downloads (if not already cached on disk) and parses Data Dragon
    /// champion data for the given champion ids, merges CommunityDragon BIN data
    /// (AD/AP/other spell coefficients — see <see cref="ChampionBinParser"/>) via
    /// <paramref name="communityDragonClient"/>, then merges Special Property overrides
    /// from <paramref name="specialPropertiesDir"/>. Call once at app startup.</summary>
    public static async Task InitializeAsync(
        IEnumerable<string> championIds,
        DDragonClient client,
        string version,
        string ddragonCacheDir,
        CommunityDragonClient communityDragonClient,
        string? specialPropertiesDir = null,
        CancellationToken ct = default)
    {
        var champions = new Dictionary<string, ChampionData>(StringComparer.OrdinalIgnoreCase);
        foreach (var championId in championIds)
        {
            var path = Path.Combine(ddragonCacheDir, "champion", $"{championId}.json");
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var champion = DDragonParser.ParseChampion(json);

            var binPath = await communityDragonClient.EnsureCachedAsync(championId, ct).ConfigureAwait(false);
            var binJson = await File.ReadAllTextAsync(binPath, ct).ConfigureAwait(false);
            MergeBinData(champion, ChampionBinParser.ParseChampion(champion.Id, binJson));

            champions[champion.Id] = champion;
        }

        if (specialPropertiesDir is not null)
        {
            SpecialPropertyLoader.LoadAndMerge(specialPropertiesDir, champions);
        }

        _champions = champions;
        _initialized = true;
    }

    /// <summary>Loads the FULL champion roster from the on-disk offline cache (no network).
    /// Every champion in the Data Dragon summary (<paramref name="ddragonCacheDir"/>/champion.json)
    /// that has a cached CommunityDragon BIN under <paramref name="communityDragonCacheDir"/> is
    /// parsed and served. Champions whose per-champion Data Dragon detail file is also cached (the
    /// originally sampled set) keep full skill metadata (names/cooldowns/costs) merged with BIN
    /// spell data; the rest are built from <see cref="ChampionSummary"/> base resistances plus the
    /// BIN's Q/W/E/R DataValues/SpellCalculations — enough for the damage engine. A single champion
    /// whose BIN is missing or fails to parse is skipped (never aborts the whole load). All reads
    /// are synchronous file I/O; call it off the UI thread (e.g. via Task.Run).</summary>
    public static void InitializeFromCache(
        string ddragonCacheDir,
        string communityDragonCacheDir,
        string? specialPropertiesDir = null)
    {
        var champions = new Dictionary<string, ChampionData>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ddragonCacheDir, "champion.json")));
        var data = doc.RootElement.GetProperty("data");

        foreach (var champ in data.EnumerateObject())
        {
            // The Data Dragon summary's property name IS the champion id, whose lower-cased form
            // is the CommunityDragon file name AND whose original casing is the BIN's internal
            // "Characters/{id}/Spells/..." prefix (verified incl. exceptions like "MonkeyKing").
            var id = champ.Name;
            var binPath = Path.Combine(communityDragonCacheDir, $"{id.ToLowerInvariant()}.bin.json");
            if (!File.Exists(binPath)) continue;

            Dictionary<string, BinSkillData> binSkills;
            try
            {
                binSkills = ChampionBinParser.ParseChampion(id, File.ReadAllText(binPath));
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
            {
                continue; // malformed/unreadable BIN — skip this champion, keep loading the rest.
            }

            var detailPath = Path.Combine(ddragonCacheDir, "champion", $"{id}.json");
            ChampionData champion;
            if (File.Exists(detailPath))
            {
                champion = DDragonParser.ParseChampion(File.ReadAllText(detailPath));
                MergeBinData(champion, binSkills);
            }
            else
            {
                var name = champ.Value.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
                champion = BuildFromSummary(id, name, binSkills);
            }
            champions[champion.Id] = champion;
        }

        if (specialPropertiesDir is not null)
        {
            SpecialPropertyLoader.LoadAndMerge(specialPropertiesDir, champions);
        }

        _champions = champions;
        _initialized = true;
    }

    /// <summary>Builds a champion that has a BIN but no cached Data Dragon detail file: base
    /// resistances come from the all-champion <see cref="ChampionSummary"/> table, and each
    /// Q/W/E/R slot carries the BIN's raw DataValues/SpellCalculations. Skill display names,
    /// cooldowns and costs are unavailable without the detail file and are left empty — the
    /// damage engine consumes DataValues/SpellCalculations, not those fields.</summary>
    private static ChampionData BuildFromSummary(string id, string name, Dictionary<string, BinSkillData> binSkills)
    {
        var stats = ChampionSummary.Get(id);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
        {
            skills[slot] = new SkillData
            {
                Key = slot,
                DataValues = bin.DataValues,
                SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                DamageType = DamageType.MIXED,
                MaxRank = slot == "R" ? 3 : 5,
            };
        }

        return new ChampionData
        {
            Id = id,
            Name = name,
            BaseStats = new ChampionBaseStats
            {
                Hp = stats?.Hp ?? 0,
                Armor = stats?.Armor ?? 0,
                Mr = stats?.Mr ?? 0,
                AttackRange = stats?.AttackRange ?? 0,
                // §48: base attacks/sec, the divisor for SkillDamage's bonus-AS ratio (mStat=4).
                AttackSpeed = stats?.AttackSpeed ?? 0,
            },
            StatsPerLevel = new ChampionStatsPerLevel
            {
                Hp = stats?.HpPerLevel ?? 0,
                Armor = stats?.ArmorPerLevel ?? 0,
                Mr = stats?.MrPerLevel ?? 0,
            },
            Skills = skills,
        };
    }

    /// <summary>Merges CommunityDragon BIN DataValues/SpellCalculations into the matching
    /// Q/W/E/R skill slots of an already Data-Dragon-parsed <see cref="ChampionData"/>.
    /// Skills only in one source are left as-is (e.g. BIN has no Passive slot here).</summary>
    private static void MergeBinData(ChampionData champion, Dictionary<string, BinSkillData> binSkills)
    {
        foreach (var (slot, binSkill) in binSkills)
        {
            if (champion.Skills.TryGetValue(slot, out var skill))
            {
                champion.Skills[slot] = new SkillData
                {
                    Key = skill.Key,
                    Name = skill.Name,
                    Cooldown = skill.Cooldown,
                    Mana = skill.Mana,
                    DataValues = binSkill.DataValues,
                    SpellCalculations = binSkill.SpellCalculations,
                    EffectAmounts = binSkill.EffectAmounts,
                    DamageType = skill.DamageType,
                    MaxRank = skill.MaxRank,
                };
            }
            else
            {
                // (M22) An EXTRA BIN spell (transform/stance/weapon/sub-spell, keyed by its leaf
                // name — see ChampionBinParser.ParseExtraSpells) that has no Data Dragon slot. Add it
                // so a curated slot's SkillHit.BinSpell can resolve calcs against it. Not a Q/W/E/R
                // ability, so MaxRank defaults to the R/non-R convention only as a rank ceiling.
                champion.Skills[slot] = new SkillData
                {
                    Key = slot,
                    DataValues = binSkill.DataValues,
                    SpellCalculations = binSkill.SpellCalculations,
                    EffectAmounts = binSkill.EffectAmounts,
                    DamageType = DamageType.MIXED,
                    MaxRank = slot.Equals("R", StringComparison.OrdinalIgnoreCase) ? 3 : 5,
                };
            }
        }
    }

    /// <summary>Synchronous initialization directly from already-parsed champions
    /// (used by tests/fixtures, and by callers that parse files themselves).</summary>
    public static void Initialize(IEnumerable<ChampionData> champions, string? specialPropertiesDir = null)
    {
        var dict = champions.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        if (specialPropertiesDir is not null)
        {
            SpecialPropertyLoader.LoadAndMerge(specialPropertiesDir, dict);
        }
        _champions = dict;
        _initialized = true;
    }

    /// <summary>Every champion id currently loaded into the cache, sorted alphabetically.
    /// After <see cref="InitializeFromCache"/> this is the full roster (all cached champions),
    /// so callers (e.g. the combo editor's champion picker) can offer combos for ANY cached
    /// champion, not just the originally sampled set. Empty until initialized.</summary>
    public static IReadOnlyList<string> LoadedIds
        => _champions.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Returns the champion, or null if unknown (never throws — callers should
    /// treat a missing champion as "not yet in the loaded sample set").</summary>
    public static ChampionData? Get(string championId)
        => _champions.TryGetValue(championId, out var champion) ? champion : null;

    /// <summary>Returns the given skill slot ("P"/"Q"/"W"/"E"/"R") for a champion, or
    /// null if the champion or slot is unknown.</summary>
    public static SkillData? GetSkill(string championId, string skillKey)
        => Get(championId)?.Skills.TryGetValue(skillKey, out var skill) == true ? skill : null;

    /// <summary>Returns a merged Special Property override by key, or null if the
    /// champion has no such property.</summary>
    public static ChampionSpecialProperty? GetSpecialProperty(string championId, string key)
        => Get(championId)?.SpecialProperties.TryGetValue(key, out var prop) == true ? prop : null;

    /// <summary>Test-only reset hook so unit tests don't leak state between cases.</summary>
    internal static void ResetForTests()
    {
        _champions = new Dictionary<string, ChampionData>();
        _initialized = false;
    }
}
