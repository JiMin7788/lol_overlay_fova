namespace Overlay.Core.ChampionDb;

/// <summary>
/// Structured (but unprocessed) representation of a CommunityDragon
/// <c>GameCalculation</c>/<c>mFormulaParts</c> tree, as found in
/// <c>{champion}.bin.json</c> under a spell's <c>mSpellCalculations</c>. Kept as a
/// direct mirror of the BIN shape per M11 Data Model note ("spellCalculations에 BIN
/// 원본을 그대로 보관") — no coefficients are pre-multiplied or simplified here.
/// <see cref="FormulaInterpreter"/> is the only place these trees are evaluated.
/// </summary>
public sealed class GameCalculation
{
    public IReadOnlyList<CalculationPart> FormulaParts { get; init; } = Array.Empty<CalculationPart>();

    /// <summary>Set when this calculation is a <c>GameCalculationModified</c> that
    /// scales another named calculation instead of defining its own formula parts
    /// (e.g. Aatrox's tooltip-only "QEdgeDamage" modifies "QDamage").</summary>
    public string? ModifiedGameCalculation { get; init; }

    /// <summary>BIN <c>mMultiplier</c>: the factor this calculation's value is scaled by.
    /// Applies to BOTH shapes a <c>GameCalculation</c> can take — a <c>GameCalculationModified</c>
    /// (e.g. Ahri W's "MultiFireDamage" = "SingleFireDamage" × <c>RepeatDamageMod</c>) AND a
    /// plain calculation with its own <c>mFormulaParts</c> (e.g. Skarner passive
    /// "PercentHealthDamage" = ByCharLevelInterpolation(5..9) × 0.01, converting a tooltip
    /// percent-point value into a fraction — dropping this multiplier silently produced a
    /// 100x-too-large number). Null ⇒ pass through unscaled (e.g. Aatrox "QEdgeDamage").
    /// <see cref="FormulaInterpreter"/> applies it unconditionally whenever present, regardless
    /// of which shape the calculation is.</summary>
    public CalculationPart? Multiplier { get; init; }
}

/// <summary>
/// One node of a <see cref="GameCalculation"/> formula tree. Only the part kinds
/// actually observed in live BIN data for the M11 sample champions (Aatrox, Ahri,
/// Annie, Zed, Jinx) are modeled — <see cref="Kind"/> lets <see cref="FormulaInterpreter"/>
/// dispatch; an unrecognized <c>__type</c> is preserved as <see cref="Kind.Unknown"/>
/// rather than guessed at.
/// </summary>
public sealed class CalculationPart
{
    public enum Kind
    {
        Unknown,
        Number,
        NamedDataValue,
        StatByNamedDataValue,
        StatByCoefficient,
        SumOfSubParts,
        ProductOfSubParts,
        ByCharLevelBreakpoints,
        ByCharLevelInterpolation,
        ByCharLevelInterpolationByDataValue,
        EffectValue,
        AbilityResourceByCoefficient,
        ByCharLevelFormula,
        StatBySubPart,
        BuffCounterByCoefficient,
        BuffCounterByNamedDataValue,

        /// <summary>(loop 488) A reference to ANOTHER named GameCalculation on the same skill,
        /// evaluated and used in place of a number. Riot's own type name is hashed
        /// (<c>{f3cbe7b2}</c>) and it carries <c>mSpellCalculationKey</c>.
        ///
        /// <para>It appears in 155 of the 173 champion BINs, almost always as the multiplier of a
        /// GameCalculationModified — Ambessa's Q is the case that surfaced it, where all four curated
        /// calcs multiply a doubled base by a separately-named half-ratio calc. Until this existed
        /// every one of those threw, and a hit whose calc throws is DROPPED, which is why Ambessa's
        /// Q2 scored nothing and her Q quietly fell through to the heuristic.</para></summary>
        SpellCalculationRef,

        /// <summary>(loop 488) Two named DataValues read as a base and a per-CHAMPION-LEVEL step:
        /// <c>base + perLevel x (level - 1)</c>. Riot's type name is hashed (<c>{b22609db}</c>) and
        /// its two fields are hashed too, which is why it went unrecognised — but the arithmetic is
        /// confirmed against the tooltip it feeds: Irelia's passive on-hit reads OnHitBaseDamage 10
        /// and OnHitPerLevel 3, and the live wiki states 10-61 based on level, which is exactly
        /// 10 + 3 x 17.</summary>
        NamedDataValueByCharLevel,
    }

