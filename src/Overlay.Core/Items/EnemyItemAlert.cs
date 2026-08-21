namespace Overlay.Core.Items;

/// <summary>
/// Structured HUD content for an "enemy just completed a legendary item" alert
/// (<see cref="EnemyItemAlertDetector"/>). Unlike the ally-side <see cref="ItemAlert"/> — a plain
/// display string — this carries the raw <see cref="ChampionName"/> and <see cref="ItemId"/> so the
/// WPF card can render the SUBJECT'S real icons (champion portrait + item icon) instead of text only,
/// per the alert-design template ("색=심각도, 아이콘=대상"). <see cref="ItemName"/> is the
/// already-localized display name (Data Dragon <c>NameKo</c> when available) so the Core producer,
/// not the render layer, owns the name resolution.
/// </summary>
public sealed record EnemyItemAlert(string ChampionName, string ItemId, string ItemName);
