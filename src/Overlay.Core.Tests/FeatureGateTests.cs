using Overlay.Core.Flags;

namespace Overlay.Core.Tests;

/// <summary>
/// lolF05 feature gate: full truth table of <see cref="FeatureGate.ShouldRun"/> and
/// <see cref="FeatureGate.HudOverride"/> for beta and non-beta builds.
/// </summary>
public class FeatureGateTests
{
    public static IEnumerable<object[]> ShouldRunTable()
    {
        foreach (var feature in new[] { "enemyJunglerSpotted", "enemyItemAlert", "recallTimer", FeatureGate.MinimapVision })
        {
            bool alwaysOn = feature is "enemyJunglerSpotted" or "enemyItemAlert";
            foreach (var configEnabled in new[] { false, true })
            foreach (var isBeta in new[] { false, true })
            foreach (var isOff in new[] { false, true })
            {
                bool expected = !isOff && (configEnabled || (isBeta && alwaysOn));
                yield return new object[] { feature, configEnabled, isBeta, isOff, expected };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ShouldRunTable))]
    public void ShouldRun_TruthTable(string feature, bool configEnabled, bool isBeta, bool isOff, bool expected)
        => Assert.Equal(expected, FeatureGate.ShouldRun(feature, configEnabled, isBeta, isOff));

    [Fact]
    public void ShouldRun_SpotChecks()
    {
        // Beta, toggled off, not killed: the always-on pair runs, others don't.
        Assert.True(FeatureGate.ShouldRun("enemyJunglerSpotted", configEnabled: false, isBeta: true, isOff: false));
        Assert.False(FeatureGate.ShouldRun("recallTimer", configEnabled: false, isBeta: true, isOff: false));
        // Non-beta: the always-on pair follows its toggle.
        Assert.False(FeatureGate.ShouldRun("enemyItemAlert", configEnabled: false, isBeta: false, isOff: false));
        // Killed always wins.
        Assert.False(FeatureGate.ShouldRun("enemyItemAlert", configEnabled: true, isBeta: true, isOff: true));
    }

    [Theory]
    [InlineData("enemyJunglerSpotted")]
    [InlineData("enemyItemAlert")]
    [InlineData("recallTimer")]
    [InlineData("minimapVision")]
    public void HudOverride_IsNull_InNonBeta(string key)
    {
        Assert.Null(FeatureGate.HudOverride(key, isBeta: false, isOff: false));
        Assert.Null(FeatureGate.HudOverride(key, isBeta: false, isOff: true));
    }

    [Theory]
    [InlineData("recallTimer")]
    [InlineData("laneReturn")]
    [InlineData("minimapVision")]
    [InlineData("buffStackVision")]
    public void HudOverride_IsNull_ForKeysOutsideTheSet_InBeta(string key)
    {
        Assert.Null(FeatureGate.HudOverride(key, isBeta: true, isOff: false));
        Assert.Null(FeatureGate.HudOverride(key, isBeta: true, isOff: true));
    }

    [Theory]
    [InlineData("enemyJunglerSpotted", false, true)]
    [InlineData("enemyJunglerSpotted", true, false)]
    [InlineData("enemyItemAlert", false, true)]
    [InlineData("enemyItemAlert", true, false)]
    public void HudOverride_IsNotOff_ForTheAlwaysOnPair_InBeta(string key, bool isOff, bool expected)
        => Assert.Equal(expected, FeatureGate.HudOverride(key, isBeta: true, isOff));

    [Fact]
    public void Lookups_AreCaseInsensitive()
    {
        Assert.Contains("ENEMYJUNGLERSPOTTED", FeatureGate.BetaAlwaysOn);
        Assert.True(FeatureGate.ShouldRun("ENEMYITEMALERT", configEnabled: false, isBeta: true, isOff: false));
        Assert.Equal(true, FeatureGate.HudOverride("enemyitemalert", isBeta: true, isOff: false));
    }

    [Fact]
    public void Constants_AndAlwaysOnSet()
    {
        Assert.Equal("minimapVision", FeatureGate.MinimapVision);
        Assert.Equal("buffStackVision", FeatureGate.BuffStackVision);
        Assert.Equal(2, FeatureGate.BetaAlwaysOn.Count);
        Assert.Contains("enemyJunglerSpotted", FeatureGate.BetaAlwaysOn);
        Assert.Contains("enemyItemAlert", FeatureGate.BetaAlwaysOn);
    }
}