    public Kind PartKind { get; init; }

    /// <summary>NumberCalculationPart.mNumber.</summary>
    public double? Number { get; init; }

    /// <summary>NamedDataValueCalculationPart/StatByNamedDataValueCalculationPart.mDataValue
    /// — a key into the owning skill's <see cref="SkillData.DataValues"/>.</summary>
    public string? DataValue { get; init; }

    /// <summary>(loop 488) <c>mSpellCalculationKey</c> — a key into the owning skill's
    /// <see cref="SkillData.SpellCalculations"/>, for <see cref="Kind.SpellCalculationRef"/>. Kept
    /// separate from <see cref="DataValue"/> because they index different dictionaries and a typo
    /// that crossed them would resolve silently to zero.</summary>
    public string? SpellCalculationKey { get; init; }

    /// <summary>(loop 488) The PER-LEVEL half of a <see cref="Kind.NamedDataValueByCharLevel"/> part;
    /// <see cref="DataValue"/> carries its base.</summary>
    public string? DataValuePerLevel { get; init; }

    /// <summary>StatByNamedDataValueCalculationPart/StatByCoefficientCalculationPart.mStat —
    /// the raw CommunityDragon stat-source enum id. Riot does not publish this enum's
    /// name mapping (e.g. which id means "bonus AD" vs "AP"), so it is passed through
    /// unresolved: callers supply a <c>statResolver(int) -&gt; double</c> that knows the
    /// current mapping, rather than M11 guessing/fabricating it.</summary>
    public int? Stat { get; init; }

    /// <summary>BIN <c>mStatFormula</c>; on an AD stat part (<see cref="Stat"/>==2), <c>2</c>
    /// ⇒ scales on BONUS AD, absent ⇒ TOTAL AD. Riot splits the AD sub-type here, not in
    /// <c>mStat</c>.</summary>
    public int? StatFormula { get; init; }

    /// <summary>StatByCoefficientCalculationPart.mCoefficient — a fixed multiplier applied
    /// to the resolved stat value (no DataValue lookup).</summary>
    public double? Coefficient { get; init; }

    /// <summary>SumOfSubParts/ProductOfSubParts children.</summary>
    public IReadOnlyList<CalculationPart> SubParts { get; init; } = Array.Empty<CalculationPart>();

    /// <summary>StatBySubPartCalculationPart.mSubpart — the SINGLE nested part (not a list;
    /// the BIN field is <c>mSubpart</c> singular, distinct from Sum/ProductOfSubParts'
    /// <c>mSubparts</c> array). Confirmed against 8 champions' live BIN data (Riven, XinZhao,
    /// Urgot, TahmKench, Samira, MissFortune, Graves, Hecarim, Ashe passives): the part always
    /// carries <c>mStat</c> (+ optional <c>mStatFormula</c>) plus exactly one <c>mSubpart</c>
    /// child of a kind already modeled elsewhere (ByCharLevelInterpolation/Breakpoints/Formula,
    /// SumOfSubParts, ...). Evaluated as <c>statResolver(Stat, StatFormula) *
    /// EvaluatePart(SubPart)</c> — e.g. Riven passive TotalDamage = TotalAD ×
    /// ByCharLevelInterpolation(0.30→0.45).</summary>
    public CalculationPart? SubPart { get; init; }

    /// <summary>ByCharLevelBreakpointsCalculationPart: value at level 1
    /// (<c>mLevel1Value</c>), plus the flat per-level growth rate from level 1 up to the
    /// first breakpoint (<c>mInitialBonusPerLevel</c>, null when the BIN omits it — flavor
    /// used by e.g. Ekko/Akali, which only carry one-off <see cref="Breakpoints"/> bumps).</summary>
    public double? Level1Value { get; init; }

    /// <summary>BIN <c>mInitialBonusPerLevel</c>: the per-level growth rate applied from
    /// level 2 up to (and including) the first breakpoint's level, before any breakpoint's
    /// <c>mBonusPerLevelAtAndAfter</c> takes over (see <see cref="FormulaInterpreter"/>'s
    /// ByCharLevelBreakpoints evaluation).</summary>
    public double? InitialBonusPerLevel { get; init; }

