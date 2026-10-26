namespace Overlay.Core.Vision;

/// <summary>
/// lolF03 R8 (TODO:63): decides when the capture source must restart because the game window's
/// display mode, monitor or DPI changed (e.g. alt-enter). Fed one <see cref="CaptureProbe"/> per
/// second. A restart is due only when two consecutive valid samples are equal and both differ from
/// the session's baseline — a single differing sample is an alt-enter still in progress — and at
/// most once per <see cref="MinRestartIntervalMs"/>. Not thread-safe; the caller serialises calls.
/// </summary>
public sealed class CaptureRestartPolicy
{
    public const long MinRestartIntervalMs = 5000;

    private CaptureProbe _baseline;
    private CaptureProbe? _previous;
    private long? _lastRestartMs;

    /// <summary>The probe the current session started with (or the last failed restart's target).</summary>
    public CaptureProbe Baseline => _baseline;

    /// <summary>A capture session started with <paramref name="baseline"/>.</summary>
    public void Begin(CaptureProbe baseline, long nowMs)
    {
        _baseline = baseline;
        _previous = null;
    }

    /// <summary>Feed one probe; true = restart the capture now.</summary>
    public bool Observe(CaptureProbe sample, long nowMs)
    {
        // AI-NOTE: an invalid sample (minimized/closing window) neither counts as a change nor
        // breaks the pending pair, so a blip between two equal new probes still restarts.
        if (!sample.IsValid) return false;

        CaptureProbe? previous = _previous;
        _previous = sample;
        if (previous != sample || sample == _baseline) return false;
        if (_lastRestartMs is { } last && nowMs - last < MinRestartIntervalMs) return false;

        _lastRestartMs = nowMs;
        return true;
    }

    /// <summary>The restart to <paramref name="attempted"/> failed. Adopting it as the baseline keeps
    /// the same probe from re-triggering every 5 s; the next real change starts again.</summary>
    public void RestartFailed(CaptureProbe attempted, long nowMs)
    {
        _baseline = attempted;
        _previous = null;
    }
}
