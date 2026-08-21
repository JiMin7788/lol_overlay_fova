using Vortice.Direct3D11;

namespace Overlay.Capture;

/// <summary>
/// Internal M31 §1 backend abstraction: delivers full-frame GPU textures from one of the two
/// capture technologies (WGC or DXGI Desktop Duplication). The orchestrator crops the calibrated
/// ROI off each delivered texture (<see cref="RoiReadback"/>) — so the full frame never leaves
/// the GPU here.
///
/// <para><paramref name="onFrame"/> is invoked SYNCHRONOUSLY while the texture is still alive
/// (WGC: before the capture frame is disposed; DXGI: before <c>ReleaseFrame</c>). The handler
/// must finish its GPU copy before returning; it must not retain the texture.</para>
/// </summary>
internal interface IFrameProvider : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Begin delivering frames. <paramref name="onFrame"/> = (fullFrameTexture,
    /// timestampMs), called on a background/pool thread.</summary>
    void Start(Action<ID3D11Texture2D, long> onFrame);

    void Stop();
}
