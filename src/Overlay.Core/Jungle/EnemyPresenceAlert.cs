namespace Overlay.Core;

/// <summary>
/// M31 §0 (docs/M31_dispatch_commands.md "0. 공통 계약") — the kind of enemy-presence
/// transition an <see cref="EnemyPresenceAlert"/> reports.
/// </summary>
public enum EnemyAlertKind
{
    /// <summary>A tracked enemy re-appeared on the minimap (currently jungler-only, per §A).</summary>
    Appear,

    /// <summary>A single tracked enemy dropped out of vision (Last-Seen).</summary>
    Disappear,

    /// <summary>Multiple enemies dropped out of vision together (see <see cref="EnemyPresenceAlert.GroupCount"/>).</summary>
    GroupDisappear,
}

/// <summary>
/// M31 §0 shared contract — the structured enemy-presence alert published on the Event Bus
/// under <c>UI.ENEMY_PRESENCE</c> by the P3 presence tracker (§A) and consumed by the
/// pre-recorded voice player (§B, <c>EnemyVoicePlayer</c>) and the minimap afterimage
/// renderer (§C, OverlayHost). It carries the structured role / zone / coordinate data that
/// the legacy <c>UI.NOTIFICATION</c> (string) and <c>VOICE.SPEAK</c> (TTS) events lack, so
/// consumers can pick a voice file and place an afterimage marker without re-deriving it.
///
/// <para>Lives in the root <c>Overlay.Core</c> namespace (like <c>ChampionDiedPayload</c> and
/// the other cross-cutting Event Bus payloads) so publishers and subscribers in sibling
/// namespaces / assemblies see it without an extra using. A pure value type — no allocation.</para>
///
/// <para>The existing <c>UI.NOTIFICATION</c> toast may stay; its text is derived from this
/// payload's fields (e.g. <c>$"적 {ZoneLabel} 사라짐"</c>).</para>
/// </summary>
/// <param name="Kind">Which presence transition this alert reports.</param>
/// <param name="ChampionId">English Data Dragon champion id; empty string for a group alert.</param>
/// <param name="RoleKey">Role key: "top"/"jungle"/"mid"/"adc"/"support", or "" when unknown.</param>
/// <param name="ZoneKey">map_regions.json zones key (e.g. "top_lane") — English key for voice-file selection.</param>
/// <param name="ZoneLabel">Korean zone label (e.g. "탑") — for toast text.</param>
/// <param name="X01">Canonical map-space X in [0,1] (last-seen position, for afterimage placement).</param>
/// <param name="Y01">Canonical map-space Y in [0,1] (last-seen position, for afterimage placement).</param>
/// <param name="GroupCount">Number of enemies in a <see cref="EnemyAlertKind.GroupDisappear"/> (1 for a single alert).</param>
public readonly record struct EnemyPresenceAlert(
    EnemyAlertKind Kind,
    string ChampionId,
    string RoleKey,
    string ZoneKey,
    string ZoneLabel,
    double X01,
    double Y01,
    int GroupCount);
