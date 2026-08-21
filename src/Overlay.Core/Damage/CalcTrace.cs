namespace Overlay.Core.Damage;

/// <summary>
/// M26 §6 Trace Mode (T1, CLAUDE_CODE_TODO.md §15): an ambient, always-on collector for one
/// combo evaluation's per-node calculation trace (docs/modules/M26_CALCULATION_ACCURACY_PROTOCOL.md
/// §6). <see cref="Begin"/>/<see cref="End"/> bracket a single combo trigger's main damage
/// computation (see ComboRunner.ComputeAndPublish); <see cref="DamageEngine"/>'s ProcessNode and
/// ComboRunner's ApplyBinDamage append one <see cref="Row"/> per Blend-track node / hit-generation
/// branch while collecting. Observation only — this type never reads or changes any computed
/// damage number, and <see cref="Row"/> is a no-op whenever no block is open.
///
/// Minimal by design (T1): rows are pre-formatted strings, not a structured schema — see the
/// TODO's T2 follow-up for golden-test-grade structured capture.
/// </summary>
public static class CalcTrace
{
    private static readonly object Gate = new();
    private static List<string>? _rows;

    /// <summary>True while a Begin...End block is open; <see cref="Row"/> is a no-op otherwise.</summary>
    public static bool IsCollecting;

    /// <summary>Starts a new trace block for one combo trigger. <paramref name="header"/> is a
    /// free-form attacker/defender summary line, recorded once up front.</summary>
    public static void Begin(string comboId, string header)
    {
        lock (Gate)
        {
            _rows = new List<string> { $"=== combo {comboId} ===", header };
            IsCollecting = true;
        }
    }

    /// <summary>Appends one line to the currently-open trace block. No-op when no block is open
    /// (<see cref="IsCollecting"/> false) — callers never need to guard this themselves.</summary>
    public static void Row(string line)
    {
        lock (Gate)
        {
            if (!IsCollecting || _rows is null) return;
            _rows.Add(line);
        }
    }

    /// <summary>Closes the open block and returns its full text (<see langword="null"/> if none was
    /// open), resetting state so the next <see cref="Begin"/> starts clean.</summary>
    public static string? End()
    {
        lock (Gate)
        {
            if (_rows is null) return null;
            string block = string.Join(Environment.NewLine, _rows);
            _rows = null;
            IsCollecting = false;
            return block;
        }
    }
}
