namespace Overlay.Core.Items;

/// <summary>Spec Data Model (M07): the kind of alert an <see cref="ItemAlert"/> carries.</summary>
public enum ItemAlertType
{
    /// <summary>A finished item (built from components, nothing further builds from it)
    /// newly appeared in the tracked player's inventory.</summary>
    ItemCompleted,

    /// <summary>The tracked player crossed a 100-gold milestone toward the target item.</summary>
    GoldMilestone,

    /// <summary>Static build-order suggestion (V2 scope — not emitted by the MVP).</summary>
    BuildSuggestion,
}

/// <summary>Spec Data Model (M07). One item-tracker notification.</summary>
public sealed record ItemAlert(ItemAlertType Type, string Message, long Timestamp);
