namespace Overlay.Core.ChampionDb;

/// <summary>
/// Evaluates a <see cref="GameCalculation"/> formula tree (M11 spec Interfaces:
/// <c>FormulaInterpreter.evaluate(spellCalculation, context) -&gt; number</c>) against a
/// specific skill rank and a caller-supplied stat resolver. This is the only place
/// <see cref="SkillData.SpellCalculations"/>/<see cref="SkillData.DataValues"/> BIN data is
/// turned into a number — M11 itself never pre-computes or caches a result, per the
/// spec's "사전 계산값 저장 금지" (no precomputed values stored) note.
/// </summary>
public static class FormulaInterpreter
{
    /// <summary>
    /// Evaluates the named calculation for the given 1-based skill rank.
    /// <paramref name="statResolver"/> maps a raw BIN stat-source id (<see
    /// cref="CalculationPart.Stat"/>) plus its <see cref="CalculationPart.StatFormula"/>
    /// (BIN <c>mStatFormula</c>, which splits total- vs bonus-AD on the same <c>mStat</c>=2)
    /// to the caller's current value for that stat (e.g. live total/bonus AD or AP) — M11
    /// does not know or fabricate what each id means (Riot doesn't publish this enum), so
    /// resolving it is the caller's (M05's) responsibility.
    /// </summary>
    /// <param name="casterResource">The caster's ability resource used by
    /// <see cref="CalculationPart.Kind.AbilityResourceByCoefficient"/> parts — the live caster
    /// MAX mana (<see cref="ActivePlayerStats.ResourceMax"/>). Defaults to 0 so callers whose
    /// formulas don't use that part are unaffected. The BIN carries no explicit max/bonus/current
    /// selector for this part, so max mana is used as a documented approximation (see
    /// <see cref="EvaluatePart"/>).</param>
    /// <param name="stackCount">(M25 §11.G) Live buff-stack count fed to
    /// <see cref="CalculationPart.Kind.BuffCounterByCoefficient"/>/<see cref="CalculationPart.Kind.BuffCounterByNamedDataValue"/>
    /// per-stack terms (e.g. Nasus Q's Siphoning-Strike stacks, Kindred's Mark). The engine cannot
    /// observe buff-stack counts, so the caller threads the user's "몇 스택" knob here; defaults to 0
    /// (the conservative un-stacked floor) so callers whose formulas don't use a stack term are
    /// unaffected — a BuffCounter part evaluates to value × 0 = 0.</param>
    /// <param name="championLevel">(M25 §12③) The caster's CHAMPION level (1-18), used to index
    /// <see cref="CalculationPart.Kind.ByCharLevelBreakpoints"/>/<see cref="CalculationPart.Kind.ByCharLevelInterpolation"/>/
    /// <see cref="CalculationPart.Kind.ByCharLevelFormula"/> parts — which are "based on level" and must
    /// scale with champion level, NOT the ability rank. For a Q/W/E/R slot the caller passes ability
    /// rank as <paramref name="rank"/> (correct for per-rank DataValues) but champion level here; for the
    /// passive "P" the two coincide. Null (default) falls back to <paramref name="rank"/>, preserving the
    /// prior single-index behavior for direct callers that don't supply it.</param>
    public static double Evaluate(
        SkillData skill,
        string calculationName,
        int rank,
        Func<int, int?, double> statResolver,
        double casterResource = 0,
        int stackCount = 0,
        int? championLevel = null)
    {
        if (!skill.SpellCalculations.TryGetValue(calculationName, out var calc))
            throw new KeyNotFoundException($"No spell calculation named '{calculationName}'");
        return EvaluateCalculation(skill, calc, rank, statResolver, casterResource, stackCount, championLevel, depth: 0);
    }

