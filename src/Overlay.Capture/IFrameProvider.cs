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

    /// <summary>(loop 516) False when <see cref="Stop"/> could not prove the delivery thread has
    /// exited (DXGI: the poll thread outlived its join timeout). The orchestrator must then LEAK
    /// the shared D3D objects instead of disposing them — freeing a device/staging texture under a
    /// thread still inside CopySubresourceRegion/Map is a native access violation, not a catchable
    /// exception. WGC is always clean: its Stop blocks on the callback gate.</summary>
    bool StoppedCleanly { get; }

    /// <summary>(loop 516) Raised at most once, on the provider's own thread, when the backend
    /// self-disables (device removed, target window closed, unrecoverable duplication error) —
    /// previously this was silent: nothing read <see cref="IsRunning"/>, so the orchestrator's
    /// IsCapturing kept claiming capture was live. Handlers must not tear the provider down
    /// synchronously (the DXGI poll thread would be joining itself).</summary>
    event Action? Died;

    /// <summary>Begin delivering frames. <paramref name="onFrame"/> = (fullFrameTexture,
    /// timestampMs), called on a background/pool thread.</summary>
    void Start(Action<ID3D11Texture2D, long> onFrame);

    void Stop();
}
