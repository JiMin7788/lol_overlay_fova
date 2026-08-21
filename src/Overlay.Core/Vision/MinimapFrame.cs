namespace Overlay.Core.Vision;

/// <summary>
/// M31 P1 output contract: one captured minimap ROI, handed to P2 (<c>MinimapDetector</c>).
///
/// <para>The <see cref="Bgra"/> buffer is the SMALL (~256–320 px square) crop the GPU produced
/// via <c>CopySubresourceRegion</c>. Per the M31 §1 BINDING RULE the full game frame NEVER
/// reaches the CPU — only this ROI is mapped and read back (~9 MB/s at 30 fps vs ~850 MB/s for
/// a full-frame readback). This type therefore models JUST the minimap region, never a full
/// screenshot.</para>
///
/// <para>Pixel layout is 8-bit BGRA, row-major, top-left origin (the D3D11 staging-texture
/// layout). Rows are <see cref="Stride"/> bytes apart — a mapped staging texture may pad each
/// row past <c>Width*4</c>, so consumers MUST index with <see cref="Stride"/>, not
/// <c>Width*4</c>. Use <see cref="PixelOffset"/> to locate a pixel.</para>
///
/// <para><see cref="Flipped"/> carries the calibrated minimap orientation (game.cfg
/// <c>FlipMiniMap</c>, M31 §2 layer 0) so P2's ROI-relative → 0..1 map-space transform can be
/// flip-aware without re-reading config. This is metadata about the user's OWN HUD, not any
/// inferred game state — P1/P2-neutral.</para>
/// </summary>
public readonly struct MinimapFrame
{
    /// <summary>BGRA8 pixels, row-major, top-left origin. Length is at least
    /// <see cref="Stride"/> × <see cref="Height"/>. Owned by the capture source; treat as
    /// read-only and do not retain past the <c>FrameCaptured</c> callback (the source may
    /// reuse the buffer for the next frame).</summary>
    public byte[] Bgra { get; }

    /// <summary>ROI width in pixels.</summary>
    public int Width { get; }

    /// <summary>ROI height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row. ≥ <see cref="Width"/> × 4; a mapped D3D11 staging texture may
    /// pad rows, so this can exceed the tight <c>Width*4</c>.</summary>
    public int Stride { get; }

    /// <summary>Capture timestamp in monotonic milliseconds (source clock). Used by P3's
    /// presence debounce/Last-Seen timing.</summary>
    public long TimestampMs { get; }

    /// <summary>True when the minimap is flipped (game.cfg <c>FlipMiniMap</c>): the player's
    /// team base sits top-right rather than bottom-left. Governs P2's map-coordinate transform.</summary>
    public bool Flipped { get; }

    public MinimapFrame(byte[] bgra, int width, int height, int stride, long timestampMs, bool flipped)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < width * 4) throw new ArgumentOutOfRangeException(nameof(stride),
            "stride must be at least width*4 for BGRA8");
        if (bgra.Length < (long)stride * height) throw new ArgumentException(
            "buffer is smaller than stride*height", nameof(bgra));

        Bgra = bgra;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampMs = timestampMs;
        Flipped = flipped;
    }

    /// <summary>Byte offset of pixel (<paramref name="x"/>, <paramref name="y"/>) within
    /// <see cref="Bgra"/>. The four bytes at the offset are B, G, R, A in that order.</summary>
    public int PixelOffset(int x, int y) => y * Stride + x * 4;
}
