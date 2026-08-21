namespace Overlay.Capture;

/// <summary>
/// M31 §5/§7 perf logging harness: accumulates per-frame crop+readback time (ms) and PCIe
/// readback bytes, and emits a rolling summary to the log sink every <see cref="_windowMs"/>
/// (avg + max ms/frame, effective fps, MB/s readback). Cheap — a few counters, no allocation per
/// frame. These are the numbers the §5 budget table is stated in (ROI readback ~9 MB/s @30 fps,
/// CPU ≤ 5%/core) so a live run can be checked against the hard gates without a profiler.
///
/// <para>Analytical predictions in the spec are to be REPLACED by these measured numbers (M31
/// Changelog v1.0). Single-threaded use from the capture callback; not thread-safe by design.</para>
/// </summary>
internal sealed class CapturePerfHarness
{
    private readonly Action<string>? _log;
    private readonly long _windowMs;

    private int _frames;
    private double _sumMs;
    private double _maxMs;
    private long _sumBytes;
    private long _windowStartMs;

    public CapturePerfHarness(Action<string>? log, long windowMs = 2000)
    {
        _log = log;
        _windowMs = windowMs;
        _windowStartMs = Environment.TickCount64;
    }

    /// <summary>Record one processed frame. <paramref name="elapsedMs"/> is the crop+readback
    /// time; <paramref name="readbackBytes"/> is the ROI bytes mapped to the CPU (stride×height);
    /// <paramref name="nowMs"/> is a monotonic clock (<c>Environment.TickCount64</c>). Emits a
    /// summary line once per window.</summary>
    public void Record(double elapsedMs, long readbackBytes, long nowMs)
    {
        _frames++;
        _sumMs += elapsedMs;
        if (elapsedMs > _maxMs) _maxMs = elapsedMs;
        _sumBytes += readbackBytes;

        long dt = nowMs - _windowStartMs;
        if (dt < _windowMs) return;

        double avgMs = _sumMs / _frames;
        double fps = _frames * 1000.0 / dt;
        double mbps = _sumBytes / (1024.0 * 1024.0) * 1000.0 / dt;
        _log?.Invoke(
            $"minimap perf: {avgMs:F2} ms/frame avg, {_maxMs:F2} ms max, {fps:F1} fps, " +
            $"{mbps:F1} MB/s readback ({_frames} frames / {dt} ms)");

        _frames = 0;
        _sumMs = 0;
        _maxMs = 0;
        _sumBytes = 0;
        _windowStartMs = nowMs;
    }
}
