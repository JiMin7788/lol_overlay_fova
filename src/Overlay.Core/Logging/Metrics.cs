using Overlay.Core.Overlay;

namespace Overlay.Core.Logging;

/// <summary>
/// M18 Metrics: an in-memory sink for performance samples (frame-drop rate, API latency, …; spec
/// Internal Logic #3). Modules call <see cref="Record"/>; a Performance dashboard (M02 UI, out of
/// M18 scope) reads them back via <see cref="Snapshot"/>/<see cref="Recent"/> (Acceptance
/// Criteria #3). In-memory only — no file, no network.
///
/// <para>Timestamps come from the injected <see cref="IClock"/> so recording is deterministic in
/// tests. All access is lock-guarded; the query methods return copies so a reader cannot observe a
/// torn list while another thread records.</para>
/// </summary>
public sealed class Metrics
{
    private static readonly IReadOnlyDictionary<string, string> EmptyTags =
        new Dictionary<string, string>();

    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly List<MetricEntry> _entries = new();

    /// <param name="clock">Time source for sample timestamps (defaults to the system clock).</param>
    public Metrics(IClock? clock = null)
    {
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Record a performance sample (spec Interfaces
    /// <c>Metrics.record(name, value, tags?)</c>). Tags default to empty when null.</summary>
    public void Record(string name, double value, IReadOnlyDictionary<string, string>? tags = null)
    {
        var entry = new MetricEntry(name ?? string.Empty, value, tags ?? EmptyTags, _clock.NowMs);
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>Point-in-time copy of all recorded samples, oldest first (dashboard read path).</summary>
    public IReadOnlyList<MetricEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }

    /// <summary>All samples recorded under <paramref name="name"/>, oldest first.</summary>
    public IReadOnlyList<MetricEntry> Recent(string name)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Name == name).ToList();
        }
    }
}
