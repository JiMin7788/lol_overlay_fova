namespace Overlay.Core.Vision;

/// <summary>
/// lolF03 R9 (TODO:73 part 3): decides when a WGC capture session that delivers no frames should
/// fall back to DXGI duplication — the style probe cannot tell exclusive fullscreen from borderless,
/// and WGC silently delivers nothing for the former. Falls back when no frame arrived since
/// <see cref="Start"/>, the window is not minimized, and <see cref="TimeoutMs"/> passed since the later
/// of Start and the last restore. A restore restarts the wait; minimized time is not accumulated.
/// Not thread-safe; the caller serialises calls.
/// </summary>
public sealed class WgcNoFramePolicy
{
    public const long TimeoutMs = 3000;

    private bool _started;
    private bool _sawFrame;
    private bool _minimized;
    private long _waitFromMs;

    /// <summary>A WGC session started. Never called for a DXGI session, so DXGI never falls back.</summary>
    public void Start(long nowMs)
    {
        _started = true;
        _sawFrame = false;
        _minimized = false;
        _waitFromMs = nowMs;
    }

    public void OnFrame(long nowMs) => _sawFrame = true;

    public void SetMinimized(bool minimized, long nowMs)
    {
        if (_minimized && !minimized) _waitFromMs = Math.Max(_waitFromMs, nowMs);
        _minimized = minimized;
    }

    public bool ShouldFallback(long nowMs)
        => _started && !_sawFrame && !_minimized && nowMs - _waitFromMs >= TimeoutMs;
}
