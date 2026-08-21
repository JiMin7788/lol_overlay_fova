namespace Overlay.Core.EventBus;

/// <summary>
/// M15 Data Model: the immutable envelope delivered to every subscriber.
/// Matches the spec's Data Model section exactly (id/type/source/timestamp/payload).
/// </summary>
public sealed class Event
{
    /// <summary>Unique id for this event instance (uuid).</summary>
    public required string Id { get; init; }

    /// <summary>Namespaced event type, e.g. "GAME.CS_UPDATE". Always one of the
    /// enforced namespaces (GAME./COMBO./UI./VOICE./SYSTEM.).</summary>
    public required string Type { get; init; }

    /// <summary>Publishing module's name. May be empty when the caller omitted it —
    /// EventBus logs a warning in that case (see EventBusLog) but still delivers the
    /// event; source is for debugging traceability, not a delivery gate.</summary>
    public required string Source { get; init; }

    /// <summary>Unix-epoch milliseconds at publish time.</summary>
    public required long Timestamp { get; init; }

    /// <summary>Caller-supplied event data. Never inspected/mutated by EventBus itself.</summary>
    public object? Payload { get; init; }
}