    /// <summary>Each breakpoint carries EITHER a one-off <c>mAdditionalBonusAtThisLevel</c>
    /// (added once, when the evaluated level reaches the breakpoint's <c>Level</c> — e.g.
    /// Ekko/Akali) OR a <c>mBonusPerLevelAtAndAfter</c> rate (replaces the running per-level
    /// growth rate from that <c>Level</c> onward — e.g. Diana/Lissandra/Ziggs). Kept as separate
    /// nullable fields rather than merged so <see cref="FormulaInterpreter"/> can apply each
    /// flavor's own semantics instead of conflating a one-off bump with a rate change.</summary>
    public IReadOnlyList<(int Level, double? AdditionalBonus, double? PerLevelRate)> Breakpoints { get; init; }
        = Array.Empty<(int, double?, double?)>();

    /// <summary>ByCharLevelInterpolationCalculationPart: linear interpolation from level 1
    /// to level 18.</summary>
    public double? InterpolationStart { get; init; }
    public double? InterpolationEnd { get; init; }

    /// <summary>(§16 P-round) <see cref="Kind.ByCharLevelInterpolationByDataValue"/> — the same
    /// level-1→18 linear interpolation as <see cref="InterpolationStart"/>/<see cref="InterpolationEnd"/>,
    /// but with the two endpoints sourced LIVE from named <see cref="SkillData.DataValues"/> instead of
    /// literal <c>mStartValue</c>/<c>mEndValue</c>. Motivating case: K'Sante's <c>PercentHealthDamage</c>
    /// mark %HP, whose BIN part (an unlabeled-hash <c>__type "{ee18a47b}"</c>) carries two hash-named
    /// fields whose STRING VALUES are DataValue names (<c>MarkDamagePercentMin</c>=0.01 →
    /// <c>MarkDamagePercentMax</c>=0.02). Keeping the endpoints as names (not baked numbers) honors the
    /// "patch-dependent values are always a dynamic BIN lookup" Hard Rule.</summary>
    public string? InterpolationStartDataValue { get; init; }
    public string? InterpolationEndDataValue { get; init; }

    /// <summary>EffectValueCalculationPart.mEffectIndex — a 1-based index into the owning
    /// spell's effect-amount table (<see cref="SkillData.EffectAmounts"/>). Riot stores a
    /// spell's per-rank base values there (BIN <c>mSpell.mEffectAmount[i].value</c>) rather
    /// than in DataValues for many champions (e.g. Khazix Q/W/E, Taric E).</summary>
    public int? EffectIndex { get; init; }

    /// <summary>ByCharLevelFormulaCalculationPart.values — a flat per-level array (BIN field
    /// name is literally "values", confirmed in Leona/Khazix/Lux's passive TotalDamage calcs;
    /// no "mFormula"/reference sub-part exists alongside it). <see cref="FormulaInterpreter"/>
    /// indexes this the same way <c>DataValues</c>/<c>EffectAmount</c> arrays already are
    /// (index = champion level directly, clamped to bounds — index 0 is an unused placeholder,
    /// matching the convention <see cref="LookupDataValue"/>-style code already uses). Verified
    /// against Leona's passive Sunlight: values[1]=32, values[18]=151, exactly matching the
    /// live tooltip "32 – 151 (based on level) bonus magic damage".</summary>
    public IReadOnlyList<double> PerLevelValues { get; init; } = Array.Empty<double>();

    // AbilityResourceByCoefficientCalculationPart reuses <see cref="Coefficient"/>
    // (BIN mCoefficient) and scales the caster's ability resource (mana) by it. The live BIN
    // shape carries only mCoefficient (+ an mStatFormula flag captured into StatFormula); it
    // exposes no explicit "max vs bonus vs current" resource selector, so FormulaInterpreter
    // evaluates it as mCoefficient × caster MAX mana (documented approximation — see
    // FormulaInterpreter.EvaluatePart).

    // BuffCounterBy{Coefficient,NamedDataValue}CalculationPart (M25 §11.G stack-count support):
    // a per-STACK term whose value is (mCoefficient — reuses <see cref="Coefficient"/>) or
    // (mDataValue lookup — reuses <see cref="DataValue"/>) MULTIPLIED by a live buff-stack count.
    // The BIN also carries an mBuffName identifying WHICH buff's stacks (e.g. Nasus's Siphoning
    // Strike, Kindred's Mark), but the engine does not observe live buff-stack counts, so
    // FormulaInterpreter multiplies by a caller-supplied stackCount instead — threaded from the
    // user's "몇 스택" knob (ComboNode.UserStackCount), defaulting to 0 = the conservative
    // un-stacked floor (P2, never over-states). The buff name is therefore not resolved.
}
