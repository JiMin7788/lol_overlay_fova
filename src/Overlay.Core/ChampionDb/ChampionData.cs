using System.Text.Json.Serialization;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// M11 Champion Database public data model — see docs/modules/M11_CHAMPION_DATABASE.md
/// "Data Model". Populated exclusively from Riot Data Dragon (P1) plus optional
/// Special Property override files; never hand-typed per Hard Rule #4.
/// </summary>
public sealed class ChampionData
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ChampionBaseStats BaseStats { get; init; } = new();
    public ChampionStatsPerLevel StatsPerLevel { get; init; } = new();

    /// <summary>Keyed by skill slot: "P", "Q", "W", "E", "R".</summary>
    public Dictionary<string, SkillData> Skills { get; init; } = new();

    /// <summary>Special Property overrides merged in from data/special_properties/*.json,
    /// keyed by <see cref="ChampionSpecialProperty.Key"/>.</summary>
    public Dictionary<string, ChampionSpecialProperty> SpecialProperties { get; init; } = new();
}

public sealed class ChampionBaseStats
{
    public double Hp { get; init; }
    public double Armor { get; init; }
    public double Mr { get; init; }
    public double Ad { get; init; }
    public double AttackSpeed { get; init; }
    public double Ms { get; init; }
    public double Mp { get; init; }
    public double HpRegen { get; init; }
    public double MpRegen { get; init; }
    public double AttackRange { get; init; }
}

public sealed class ChampionStatsPerLevel
{
    public double Hp { get; init; }
    public double Armor { get; init; }
    public double Mr { get; init; }
    public double Ad { get; init; }
    public double AttackSpeed { get; init; }
    public double HpRegen { get; init; }
    public double MpRegen { get; init; }
}

/// <summary>
/// League's real per-level stat growth curve. A user-reported overlay-vs-real-game gap
/// (armor 101 shown vs 97 actual, mr 46 vs 44, at 4 Cloth Armor — i.e. the discrepancy existed
/// even before items, in the base+level component) traced to this: <c>ComboRunner</c>/
/// <c>ChampionSummary</c> were computing level-scaled stats as naive linear interpolation
/// (<c>base + perLevel * (level-1)</c>), but Riot's actual formula is a slightly super-linear
/// curve (<c>base + perLevel * n * (0.7025 + 0.0175*n)</c>, n = level-1) — this project already
/// had a CORRECT implementation of this exact formula in the legacy, never-wired-in
/// <see cref="Overlay.Core.KillableCalculator"/>; this centralizes it for the live combo path
/// too instead of duplicating a second copy.
/// </summary>
public static class LevelGrowth
{
    private const double GrowthA = 0.7025;
    private const double GrowthB = 0.0175;

    /// <summary>Level-scaled value of a growth stat (HP, armor, MR, AD, etc.) at
    /// <paramref name="level"/> (1-18). Level 1 always returns <paramref name="baseValue"/>
    /// unchanged (n=0 collapses the growth term to 0 regardless of formula).</summary>
    public static double Stat(double baseValue, double perLevel, int level)
    {
        int n = Math.Max(1, level) - 1;
        return baseValue + perLevel * n * (GrowthA + GrowthB * n);
    }
}

public enum DamageType
{
    PHYSICAL,
    MAGIC,
    TRUE,
    MIXED,
}

public sealed class SkillData
{
    /// <summary>P, Q, W, E, or R.</summary>
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>Per-rank cooldown values in seconds, as published by Data Dragon.</summary>
    public double[] Cooldown { get; init; } = Array.Empty<double>();

    /// <summary>Per-rank resource cost (mana/energy/etc.), as published by Data Dragon.</summary>
    public double[] Mana { get; init; } = Array.Empty<double>();

    /// <summary>Per-rank numeric constants (BIN <c>DataValues</c>, e.g. "QBaseDamage"),
    /// keyed by name, indexed by rank. Sourced from CommunityDragon
    /// (<see cref="ChampionBinParser"/>) — Data Dragon does not expose these (M11 spec
    /// Changelog v2.0). Kept raw and unprocessed per spec Data Model note ("사전 계산값
    /// 저장 금지"); <see cref="FormulaInterpreter"/> is the only consumer.</summary>
    public Dictionary<string, double[]> DataValues { get; init; } = new();

    /// <summary>Raw BIN <c>mSpellCalculations</c> formula trees, keyed by calculation name
    /// (e.g. "QDamage"). AD/AP/other stat ratios are expressed here as formula parts, not
    /// as separate hardcoded ratio fields — see <see cref="FormulaInterpreter"/>.</summary>
    public Dictionary<string, GameCalculation> SpellCalculations { get; init; } = new();

    /// <summary>Per-rank effect-amount arrays (BIN <c>mSpell.mEffectAmount[i].value</c>),
    /// 0-based; an <see cref="CalculationPart.EffectIndex"/> (BIN <c>mEffectIndex</c>) selects
    /// one entry 1-based. Sourced from CommunityDragon (<see cref="ChampionBinParser"/>) — Riot
    /// keeps many champions' per-rank base damages here rather than in <see cref="DataValues"/>
    /// (e.g. Khazix Q/W/E, Taric E). Kept raw; <see cref="FormulaInterpreter"/> is the only
    /// consumer.</summary>
    public IReadOnlyList<double[]> EffectAmounts { get; init; } = Array.Empty<double[]>();

    public DamageType DamageType { get; init; } = DamageType.MIXED;

    public int MaxRank { get; init; }
}

/// <summary>
/// Per-champion special-mechanic definition (e.g. passive stack damage) loaded from
/// data/special_properties/*.json overrides and merged into <see cref="ChampionData"/>.
/// This is the one extension point where a new champion mechanic can be added without
/// touching any calculation-engine code (M11 Acceptance Criteria #2).
/// </summary>
public sealed class ChampionSpecialProperty
{
    [JsonPropertyName("championId")]
    public string ChampionId { get; init; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>Arithmetic-only formula string (+ - * / parentheses, variable names).
    /// Evaluated by <see cref="FormulaParser"/> — no eval()/dynamic code execution.</summary>
    [JsonPropertyName("valueFormula")]
    public string ValueFormula { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}
