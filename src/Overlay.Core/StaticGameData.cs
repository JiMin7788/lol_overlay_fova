using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Core;

/// <summary>
/// In-memory, deserialized view of the versioned static-game-data JSON
/// (champion base/per-level stats, ability burst profiles, item stat
/// contributions). Loaded ONCE per session by <see cref="StaticGameDataLoader"/>
/// and treated as immutable thereafter (data-algo SKILL "Static data" rule).
///
/// This is reference/static data only — it is NOT produced by the poller and is
/// never on the 0.5s hot path. Per Hard Rule #4 every constant the KILLABLE calc
/// uses lives in the JSON file, never inline in algorithm code.
/// </summary>
public sealed class StaticGameData
{
    /// <summary>Riot patch these constants were authored against (e.g. "14.12").</summary>
    [JsonPropertyName("patch")]
    public string Patch { get; init; } = "unknown";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("champions")]
    public Dictionary<string, ChampionStaticData> Champions { get; init; } = new();

    [JsonPropertyName("items")]
    public Dictionary<string, ItemStaticData> Items { get; init; } = new();

    /// <summary>Key used for the generic fallback champion profile.</summary>
    public const string DefaultChampionKey = "_default";

    /// <summary>Look up a champion by its Live Client <c>championName</c>, falling
    /// back to the <c>_default</c> profile (degrades gracefully instead of
    /// throwing for champions not yet seeded in the data file).</summary>
    public ChampionStaticData GetChampionOrDefault(string? championName)
    {
        if (!string.IsNullOrEmpty(championName) &&
            Champions.TryGetValue(championName, out var data))
        {
            return data;
        }
        return Champions.TryGetValue(DefaultChampionKey, out var fallback)
            ? fallback
            : ChampionStaticData.Empty;
    }

    /// <summary>Resolve an item id to its modeled stat contribution, or null if the
    /// id is unknown/irrelevant to the burst calc (treated as zero contribution).</summary>
    public ItemStaticData? GetItem(int itemId)
        => Items.TryGetValue(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var item)
            ? item
            : null;
}

/// <summary>Static stat block + ability burst profile for one champion.</summary>
public sealed class ChampionStaticData
{
    public static readonly ChampionStaticData Empty = new();

    [JsonPropertyName("resource")] public string Resource { get; init; } = "Mana";

    [JsonPropertyName("baseHealth")] public double BaseHealth { get; init; }
    [JsonPropertyName("healthPerLevel")] public double HealthPerLevel { get; init; }
    [JsonPropertyName("baseArmor")] public double BaseArmor { get; init; }
    [JsonPropertyName("armorPerLevel")] public double ArmorPerLevel { get; init; }
    [JsonPropertyName("baseMagicResist")] public double BaseMagicResist { get; init; }
    [JsonPropertyName("magicResistPerLevel")] public double MagicResistPerLevel { get; init; }
    [JsonPropertyName("baseAttackDamage")] public double BaseAttackDamage { get; init; }
    [JsonPropertyName("attackDamagePerLevel")] public double AttackDamagePerLevel { get; init; }
    [JsonPropertyName("baseMoveSpeed")] public double BaseMoveSpeed { get; init; }

    [JsonPropertyName("abilities")] public AbilityComponent[] Abilities { get; init; }
        = Array.Empty<AbilityComponent>();
}

/// <summary>One damage component of a champion's max-combo rotation.</summary>
public sealed class AbilityComponent
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>"Physical", "Magic", or "True" — selects which target resist applies.</summary>
    [JsonPropertyName("type")] public string Type { get; init; } = "Magic";

    /// <summary>Flat (rank-maxed) base damage of the component.</summary>
    [JsonPropertyName("flat")] public double Flat { get; init; }

    /// <summary>Coefficient on attacker total Ability Power.</summary>
    [JsonPropertyName("perAp")] public double PerAp { get; init; }

    /// <summary>Coefficient on attacker total Attack Damage.</summary>
    [JsonPropertyName("perAd")] public double PerAd { get; init; }

    /// <summary>Coefficient on attacker BONUS Attack Damage (total - base).</summary>
    [JsonPropertyName("perBonusAd")] public double PerBonusAd { get; init; }

    /// <summary>Execute-style multiplier applied to the sum of all OTHER components
    /// (e.g. Zed R amplifies the combo). 0 = not an amplifier.</summary>
    [JsonPropertyName("percentOfComboDamage")] public double PercentOfComboDamage { get; init; }
}

/// <summary>Item stat contributions relevant to the burst calc.</summary>
public sealed class ItemStaticData
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    [JsonPropertyName("attackDamage")] public double AttackDamage { get; init; }
    [JsonPropertyName("abilityPower")] public double AbilityPower { get; init; }
    [JsonPropertyName("armor")] public double Armor { get; init; }
    [JsonPropertyName("magicResist")] public double MagicResist { get; init; }
    [JsonPropertyName("health")] public double Health { get; init; }

    /// <summary>Lethality (converted to flat armor pen vs target level by the calc).</summary>
    [JsonPropertyName("lethality")] public double Lethality { get; init; }
    [JsonPropertyName("flatMagicPen")] public double FlatMagicPen { get; init; }
    /// <summary>Percent armor pen as a fraction (0.35 = 35%).</summary>
    [JsonPropertyName("percentArmorPen")] public double PercentArmorPen { get; init; }
    /// <summary>Percent magic pen as a fraction (0.40 = 40%).</summary>
    [JsonPropertyName("percentMagicPen")] public double PercentMagicPen { get; init; }
}

/// <summary>
/// Loads <see cref="StaticGameData"/> from the bundled/patch-versioned JSON file.
/// Call once per session and cache the result (data-algo SKILL "Static data").
/// </summary>
public static class StaticGameDataLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Default on-disk location relative to the app base directory.</summary>
    public static string DefaultDataPath
        => Path.Combine(AppContext.BaseDirectory, "Data", "static-game-data.json");

    /// <summary>Load and deserialize from <paramref name="path"/> (defaults to the
    /// bundled file). Throws if the file is missing or malformed — this is a
    /// session-startup operation, not the hot path, so failing loud is correct.</summary>
    public static StaticGameData Load(string? path = null)
    {
        path ??= DefaultDataPath;
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<StaticGameData>(stream, Options);
        return data ?? throw new InvalidDataException($"static game data at '{path}' deserialized to null");
    }

    /// <summary>Deserialize from an already-read UTF-8 JSON string (testing/embedding).</summary>
    public static StaticGameData LoadFromJson(string json)
    {
        var data = JsonSerializer.Deserialize<StaticGameData>(json, Options);
        return data ?? throw new InvalidDataException("static game data JSON deserialized to null");
    }
}
