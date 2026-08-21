using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// Proves the DATA-LAYER query the combo editor's new auto-attach-on-drop feature relies on
/// (<c>ComboSettingsView.AutoAttachOnAbilityBonusEffects</c>, Overlay.Client): when a Q/W/E/R skill
/// node is dropped into a combo, the editor now auto-populates that node's
/// <see cref="ComboNode.UserBonusEffects"/> with every <see cref="BonusTrigger.OnAbility"/> effect
/// <see cref="SkillDamageDb.GetAttachableBonusEffects"/> returns for the champion, instead of
/// requiring a manual "+fx" pick. The WPF glue itself (a private method on a <c>UserControl</c>,
/// wired into a real <c>DragDrop.DoDragDrop</c> drop handler) is not meaningfully unit-testable
/// headlessly — same documented gap as the item drag-and-hold gesture (see M04_COMBO_EDITOR.md's
/// v1.4 changelog entry) — so this instead proves the exact query the glue method performs:
/// <c>GetAttachableBonusEffects(championId).Where(e => e.Effect.Trigger == BonusTrigger.OnAbility)</c>.
/// </summary>
public class AutoAttachOnAbilityEffectsTests : IDisposable
{
    public AutoAttachOnAbilityEffectsTests() => SkillDamageDb.ResetForTests();

    public void Dispose() => SkillDamageDb.ResetForTests();

    private static IReadOnlyList<AttachableBonusEffect> OnAbilityEffects(string championId)
        => SkillDamageDb.GetAttachableBonusEffects(championId)
            .Where(e => e.Effect.Trigger == BonusTrigger.OnAbility)
            .ToList();

    [Fact]
    public void Akali_HasOnAbilityPassive_WouldAutoAttach()
    {
        var onAbility = OnAbilityEffects("Akali");
        var eff = Assert.Single(onAbility);
        Assert.Equal("P", eff.Slot);
        Assert.Equal(BonusTrigger.OnAbility, eff.Effect.Trigger);
        Assert.Contains(eff.Effect.Hits, h => h.Calc == "Damage" && h.Type == HitDamageType.Magic);
    }

    [Fact]
    public void Velkoz_HasOnAbilityPassive_WouldAutoAttach()
    {
        var onAbility = OnAbilityEffects("Velkoz");
        var eff = Assert.Single(onAbility);
        Assert.Equal("P", eff.Slot);
        Assert.Equal(BonusTrigger.OnAbility, eff.Effect.Trigger);
    }

    [Fact]
    public void Sylas_HasNoOnAbilityEffect_SelfPassiveNotOnAbility_WouldNotAutoAttach()
    {
        // Sylas's P is exposed as a Self effect (its own direct damage, T3.3/T8), not OnAbility —
        // the auto-attach feature must not pick it up (only OnAbility triggers qualify).
        var onAbility = OnAbilityEffects("Sylas");
        Assert.Empty(onAbility);

        var all = SkillDamageDb.GetAttachableBonusEffects("Sylas");
        Assert.Contains(all, e => e.Effect.Trigger == BonusTrigger.Self);
    }

    [Fact]
    public void Warwick_OnHitPassive_IsNotOnAbility_WouldNotAutoAttach()
    {
        // Warwick's curated P is OnHit (applies to auto-attacks), a different trigger from
        // OnAbility — confirms the auto-attach filter is trigger-specific, not "any bonus effect".
        var onAbility = OnAbilityEffects("Warwick");
        Assert.Empty(onAbility);

        var all = SkillDamageDb.GetAttachableBonusEffects("Warwick");
        Assert.Contains(all, e => e.Effect.Trigger == BonusTrigger.OnHit);
    }

    [Fact]
    public void UncuratedOrNoBonusEffectChampion_ProducesEmptyList_NoCrash()
    {
        Assert.Empty(OnAbilityEffects("ZZNoSuchChampionForAutoAttachTest"));
    }
}
