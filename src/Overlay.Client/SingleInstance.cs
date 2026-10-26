using System.Threading;

namespace Overlay.Client;

/// <summary>
/// One Fova per user session. With the tray-resident autostart a second launch (desktop shortcut,
/// Start menu) would otherwise start a second overlay and a second keyboard hook; instead it asks
/// the running instance to show its dashboard and exits.
/// </summary>
internal static class SingleInstance
{
    // AI-NOTE: test mode gets its own names (per data dir) so a UI-test copy runs next to the
    // user's Fova instead of waking it and exiting.
    private static readonly string InstanceSuffix = AppPaths.TestDataDir is { } dir
        ? Overlay.Core.TestDataDir.InstanceSuffix(dir) : "";
    private static readonly string MutexName = @"Local\Fova.SingleInstance" + InstanceSuffix;
    private static readonly string ShowEventName = @"Local\Fova.Show" + InstanceSuffix;

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    /// <summary>True when this process is the first instance (keep the result alive for the
    /// process lifetime — the mutex is held in a static).</summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        if (created) return true;
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// <summary>Second instance: wake the first one's dashboard.</summary>
    public static void SignalFirstInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
        }
        catch
        {
            // The first instance is still starting (event not created yet) — nothing to wake.
        }
    }

    /// <summary>First instance: call <paramref name="onShow"/> (on a background thread) whenever
    /// a later launch signals.</summary>
    public static void ListenForShow(Action onShow)
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne()) onShow();
        })
        { IsBackground = true, Name = "Fova.SingleInstance" };
        thread.Start();
    }
}
