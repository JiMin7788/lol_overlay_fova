namespace Overlay.Core;

/// <summary>
/// (M24 P9) A single HARD-EXECUTE rule: "if the target is at/below a health line,
/// it dies regardless of the raw damage number". This is the KILL-LINE half of the
/// kill-angle model, kept strictly separate from smooth low-HP damage SCALING
/// (Garen R, Akali R, missing-HP bonuses) which are ordinary damage and live in the
/// combo total, never here.
///
/// Design goals (see docs/modules/M24_*.md §P9):
///  - data-only extensibility: a new execute (item / passive / ability / buff) is one
///    row in <c>Data/execute_effects.json</c>, no new code;
///  - one threshold EXPRESSION, not a constant, so stack/level/rank-scaled executes
///    (Locke seal stacks, Aurelion Sol stardust, Draven adoration, Pyke bonus-AD/level)
///    fit the same shape;
///  - the two orthogonal axes that actually change whether a target dies —
///    <see cref="PiercesShield"/> and negation (invuln / undying / min-HP guarantee) —
///    are explicit, not folded into the threshold.
///
/// This type is pure data + arithmetic. It performs no I/O and does not know about the
/// Live Client API; whoever builds an <see cref="ExecuteContext"/> is responsible for
/// sourcing live stack/rank values (or falling back to a user knob — P2 no-inference).
/// </summary>
public readonly struct ExecuteRule
{
    /// <summary>Stable id, e.g. "collector", "elder", "pyke_r", "locke_r".</summary>
    public string Id { get; init; }

    /// <summary>Human label for the overlay/editor, e.g. "징수의 총".</summary>
    public string? Label { get; init; }

    public ExecuteSource Source { get; init; }
    public ExecuteThresholdKind Kind { get; init; }

    // --- Threshold expression terms. threshold(raw) =
    //     Base + PerLevel*(level-1) + PerRank*(rank-1) + PerStack*stacks
    //          + PerAp*AP + PerBonusAd*bonusAD + PerLethality*lethality.
    //     For PercentMaxHp the raw value is a FRACTION (0.05 = 5%) multiplied by the
    //     target's max HP; for FlatHp it is already an absolute HP amount.

    public double Base { get; init; }
    public double PerLevel { get; init; }

    /// <summary>(§8) Optional per-level flat base override, one entry per champion level 1..18.
    /// When present it REPLACES the <c>Base + PerLevel*(level-1)</c> flat term (the other terms —
    /// PerRank/PerStack/PerAp/PerBonusAd/PerLethality — still add on top), letting a piecewise
    /// <c>ByCharLevelBreakpoints</c> curve (Pyke R: 250 flat to L6, then +40/+30/+20/+10 per-level
    /// steps) be modeled exactly without a linear approximation and without FormulaInterpreter —
    /// data-only. Null (the common case) keeps the linear Base+PerLevel term. Indexed by
    /// <c>clamp(level,1,Length)-1</c>.</summary>
    public double[]? BaseByLevel { get; init; }

    public double PerRank { get; init; }
    public double PerStack { get; init; }
    public double PerAp { get; init; }
    public double PerBonusAd { get; init; }
    public double PerLethality { get; init; }

    /// <summary>True: the execute ignores shields (all currently-known real executes —
    /// Collector, Elder, Pyke, Urgot, Syndra, Draven, Aurelion Sol, Zeri, Smolder,
    /// Locke). Default true. A future shield-blocked execute sets this false and the
    /// evaluator then also requires the shield to be gone.</summary>
    public bool PiercesShield { get; init; }

    /// <summary>Minimum stacks required for the rule to be active at all (e.g. Syndra's
    /// R only executes once she has 100 Splinters of Wrath). 0 = no gate.</summary>
    public int GateStacks { get; init; }

    /// <summary>Optional free-form gate note (e.g. "화상 대상") for curation traceability;
    /// not evaluated here.</summary>
    public string? GateNote { get; init; }

    /// <summary>Factory with the sane default <see cref="PiercesShield"/>=true so JSON /
    /// callers that omit it get the common case.</summary>
    public static ExecuteRule Create(
        string id, ExecuteSource source, ExecuteThresholdKind kind, double @base)
        => new()
        {
            Id = id,
            Source = source,
            Kind = kind,
            Base = @base,
            PiercesShield = true,
        };
}

public enum ExecuteSource { Item, Passive, Ability, Buff }

