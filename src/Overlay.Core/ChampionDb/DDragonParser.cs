using System.Text.Json;
using System.Text.RegularExpressions;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Pure parsing: turns raw Data Dragon JSON (already on disk, see <see cref="DDragonClient"/>)
/// into the M11 public POCOs. No network/file I/O beyond the streams handed in — kept
/// separate from <see cref="DDragonClient"/> so parsing logic can be unit-tested against
/// fixture JSON without a network call.
/// </summary>
public static class DDragonParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ChampionData ParseChampion(string championDetailJson)
    {
        var file = JsonSerializer.Deserialize<DDragonChampionDetailFile>(championDetailJson, Options)
            ?? throw new InvalidDataException("champion detail JSON deserialized to null");
        var raw = file.Data.Values.FirstOrDefault()
            ?? throw new InvalidDataException("champion detail JSON contained no champion entry");

        var skills = new Dictionary<string, SkillData>
        {
            ["P"] = new SkillData
            {
                Key = "P",
                Name = raw.Passive.Name,
                Cooldown = Array.Empty<double>(),
                Mana = Array.Empty<double>(),
                DamageType = DamageType.MIXED,
                MaxRank = 1,
            },
        };

        string[] slotKeys = { "Q", "W", "E", "R" };
        for (var i = 0; i < raw.Spells.Count && i < slotKeys.Length; i++)
        {
            var spell = raw.Spells[i];
            skills[slotKeys[i]] = new SkillData
            {
                Key = slotKeys[i],
                Name = spell.Name,
                Cooldown = spell.Cooldown.ToArray(),
                Mana = spell.Cost.ToArray(),
                // AD/AP ratio coefficients come from CommunityDragon BIN data
                // (ChampionBinParser/FormulaInterpreter), merged in by
                // ChampionRepository.InitializeAsync — Data Dragon's spell.vars is empty
                // on current patches for all champions checked (see M11 Changelog v2.0).
                DamageType = GuessDamageType(spell.Tooltip),
                MaxRank = spell.MaxRank,
            };
        }

        return new ChampionData
        {
            Id = raw.Id,
            Name = raw.Name,
            BaseStats = new ChampionBaseStats
            {
                Hp = raw.Stats.Hp,
                Armor = raw.Stats.Armor,
                Mr = raw.Stats.SpellBlock,
                Ad = raw.Stats.AttackDamage,
                AttackSpeed = raw.Stats.AttackSpeed,
                Ms = raw.Stats.MoveSpeed,
                Mp = raw.Stats.Mp,
                HpRegen = raw.Stats.HpRegen,
                MpRegen = raw.Stats.MpRegen,
                AttackRange = raw.Stats.AttackRange,
            },
            StatsPerLevel = new ChampionStatsPerLevel
            {
                Hp = raw.Stats.HpPerLevel,
                Armor = raw.Stats.ArmorPerLevel,
                Mr = raw.Stats.SpellBlockPerLevel,
                Ad = raw.Stats.AttackDamagePerLevel,
                AttackSpeed = raw.Stats.AttackSpeedPerLevel,
                HpRegen = raw.Stats.HpRegenPerLevel,
                MpRegen = raw.Stats.MpRegenPerLevel,
            },
            Skills = skills,
        };
    }

    /// <summary>Best-effort damage-type classification from the Data Dragon tooltip's
    /// markup tags (&lt;physicalDamage&gt;, &lt;magicDamage&gt;, &lt;trueDamage&gt;).
    /// This is a heuristic, not an authoritative field. Falls back to MIXED when no tag
    /// is found or multiple distinct tags appear.</summary>
    private static DamageType GuessDamageType(string tooltip)
    {
        var hasPhysical = tooltip.Contains("physicalDamage", StringComparison.OrdinalIgnoreCase);
        var hasMagic = tooltip.Contains("magicDamage", StringComparison.OrdinalIgnoreCase);
        var hasTrue = tooltip.Contains("trueDamage", StringComparison.OrdinalIgnoreCase);

        var count = (hasPhysical ? 1 : 0) + (hasMagic ? 1 : 0) + (hasTrue ? 1 : 0);
        if (count != 1) return DamageType.MIXED;
        if (hasPhysical) return DamageType.PHYSICAL;
        if (hasMagic) return DamageType.MAGIC;
        return DamageType.TRUE;
    }

    /// <param name="itemListJson">Primary (English) Data Dragon item.json — required, same as before.</param>
    /// <param name="itemListJsonKo">Optional Korean locale-variant item.json (Part 3 — see
    /// <see cref="DDragonClient.EnsureCachedAsync"/>'s <c>item.ko_KR.json</c> fetch), used only to
    /// populate <see cref="ItemData.NameKo"/>. Defaults to null so every existing caller/test that
    /// only has the English file keeps compiling and behaving exactly as before.</param>
    public static IReadOnlyList<ItemData> ParseItems(string itemListJson, string? itemListJsonKo = null)
    {
        var file = JsonSerializer.Deserialize<DDragonItemListFile>(itemListJson, Options)
            ?? throw new InvalidDataException("item.json deserialized to null");
        var koNames = ParseItemNames(itemListJsonKo);

        var result = new List<ItemData>(file.Data.Count);
        foreach (var (id, raw) in file.Data)
        {
            var isBoots = raw.Tags.Contains("Boots");
            var isActive = raw.Tags.Contains("Active");
            var availableOnSummonersRift = raw.Gold.Purchasable && (raw.Maps?.GetValueOrDefault("11") ?? false);
            result.Add(new ItemData
            {
                Id = id,
                Name = raw.Name,
                NameKo = koNames.GetValueOrDefault(id),
                Stats = new ItemStats
                {
                    Ad = raw.Stats.GetValueOrDefault("FlatPhysicalDamageMod"),
                    Ap = raw.Stats.GetValueOrDefault("FlatMagicDamageMod"),
                    Haste = raw.Stats.GetValueOrDefault("FlatCooldownReductionMod")
                        // Modern items encode haste in the localized description only;
                        // some older entries still expose FlatCooldownReductionMod as a
                        // fraction (e.g. 0.1 = 10%). Left as-is; engines should treat
                        // this field as best-effort until confirmed against tooltip text.
                        ,
                    Armor = raw.Stats.GetValueOrDefault("FlatArmorMod"),
                    Mr = raw.Stats.GetValueOrDefault("FlatSpellBlockMod"),
                    Hp = raw.Stats.GetValueOrDefault("FlatHPPoolMod"),
                    // (loop 125) Movement speed for the enemy-return estimate. Same best-effort caveat
                    // as Haste: Data Dragon's stats block is incomplete for modern items.
                    MoveSpeedFlat = raw.Stats.GetValueOrDefault("FlatMovementSpeedMod"),
                    MoveSpeedPercent = raw.Stats.GetValueOrDefault("PercentMovementSpeedMod"),
                    // (GOLDEN #3 round 3) Item-sourced bonus attack speed for Garen E's strike count.
                    AttackSpeedPercent = raw.Stats.GetValueOrDefault("PercentAttackSpeedMod"),
                },
                IsBoots = isBoots,
                BootsType = isBoots ? GuessBootsType(raw.Name) : null,
                IsActive = isActive,
                AvailableOnSummonersRift = availableOnSummonersRift,
                GoldTotal = raw.Gold.Total,
                BuildsFrom = raw.From ?? (IReadOnlyList<string>)Array.Empty<string>(),
                BuildsInto = raw.Into ?? (IReadOnlyList<string>)Array.Empty<string>(),
            });
        }
        return result;
    }

    /// <summary>Parses just the id -&gt; localized name map out of an optional locale-variant
    /// item.json (Part 3), for <see cref="ItemData.NameKo"/>. Best-effort: null/missing input,
    /// or a malformed file, yields an empty map rather than throwing — this is a secondary
    /// display-only source layered on top of the required English item.json, not primary data
    /// (mirrors this project's established silent-fallback posture for optional network data).</summary>
    private static IReadOnlyDictionary<string, string> ParseItemNames(string? itemListJson)
    {
        if (string.IsNullOrEmpty(itemListJson)) return new Dictionary<string, string>();
        try
        {
            var file = JsonSerializer.Deserialize<DDragonItemListFile>(itemListJson, Options);
            return file?.Data.ToDictionary(kv => kv.Key, kv => kv.Value.Name)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Derives a boots "flavor" label from the item name (e.g. "Ionian Boots of
    /// Lucidity" -&gt; "Ionian"). Data Dragon has no dedicated bootsType field, so this
    /// is name-pattern based; plain "Boots" (id 1001) yields null (no sub-type yet).</summary>
    private static string? GuessBootsType(string name)
    {
        var match = Regex.Match(name, @"^(\w+)\s+Boots\b");
        return match.Success ? match.Groups[1].Value : null;
    }

    public static IReadOnlyList<RuneData> ParseRunes(string runesReforgedJson)
    {
        var trees = JsonSerializer.Deserialize<List<DDragonRuneTree>>(runesReforgedJson, Options)
            ?? throw new InvalidDataException("runesReforged.json deserialized to null");

        var result = new List<RuneData>();
        foreach (var tree in trees)
        {
            foreach (var slot in tree.Slots)
            {
                foreach (var rune in slot.Runes)
                {
                    var apiTrackable = !RuneApiTrackability.NonTrackableRuneIds.Contains(rune.Id);
                    result.Add(new RuneData
                    {
                        Id = rune.Id.ToString(),
                        Name = rune.Name,
                        Tree = tree.Name,
                        ApiTrackable = apiTrackable,
                        EffectFormula = apiTrackable ? string.Empty : null,
                        Icon = rune.Icon,
                    });
                }
            }
        }
        return result;
    }
}
