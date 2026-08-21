using System.Runtime.InteropServices;
using Overlay.Core.Vision;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Overlay.Capture;

/// <summary>
/// M31 §1 BINDING RULE enforcement: crop ONLY the calibrated minimap ROI off a full-frame GPU
/// texture and read back just that. A <c>CopySubresourceRegion</c> copies the ROI (GPU→GPU) into
/// a small CPU-readable staging texture; only the staging texture is mapped. The full frame is
/// NEVER mapped to the CPU (a 1440p60 full readback would be ~850 MB/s vs the ROI's ~9 MB/s).
///
/// <para>The staging texture and the output byte buffer are reused across frames (reallocated
/// only when the ROI size changes) to keep per-frame allocation at zero. Produces a tight
/// (stride = width*4) <see cref="MinimapFrame"/>. UNVERIFIED — no GPU in Cowork (§38-B).</para>
/// </summary>
internal sealed class RoiReadback : IDisposable
{
    private readonly ID3D11Device _device;
    private ID3D11Texture2D? _staging;
    private int _stagingW, _stagingH;
    private byte[] _buffer = Array.Empty<byte>();

    public RoiReadback(ID3D11Device device) => _device = device;

    /// <summary>Crop <paramref name="roi"/> from <paramref name="source"/> and read it back as a
    /// <see cref="MinimapFrame"/>. The ROI is clamped to the source bounds. Returns null if the
    /// clamped ROI is empty. The returned frame's buffer is REUSED next call — consumers must copy
    /// out anything they retain (matches the <see cref="IMinimapCaptureSource"/> contract).</summary>
    public MinimapFrame? CropAndRead(
        ID3D11DeviceContext context, ID3D11Texture2D source,
        int roiX, int roiY, int roiW, int roiH, long timestampMs, bool flipped)
    {
        var desc = source.Description;
        // Clamp the ROI into the source frame (calibration is pixel-approximate; never over-read).
        int x = Math.Clamp(roiX, 0, (int)desc.Width);
        int y = Math.Clamp(roiY, 0, (int)desc.Height);
        int w = Math.Clamp(roiW, 0, (int)desc.Width - x);
        int h = Math.Clamp(roiH, 0, (int)desc.Height - y);
        if (w <= 0 || h <= 0) return null;

        EnsureStaging(w, h);

        // GPU→GPU crop of just the ROI box into the staging texture's (0,0).
        var box = new Box(x, y, 0, x + w, y + h, 1);
        context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, source, 0, box);

        // Map ONLY the small staging texture.
        MappedSubresource map = context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int dstStride = w * 4;
            int rowPitch = (int)map.RowPitch;
            for (int row = 0; row < h; row++)
            {
                IntPtr src = IntPtr.Add(map.DataPointer, row * rowPitch);
                Marshal.Copy(src, _buffer, row * dstStride, dstStride);
            }
            return new MinimapFrame(_buffer, w, h, dstStride, timestampMs, flipped);
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }
    }

    private void EnsureStaging(int w, int h)
    {
        if (_staging is not null && _stagingW == w && _stagingH == h) return;

        _staging?.Dispose();
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        _staging = _device.CreateTexture2D(desc);
        _stagingW = w;
        _stagingH = h;

        int needed = w * h * 4;
        if (_buffer.Length < needed) _buffer = new byte[needed];
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _staging = null;
    }
}