    private static double EvaluateCalculation(
        SkillData skill, GameCalculation calc, int rank, Func<int, int?, double> statResolver,
        double casterResource, int stackCount, int? championLevel, int depth)
    {
        if (depth > 16)
            throw new InvalidOperationException("GameCalculation reference cycle detected");

        if (calc.ModifiedGameCalculation is { } refName)
        {
            if (!skill.SpellCalculations.TryGetValue(refName, out var referenced))
                throw new KeyNotFoundException($"GameCalculationModified references unknown calculation '{refName}'");
            double baseValue = EvaluateCalculation(skill, referenced, rank, statResolver, casterResource, stackCount, championLevel, depth + 1);
            // Apply mMultiplier when present (e.g. Ahri W MultiFireDamage = SingleFireDamage ×
            // RepeatDamageMod); absent ⇒ pass the referenced calc through unscaled.
            return calc.Multiplier is { } mul
                ? baseValue * EvaluatePart(skill, mul, rank, statResolver, casterResource, stackCount, championLevel, depth)
                : baseValue;
        }

        var total = 0.0;
        foreach (var part in calc.FormulaParts)
        {
            total += EvaluatePart(skill, part, rank, statResolver, casterResource, stackCount, championLevel, depth);
        }
        // A plain (non-Modified) GameCalculation can also carry mMultiplier (e.g. Skarner
        // passive PercentHealthDamage ×0.01) — apply it unconditionally whenever present,
        // not just on the GameCalculationModified branch above (that used to be the only
        // place mMultiplier was applied, silently dropping it here).
        if (calc.Multiplier is { } totalMul)
        {
            total *= EvaluatePart(skill, totalMul, rank, statResolver, casterResource, stackCount, championLevel, depth);
        }
        return total;
    }

