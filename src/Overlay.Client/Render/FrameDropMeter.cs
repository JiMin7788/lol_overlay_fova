using System.Diagnostics;
using System.Windows.Media;

namespace Overlay.Client.Render;

/// <summary>
/// Benchmark hook for the M16 Acceptance Criterion "60fps 기준 프레임 드랍률 1% 미만".
///
/// <para>The real frame-drop number can only be captured while the WPF app is actually
/// compositing on a display — it cannot be measured headlessly. This meter provides the
/// measurement mechanism: subscribe to WPF's per-frame
/// <see cref="CompositionTarget.Rendering"/> event (which fires once per compositor
/// frame, i.e. Vsync-aligned), and count any inter-frame gap longer than
/// <see cref="TargetFrameMs"/> × <see cref="DropThreshold"/> as a dropped frame.</para>
///
/// <para>To capture the benchmark: on the overlay window, do
/// <c>var meter = new FrameDropMeter(); meter.Start();</c>, run a representative HUD
/// scene (5+ elements) for ~60s, then read <see cref="DropRate"/> and assert it is
/// &lt; 0.01. This is intentionally NOT run in CI (no display) — it is a manual,
/// display-attached measurement whose result is pasted into the Agent Report.</para>
///
/// This is pure timing instrumentation: it reads a clock and counts frames. It performs
/// no rendering, no I/O, and no game-process access.
/// </summary>
public sealed class FrameDropMeter
{
    /// <summary>Nominal frame budget at 60fps (milliseconds).</summary>
    public const double TargetFrameMs = 1000.0 / 60.0;

    /// <summary>A frame is counted as dropped when the gap exceeds this multiple of
    /// <see cref="TargetFrameMs"/> (1.5 = allow 50% slack before calling it a drop).</summary>
    public const double DropThreshold = 1.5;

    private readonly Stopwatch _clock = new();
    private double _lastMs = -1;
    private bool _running;

    /// <summary>Total compositor frames observed since <see cref="Start"/>.</summary>
    public long TotalFrames { get; private set; }

    /// <summary>Frames whose preceding gap exceeded the drop threshold.</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>Observed drop rate in [0,1]; 0 when no frames have been observed.</summary>
    public double DropRate => TotalFrames == 0 ? 0.0 : (double)DroppedFrames / TotalFrames;

    // ── Gap diagnostics (2026-07-23): classify each dropped frame's gap so a failing run says
    // WHAT kind of stall it is — many 25-50ms gaps = composition jitter, periodic 50-100ms =
    // heavy timer tick, >100ms = major dispatcher stalls. Cumulative since Start().
    /// <summary>Dropped-frame gaps in (25, 50] ms.</summary>
    public long GapMild { get; private set; }
    /// <summary>Dropped-frame gaps in (50, 100] ms.</summary>
    public long GapModerate { get; private set; }
    /// <summary>Dropped-frame gaps over 100 ms.</summary>
    public long GapSevere { get; private set; }
    /// <summary>Largest inter-frame gap observed since <see cref="Start"/> (ms).</summary>
    public double MaxGapMs { get; private set; }

    /// <summary>Begin counting frames. Idempotent.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        TotalFrames = 0;
        DroppedFrames = 0;
        GapMild = 0;
        GapModerate = 0;
        GapSevere = 0;
        MaxGapMs = 0;
        _lastMs = -1;
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Stop counting frames. Idempotent.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_lastMs >= 0)
        {
            TotalFrames++;
            double gap = now - _lastMs;
            if (gap > MaxGapMs) MaxGapMs = gap;
            if (gap > TargetFrameMs * DropThreshold)
            {
                DroppedFrames++;
                if (gap <= 50) GapMild++;
                else if (gap <= 100) GapModerate++;
                else GapSevere++;
            }
        }
        _lastMs = now;
    }
}
