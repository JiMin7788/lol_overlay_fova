namespace Overlay.Core.Vision;

/// <summary>
/// M31 P1 → P2 boundary. A capture source selects a backend (WGC for windowed/borderless,
/// DXGI Desktop Duplication for exclusive fullscreen — M31 §1), crops the calibrated minimap
/// ROI on the GPU, and raises <see cref="FrameCaptured"/> once per delivered frame (frame-on-
/// change, capped at <c>minimap.captureFps</c>). P2's <c>MinimapDetector</c> subscribes.
///
/// <para>This is deliberately a direct callback, NOT the M15 Event Bus: frames arrive at up to
/// 30–60 fps and are a high-frequency data stream, whereas the Event Bus carries discrete game
/// events. P2 is what emits discrete <c>MinimapSighting</c> events onto the bus.</para>
///
/// <para><b>The concrete WGC/DXGI/D3D11 implementations are intentionally not part of this P1
/// core slice</b> (native, GPU- and live-game-dependent, unverifiable in the build-less Cowork
/// sandbox — see the M31 P1 entry in <c>CLAUDE_CODE_TODO.md</c>). This interface fixes the
/// contract so P2 can be built and unit-tested against it in the meantime.</para>
/// </summary>
public interface IMinimapCaptureSource : IDisposable
{
    /// <summary>Raised on each captured minimap ROI. The <see cref="MinimapFrame"/>'s buffer is
    /// valid only for the duration of the handler; copy out anything you need to retain.</summary>
    event Action<MinimapFrame>? FrameCaptured;

    /// <summary>True once a backend is selected and calibration is valid (frames are flowing).
    /// False before <see cref="Start"/>, after <see cref="Stop"/>, or while the feature has
    /// disabled itself (M31 §6: capture failure → silent self-disable, no retry storm).</summary>
    bool IsCapturing { get; }

    /// <summary>Begin capture: probe the tracked game window's display mode, pick the backend,
    /// run calibration, and start delivering frames. Idempotent; a no-op while already capturing.</summary>
    void Start();

    /// <summary>Stop delivering frames and release the backend's GPU resources. Idempotent.</summary>
    void Stop();
}
