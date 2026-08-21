namespace Overlay.Core;

/// <summary>
/// HUD content for an enemy-presence toast (appear / disappear / group-disappear), carried by the
/// <c>UI.NOTIFICATION</c> <see cref="Overlay.HUDPayload"/> the <see cref="Jungle.JunglePresenceTracker"/>
/// publishes. Unlike the legacy bare string it replaces, this carries the <see cref="ChampionId"/> so
/// the card can draw the enemy's champion PORTRAIT, and the <see cref="Kind"/>/<see cref="GroupCount"/>
/// so the card can pick a severity color — the same "색=심각도, 아이콘=대상" design as the enemy-item card.
///
/// <para><see cref="Message"/> is the fully-formatted display text (e.g. "적 정글 발견 · 탑"), still
/// built in the tracker so the exact wording / role-label logic stays in one place. <see cref="ToString"/>
/// returns it so any generic string consumer (e.g. a fallback <c>Content?.ToString()</c>) still shows
/// the text unchanged.</para>
/// </summary>
public sealed record EnemyPresenceHud(string Message, string ChampionId, EnemyAlertKind Kind, int GroupCount)
{
    public override string ToString() => Message;
}
