using System.IO;
using System.Linq;
using System.Text.Json;

namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 15) Every field a CommunityDragon calculation part carries is either READ by
/// ChampionBinParser or IGNORED for a stated reason. <c>mScaleByStatProgressionMultiplier</c> sat unread for
/// months - every level interpolation flagged with it ran on a straight line instead of the stat-growth
/// curve - and nothing failed, because an unread field changes no type and throws nothing. A field this
/// list does not know (a new patch, a newly resolved hash) fails here until someone decides what it means.
/// </summary>
public class BinFieldInventoryTests
{
    // "read" = the parser consumes it; anything else is the reason it does not need to.
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["*.__type"] = "read",
        ["GameCalculation.mFormulaParts"] = "read",
        ["GameCalculation.mMultiplier"] = "read",
        ["GameCalculation.mDisplayAsPercent"] = "display; the >=1-is-a-percentage readers handle units",
        ["GameCalculation.mPrecision"] = "display",
        ["GameCalculation.mSimpleTooltipCalculationDisplay"] = "display",
        ["GameCalculation.mExpandedTooltipCalculationDisplay"] = "display",
        ["GameCalculation.tooltipOnly"] = "a flag for the client tooltip; the number is still the calc",
        ["GameCalculation.DamageType"] = "informational; the curated hit states its type",
        ["GameCalculation.ResultModifier"] = "Aurelion Sol Stardust UI calcs only, none curated",
        ["GameCalculation.{72c5c2a8}"] = "unresolved display hash",
        ["GameCalculationModified.mModifiedGameCalculation"] = "read",
        ["GameCalculationModified.mMultiplier"] = "read",
        ["GameCalculationModified.mExpandedTooltipCalculationDisplay"] = "display",
        ["GameCalculationModified.mSimpleTooltipCalculationDisplay"] = "display",
        ["GameCalculationModified.tooltipOnly"] = "a flag for the client tooltip",
        ["GameCalculationModified.DamageType"] = "informational",
        ["GameCalculationModified.mOverrideSpellLevel"] = "Akali MinionDamage only (minions)",
        ["GameCalculationModified.{72c5c2a8}"] = "unresolved display hash",
        ["GameCalculationConditional.mConditionalCalculationRequirements"] = "not evaluated; no curated hit names a conditional calc",
        ["GameCalculationConditional.mConditionalGameCalculation"] = "not evaluated (see requirements)",
        ["GameCalculationConditional.mDefaultGameCalculation"] = "not evaluated (see requirements)",
        ["GameCalculationConditional.mExpandedTooltipCalculationDisplay"] = "display",
        ["GameCalculationConditional.mSimpleTooltipCalculationDisplay"] = "display",
        ["GameCalculationConditional.tooltipOnly"] = "a flag for the client tooltip",
        ["GameCalculationConditional.{353099b0}"] = "unresolved hash (Zed FinalDamage, not curated)",
        ["GameCalculationConditional.{72c5c2a8}"] = "unresolved display hash",
        ["HasBuffCastRequirement.mBuffName"] = "requirement of a conditional calc (not evaluated)",
        ["HasBuffCastRequirement.mInvertResult"] = "requirement of a conditional calc (not evaluated)",
        ["HasAllSubRequirementsCastRequirement.mSubRequirements"] = "requirement of a conditional calc (not evaluated)",
        ["AboveHealthPercentCastRequirement.mInvertResult"] = "requirement of a conditional calc (not evaluated)",
        ["AboveHealthPercentCastRequirement.{137cf12a}"] = "requirement of a conditional calc (not evaluated)",
        ["NamedDataValueCalculationPart.mDataValue"] = "read",
        ["NumberCalculationPart.mNumber"] = "read",
        ["StatByCoefficientCalculationPart.mCoefficient"] = "read",
        ["StatByCoefficientCalculationPart.mStat"] = "read",
        ["StatByCoefficientCalculationPart.mStatFormula"] = "read",
        ["StatByCoefficientCalculationPart.{a8cb9c14}"] = "the TARGET's stat (Kindred / Zed / Ekko %HP calcs); curated hits use the %HP shape instead",
        ["StatByNamedDataValueCalculationPart.mDataValue"] = "read",
        ["StatByNamedDataValueCalculationPart.mStat"] = "read",
        ["StatByNamedDataValueCalculationPart.mStatFormula"] = "read",
        ["StatBySubPartCalculationPart.mStat"] = "read",
        ["StatBySubPartCalculationPart.mStatFormula"] = "read",
        ["StatBySubPartCalculationPart.mSubpart"] = "read",
        ["StatBySubPartCalculationPart.{a8cb9c14}"] = "the TARGET's stat (see StatByCoefficient)",
        ["SumOfSubPartsCalculationPart.mSubparts"] = "read",
        ["ProductOfSubPartsCalculationPart.mPart1"] = "read",
        ["ProductOfSubPartsCalculationPart.mPart2"] = "read",
        ["ByCharLevelInterpolationCalculationPart.mStartValue"] = "read",
        ["ByCharLevelInterpolationCalculationPart.mEndValue"] = "read",
        ["ByCharLevelInterpolationCalculationPart.mScaleByStatProgressionMultiplier"] = "read (M45 loop 15)",
        ["ByCharLevelInterpolationCalculationPart.mScalePastDefaultMaxLevel"] = "levels past 18 (other modes)",
        ["ByCharLevelBreakpointsCalculationPart.mLevel1Value"] = "read",
        ["ByCharLevelBreakpointsCalculationPart.mBreakpoints"] = "read",
        ["ByCharLevelBreakpointsCalculationPart.mInitialBonusPerLevel"] = "read",
        ["Breakpoint.mLevel"] = "read",
        ["Breakpoint.mAdditionalBonusAtThisLevel"] = "read",
        ["Breakpoint.mBonusPerLevelAtAndAfter"] = "read",
        ["ByCharLevelFormulaCalculationPart.values"] = "read",
        ["EffectValueCalculationPart.mEffectIndex"] = "read",
        ["AbilityResourceByCoefficientCalculationPart.mCoefficient"] = "read",
        ["AbilityResourceByCoefficientCalculationPart.mStatFormula"] = "read",
        ["AbilityResourceByCoefficientCalculationPart.mAbilityResource"] = "Tryndamere heal only",
        ["BuffCounterByCoefficientCalculationPart.mBuffName"] = "read",
        ["BuffCounterByCoefficientCalculationPart.mCoefficient"] = "read",
        ["BuffCounterByCoefficientCalculationPart.mIconKey"] = "display",
        ["BuffCounterByCoefficientCalculationPart.mScalingTagKey"] = "display",
        ["BuffCounterByNamedDataValueCalculationPart.mBuffName"] = "read",
        ["BuffCounterByNamedDataValueCalculationPart.mDataValue"] = "read",
        ["BuffCounterByNamedDataValueCalculationPart.mIconKey"] = "display",
        ["BuffCounterByNamedDataValueCalculationPart.mScalingTagKey"] = "display",
        ["ClampSubPartsCalculationPart.mSubparts"] = "movement speed / cast time / Udyr stance calcs only, none curated",
        ["ClampSubPartsCalculationPart.mFloor"] = "(see mSubparts)",
        ["ClampSubPartsCalculationPart.mCeiling"] = "(see mSubparts)",
        ["CooldownMultiplierCalculationPart.*"] = "cooldown calcs only",
        ["PercentageOfBuffNameElapsed.buffName"] = "Rell / Udyr buff-elapsed calcs, none curated",
        ["PercentageOfBuffNameElapsed.Coefficient"] = "(see buffName)",
        ["{0333530c}.level"] = "Bel'Veth / Zaahen stack tables, none curated",
        ["{0333530c}.{ae9b464d}"] = "(see level)",
        ["{0333530c}.{b0d8b2ac}"] = "(see level)",
        ["{4ce08984}.{91d404a5}"] = "Bel'Veth / Zaahen stack tables, none curated",
        ["{4ce08984}.{9823b29a}"] = "(see above)",
        ["{4ce08984}.{bbd778a2}"] = "(see above)",
        ["{b22609db}.{91d404a5}"] = "read (Irelia OnHitBonus)",
        ["{b22609db}.{b2cd0eb0}"] = "read (Irelia OnHitBonus)",
        ["{9e9e2e5c}.DataValue"] = "Elise cross-object DataValue, not curated",
        ["{9e9e2e5c}.SourceObject"] = "(see DataValue)",
        ["{ee18a47b}.StartDataValue"] = "read",
        ["{ee18a47b}.EndDataValue"] = "read",
        ["{ee18a47b}.{0589a59c}"] = "read",
        ["{ee18a47b}.{0b65bc23}"] = "read",
        ["{f3cbe7b2}.mSpellCalculationKey"] = "read",
    };

    private static bool IsKnown(string type, string field)
        => Known.ContainsKey($"{type}.{field}") || Known.ContainsKey($"{type}.*") || Known.ContainsKey($"*.{field}");

    [Fact]
    public void EveryCalculationPartFieldIsReadOrIgnoredOnPurpose()
    {
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        int scanned = 0;
        string dir = Path.Combine(AppContext.BaseDirectory, "data", "communitydragon");
        foreach (var file in Directory.GetFiles(dir, "*.bin.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || !entry.Value.TryGetProperty("mSpell", out var spell) || spell.ValueKind != JsonValueKind.Object
                    || !spell.TryGetProperty("mSpellCalculations", out var calcs) || calcs.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var calc in calcs.EnumerateObject())
                {
                    scanned++;
                    Walk(calc.Value, Path.GetFileName(file) + ":" + calc.Name, unknown);
                }
            }
        }
        Assert.True(scanned > 1500, $"only {scanned} calculations scanned - is the BIN corpus in the output?");
        Assert.True(unknown.Count == 0, "calculation-part fields neither read nor ignored on purpose (decide what each "
            + "means, then read it or list it with its reason): " + string.Join("; ", unknown.Take(20)));
    }

    private static void Walk(JsonElement e, string where, SortedSet<string> unknown)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in e.EnumerateArray()) Walk(v, where, unknown);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        if (e.TryGetProperty("__type", out var t) && t.ValueKind == JsonValueKind.String)
        {
            string type = t.GetString()!;
            foreach (var p in e.EnumerateObject())
                if (!IsKnown(type, p.Name))
                    unknown.Add($"{type}.{p.Name} ({where})");
        }
        foreach (var p in e.EnumerateObject()) Walk(p.Value, where, unknown);
    }
}
