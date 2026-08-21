using System.Text.Json.Serialization;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Raw DTOs mirroring Riot Data Dragon's on-disk JSON schema exactly (field names
/// verified against a live fetch of champion.json / champion/{id}.json / item.json /
/// runesReforged.json on patch 16.13.1). These are deserialization-only structures —
/// never exposed outside <see cref="Overlay.Core.ChampionDb"/>; the public API maps
/// them into the M11 spec's ChampionData/ItemData/RuneData POCOs.
/// </summary>
internal sealed class DDragonVersionsResponse
{
    // versions.json is a bare JSON array of strings; parsed directly as List&lt;string&gt;.
}

internal sealed class DDragonChampionListFile
{
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("data")] public Dictionary<string, DDragonChampionSummary> Data { get; init; } = new();
}

/// <summary>Entry shape from the bulk champion.json (used only to enumerate champion ids;
/// per-champion skill detail requires the champion/{id}.json detail file).</summary>
internal sealed class DDragonChampionSummary
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
}

internal sealed class DDragonChampionDetailFile
{
    [JsonPropertyName("data")] public Dictionary<string, DDragonChampionDetail> Data { get; init; } = new();
}

internal sealed class DDragonChampionDetail
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("partype")] public string Partype { get; init; } = string.Empty;
    [JsonPropertyName("stats")] public DDragonStats Stats { get; init; } = new();
    [JsonPropertyName("spells")] public List<DDragonSpell> Spells { get; init; } = new();
    [JsonPropertyName("passive")] public DDragonPassive Passive { get; init; } = new();
}

internal sealed class DDragonStats
{
    [JsonPropertyName("hp")] public double Hp { get; init; }
    [JsonPropertyName("hpperlevel")] public double HpPerLevel { get; init; }
    [JsonPropertyName("mp")] public double Mp { get; init; }
    [JsonPropertyName("mpperlevel")] public double MpPerLevel { get; init; }
    [JsonPropertyName("movespeed")] public double MoveSpeed { get; init; }
    [JsonPropertyName("armor")] public double Armor { get; init; }
    [JsonPropertyName("armorperlevel")] public double ArmorPerLevel { get; init; }
    [JsonPropertyName("spellblock")] public double SpellBlock { get; init; }
    [JsonPropertyName("spellblockperlevel")] public double SpellBlockPerLevel { get; init; }
    [JsonPropertyName("attackrange")] public double AttackRange { get; init; }
    [JsonPropertyName("hpregen")] public double HpRegen { get; init; }
    [JsonPropertyName("hpregenperlevel")] public double HpRegenPerLevel { get; init; }
    [JsonPropertyName("mpregen")] public double MpRegen { get; init; }
    [JsonPropertyName("mpregenperlevel")] public double MpRegenPerLevel { get; init; }
    [JsonPropertyName("attackdamage")] public double AttackDamage { get; init; }
    [JsonPropertyName("attackdamageperlevel")] public double AttackDamagePerLevel { get; init; }
    [JsonPropertyName("attackspeed")] public double AttackSpeed { get; init; }
    [JsonPropertyName("attackspeedperlevel")] public double AttackSpeedPerLevel { get; init; }
}

internal sealed class DDragonSpell
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("tooltip")] public string Tooltip { get; init; } = string.Empty;
    [JsonPropertyName("maxrank")] public int MaxRank { get; init; }
    [JsonPropertyName("cooldown")] public List<double> Cooldown { get; init; } = new();
    [JsonPropertyName("cost")] public List<double> Cost { get; init; } = new();
    [JsonPropertyName("costType")] public string CostType { get; init; } = string.Empty;
}

internal sealed class DDragonPassive
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
}

internal sealed class DDragonItemListFile
{
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
    [JsonPropertyName("data")] public Dictionary<string, DDragonItem> Data { get; init; } = new();
}

internal sealed class DDragonItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = new();
    [JsonPropertyName("stats")] public Dictionary<string, double> Stats { get; init; } = new();
    [JsonPropertyName("gold")] public DDragonItemGold Gold { get; init; } = new();
    // from/into are omitted entirely for basic items, so they deserialize to null.
    [JsonPropertyName("from")] public List<string>? From { get; init; }
    [JsonPropertyName("into")] public List<string>? Into { get; init; }
    // maps is keyed by map id (e.g. "11" = Summoner's Rift); missing/absent for some legacy entries.
    [JsonPropertyName("maps")] public Dictionary<string, bool>? Maps { get; init; }
}

internal sealed class DDragonItemGold
{
    [JsonPropertyName("total")] public int Total { get; init; }
    [JsonPropertyName("purchasable")] public bool Purchasable { get; init; }
}

/// <summary>runesReforged.json is a bare JSON array of trees (no wrapper object).</summary>
internal sealed class DDragonRuneTree
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("slots")] public List<DDragonRuneSlot> Slots { get; init; } = new();
}

internal sealed class DDragonRuneSlot
{
    [JsonPropertyName("runes")] public List<DDragonRune> Runes { get; init; } = new();
}

internal sealed class DDragonRune
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>Relative icon path (e.g. "perk-images/Styles/Domination/CheapShot/CheapShot.png"),
    /// served un-versioned from Data Dragon at <c>cdn/img/{icon}</c>. Added for the M04 icon-tile
    /// rune picker (loop 38 pending change item 5).</summary>
    [JsonPropertyName("icon")] public string Icon { get; init; } = string.Empty;
}
