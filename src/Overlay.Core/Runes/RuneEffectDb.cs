using System.Text.Json;

namespace Overlay.Core.Runes;

/// <summary>
/// Loads the bundled, hand-authored manual-rune damage-formula map (<c>data/rune_effects.json</c>)
/// and evaluates each covered rune's flat/level/AD/AP/max-health formula against a caster's live
/// stats. Mirrors <see cref="Overlay.Core.Items.ItemEffectDb"/>'s lazy-load/lookup shape, but
/// simpler: rune formulas here are flat numbers with linear level scaling and occasional AD/AP/
/// max-health ratios (see rune_effects.json's top-level "_note"), not full BIN calc trees, so no
/// <c>FormulaInterpreter</c> is needed — <see cref="Evaluate"/> is the entire evaluator.
///
/// Only the 8 non-API-trackable runes (<see cref="ChampionDb.RuneApiTrackability.NonTrackableRuneIds"/>)
/// are in scope for this file; an uncovered/unknown rune id simply returns <c>null</c> from
/// <see cref="Get"/>, and a rune whose real effect has no damage component (or doesn't fit this
/// file's single-DamageBonus shape, e.g. First Strike's % damage amplifier) is correctly absent —
/// see rune_effects.json's own top-level "_note" for the full reasoning. Tolerant of a missing/
/// corrupt file (returns empty), so a build with no rune_effects.json is a no-op.
/// </summary>
public static class RuneEffectDb
{
    private static Dictionary<string, RuneEffectFormula>? _cache;
    private static readonly object Gate = new();