/// <summary>How <see cref="ExecuteRule"/>'s raw threshold value is interpreted.
/// StackVsHp (Mel-style "accumulated damage vs HP+shield") is intentionally ABSENT —
/// that mechanic is ordinary damage compared to HP, not a kill line, so it is not an
/// execute (see M24 v1.1).</summary>
public enum ExecuteThresholdKind
{
    /// <summary>Threshold is a fraction of the target's MAX HP.</summary>
    PercentMaxHp,

    /// <summary>Threshold is an absolute HP amount.</summary>
    FlatHp,
}

/// <summary>Effects on the TARGET that negate ALL executes even shield-piercing ones
/// (invulnerability like Zhonya, undying like Kindred R, or a minimum-HP guarantee).
/// Orthogonal to <see cref="ExecuteRule.PiercesShield"/>.</summary>
[Flags]
public enum ExecuteNegation
{
    None = 0,
    Invulnerable = 1,
    Undying = 2,
    MinHpGuarantee = 4,
}

/// <summary>Live/knob context needed to evaluate an execute's threshold this frame:
/// the caster's ability rank and any stack count driving the threshold, plus the
/// caster level. AP / bonus-AD / lethality are supplied separately by the killable
/// calculator, which already derives them.</summary>
public readonly struct ExecuteContext
{
    /// <summary>Ability rank 1..N (ults are 1..3). Use 1 when not rank-scaled.</summary>
    public int AbilityRank { get; init; }

    /// <summary>Caster level 1..18 (for level-scaled flat executes like Pyke/Zeri).</summary>
    public int CasterLevel { get; init; }

    /// <summary>Stacks driving the threshold (Locke seal / Aurelion stardust /
    /// Draven adoration / Syndra splinters). 0 when unknown or not stack-scaled.</summary>
    public int Stacks { get; init; }
}

/// <summary>Pure evaluator for <see cref="ExecuteRule"/>. No state, no I/O.</summary>
public static class ExecuteEvaluator
{
    /// <summary>The absolute HP line for this rule given context and the caster's
    /// derived offensive stats. For a gated rule below its gate this still returns the
    /// nominal line (callers use <see cref="IsActive"/> to know it is inert).</summary>
    public static double ThresholdHp(
        in ExecuteRule rule, in ExecuteContext ctx,
        double targetMaxHp, double ap, double bonusAd, double lethality)
    {
        int lvl = ctx.CasterLevel > 0 ? ctx.CasterLevel : 1;
        int rank = ctx.AbilityRank > 0 ? ctx.AbilityRank : 1;
        // A per-level table (Pyke R's piecewise ByCharLevelBreakpoints) replaces the linear flat
        // term when present; otherwise the ordinary Base + PerLevel*(level-1). Other terms add on top.
        double flatBase = rule.BaseByLevel is { Length: > 0 } byLevel
            ? byLevel[Math.Clamp(lvl, 1, byLevel.Length) - 1]
            : rule.Base + rule.PerLevel * (lvl - 1);
        double raw = flatBase
            + rule.PerRank * (rank - 1)
            + rule.PerStack * ctx.Stacks
            + rule.PerAp * ap
            + rule.PerBonusAd * bonusAd
            + rule.PerLethality * lethality;
        if (raw < 0) raw = 0;
        return rule.Kind == ExecuteThresholdKind.PercentMaxHp ? raw * targetMaxHp : raw;
    }

    /// <summary>A gated rule is inactive until its stack gate is met.</summary>
    public static bool IsActive(in ExecuteRule rule, in ExecuteContext ctx)
        => rule.GateStacks <= 0 || ctx.Stacks >= rule.GateStacks;

    /// <summary>
    /// Does this execute finish the target, given the combo's mitigated damage?
    /// Model: damage eats the shield first, the remainder eats health; the target dies
    /// if the health left after the combo is at or below the threshold line. A
    /// shield-piercing execute ignores any surviving shield; a non-piercing one also
    /// requires the shield to be gone. Any negation flag disables the execute entirely.
    /// </summary>
    public static bool Kills(
        in ExecuteRule rule, in ExecuteContext ctx,
        double currentHp, double shield, double mitigated,
        double targetMaxHp, double ap, double bonusAd, double lethality,
        ExecuteNegation negation)
    {
        if (!IsActive(rule, ctx)) return false;
        if (negation != ExecuteNegation.None) return false;

        double thr = ThresholdHp(rule, ctx, targetMaxHp, ap, bonusAd, lethality);
        double damageToHealth = Math.Max(0, mitigated - Math.Max(0, shield));
        double healthAfter = Math.Max(0, currentHp - damageToHealth);
        double shieldAfter = Math.Max(0, shield - mitigated);

        if (!rule.PiercesShield && shieldAfter > 0) return false;
        return healthAfter <= thr;
    }
}
