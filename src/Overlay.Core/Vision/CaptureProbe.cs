namespace Overlay.Core.Vision;

/// <summary>One probe of the tracked game window, taken by the capture source to decide whether the
/// capture backend must be re-chosen (lolF03 R8, TODO:63): the style-based display mode
/// (<c>GameDisplayMode</c> name, a string because that enum lives in Overlay.Capture), the monitor
/// handle the window is on, and the window's DPI.</summary>
/// <param name="Mode">Display-mode name; empty when the window is gone.</param>
/// <param name="Monitor">Monitor handle as a number; 0 when the window is on no monitor (minimized/gone).</param>
/// <param name="Dpi">Window DPI; 0 when the window is gone.</param>
public readonly record struct CaptureProbe(string Mode, long Monitor, uint Dpi)
{
    /// <summary>False for a probe of a minimized, closing or vanished window — such a probe says
    /// nothing about the display mode and is ignored.</summary>
    public bool IsValid => Monitor != 0 && Dpi != 0 && !string.IsNullOrEmpty(Mode);

    public override string ToString() => $"{Mode}/{Monitor}/{Dpi}";
}
