namespace Overlay.Core.Flags;

/// <summary>
/// Start/stop seam for one beta_kill-able capturer (lolF05). The Client passes its start and stop
/// callbacks in; this class owns the race between a capture starting and a mid-session
/// <c>beta_kill</c> refresh stopping it, so the race is unit-tested in Core (plan review round 1).
///
/// <para>Both <see cref="TryStart"/> and <see cref="StopIfKilled"/> run under one lock, so a stop
/// never runs inside a start. Once stopped, the gate is latched: nothing restarts in-session even if
/// the kill is lifted again.</para>
/// </summary>
public sealed class KillGate
{
    private readonly object _lock = new();
    private readonly string _killName;
    private readonly string _what;
    private readonly Func<bool> _isKilled;
    private readonly Action<string> _log;
    private bool _running;
    private bool _latched;

    /// <param name="killName">The beta_kill name, e.g. <see cref="FeatureGate.MinimapVision"/>.</param>
    /// <param name="what">Human-readable name for the log line, e.g. "minimap capture".</param>
    /// <param name="isKilled">Reads the current kill state (typically RemoteFlags.IsOff).</param>
    /// <param name="log">Log sink.</param>
    public KillGate(string killName, string what, Func<bool> isKilled, Action<string> log)
    {
        _killName = killName;
        _what = what;
        _isKilled = isKilled;
        _log = log;
    }

    /// <summary>True between a successful <see cref="TryStart"/> and the stop.</summary>
    public bool IsRunning
    {
        get { lock (_lock) return _running; }
    }

    /// <summary>Calls <paramref name="start"/> unless already running, latched or killed. Returns
    /// whether the capture is now running. An exception from <paramref name="start"/> propagates
    /// and leaves the gate not running.</summary>
    public bool TryStart(Func<bool> start)
    {
        lock (_lock)
        {
            if (_running || _latched) return false;
            if (_isKilled())
            {
                _log($"beta_kill lists {_killName}: {_what} not started");
                return false;
            }
            _running = start();
            return _running;
        }
    }

    /// <summary>Calls <paramref name="stop"/> only when running and killed, then latches. Returns
    /// whether it stopped.</summary>
    public bool StopIfKilled(Action stop)
    {
        lock (_lock)
        {
            if (!_running || !_isKilled()) return false;
            stop();
            _running = false;
            _latched = true;
            _log($"beta_kill lists {_killName}: {_what} stopped");
            return true;
        }
    }
}
