namespace Overlay.Core.Vision;

/// <summary>
/// M31 P2 (docs/modules/M31_MINIMAP_VISION.md §3 stage 2): a small, circle-masked, downscaled
/// color reference for one known enemy champion's minimap icon, used by
/// <see cref="MinimapDetector"/> to identify which of the (≤5) known enemies a surviving
/// red-ring candidate actually is.
///
/// <para><b>Out of scope (deliberately):</b> fetching the source square portrait from Data
/// Dragon is P1/a future round's job — <see cref="FromSquareIcon"/> is a PURE function that
/// takes already-decoded raw BGRA bytes. No HTTP, no file I/O, no DDragon knowledge here.</para>
/// </summary>
public sealed class EnemyTemplate
{
    /// <summary>Fixed output size (both dimensions) every template is downscaled to. 16×16 was
    /// chosen as the smallest size that still preserves enough color-identity signal to tell
    /// five champions' portraits apart by mean color (this is a coarse color-distance match,
    /// not a shape/feature match — see <see cref="MinimapDetector"/> class doc) while keeping
    /// the per-candidate comparison cost trivial (256 pixels × ≤5 templates per frame).</summary>
    public const int Size = 16;

    /// <summary>Fraction of the inscribed radius used for matching. Trims the team-coloured ring
    /// without discarding the band that tells champions apart.
    ///
    /// <para><b>Corrected 2026-07-20 from 0.68, which was wrong.</b> The 0.68 figure came from a
    /// SIMULATION that painted a ring over the outer 28% of the icon; real captures show a far
    /// thinner ring, so 0.68 was throwing away a third of the portrait — and that discarded band is
    /// where several champions carry their identifying colour. The concrete failure: a 21-frame run
    /// on a crop that is visually SERAPHINE (pink hair, pale face) was reported as Sett, because her
    /// distinguishing hair sits outside 0.68 while her pale centre resembles Sett's mid-tone. Scoring
    /// that same crop across fractions, the winner flips to the correct champion at 0.85 and holds
    /// with a comfortable margin by 0.90. This is why the user saw the same champion both MISSED and
    /// reported in places he was not: his template had become the answer for other champions' faces.</para>
    ///
    /// <para>Measured on 150 real frames, detections are flat from 0.68 to 0.88 (105 vs 104) and fall
    /// off by 1.00 (91), so widening the mask costs nothing here while restoring discrimination.</para></summary>
    public const double MatchRadiusFraction = 0.90;

    /// <summary>Champion identity this template represents (e.g. DDragon champion id).</summary>
    public string ChampionId { get; }

    /// <summary>False for an ALLY champion, present in the match set only so the detector has
    /// something correct to lose to.
    ///
    /// <para>(2026-07-20, live feedback) Building templates for enemies only meant an ally icon
    /// that survived the red prefilter — an ally Gragas, whose portrait is red-heavy — had no
    /// correct answer available and was forced onto whichever enemy happened to be nearest in
    /// color. It was reported as an enemy Anivia. Matching against the full roster and then
    /// dropping ally winners turns that class of error into a silent, correct rejection.</para></summary>
    public bool IsEnemy { get; }

    /// <summary>Downscaled BGRA pixels, tightly packed (<see cref="Size"/> × <see cref="Size"/> ×
    /// 4 bytes, row-major, no stride padding).</summary>
    public byte[] Bgra { get; }

    /// <summary>Per-pixel circular-mask flags, same row-major indexing as <see cref="Bgra"/>
    /// (index = y * <see cref="Size"/> + x). Only pixels inside the inscribed circle are
    /// <c>true</c> — corner pixels (outside the circle) are excluded from matching because a
    /// square DDragon portrait has background/frame detail in the corners that a round minimap
    /// icon never shows, and letting it influence the match would bias against the real icon.</summary>
    public bool[] CircleMask { get; }

    private EnemyTemplate(string championId, bool isEnemy, byte[] bgra, bool[] circleMask)
    {
        ChampionId = championId;
        IsEnemy = isEnemy;
        Bgra = bgra;
        CircleMask = circleMask;
    }

    /// <summary>
    /// Builds a template from a raw square BGRA icon buffer (e.g. a decoded DDragon champion
    /// square portrait — decoding/fetching happens elsewhere, out of scope here).
    /// </summary>
    /// <param name="bgra">Tightly packed BGRA8 pixels, row-major, length must equal
    /// <paramref name="width"/> × <paramref name="height"/> × 4.</param>
    /// <param name="width">Source icon width in pixels.</param>
    /// <param name="height">Source icon height in pixels. Must equal <paramref name="width"/> —
    /// DDragon square portraits are square by construction; a non-square input is a caller bug.</param>
    /// <param name="championId">Champion identity to stamp on the resulting template.</param>
    /// <param name="targetSize">Output size (both dimensions); defaults to <see cref="Size"/>.
    /// Exposed for testing, but production callers should use the default so every template in
    /// a match set is directly comparable.</param>
    /// <param name="isEnemy">See <see cref="IsEnemy"/>. Defaults true so existing enemy-only
    /// callers and fixtures keep their meaning; pass false for the ally decoys.</param>
    public static EnemyTemplate FromSquareIcon(
        byte[] bgra, int width, int height, string championId, int targetSize = Size, bool isEnemy = true)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentException.ThrowIfNullOrEmpty(championId);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "icon dimensions must be positive");
        if (width != height)
            throw new ArgumentException("source icon must be square", nameof(height));
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("buffer length must equal width*height*4", nameof(bgra));
        if (targetSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetSize));

        byte[] downscaled = MinimapPixelSampling.NearestNeighborDownscale(
            bgra, stride: width * 4, srcX: 0, srcY: 0, srcSize: width, targetSize: targetSize);
        bool[] mask = MinimapPixelSampling.BuildCircleMask(targetSize, MatchRadiusFraction);

        return new EnemyTemplate(championId, isEnemy, downscaled, mask);
    }
}
