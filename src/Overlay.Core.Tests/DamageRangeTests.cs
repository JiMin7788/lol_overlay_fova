using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// M24 P1: the <see cref="DamageRange"/> uncertainty-range primitive — Single/Zero/Width, the
/// crit-range builder + its degenerate fallback, and the sum/scalar/amplify arithmetic P2/P3/P4
/// fold their axes through. Pure value-type tests (no engine); the combo-level wiring
/// (ComboResult.RangeMin/Max == CritMin/Max) is proved in
/// <see cref="ComboEngineTests"/>.
/// </summary>
public class DamageRangeTests
{
    [Fact]
    public void Single_IsCertain_MinEqualsMax_WidthZero()
    {
        var r = DamageRange.Single(42);
        Assert.Equal(42, r.Min);
        Assert.Equal(42, r.Max);
        Assert.Equal(0, r.Width);
    }

    [Fact]
    public void Zero_IsEmpty()
    {
        Assert.Equal(0, DamageRange.Zero.Min);
        Assert.Equal(0, DamageRange.Zero.Max);
    }

    [Fact]
    public void Width_IsMaxMinusMin()
    {
        Assert.Equal(30, new DamageRange(70, 100).Width);
    }

    [Fact]
    public void FromCritRange_NormalEndpoints_UsesThem()
    {
        var r = DamageRange.FromCritRange(min: 80, max: 120, fallback: 100);
        Assert.Equal(80, r.Min);
        Assert.Equal(120, r.Max);
    }

    [Fact]
    public void FromCritRange_DegenerateEndpoints_FallsBackToSingle()
    {
        // A synthetic/empty DamageCalcResult carries 0 crit endpoints -> collapse to the certain total.
        var r = DamageRange.FromCritRange(min: 0, max: 0, fallback: 250);
        Assert.Equal(250, r.Min);
        Assert.Equal(250, r.Max);
    }

    [Fact]
    public void FromCritRange_OrdersEndpoints_MinNeverAboveMax()
    {
        var r = DamageRange.FromCritRange(min: 120, max: 80, fallback: 100);
        Assert.Equal(80, r.Min);
        Assert.Equal(120, r.Max);
    }

    [Fact]
    public void Sum_AddsFloorsAndCeilingsIndependently()
    {
        var total = new DamageRange(10, 20) + new DamageRange(3, 7);
        Assert.Equal(13, total.Min);
        Assert.Equal(27, total.Max);
    }

    [Fact]
    public void ScalarMultiply_ScalesBothEndpoints_EitherOrder()
    {
        var a = new DamageRange(10, 20) * 1.5;
        var b = 1.5 * new DamageRange(10, 20);
        Assert.Equal(15, a.Min);
        Assert.Equal(30, a.Max);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Amplify_MultipliesBothEndpointsBy1PlusAmp()
    {
        var r = new DamageRange(100, 200).Amplify(0.12); // certain +12%
        Assert.Equal(112, r.Min, 6);
        Assert.Equal(224, r.Max, 6);
    }
}
