namespace Overlay.Core.Overlay;

/// <summary>
/// Kind of HUD element the Overlay Engine (M02) displays. Mirrors the spec's
/// <c>HUDPayload.type</c> enum ({ COMBO_RESULT, ITEM_ALERT, RECALL_TIMER,
/// NOTIFICATION }), plus <see cref="InhibitorTimer"/> added by M19 §3.2 and
/// <see cref="GlobalGold"/> added by M19 §3.3. The type drives the default Z-Order priority
/// (see <see cref="OverlayCoordinator.DefaultZOrder(HudType)"/>).
/// </summary>
public enum HudType
{
    ComboResult,
    ItemAlert,
    RecallTimer,
    Notification,
    InhibitorTimer,
    GlobalGold,
    /// <summary>M30 TEMPORARY debug/test panel — shows the live enemy-jungler detection state
    /// (found?/champion/CS/items) so the feature can be visually verified in a real game.
    /// Not part of the M30 spec's Scope; safe to remove once verification is done.</summary>
    EnemyJunglerDebug,
    /// <summary>M30 real "적 정글 발견" alert (docs/modules/M30_ENEMY_JUNGLER_SPOTTED.md) — a
    /// short top-center toast, shown 3s then faded out, anchored above the combo/skill HUD
    /// (<see cref="ComboResult"/>). Distinct from <see cref="EnemyJunglerDebug"/> (the temporary
    /// always-on verification panel).</summary>
    EnemyJunglerSpottedAlert,
    /// <summary>Enemy legendary-item-completed alert (<see cref="Overlay.Core.Items.EnemyItemAlertDetector"/>) —
    /// a top-right toast whose <see cref="HUDPayload.Content"/> is an
    /// <see cref="Overlay.Core.Items.EnemyItemAlert"/> so the card can render the enemy's champion
    /// portrait + the item's icon (not text only).</summary>
    EnemyItemAlert,
    /// <summary>Enemy appear/disappear presence toast (<see cref="Overlay.Core.Jungle.JunglePresenceTracker"/>)
    /// whose <see cref="HUDPayload.Content"/> is an <see cref="Overlay.Core.EnemyPresenceHud"/> — the rich
    /// replacement for the old plain-string notification, so the card shows the enemy's champion portrait
    /// + a severity color. Shares the "notification" enable toggle.</summary>
    EnemyPresence,
}

/// <summary>
/// On-screen anchor for a HUD element, in overlay-window DIP coordinates (spec's
/// <c>position { x, y }</c>). A pure value type — no allocation. A null
/// <see cref="HUDPayload.Position"/> means "use the Config default"
/// (<c>overlay.position.x/y</c>), resolved by <see cref="OverlayCoordinator"/>.
/// </summary>
public readonly record struct HudPosition(double X, double Y);

/// <summary>
/// M02 Data Model: a volatile, display-only HUD instruction produced from a
/// <c>UI.*</c> Event Bus event (or handed directly to
/// <see cref="OverlayCoordinator.ShowHUD(HUDPayload, int?)"/>). Matches the spec's
/// <c>HUDPayload</c> shape exactly.
///
/// <para><see cref="Content"/> is intentionally an opaque <c>object</c> — the spec
/// says "타입별 상이한 표시 데이터" (type-specific display data). This module never
/// interprets it; the WPF display layer turns it into pixels. <see cref="Position"/>
/// is null when the caller wants the Config default position.</para>
/// </summary>
public sealed record HUDPayload(string Id, HudType Type, object? Content, HudPosition? Position = null);
