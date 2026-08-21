namespace Overlay.Core.EventBus;

/// <summary>
/// Minimal injectable logging seam used only for the M15 "missing source module name"
/// warning (spec Internal Logic #3). This is a PLACEHOLDER pending the real M18
/// Logging module, which does not exist as code yet — EventBus must not depend on it
/// (M15 Dependencies: "없음"). Once M18 ships, its sink can implement this interface
/// (or EventBus can be re-pointed at it) with no change to EventBus's public API.
/// </summary>
public interface IEventBusLogger
{
    void Warn(string message);
}

/// <summary>
/// Default sink: writes to <see cref="System.Diagnostics.Debug"/> (and console when a
/// debugger/console is not attached is irrelevant — Debug.WriteLine is a no-op-safe
/// placeholder). Swap via <see cref="EventBus.Logger"/> in tests or once M18 exists.
/// </summary>
public sealed class DebugEventBusLogger : IEventBusLogger
{
    public void Warn(string message) => System.Diagnostics.Debug.WriteLine($"[EventBus WARN] {message}");
}
