namespace Overlay.Core.Jungle;

/// <summary>
/// M31 §B — decides which clips voice an <see cref="EnemyPresenceAlert"/>, and in what order.
///
/// <para>Alerts are spoken as pieces rather than pre-baked sentences: <c>[대상 포지션] [위치]
/// [이벤트]</c>, e.g. <c>role_jungle</c> + <c>loc_enemy_camp</c> + <c>event_appear</c>. Thirty-four
/// clips cover 230 combinations, at the cost of ~1s of extra length per alert (measured; see
/// docs/M31_voice_files.md).</para>
///
/// <para>Split out from <c>EnemyVoicePlayer</c> so the ordering rules are testable without a
/// sound device — this returns clip keys and never touches audio.</para>
/// </summary>
public static class EnemyVoiceScript
{
    /// <summary>Role key -> "대상 포지션" clip. An unknown role has no clip; the alert is then
    /// voiced as location + event, which still reads correctly ("적 캠프, 사라짐").</summary>
    private static readonly Dictionary<string, string> RoleClips = new(StringComparer.OrdinalIgnoreCase)
    {
        ["top"] = "role_top",
        ["jungle"] = "role_jungle",
        ["mid"] = "role_mid",
        ["adc"] = "role_adc",
        ["support"] = "role_support",
    };

    /// <summary>
    /// Returns the clip keys to play back-to-back, or an empty list when the alert cannot be
    /// voiced at all.
    /// </summary>
    /// <param name="alert">The presence alert to voice.</param>
    /// <param name="locationClip">
    /// Location clip from <see cref="VoiceLocationResolver"/>, or <c>null</c> if the position did
    /// not resolve. A null location is not fatal — the alert still says who and what happened,
    /// which beats staying silent about an enemy going missing.
    /// </param>
    public static IReadOnlyList<string> Build(EnemyPresenceAlert alert, string? locationClip)
    {
        var clips = new List<string>(3);

        switch (alert.Kind)
        {
            case EnemyAlertKind.GroupDisappear:
                // Group alerts name a count instead of a role, and deliberately carry no location:
                // the members scatter, so a single point would misrepresent where they all went.
                var n = Math.Clamp(alert.GroupCount, 2, 5);
                clips.Add($"group_{n}");
                clips.Add("event_disappear");
                return clips;

            case EnemyAlertKind.Appear:
            case EnemyAlertKind.Disappear:
                if (RoleClips.TryGetValue(alert.RoleKey ?? string.Empty, out var role))
                    clips.Add(role);
                if (locationClip is { Length: > 0 })
                    clips.Add(locationClip);

                // Nothing but the event word would be meaningless ("발견"), so stay silent.
                if (clips.Count == 0) return Array.Empty<string>();

                clips.Add(alert.Kind == EnemyAlertKind.Appear ? "event_appear" : "event_disappear");
                return clips;

            default:
                return Array.Empty<string>();
        }
    }
}
