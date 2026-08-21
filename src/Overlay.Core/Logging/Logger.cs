using System.Globalization;
using System.Text;
using System.Text.Json;
using Overlay.Core.Overlay;

namespace Overlay.Core.Logging;

/// <summary>
/// M18 Logging: the testable core of the app-wide logging infrastructure. Writes structured,
/// greppable lines to a daily-rotated local file (<c>logs/YYYY-MM-DD.log</c>) and prunes files
/// older than a retention window so logs never grow unbounded (spec Internal Logic #2,
/// Acceptance Criteria #2). This is the lowest-level common infrastructure module (no
/// dependencies); other modules routing their logging through it is a later Lead integration
/// step — M18 only PROVIDES the seam.
///
/// <para><b>Local-only.</b> There is ZERO network/telemetry code here (spec Out of Scope + Policy
/// Internal Logic #4): logs go to a local file only. There is no remote-transmission path to
/// gate behind consent because none exists.</para>
///
/// <para><b>No sensitive-info capture.</b> M18 introduces no data source of its own — it records
/// exactly the <c>module</c>/<c>message</c>/<c>meta</c> a caller passes. It reads no credentials,
/// tokens, or keystrokes. Callers must not pass secrets in <c>meta</c>.</para>
///
/// <para><b>Async buffered I/O</b> (spec Agent Implementation Notes). <see cref="Log"/> only
/// enqueues under a short lock and returns, so a render/API thread is never blocked on disk. A
/// single background worker thread drains the queue to disk. <see cref="Flush"/> drains
/// synchronously (also invoked on <see cref="Dispose"/>) so tests can assert file contents without
/// racing the buffer.</para>
///
/// <para><b>Testability.</b> Both the log directory and the <see cref="IClock"/> are injectable:
/// tests point at a temp dir and drive a virtual clock, so "today's file name", daily rotation,
/// and "delete files older than N days" are deterministic with no real wall-clock waiting.</para>
/// </summary>
public sealed class Logger : IDisposable
{
    /// <summary>Spec default retention window: files older than this many days are pruned.</summary>
    public const int DefaultRetentionDays = 7;

    /// <summary>Max time the worker sleeps between drains when idle (also caps flush latency).</summary>
    private const int WorkerIntervalMs = 250;

    private static readonly JsonSerializerOptions MetaSerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _logDirectory;
    private readonly IClock _clock;
    private readonly int _retentionDays;

    private readonly object _queueGate = new();
    private readonly object _writeGate = new();
    private readonly Queue<LogEntry> _queue = new();

    private readonly Thread _worker;
    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _disposed;

    /// <summary>Default on-disk location: <c>logs/</c> under the app base directory
    /// (spec Internal Logic #2).</summary>
    public static string DefaultLogDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <param name="logDirectory">Directory for daily log files (defaults to
    /// <see cref="DefaultLogDirectory"/>; tests inject a temp dir).</param>
    /// <param name="clock">Time source for timestamps, the active file's date, and the retention
    /// cutoff (defaults to the system clock).</param>
    /// <param name="retentionDays">Days of logs to keep; older daily files are deleted
    /// (defaults to <see cref="DefaultRetentionDays"/>).</param>
    public Logger(string? logDirectory = null, IClock? clock = null, int? retentionDays = null)
    {
        _logDirectory = logDirectory ?? DefaultLogDirectory;
        _clock = clock ?? new SystemClock();
        _retentionDays = retentionDays ?? DefaultRetentionDays;
        Directory.CreateDirectory(_logDirectory);

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Overlay.Logging.Worker",
        };
        _worker.Start();
    }

    // ── Public API (spec Interfaces) ────────────────────────────────────────────────

    /// <summary>Enqueue a structured log line. Non-blocking: the entry is buffered and written to
    /// disk by the background worker (or on the next <see cref="Flush"/>). Empty
    /// <paramref name="module"/>/<paramref name="message"/> are recorded verbatim — the caller owns
    /// content.</summary>
    public void Log(LogLevel level, string module, string message, object? meta = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Logger));

        var entry = new LogEntry(_clock.NowMs, level, module ?? string.Empty, message ?? string.Empty, meta);
        lock (_queueGate)
        {
            _queue.Enqueue(entry);
        }
        _signal.Set();
    }

    /// <summary>Synchronously drain all buffered entries to disk and prune expired daily files.
    /// Deterministic (no timing dependence) so tests can assert file contents immediately after.</summary>
    public void Flush() => DrainAndWrite();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _signal.Set();
        _worker.Join();
        DrainAndWrite();
        _signal.Dispose();
    }

    // ── Background worker ───────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            _signal.WaitOne(WorkerIntervalMs);
            DrainAndWrite();
        }
    }

    // ── Write + rotation + retention ────────────────────────────────────────────────

    /// <summary>Drains the queue and appends each entry to its date's daily file, then prunes
    /// expired files. Serialized by <see cref="_writeGate"/> so the worker and a concurrent
    /// <see cref="Flush"/> never interleave file writes.</summary>
    private void DrainAndWrite()
    {
        lock (_writeGate)
        {
            List<LogEntry> batch;
            lock (_queueGate)
            {
                if (_queue.Count == 0) return;
                batch = new List<LogEntry>(_queue);
                _queue.Clear();
            }

            // Group by each entry's own date so a batch spanning a rotation boundary lands in the
            // right daily file (spec Internal Logic #2: logs/YYYY-MM-DD.log daily rotation).
            foreach (var group in batch.GroupBy(e => DateFor(e.Timestamp)))
            {
                var sb = new StringBuilder();
                foreach (var entry in group) sb.AppendLine(Format(entry));
                File.AppendAllText(FilePathForDate(group.Key), sb.ToString(), Encoding.UTF8);
            }

            DeleteExpired();
        }
    }

    /// <summary>Deletes <c>*.log</c> files whose date is older than the retention window, based on
    /// the file's date parsed from its name (not a wall-clock sleep). "Today" is the injected
    /// clock's date, so the cutoff is fully virtual-clock driven (Acceptance Criteria #2).</summary>
    private void DeleteExpired()
    {
        DateOnly cutoff = DateFor(_clock.NowMs).AddDays(-_retentionDays);

        foreach (var path in Directory.EnumerateFiles(_logDirectory, "*.log"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
                continue; // leave files that don't match the daily-log naming untouched.

            if (fileDate < cutoff)
            {
                try { File.Delete(path); }
                catch (IOException) { /* file locked/removed concurrently — retry on next write. */ }
            }
        }
    }

    /// <summary>Structured, greppable line:
    /// <c>2026-07-08T12:34:56.789Z [ERROR] Module: message {"meta":...}</c>.</summary>
    private static string Format(LogEntry entry)
    {
        string ts = DateTimeOffset.FromUnixTimeMilliseconds(entry.Timestamp)
            .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        string line = $"{ts} [{entry.Level.ToString().ToUpperInvariant()}] {entry.Module}: {entry.Message}";
        if (entry.Meta is not null)
            line += " " + JsonSerializer.Serialize(entry.Meta, MetaSerializerOptions);
        return line;
    }

    private string FilePathForDate(DateOnly date)
        => Path.Combine(_logDirectory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    private static DateOnly DateFor(long unixMs)
        => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime);
}
