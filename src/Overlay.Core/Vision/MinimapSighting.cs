namespace Overlay.Core.Vision;

/// <summary>A point in normalized 0..1 minimap-ROI space (0,0 = ROI top-left, 1,1 = ROI
/// bottom-right). NOT flip/anchor-corrected — see <see cref="MinimapSighting"/> doc.</summary>
public readonly record struct MapPosition01(double X, double Y);

/// <summary>
/// M31 P2 output (docs/modules/M31_MINIMAP_VISION.md §1/§3): one identified enemy champion icon
/// on the minimap, for a single frame.
///
/// <para><b><see cref="MapPos01"/> is a raw ROI-relative centroid, 0..1 within the captured
/// frame — NOT map space.</b> Any flip-awareness or anchor/orientation transform into "real" map
/// coordinates is explicitly OUT OF SCOPE for this class (spec §3 stage 2 assigns that to
/// P1/P3); <see cref="MinimapDetector"/> only reports where in the CAPTURED ROI the icon sits.</para>
///
/// <para><b>P2 policy note (no-inference / Last-Seen doctrine):</b> this is a direct read of a
/// currently-visible icon's position in the current frame, never a predicted or extrapolated
/// position — consistent with P2 (no inference beyond what the user's own screen currently
/// shows). Turning "last sighting" into a Last-Seen alert is P3's job.</para>
/// </summary>
/// <param name="ChampionId">Identity of the best-matching <see cref="EnemyTemplate"/>.</param>
/// <param name="MapPos01">Candidate centroid, normalized 0..1 within the ROI (see class doc —
/// not map-space-transformed).</param>
/// <param name="Confidence">Similarity score of the winning template match, 0..1 (see
/// <see cref="MinimapDetector"/> for the color-distance method).</param>
/// <param name="TimestampMs">Carried from the source <see cref="MinimapFrame.TimestampMs"/>.</param>
public readonly record struct MinimapSighting(
    string ChampionId, MapPosition01 MapPos01, double Confidence, long TimestampMs);
