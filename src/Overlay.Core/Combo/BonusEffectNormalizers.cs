using Overlay.Core.Items;

namespace Overlay.Core.Combo;

/// <summary>
/// M23 Phase 2 Step 2 — pure adapters that classify the two pre-existing bonus-effect sources
/// (curated skill bonus effects, item procs) onto the <see cref="BonusEffect"/> vocabulary. Adds
/// NO new call sites: <see cref="ComboRunner"/> still runs its original <c>SkillBonusEffect</c>/
/// <c>ItemProc</c> paths unchanged. These normalizers exist so Phase 2 Step 3 has a single
/// classification surface to build the unified application pass against, and so a coverage-audit
/// test (Phase 2 Step 6) can assert every curated effect maps to a known archetype.
///
/// <b>Item normalizer scope note:</b> a <see cref="SkillHit"/> resolves its number via a champion
/// skill slot's own BIN <c>Calc</c>/<c>binSpell</c> (<see cref="ComboRunner"/>'s
/// <c>TryBuildHitShape</c>); an <see cref="ItemEffect"/> resolves its number via its OWN
/// <see cref="ItemEffect.Skill"/>/<see cref="ItemEffect.Calc"/> pair (or a melee/ranged wiki
/// fraction, or a stack-then-consume level interpolation) through a completely different
/// resolver (<see cref="ItemEffectDb"/> + <c>ComboRunner.ResolveBuildProcs</c>/
/// <c>ComputeItemProc</c>). The two are not hit-shape-compatible, so
/// <see cref="FromItemEffect"/> normalizes ONLY the classification (Trigger/Condition/Source) —
/// <see cref="BonusEffect.Hits"/> is intentionally empty for an item-sourced effect; the actual
/// number still flows through the existing item-proc resolver, unchanged.
/// </summary>
public static class BonusEffectNormalizers
{
    /// <summary>Maps a curated <see cref="SkillBonusEffect"/> (skill/passive on-hit, on-ability,
    /// or self bonus) onto the unified model. 1:1 trigger mapping, condition always null (every
    /// pre-existing skill bonus effect is unconditional — Always), hits carried through as-is
    /// since skill bonus hits ARE <see cref="SkillHit"/>-shaped.</summary>
    public static BonusEffect FromSkillBonusEffect(SkillBonusEffect effect, EffectSource source = EffectSource.Skill)
    {
        var trigger = effect.Trigger switch
        {
            BonusTrigger.Self => EffectTrigger.Self,
            BonusTrigger.OnHit => EffectTrigger.OnBasicAttack,
            BonusTrigger.OnAbility => EffectTrigger.OnAbilityHit,
            _ => EffectTrigger.Self,
        };
        return new BonusEffect(trigger, Condition: null, effect.Hits, source);
    }

    /// <summary>Maps an <see cref="ItemEffect"/>'s trigger onto the unified model's
    /// (Trigger, Condition) pair — classification only, see class doc comment for why
    /// <see cref="BonusEffect.Hits"/> is empty here. <see cref="ItemTrigger.OnHit"/> → every basic
    /// attack, Always. <see cref="ItemTrigger.Spellblade"/> → <see cref="EffectTrigger.OnHitEmpowered"/>
    /// (the first on-hit trigger after an ability cast). <see cref="ItemTrigger.StackThenConsume"/> →
    /// on-basic-attack gated by <see cref="ConditionType.EveryNth"/>(<see cref="ItemEffect.StacksRequired"/> + 1)
    /// (Kraken Slayer: StacksRequired=2 → fires on the 3rd, 6th, … AA). <see cref="ItemTrigger.ManualActiveBurst"/> →
    /// <see cref="EffectTrigger.Self"/> (fires only on the one node the user explicitly drag-attached
    /// it to — already gated by that explicit user action, not a live-state condition, so
    /// <see cref="BonusEffect.UserAssumed"/> stays false).</summary>
    public static BonusEffect FromItemEffect(ItemEffect effect)
    {
        return effect.Trigger switch
        {
            ItemTrigger.OnHit => new BonusEffect(EffectTrigger.OnBasicAttack, null, Array.Empty<SkillHit>(), EffectSource.Item),
            ItemTrigger.Spellblade => new BonusEffect(
                EffectTrigger.OnHitEmpowered, new Condition(ConditionType.OnHitEmpowered, 0),
                Array.Empty<SkillHit>(), EffectSource.Item),
            ItemTrigger.StackThenConsume => new BonusEffect(
                EffectTrigger.OnBasicAttack, new Condition(ConditionType.EveryNth, (effect.StacksRequired ?? 0) + 1),
                Array.Empty<SkillHit>(), EffectSource.Item),
            ItemTrigger.ManualActiveBurst => new BonusEffect(EffectTrigger.Self, null, Array.Empty<SkillHit>(), EffectSource.Item),
            _ => new BonusEffect(EffectTrigger.OnBasicAttack, null, Array.Empty<SkillHit>(), EffectSource.Item),
        };
    }
}
