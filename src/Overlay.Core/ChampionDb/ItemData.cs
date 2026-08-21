namespace Overlay.Core.ChampionDb;

/// <summary>
/// M11 item static data — see docs/modules/M11_CHAMPION_DATABASE.md "Data Model".
/// Sourced exclusively from Data Dragon's item.json (P1); never hand-typed.
/// </summary>
public sealed class ItemData
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>Korean display name (Data Dragon's locale-variant <c>ko_KR/item.json</c>), or
    /// null when that file wasn't fetched (e.g. offline first run) or has no entry for this id.
    /// Consumed by <c>Overlay.Client.Localization.ItemName</c> for display and by the M04 item
    /// picker's search box to match either language regardless of the current UI language.</summary>
    public string? NameKo { get; init; }

    public ItemStats Stats { get; init; } = new();
    public bool IsBoots { get; init; }

    /// <summary>e.g. "Ionian", "Berserker's", "Plated" — null when <see cref="IsBoots"/>
    /// is false or the boots type cannot be derived from the item name.</summary>
    public string? BootsType { get; init; }

    /// <summary>True when the item's Data Dragon <c>tags</c> array contains <c>"Active"</c> — i.e.
    /// it has a manual-activation effect (e.g. Zhonya's Hourglass, Youmuu's Ghostblade), as opposed
    /// to a purely passive item.</summary>
    public bool IsActive { get; init; }

    /// <summary>True when the item is currently purchasable on Summoner's Rift (Data Dragon
    /// <c>gold.purchasable == true</c> and <c>maps["11"] == true</c>). Data Dragon's item.json
    /// contains many non-purchasable/legacy/other-game-mode duplicate entries sharing a real item's
    /// display name (e.g. Giant's Belt id 1011 vs. legacy id 221011) — this field distinguishes them.</summary>
    public bool AvailableOnSummonersRift { get; init; }

    /// <summary>Total gold cost (Data Dragon <c>gold.total</c>). Sourced exclusively from
    /// item.json (P1). Consumed by M07 for exact missing-gold calculation.</summary>
    public int GoldTotal { get; init; }

    /// <summary>Ids of the direct components this item is built from (Data Dragon <c>from</c>;
    /// empty for a raw/basic item). Consumed by M07 for item-completion detection.</summary>
    public IReadOnlyList<string> BuildsFrom { get; init; } = Array.Empty<string>();

    /// <summary>Ids of the items this item builds into (Data Dragon <c>into</c>; empty when
    /// nothing further builds from it, i.e. a finished item). Consumed by M07.</summary>
    public IReadOnlyList<string> BuildsInto { get; init; } = Array.Empty<string>();
}

public sealed class ItemStats
{
    public double Ad { get; init; }
    public double Ap { get; init; }
    public double Haste { get; init; }
    public double Armor { get; init; }
    public double Mr { get; init; }
    public double Hp { get; init; }

    /// <summary>(loop 125) Flat movement speed the item grants (Data Dragon
    /// <c>FlatMovementSpeedMod</c>), e.g. basic boots. NOTE: like <see cref="Haste"/>, Data Dragon's
    /// item <c>stats</c> block is incomplete for many MODERN items (they encode MS in the localized
    /// description only), so this can read 0 for items that really give MS — the enemy-MS estimate
    /// (M-XX LaneReturnPredictor) treats it as a best-effort lower bound plus curated conditional bumps.</summary>
    public double MoveSpeedFlat { get; init; }

    /// <summary>(loop 125) Percent movement speed the item grants (Data Dragon
    /// <c>PercentMovementSpeedMod</c>, e.g. 0.05 = +5%), e.g. boots/Zeal items. Same Data-Dragon
    /// incompleteness caveat as <see cref="MoveSpeedFlat"/>.</summary>
    public double MoveSpeedPercent { get; init; }

    /// <summary>(GOLDEN #3 round 3) Percent bonus attack speed the item grants (Data Dragon
    /// <c>PercentAttackSpeedMod</c>, e.g. 0.15 = +15%), e.g. Recurve Bow/Zeal-line/Rapid Firecannon.
    /// Consumed by <c>ComboRunner</c>'s attack-speed-scaled strike count (Garen E) — see
    /// <see cref="Combo.SkillHit.AttackSpeedStrikeStep"/>'s doc comment for why this is
    /// deliberately ITEM-only (never rune/shard-sourced).</summary>
    public double AttackSpeedPercent { get; init; }
}
