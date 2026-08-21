using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 475) Which nodes a bonus effect may ride. Before this, the editor offered every effect a
/// champion had under every node, and auto-attached every on-ability effect to all four abilities —
/// so a passive that only two abilities detonate was silently counted on all of them, and an on-hit
/// passive could be dropped onto a spell that never triggers it. Both routes now go through the same
/// applicability check.
///
/// <para>The default comes from the trigger and is honest in both directions: an on-hit effect rides
/// attacks, an on-ability effect rides abilities. Real exceptions are curated — Lux's Illumination is
/// on-hit, but the wiki is explicit that her basic attacks AND Final Spark consume the mark while
/// Q/W/E only apply it.</para>
/// </summary>
public class BonusEffectApplicabilityTests
{
    private static SkillBonusEffect Effect(BonusTrigger trigger, params string[] appliesTo)
        => new() { Trigger = trigger, AppliesTo = appliesTo.Length == 0 ? null : appliesTo };

    [Fact]
    public void OnHitRidesAttacks_OnAbilityRidesAbilities()
    {
        var onHit = Effect(BonusTrigger.OnHit);
        Assert.True(onHit.AppliesToSlot("AA"));
        Assert.False(onHit.AppliesToSlot("Q"));
        Assert.False(onHit.AppliesToSlot("R"));

        var onAbility = Effect(BonusTrigger.OnAbility);
        Assert.False(onAbility.AppliesToSlot("AA"));
        Assert.True(onAbility.AppliesToSlot("Q"));
        Assert.True(onAbility.AppliesToSlot("R"));
    }

    [Fact]
    public void SelfRidesAnything_BecauseTheUserIsAssertingWhenItHappened()
    {
        var self = Effect(BonusTrigger.Self);
        Assert.True(self.AppliesToSlot("AA"));
        Assert.True(self.AppliesToSlot("E"));
    }

    [Fact]
    public void AnExplicitListOverridesTheTriggerDefault()
    {
        // Lux's shape: an on-hit effect that her ultimate also detonates.
        var illumination = Effect(BonusTrigger.OnHit, "AA", "R");
        Assert.True(illumination.AppliesToSlot("AA"));
        Assert.True(illumination.AppliesToSlot("r"));      // case-insensitive
        Assert.False(illumination.AppliesToSlot("Q"));
        Assert.False(illumination.AppliesToSlot("E"));
    }

    [Fact]
    public void LuxIlluminationIsOfferedOnAttacksAndTheUltimateOnly()
    {
        // Against the real curated file, not a hand-built effect.
        Assert.NotEmpty(SkillDamageDb.GetAttachableBonusEffects("Lux", "AA"));
        Assert.NotEmpty(SkillDamageDb.GetAttachableBonusEffects("Lux", "R"));
        Assert.Empty(SkillDamageDb.GetAttachableBonusEffects("Lux", "Q"));
        Assert.Empty(SkillDamageDb.GetAttachableBonusEffects("Lux", "E"));

        // The unfiltered view still returns it once — the filter is a view, not a deletion.
        Assert.Single(SkillDamageDb.GetAttachableBonusEffects("Lux"));
    }

    [Fact]
    public void VexGloomIsOfferedOnAttacksQAndW_ButNotEOrR()
    {
        // The user's report, pinned against the real file. The wiki is explicit: a basic attack or a
        // BASIC ability detonates Gloom, Looming Darkness (E) applies the mark but cannot consume it,
        // and the ultimate is not a basic ability. So three of five carry it.
        Assert.NotEmpty(SkillDamageDb.GetAttachableBonusEffects("Vex", "AA"));
        Assert.NotEmpty(SkillDamageDb.GetAttachableBonusEffects("Vex", "Q"));
        Assert.NotEmpty(SkillDamageDb.GetAttachableBonusEffects("Vex", "W"));
        Assert.Empty(SkillDamageDb.GetAttachableBonusEffects("Vex", "E"));
        Assert.Empty(SkillDamageDb.GetAttachableBonusEffects("Vex", "R"));
    }

    [Fact]
    public void PantheonsEmpoweredCastsStayOnTheirOwnAbility()
    {
        // Mortal Will empowers the next basic ability, and each ability has its own empowered calc.
        // The empowered W in particular CONTAINS its three strikes - it is not carried by a later
        // basic attack - so neither effect may be hung on the other's slot.
        Assert.Single(SkillDamageDb.GetAttachableBonusEffects("Pantheon", "Q"));
        Assert.Single(SkillDamageDb.GetAttachableBonusEffects("Pantheon", "W"));
        Assert.Empty(SkillDamageDb.GetAttachableBonusEffects("Pantheon", "E"));
        Assert.Empty(SkillDamageDb.GetAttachableBonusEffects("Pantheon", "AA"));
    }

    [Fact]
    public void AnOnDeathPassiveIsNotOfferedAsARider()
    {
        // Kog'Maw's Icathian Surprise fires when he dies; it rides nothing. It stays addable as its
        // own P node, which is why the curation marks it rather than deleting the hit.
        Assert.Empty(SkillDamageDb.GetAttachableBonusEffects("KogMaw", "Q"));
        Assert.Empty(SkillDamageDb.GetAttachableBonusEffects("KogMaw", "R"));
        // W's Bio-Arcane Barrage is a genuine on-hit empowerment and is unaffected.
        Assert.NotEmpty(SkillDamageDb.GetAttachableBonusEffects("KogMaw", "AA"));
    }
}