    private static double EvaluatePart(
        SkillData skill, CalculationPart part, int rank, Func<int, int?, double> statResolver,
        double casterResource, int stackCount, int? championLevel, int depth)
    {
        // (M25 §12③) "based on level" parts index by CHAMPION LEVEL, not ability rank. For a P slot the
        // caller already passes champion level as rank so they coincide; for Q/W/E/R this uses the real
        // champion level while per-rank DataValues below keep using the ability rank.
        int levelIndex = championLevel ?? rank;
        switch (part.PartKind)
        {
            case CalculationPart.Kind.Number:
                return part.Number ?? 0;

            case CalculationPart.Kind.NamedDataValue:
                return LookupDataValue(skill, part.DataValue, rank);

            case CalculationPart.Kind.StatByNamedDataValue:
                return LookupDataValue(skill, part.DataValue, rank) * statResolver(part.Stat ?? 0, part.StatFormula);

            case CalculationPart.Kind.StatByCoefficient:
                return (part.Coefficient ?? 0) * statResolver(part.Stat ?? 0, part.StatFormula);

            case CalculationPart.Kind.SumOfSubParts:
                return part.SubParts.Sum(p => EvaluatePart(skill, p, rank, statResolver, casterResource, stackCount, championLevel, depth));

            case CalculationPart.Kind.ProductOfSubParts:
                return part.SubParts.Aggregate(
                    1.0, (acc, p) => acc * EvaluatePart(skill, p, rank, statResolver, casterResource, stackCount, championLevel, depth));

            // (M25 §11.G) A per-stack term: (mCoefficient | mDataValue lookup) × the caller-supplied
            // live buff-stack count (0 by default = the conservative un-stacked floor, so a formula
            // carrying a stack term resolves to its base when the user hasn't entered a stack count).
            case CalculationPart.Kind.BuffCounterByCoefficient:
                return (part.Coefficient ?? 0) * stackCount;

            case CalculationPart.Kind.BuffCounterByNamedDataValue:
                return LookupDataValue(skill, part.DataValue, rank) * stackCount;

            case CalculationPart.Kind.ByCharLevelBreakpoints:
            {
                // value(1) = mLevel1Value. Each subsequent level (2..rank) adds the current
                // per-level growth rate, which starts at mInitialBonusPerLevel (0 when the BIN
                // omits it — the flat one-off-bump flavor below) and is REPLACED by a
                // breakpoint's mBonusPerLevelAtAndAfter starting with the level that reaches
                // that breakpoint (i.e. the increment step ENDING at the breakpoint's level
                // already uses the new rate). A breakpoint's mAdditionalBonusAtThisLevel
                // (the other flavor — no InitialBonusPerLevel/PerLevelRate involved) is instead
                // added exactly once, the first time the evaluated level reaches it. Verified
                // against Lissandra's passive TotalDamage (Level1Value=120,
                // InitialBonusPerLevel=20, breakpoint @13 rate=30): level 18 = 120 + 20*11 +
                // 30*6 = 520, matching the champion's published 120–520 tooltip range.
                var value = part.Level1Value ?? 0;
                var rate = part.InitialBonusPerLevel ?? 0;
                var sortedBreakpoints = part.Breakpoints.OrderBy(b => b.Level).ToList();
                var bpIndex = 0;
                for (var lvl = 2; lvl <= levelIndex; lvl++)
                {
                    while (bpIndex < sortedBreakpoints.Count && sortedBreakpoints[bpIndex].Level <= lvl)
                    {
                        var bp = sortedBreakpoints[bpIndex];
                        if (bp.PerLevelRate is { } newRate) rate = newRate;
                        if (bp.AdditionalBonus is { } bump) value += bump;
                        bpIndex++;
                    }
                    value += rate;
                }
                return value;
            }

            case CalculationPart.Kind.ByCharLevelInterpolation:
            {
                var start = part.InterpolationStart ?? 0;
                var end = part.InterpolationEnd ?? 0;
                var t = Math.Clamp((levelIndex - 1) / 17.0, 0.0, 1.0);
                return start + (end - start) * t;
            }

            case CalculationPart.Kind.ByCharLevelInterpolationByDataValue:
            {
                // (§16 P-round) Identical level-1→18 linear interpolation as ByCharLevelInterpolation,
                // but the two endpoints are LIVE DataValue lookups (K'Sante mark %HP:
                // MarkDamagePercentMin 0.01 → MarkDamagePercentMax 0.02, giving 1%@L1 → 2%@L18). Both
                // endpoints resolve at the same rank the rest of this calc uses; for a "P" passive that
                // rank IS the champion level (see ComputeCalcDamage's ChooseRank).
                var startV = LookupDataValue(skill, part.InterpolationStartDataValue, rank);
                var endV = LookupDataValue(skill, part.InterpolationEndDataValue, rank);
                var t = Math.Clamp((levelIndex - 1) / 17.0, 0.0, 1.0);
                return startV + (endV - startV) * t;
            }

            case CalculationPart.Kind.EffectValue:
                return LookupEffectAmount(skill, part.EffectIndex, rank);

            case CalculationPart.Kind.AbilityResourceByCoefficient:
                // Scales the caster's ability resource (mana) by mCoefficient. The BIN carries
                // no explicit max/bonus/current selector (only mCoefficient + an mStatFormula
                // flag), so caster MAX mana is used — the value the caller threads in as
                // casterResource (ActivePlayerStats.ResourceMax). E.g. Ryze Q/W/E each add
                // ~2-3% of max mana this way.
                return (part.Coefficient ?? 0) * casterResource;

            case CalculationPart.Kind.ByCharLevelFormula:
            {
                // Flat per-level lookup table (BIN "values"), indexed by CHAMPION level
                // (levelIndex = championLevel ?? rank; §12③) — index 0 is an unused placeholder;
                // index N holds the value at champion level N. Verified against Leona's passive
                // Sunlight (values[1]=32, values[18]=151 = live tooltip "32-151 based on level").
                // For "P" calcs the caller passes champion level as rank AND championLevel so they
                // coincide; for Q/W/E/R the ability rank drives per-rank DataValues while this
                // "based on level" table uses the real champion level.
                var values = part.PerLevelValues;
                if (values.Count == 0) return 0;
                var index = Math.Clamp(levelIndex, 0, values.Count - 1);
                return values[index];
            }

            case CalculationPart.Kind.StatBySubPart:
            {
                // statResolver(stat) × the nested sub-part's own evaluated value (e.g. Riven
                // passive TotalDamage = TotalAD × ByCharLevelInterpolation(0.30→0.45); Hecarim
                // passive BonusAD = BonusMoveSpeed × ByCharLevelBreakpoints(...)). See
                // CalculationPart.SubPart remarks for the 8-champion confirmation.
                if (part.SubPart is not { } subPart)
                    throw new NotSupportedException("StatBySubPartCalculationPart missing mSubpart");
                return statResolver(part.Stat ?? 0, part.StatFormula)
                    * EvaluatePart(skill, subPart, rank, statResolver, casterResource, stackCount, championLevel, depth);
            }

            case CalculationPart.Kind.NamedDataValueByCharLevel:
            {
                // base + perLevel x (level-1), indexed by CHAMPION level like every other
                // "based on level" part — not by ability rank.
                double baseValue = LookupDataValue(skill, part.DataValue, rank);
                double perLevel = LookupDataValue(skill, part.DataValuePerLevel, rank);
                return baseValue + perLevel * (Math.Clamp(levelIndex, 1, 18) - 1);
            }

            case CalculationPart.Kind.SpellCalculationRef:
            {
                // (loop 488) The part IS another calculation on the same skill. Resolved through the
                // ordinary calculation path so a referenced calc's own modifiers and sub-parts behave
                // identically to being evaluated directly, and through the same depth counter so a
                // BIN that referenced itself would hit the existing cycle guard rather than recurse
                // forever. A key naming nothing is a data error, not a silent zero.
                if (part.SpellCalculationKey is not { Length: > 0 } key
                    || !skill.SpellCalculations.TryGetValue(key, out var referenced))
                    throw new KeyNotFoundException(
                        $"SpellCalculationRef names unknown calculation '{part.SpellCalculationKey}'");
                return EvaluateCalculation(skill, referenced, rank, statResolver, casterResource,
                    stackCount, championLevel, depth + 1);
            }

            default:
                throw new NotSupportedException(
                    $"Unrecognized CalculationPart kind '{part.PartKind}' — BIN schema may have changed.");
        }
    }

