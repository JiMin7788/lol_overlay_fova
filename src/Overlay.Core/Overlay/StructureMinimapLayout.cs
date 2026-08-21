namespace Overlay.Core.Overlay;

/// <summary>
/// Pure, unit-testable mapping from a (canonical or raw) Live Client structure id to its APPROXIMATE
/// normalized minimap position (0..1, origin top-left). Extracted out of the WPF <c>OverlayHost</c> so
/// the exact decode that decides WHERE each inhibitor / Nexus-turret timer chip is drawn can be proven
/// in tests (the host itself is not test-buildable off-Windows).
///
/// <para>Ids arrive already normalized by <see cref="LiveClientEventPublisher"/> — inhibitors as
/// <c>"Order_L2"</c>/<c>"Chaos_L1"</c>, Nexus turrets as <c>"Order_NexusTop"</c>/<c>"Chaos_NexusBot"</c> —
/// but the raw live shapes (<c>Inhib_TChaos_L2_P1_…</c>, <c>Turret_TChaos_L1_P4_…</c>) and the legacy
/// datamined shapes are ALSO accepted as fallbacks. Team: Order = blue (bottom-left base), Chaos = red
/// (top-right base). Lanes: L2=top, L1=mid, L0=bot (live-confirmed L2=top). Coordinates are
/// calibration-approximate — tune if slightly off the real icons.</para>
/// </summary>
public static class StructureMinimapLayout
{
    // Normalized minimap positions below are DATA-DRIVEN from the datamined Summoner's Rift world
    // coordinates (hextechdocs.dev/map-data) and the map bounds min(-120,-120) max(14870,14980):
    //   nx = (worldX + 120) / 14990,  ny = 1 - (worldY + 120) / 15100   (screen-y is world-y flipped).
    // e.g. BLUE_MID_LANE_INHIBITOR {3203,3208} → (0.222, 0.780). Accurate to the real icons, not eyeballed.

    /// <summary>Normalized minimap position of a destroyed inhibitor, or null if unplaceable.</summary>
    public static (double nx, double ny)? Inhibitor(string id)
    {
        bool? order = IsOrder(id);
        if (order is null) return null;
        int lane = FirstLaneDigit(id); // L0=bot, L1=mid, L2=top; -1 if none
        return (order.Value, lane) switch
        {
            (true, 2)  => (0.086, 0.756),  // Order top inhibitor (world 1171,3571)
            (true, 1)  => (0.222, 0.780),  // Order mid (3203,3208)
            (true, 0)  => (0.238, 0.910),  // Order bot (3452,1236)
            (false, 2) => (0.759, 0.086),  // Chaos top (11261,13676)
            (false, 1) => (0.782, 0.219),  // Chaos mid (11598,11667)
            (false, 0) => (0.916, 0.243),  // Chaos bot (13604,11316)
            _ => null,
        };
    }

    /// <summary>Normalized minimap position of a destroyed Nexus ("twin") turret, or null.
    /// Canonical "NexusTop"/"NexusBot", with fallback: live _L1_P4 / legacy _C_02 = top;
    /// live _L1_P5 / legacy _C_01 = bot.</summary>
    public static (double nx, double ny)? NexusTurret(string id)
    {
        bool? order = IsOrder(id);
        if (order is null) return null;
        bool top;
        if (id.Contains("NexusTop", System.StringComparison.OrdinalIgnoreCase)) top = true;
        else if (id.Contains("NexusBot", System.StringComparison.OrdinalIgnoreCase)) top = false;
        else if (id.Contains("_L1_P4", System.StringComparison.OrdinalIgnoreCase)
                 || id.Contains("_C_02", System.StringComparison.OrdinalIgnoreCase)) top = true;
        else if (id.Contains("_L1_P5", System.StringComparison.OrdinalIgnoreCase)
                 || id.Contains("_C_01", System.StringComparison.OrdinalIgnoreCase)) top = false;
        else return null;
        return (order.Value, top) switch
        {
            (true, true)   => (0.125, 0.842),  // Order top-nexus turret (world 1748,2270)
            (true, false)  => (0.153, 0.872),  // Order bot-nexus turret (2177,1807)
            (false, true)  => (0.849, 0.126),  // Chaos top-nexus turret (12611,13084)
            (false, false) => (0.879, 0.157),  // Chaos bot-nexus turret (13052,12612)
        };
    }

    /// <summary>Team of a canonical or raw structure id: true = Order (blue), false = Chaos (red),
    /// null = unknown. Matches canonical "Order"/"Chaos" and raw "TOrder"/"TChaos"/"_T1_"/"_T2_".</summary>
    public static bool? IsOrder(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (id.Contains("Order", System.StringComparison.OrdinalIgnoreCase)) return true;   // "Order_L2" / "TOrder"
        if (id.Contains("Chaos", System.StringComparison.OrdinalIgnoreCase)) return false;  // "Chaos_L1" / "TChaos"
        if (id.Contains("_T1_", System.StringComparison.OrdinalIgnoreCase)) return true;    // legacy fixtures
        if (id.Contains("_T2_", System.StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>Digit of the first "_L{digit}" token (L0=bot/L1=mid/L2=top), or -1 if none. Mirrors
    /// <see cref="LiveClientEventPublisher"/>'s lane parse so decoder and normalizer agree.</summary>
    public static int FirstLaneDigit(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; i + 2 < id.Length; i++)
            if (id[i] == '_' && (id[i + 1] == 'L' || id[i + 1] == 'l') && char.IsAsciiDigit(id[i + 2]))
                return id[i + 2] - '0';
        return -1;
    }
}
