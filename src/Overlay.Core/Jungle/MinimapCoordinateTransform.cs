using Overlay.Core.Vision;

namespace Overlay.Core.Jungle;

/// <summary>
/// M31 P3: the ROI-relative -&gt; canonical map-space transform that P2's <see cref="MinimapSighting"/>
/// doc comment explicitly leaves unowned ("out of scope for this class... spec §3 stage 2 assigns
/// that to P1/P3"). <see cref="MinimapDetector"/> reports <c>MapPos01</c> as a raw 0..1 position
/// within the captured ROI crop, top-left origin, NOT corrected for the user's own
/// <c>FlipMiniMap</c> setting (that flag lives on <c>MinimapFrame.Flipped</c>, one level up from
/// <see cref="MinimapSighting"/>, so a caller wiring frame -&gt; <c>Detect()</c> -&gt; sighting must
/// carry it through separately — see <see cref="ToMapSpace"/>'s <c>flipped</c> parameter).
///
/// <para><b>Canonical map space</b> (what <see cref="Jungle.MapZoneLookup"/>'s zone table is
/// authored against, see <c>data/map_regions.json</c>'s own doc note): 0..1, x right / y down,
/// FIXED orientation as if <c>FlipMiniMap</c> were OFF — (0,0) is always the order/blue-base
/// corner's side of the map, regardless of which team the active player is on or whether their
/// client happens to be flipped.</para>
///
/// <para><b>ASSUMPTION — unverified against a real flipped capture</b> (no native capture/live
/// game available in this sandbox; see <c>CLAUDE_CODE_TODO.md</c> §38): League's <c>FlipMiniMap</c>
/// is understood to mirror the rendered map content 180° (both axes), not just relocate the HUD
/// widget on screen, so that the flipped viewer's own base still reads "bottom-left" from THEIR
/// screen. Undoing that to reach the canonical (always-order-base) frame is therefore also a
/// 180° reflection. If a real flipped capture shows this is wrong (e.g. only a single-axis
/// mirror), this is the one function to fix — nothing else in P3 encodes flip logic.</para>
/// </summary>
public static class MinimapCoordinateTransform
{
    public static (double X01, double Y01) ToMapSpace(MapPosition01 roiPos, bool flipped)
        => flipped ? (1.0 - roiPos.X, 1.0 - roiPos.Y) : (roiPos.X, roiPos.Y);
}
