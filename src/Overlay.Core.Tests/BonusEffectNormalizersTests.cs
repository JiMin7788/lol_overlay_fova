using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Items;

namespace Overlay.Core.Tests;

/// <summary>
/// M23 Phase 2 Step 2: proves <see cref="BonusEffectNormalizers"/> classifies each pre-existing
/// bonus-effect source (skill/passive on-hit/on-ability/self, item on-hit/spellblade/stack-then-
/// consume/manual-active-burst) onto the correct <see cref="EffectTrigger"/>/<see cref="Condition"/>
/// pair. Pure mapping tests — no combo pipeline involved (that's Phase 2 Step 3+).
/// </summary>
public class BonusEffectNormalizersTests
{
    private static readonly SkillHit[] SampleHits = { new() { Type = HitDamageType.Magic, Calc = "SomeCalc" } };

    [Theory]
    [InlineData(BonusTrigger.Self, EffectTrigger.Self)]
    [InlineData(BonusTrigger.OnHit, EffectTrigger.OnBasicAttack)]
    [InlineData(BonusTrigger.OnAbility, EffectTrigger.OnAbilityHit)]
    public void FromSkillBonusEffect_MapsTriggerOneToOne_ConditionAlwaysNull(
        BonusTrigger source, EffectTrigger expected)
    {
        var effect = new SkillBonusEffect { Trigger = source, Hits = SampleHits };

        var result = BonusEffectNormalizers.FromSkillBonusEffect(effect);

        Assert.Equal(expected, result.Trigger);
        Assert.Null(result.Condition);
        Assert.Same(SampleHits, result.Hits);
        Assert.Equal(EffectSource.Skill, result.Source);
        Assert.False(result.UserAssumed);
    }

    [Fact]
    public void FromSkillBonusEffect_HonorsExplicitSource()
    {
        var effect = new SkillBonusEffect { Trigger = BonusTrigger.Self, Hits = SampleHits };

        var result = BonusEffectNormalizers.FromSkillBonusEffect(effect, EffectSource.Passive);

        Assert.Equal(EffectSource.Passive, result.Source);
    }

    [Fact]
    public void FromItemEffect_OnHit_MapsToOnBasicAttack_Always()
    {
        var effect = new ItemEffect("3115", ItemTrigger.OnHit, HitDamageType.Magic, new SkillData(), "SomeCalc");

        var result = BonusEffectNormalizers.FromItemEffect(effect);

        Assert.Equal(EffectTrigger.OnBasicAttack, result.Trigger);
        Assert.Null(result.Condition);
        Assert.Equal(EffectSource.Item, result.Source);
    }

    [Fact]
    public void FromItemEffect_Spellblade_MapsToOnHitEmpowered()
    {
        var effect = new ItemEffect("3057", ItemTrigger.Spellblade, HitDamageType.Physical, new SkillData(), "SomeCalc");

        var result = BonusEffectNormalizers.FromItemEffect(effect);

        Assert.Equal(EffectTrigger.OnHitEmpowered, result.Trigger);
        Assert.NotNull(result.Condition);
        Assert.Equal(ConditionType.OnHitEmpowered, result.Condition!.Type);
    }

    [Fact]
    public void FromItemEffect_StackThenConsume_MapsToEveryNth_StacksRequiredPlusOne()
    {
        // Kraken Slayer: StacksRequired=2 -> fires on the 3rd AA (EveryNth(3)).
        var effect = new ItemEffect("3095", ItemTrigger.StackThenConsume, HitDamageType.Physical,
            new SkillData(), "SomeCalc", StacksRequired: 2);

        var result = BonusEffectNormalizers.FromItemEffect(effect);

        Assert.Equal(EffectTrigger.OnBasicAttack, result.Trigger);
        Assert.NotNull(result.Condition);
        Assert.Equal(ConditionType.EveryNth, result.Condition!.Type);
        Assert.Equal(3.0, result.Condition.Value);
    }

    [Fact]
    public void FromItemEffect_ManualActiveBurst_MapsToSelf()
    {
        var effect = new ItemEffect("3128", ItemTrigger.ManualActiveBurst, HitDamageType.True,
            new SkillData(), "SomeCalc");

        var result = BonusEffectNormalizers.FromItemEffect(effect);

        Assert.Equal(EffectTrigger.Self, result.Trigger);
        Assert.Null(result.Condition);
        Assert.False(result.UserAssumed);
    }
}
