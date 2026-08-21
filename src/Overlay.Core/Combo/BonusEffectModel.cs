namespace Overlay.Core.Combo;

/// <summary>
/// M23 Phase 2 — the unified taxonomy every bonus-damage effect (skill on-hit/on-ability/self,
/// item proc, rune) classifies into. See docs/modules/M23_BONUS_EFFECT_MODEL.md +
/// M23_EFFECT_CATALOG.md for the full archetype registry (A1-A16) this vocabulary implements.
///
/// This is Phase 2 Step 1: the type only. Nothing in <see cref="ComboRunner"/> constructs or
/// consumes a <see cref="BonusEffect"/> yet — the existing <c>SkillBonusEffect</c>/<c>ItemProc</c>
/// paths are untouched (see <see cref="BonusEffectNormalizers"/> for the Step 2 adapters that map
/// them onto this model). Purely additive: no behavior change.
/// </summary>
public enum EffectTrigger
{
    /// <summary>Applied to the effect's own node directly (A1: passive/skill direct damage).</summary>
    Self,
    /// <summary>Added to every basic attack (A5: on-hit skill/item procs).</summary>
    OnBasicAttack,
    /// <summary>Added to every ability cast, Q/W/E/R (A8: on-ability skill procs).</summary>
    OnAbilityHit,
    /// <summary>Fires on the next on-hit trigger following an ability cast — spellblade (A7).</summary>
    OnHitEmpowered,
    /// <summary>Duration-scaled DoT/zone damage (A9: PerSecondCalc).</summary>
    Periodic,
    /// <summary>Summoned-pet per-attack-count damage (A10: PerAttackCalc).</summary>
    Summon,
    /// <summary>Ability-cast trigger distinct from a hit landing (reserved; not yet populated by
    /// any normalizer — no current archetype needs it over <see cref="OnAbilityHit"/>).</summary>
    OnCast,
    /// <summary>Fires on a takedown (A16 — out of one-shot combo-instant scope; reserved for
    /// future post-kill modeling, e.g. Dark Harvest/Sudden Impact refresh).</summary>
    OnKill,
}

/// <summary>Where a <see cref="BonusEffect"/> originates — audit/display provenance only, no
/// effect on resolution.</summary>
public enum EffectSource { Skill, Passive, Item, Rune }

/// <summary>
/// The M23 Phase 2 normalized bonus-effect record. One shape for all three sources (skill/item/
/// rune) so a single application pass can consume them (Phase 2 Step 3). <see cref="Condition"/>
/// null means the effect's condition is implicitly "Always" (matches every pre-existing skill
/// bonus effect and item on-hit/spellblade/stack-then-consume proc, none of which had an explicit
/// condition before M23).
/// </summary>
/// <param name="Trigger">When this effect applies (see <see cref="EffectTrigger"/>).</param>
/// <param name="Condition">Null = Always. Otherwise gates the trigger — e.g.
/// <see cref="ConditionType.EveryNth"/> for Kraken Slayer, <see cref="ConditionType.OnHitEmpowered"/>
/// for spellblade.</param>
/// <param name="Hits">The existing per-hit shape resolver input (calc/binSpell/%HP/perSecond/
/// perAttack) — reused as-is; <see cref="BonusEffect"/> only normalizes the trigger/condition/
/// source classification around it, not the hit-shape resolution itself.</param>
/// <param name="Source">Audit/display provenance (Skill/Passive/Item/Rune).</param>
/// <param name="UserAssumed">True when <see cref="Condition"/> depends on live state the API
/// cannot observe (e.g. a future VsImpaired/FirstHit condition) and must be surfaced as a node
/// input rather than auto-evaluated (Hard Rule P2). False for every archetype covered by Phase 2
/// (all auto-resolvable).</param>
public sealed record BonusEffect(
    EffectTrigger Trigger,
    Condition? Condition,
    SkillHit[] Hits,
    EffectSource Source,
    bool UserAssumed = false);
