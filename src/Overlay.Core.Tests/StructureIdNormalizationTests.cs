using Overlay.Core;

namespace Overlay.Core.Tests;

/// <summary>
/// Proof for <see cref="LiveClientEventPublisher"/>'s structure-id normalizers, which collapse the
/// volatile per-game/per-instance Live Client ids (e.g. "Inhib_TChaos_L2_P1_2351107073_0",
/// "Turret_TChaos_L1_P4_392430785_0") into a STABLE structural key before publishing — so the
/// InhibRespawned↔InhibKilled match and the timers' NO-INTERFERENCE dedup key on the structure, not
/// the instance. Uses the three REAL ids captured live 2026-07-16, plus legacy fixture ids and an
/// unparseable-passthrough case.
/// </summary>
public class StructureIdNormalizationTests
{
    [Theory]
    // Live-confirmed ids from the full 2026-07-16 capture (BOTH teams; numeric instance tail dropped).
    // Lane: L0=bot, L1=mid, L2=top. All inhibitors are position P1.
    [InlineData("Inhib_TChaos_L0_P1_2116220407_0", "Chaos_L0")]  // enemy BOT inhibitor
    [InlineData("Inhib_TChaos_L1_P1_1931666598_0", "Chaos_L1")]  // enemy MID inhibitor
    [InlineData("Inhib_TChaos_L2_P1_2351107073_0", "Chaos_L2")]  // enemy TOP inhibitor
    [InlineData("Inhib_TOrder_L0_P1_2971077479_0", "Order_L0")]  // ally BOT inhibitor
    [InlineData("Inhib_TOrder_L1_P1_2786523670_0", "Order_L1")]  // ally MID inhibitor
    [InlineData("Inhib_TOrder_L2_P1_2669080337_0", "Order_L2")]  // ally TOP inhibitor
    // Legacy fixture id WITH an _L token normalizes; one WITHOUT (_C1/_R1) passes through raw by design
    // (this is why LiveClientEventPublisherTests can still assert the raw "Barracks_T2_C1").
    [InlineData("Barracks_T1_L1", "Order_L1")]
    [InlineData("Barracks_T2_C1", "Barracks_T2_C1")]            // no _L token → raw passthrough
    // Unparseable (no team token) → raw passthrough (timer still works; minimap placement skipped).
    [InlineData("something_unexpected", "something_unexpected")]
    public void NormalizeInhibitorId_CollapsesToStableTeamLaneKey(string raw, string expected)
        => Assert.Equal(expected, LiveClientEventPublisher.NormalizeInhibitorId(raw));

    [Theory]
    // Live-confirmed twin-turret ids: L1_P4 = top-side, L1_P5 = bot-side.
    [InlineData("Turret_TChaos_L1_P4_392430785_0", "Chaos_NexusTop")]
    [InlineData("Turret_TChaos_L1_P5_342097928_0", "Chaos_NexusBot")]
    [InlineData("Turret_TOrder_L1_P4_111222333_0", "Order_NexusTop")]
    // Legacy datamined twin ids: _C_02 = top, _C_01 = bot.
    [InlineData("Turret_T1_C_02_A", "Order_NexusTop")]
    [InlineData("Turret_T1_C_01_A", "Order_NexusBot")]
    // A team-tagged id that is not a twin turret passes through raw (only ever reached defensively).
    [InlineData("Turret_TChaos_L2_P1_555000_0", "Turret_TChaos_L2_P1_555000_0")]
    // Unparseable (no team) → raw passthrough.
    [InlineData("mystery", "mystery")]
    public void NormalizeNexusTurretId_CollapsesToStableNexusKey(string raw, string expected)
        => Assert.Equal(expected, LiveClientEventPublisher.NormalizeNexusTurretId(raw));
}
