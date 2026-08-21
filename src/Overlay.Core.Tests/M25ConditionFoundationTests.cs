using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (M25 §11.G foundation, step 1) The conditional-bonus Condition vocabulary + the AutoResolvable
/// vs UserAssumed classification. Pins the P2 boundary: conditions that depend on enemy/positional
/// state the Live Client API cannot observe are UserAssumed (surfaced as a knob, default unmet =
/// RangeMin), while the active player's own resource/mana and combo-sequence facts are auto-resolved.
/// Purely additive — nothing constructs the new ConditionTypes yet (the wiring is step 2), so this
/// documents the contract the wiring will honor.
/// </summary>
public class M25ConditionFoundationTests
{
    [Theory]
    // Enemy / positional state — not exposed by the API -> the user must tell us.
    [InlineData(ConditionType.VsIsolated, true)]
    [InlineData(ConditionType.VsDebuffed, true)]
    [InlineData(ConditionType.MeleeRangeCast, true)]
    [InlineData(ConditionType.HpBelow, true)]      // target current HP is not exposed
    [InlineData(ConditionType.StackGte, true)]     // enemy stack counts are not exposed
    // Resolvable from the active player's own live snapshot or the combo sequence.
    [InlineData(ConditionType.ResourceGte, false)] // own resource (GameSnapshot.ResourceValue)
    [InlineData(ConditionType.ManaGte, false)]     // own mana
    [InlineData(ConditionType.EveryNth, false)]    // combo-sequence ordinal
    [InlineData(ConditionType.OnHitEmpowered, false)] // combo-sequence cast latch
    public void IsUserAssumed_ClassifiesEachCondition(ConditionType type, bool expected)
        => Assert.Equal(expected, ConditionResolution.IsUserAssumed(type));

    [Fact]
    public void UserAssumedConditions_DependOnUnobservableState_AutoResolvableDoNot()
    {
        // The whole point of the split: no UserAssumed condition may be silently assumed true (P2).
        // Sanity that the two buckets are non-empty and disjoint by construction.
        var all = Enum.GetValues<ConditionType>();
        Assert.Contains(all, t => ConditionResolution.IsUserAssumed(t));
        Assert.Contains(all, t => !ConditionResolution.IsUserAssumed(t));
    }
}