    /// <summary>
    /// Public per-rank read of a single named DataValue (same rank-clamp + case-insensitive
    /// fallback as formula evaluation). Added for the T7 percent-of-HP path: a %HP hit's fraction
    /// (e.g. Vayne W <c>MaxHealthRatio</c>, Brand P <c>PercentHealthDamage</c>) is BIN data that
    /// varies by rank, so it must be looked up live per rank rather than baked into a literal —
    /// this exposes the existing <see cref="Kind.NamedDataValue"/> resolution to that caller
    /// without it having to synthesize a one-part <see cref="GameCalculation"/>. Throws
    /// <see cref="KeyNotFoundException"/> when the name is absent, so the caller can fall back.
    /// </summary>
    public static double EvaluateDataValue(SkillData skill, string dataValueName, int rank)
        => LookupDataValue(skill, dataValueName, rank);

    private static double LookupDataValue(SkillData skill, string? dataValueName, int rank)
    {
        if (dataValueName is null)
            throw new KeyNotFoundException("No DataValue named '<null>'");

        // Riot's own BIN data is occasionally inconsistent about the casing between a
        // DataValue's definition and the calc part that references it (e.g. Ahri R defines
        // "RAPCoefficient" but its RCalculatedDamage part references "rAPCoefficient"). The
        // exact lookup is tried first; a case-insensitive fallback recovers these quirks so a
        // real damage number is produced instead of throwing.
        if (!skill.DataValues.TryGetValue(dataValueName, out var values) || values.Length == 0)
        {
            values = null;
            foreach (var (name, arr) in skill.DataValues)
            {
                if (arr.Length > 0 && string.Equals(name, dataValueName, StringComparison.OrdinalIgnoreCase))
                {
                    values = arr;
                    break;
                }
            }
            if (values is null)
                throw new KeyNotFoundException($"No DataValue named '{dataValueName}'");
        }
        var index = Math.Clamp(rank, 0, values.Length - 1);
        return values[index];
    }

    /// <summary>Resolves an <see cref="CalculationPart.Kind.EffectValue"/> part: the per-rank
    /// value at <paramref name="effectIndex"/> (1-based, BIN <c>mEffectIndex</c>) in the skill's
    /// <see cref="SkillData.EffectAmounts"/> table, clamped to the array's rank bounds (same
    /// convention as <see cref="LookupDataValue"/>). Throws <see cref="KeyNotFoundException"/>
    /// when the index is missing/out of range or its entry has no values, so the caller (M05)
    /// falls back rather than reading garbage.</summary>
    private static double LookupEffectAmount(SkillData skill, int? effectIndex, int rank)
    {
        if (effectIndex is not { } idx || idx < 1 || idx > skill.EffectAmounts.Count)
            throw new KeyNotFoundException(
                $"No effect amount at mEffectIndex '{effectIndex?.ToString() ?? "<null>"}'");
        var values = skill.EffectAmounts[idx - 1];
        if (values.Length == 0)
            throw new KeyNotFoundException($"Effect amount at mEffectIndex '{idx}' has no values");
        var index = Math.Clamp(rank, 0, values.Length - 1);
        return values[index];
    }
}
