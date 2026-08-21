using Overlay.Core.Overlay;

namespace Overlay.Core.Tests;

/// <summary>
/// Proof that the minimap position decoder places every structure at its OWN spot — directly refuting
/// the "all inhibitors collapse to one timer at the old position" symptom. Uses the canonical ids the
/// publisher emits AND the exact raw ids captured live 2026-07-16. If this test is green, a collapse on
/// screen means a STALE BUILD (the running binary predates this logic), not a bug in this logic.
/// </summary>
public class StructureMinimapLayoutTests
{
    // Canonical inhibitor ids (what LiveClientEventPublisher.NormalizeInhibitorId actually publishes).
    // Coordinates are the datamined SR world positions mapped through nx=(x+120)/14990, ny=1-(y+120)/15100.
    [Theory]
    [InlineData("Order_L2", 0.086, 0.756)]
    [InlineData("Order_L1", 0.222, 0.780)]
    [InlineData("Order_L0", 0.238, 0.910)]
    [InlineData("Chaos_L2", 0.759, 0.086)]
    [InlineData("Chaos_L1", 0.782, 0.219)]
    [InlineData("Chaos_L0", 0.916, 0.243)]
    public void Inhibitor_CanonicalIds_MapToTheirOwnDistinctPosition(string id, double nx, double ny)
    {
        var pos = StructureMinimapLayout.Inhibitor(id);
        Assert.NotNull(pos);
        Assert.Equal(nx, pos!.Value.nx, precision: 3);
        Assert.Equal(ny, pos.Value.ny, precision: 3);
    }

    // Raw live ids (2026-07-16 capture) must decode the SAME as their canonical form (no collapse).
    [Theory]
    [InlineData("Inhib_TChaos_L0_P1_2116220407_0", 0.916, 0.243)]  // enemy bot
    [InlineData("Inhib_TChaos_L1_P1_1931666598_0", 0.782, 0.219)]  // enemy mid
    [InlineData("Inhib_TChaos_L2_P1_2351107073_0", 0.759, 0.086)]  // enemy top
    [InlineData("Inhib_TOrder_L0_P1_2971077479_0", 0.238, 0.910)]  // ally bot
    [InlineData("Inhib_TOrder_L1_P1_2786523670_0", 0.222, 0.780)]  // ally mid
    [InlineData("Inhib_TOrder_L2_P1_2669080337_0", 0.086, 0.756)]  // ally top
    public void Inhibitor_RawLiveIds_MapToDistinctPositions(string id, double nx, double ny)
    {
        var pos = StructureMinimapLayout.Inhibitor(id);
        Assert.NotNull(pos);
        Assert.Equal(nx, pos!.Value.nx, precision: 3);
        Assert.Equal(ny, pos.Value.ny, precision: 3);
    }

    [Fact]
    public void AllSixInhibitors_HavePairwiseDistinctPositions_NoCollapse()
    {
        string[] ids = { "Order_L0", "Order_L1", "Order_L2", "Chaos_L0", "Chaos_L1", "Chaos_L2" };
        var positions = ids.Select(i => StructureMinimapLayout.Inhibitor(i)!.Value).ToList();
        Assert.Equal(6, positions.Distinct().Count()); // all six are different points
    }

    [Theory]
    // Canonical + raw live twin-turret ids → four distinct nexus-turret spots.
    [InlineData("Chaos_NexusTop", 0.849, 0.126)]
    [InlineData("Chaos_NexusBot", 0.879, 0.157)]
    [InlineData("Turret_TChaos_L1_P4_392430785_0", 0.849, 0.126)]  // live enemy top twin
    [InlineData("Turret_TChaos_L1_P5_342097928_0", 0.879, 0.157)]  // live enemy bot twin
    [InlineData("Turret_TOrder_L1_P4_3675873665_0", 0.125, 0.842)] // live ally top twin
    [InlineData("Turret_TOrder_L1_P5_3625540808_0", 0.153, 0.872)] // live ally bot twin
    public void NexusTurret_Ids_MapToTheirOwnPosition(string id, double nx, double ny)
    {
        var pos = StructureMinimapLayout.NexusTurret(id);
        Assert.NotNull(pos);
        Assert.Equal(nx, pos!.Value.nx, precision: 3);
        Assert.Equal(ny, pos.Value.ny, precision: 3);
    }

    [Theory]
    [InlineData("Turret_TChaos_L0_P3_511845594_0")]  // a lane turret — not an inhibitor, no lane-0 nexus
    [InlineData("garbage")]
    [InlineData("")]
    public void NexusTurret_NonTwinIds_ReturnNull(string id)
        => Assert.Null(StructureMinimapLayout.NexusTurret(id));
}
