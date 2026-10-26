namespace Overlay.Core.Threading;

/// <summary>Runs a periodic body at most once at a time: a tick that arrives while the previous
/// one is still running is skipped, not queued. Exceptions from the body propagate to the caller,
/// and the gate is free again afterwards.</summary>
public sealed class TickGate
{
    private int _running;

    /// <summary>Runs <paramref name="body"/> unless another body is still running. Returns false
    /// (without running it) when one is.</summary>
    public bool TryRun(Action body)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        try
        {
            body();
            return true;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
