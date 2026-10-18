namespace Overlay.Core.Tests;

/// <summary>Guards for the bundled <c>static-game-data.json</c> (docs/RECURRING_TRAPS.md §2.9).</summary>
public class StaticGameDataTests
{
    /// <summary>Data Dragon publishes no lethality, so this file is the only source; a missing item
    /// silently counts 0 armor pen. These are current Summoner's Rift lethality items.</summary>
    [Theory]
    [InlineData(3179, 18)] // Umbral Glaive
    [InlineData(6694, 15)] // Serylda's Grudge
    [InlineData(6696, 18)] // Axiom Arc
    [InlineData(6697, 18)] // Hubris
    [InlineData(6698, 18)] // Profane Hydra
    [InlineData(6701, 18)] // Opportunity
    [InlineData(3142, 18)] // Youmuu's Ghostblade
    public void CurrentLethalityItemsCarryLethality(int itemId, double lethality)
        => Assert.Equal(lethality, StaticGameDataLoader.Load().GetItem(itemId)?.Lethality ?? 0);
}
