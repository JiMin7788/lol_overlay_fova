namespace Overlay.Core.Logging;

/// <summary>Severity levels for <see cref="Logger.Log"/> (spec Interfaces / Data Model).</summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// A single structured log record (spec Data Model <c>LogEntry</c>). <see cref="Timestamp"/> is
/// Unix-epoch milliseconds (sourced from the injected <see cref="Overlay.Overlay.IClock"/> so it is
/// deterministic in tests). <see cref="Meta"/> is an optional caller-supplied context object,
/// serialized to JSON on the log line; callers must NOT pass secrets (M18 captures nothing on its
/// own — it records only what it is handed).
/// </summary>
public sealed record LogEntry(long Timestamp, LogLevel Level, string Module, string Message, object? Meta);

/// <summary>
/// A single performance sample (spec Data Model <c>MetricEntry</c>), e.g.
/// <c>"render.frame_drop_rate"</c> or an API latency. Held in memory by <see cref="Metrics"/> for a
/// Performance dashboard (M02 UI, out of M18 scope) to read back.
/// </summary>
public sealed record MetricEntry(string Name, double Value, IReadOnlyDictionary<string, string> Tags, long Timestamp);