    /// <summary>Location of the bundled map next to the assembly (same convention as
    /// item_effects.json — see Overlay.Core.csproj copy rules).</summary>
    private static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "rune_effects.json");

    /// <summary>The damage formula for one rune id, or <c>null</c> when the rune has no covered
    /// formula (unknown id, or a non-damage/non-additive effect — see class doc). Never throws.</summary>
    public static RuneEffectFormula? Get(string runeId)
        => Load().TryGetValue(runeId, out var f) ? f : null;

    /// <summary>All covered rune formulas (test/introspection helper).</summary>
    public static IReadOnlyCollection<RuneEffectFormula> All() => Load().Values;

    /// <summary>
    /// Resolves <paramref name="formula"/> against <paramref name="stats"/>: linear level
    /// interpolation (level 1..18) for the base value, plus bonus-AD/AP ratio terms and a melee/
    /// ranged max-health-percent term, summed into one flat bonus. <see cref="RuneEffectFormula.DamageType"/>
    /// null means adaptive — resolved per <see cref="RuneEffectFormula.Adaptive"/>'s rule from the
    /// caster's own AD/AP contribution, exactly as each rune's cited wiki text specifies.
    /// </summary>
    public static (double DamageBonus, RuneDamageType DamageType) Evaluate(RuneEffectFormula formula, RuneCasterStats stats)
    {
        double level = Math.Clamp(stats.Level, 1, 18);
        double levelBase = formula.BaseAtLevel1 + (formula.BaseAtLevel18 - formula.BaseAtLevel1) / 17.0 * (level - 1);

        double adTerm = formula.BonusAdRatio * stats.BonusAd;
        double apTerm = formula.ApRatio * stats.Ap;
        double hpTerm = (stats.IsMelee ? formula.MeleeMaxHealthPercent : formula.RangedMaxHealthPercent) * stats.MaxHealth;

        double bonus = levelBase + adTerm + apTerm + hpTerm;
        RuneDamageType type = formula.DamageType ?? ResolveAdaptive(formula.Adaptive, stats, adTerm, apTerm);
        return (bonus, type);
    }

    /// <summary>Adaptive-force resolution: <see cref="AdaptiveRule.ByRatioContribution"/> compares
    /// THIS rune's own resolved AD-ratio vs AP-ratio terms (Arcane Comet's documented rule);
    /// <see cref="AdaptiveRule.ByBonusAdVsAp"/> compares the caster's raw bonus AD vs AP overall
    /// (Shield Bash's documented rule, since it has no AD/AP ratio terms of its own). Both default
    /// to Magic on a tie, matching each rune's cited wiki text.</summary>
    private static RuneDamageType ResolveAdaptive(AdaptiveRule rule, RuneCasterStats stats, double adTerm, double apTerm)
        => rule switch
        {
            AdaptiveRule.ByRatioContribution => adTerm > apTerm ? RuneDamageType.PHYSICAL : RuneDamageType.MAGIC,
            AdaptiveRule.ByBonusAdVsAp => stats.BonusAd > stats.Ap ? RuneDamageType.PHYSICAL : RuneDamageType.MAGIC,
            _ => RuneDamageType.MAGIC,
        };

    private static Dictionary<string, RuneEffectFormula> Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;

            var parsed = new Dictionary<string, RuneEffectFormula>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(DefaultPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(DefaultPath));
                    if (doc.RootElement.TryGetProperty("runes", out var runes)
                        && runes.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var rune in runes.EnumerateObject())
                            if (TryParseFormula(rune.Name, rune.Value, out var formula))
                                parsed[rune.Name] = formula!;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                parsed.Clear(); // treat any load/parse failure as "no rune effects"
            }

            _cache = parsed;
            return _cache;
        }
    }

    private static bool TryParseFormula(string runeId, JsonElement obj, out RuneEffectFormula? formula)
    {
        formula = null;
        try
        {
            string name = obj.TryGetProperty("name", out var n) ? (n.GetString() ?? runeId) : runeId;

            RuneDamageType? damageType = null;
            if (obj.TryGetProperty("damageType", out var dt) && dt.ValueKind == JsonValueKind.String)
                damageType = Enum.Parse<RuneDamageType>(dt.GetString()!.ToUpperInvariant());

            AdaptiveRule adaptive = AdaptiveRule.None;
            if (obj.TryGetProperty("adaptive", out var ad) && ad.ValueKind == JsonValueKind.String)
                adaptive = Enum.Parse<AdaptiveRule>(ad.GetString()!, ignoreCase: true);

            formula = new RuneEffectFormula(
                RuneId: runeId,
                Name: name,
                DamageType: damageType,
                Adaptive: adaptive,
                BaseAtLevel1: obj.GetProperty("baseAtLevel1").GetDouble(),
                BaseAtLevel18: obj.GetProperty("baseAtLevel18").GetDouble(),
                BonusAdRatio: obj.GetProperty("bonusAdRatio").GetDouble(),
                ApRatio: obj.GetProperty("apRatio").GetDouble(),
                MeleeMaxHealthPercent: obj.GetProperty("meleeMaxHealthPercent").GetDouble(),
                RangedMaxHealthPercent: obj.GetProperty("rangedMaxHealthPercent").GetDouble());
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Test-only reset so the lazy cache doesn't leak between cases.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _cache = null;
    }
}

/// <summary>How a formula's damage type resolves when <see cref="RuneEffectFormula.DamageType"/>
/// is null (adaptive) — see <see cref="RuneEffectDb.Evaluate"/>.</summary>
public enum AdaptiveRule { None, ByRatioContribution, ByBonusAdVsAp }

/// <summary>One covered rune's damage formula (see rune_effects.json's per-rune "_note" for the
/// exact wiki citation each field traces back to). <see cref="DamageType"/> null means adaptive.</summary>
public sealed record RuneEffectFormula(
    string RuneId,
    string Name,
    RuneDamageType? DamageType,
    AdaptiveRule Adaptive,
    double BaseAtLevel1,
    double BaseAtLevel18,
    double BonusAdRatio,
    double ApRatio,
    double MeleeMaxHealthPercent,
    double RangedMaxHealthPercent);
