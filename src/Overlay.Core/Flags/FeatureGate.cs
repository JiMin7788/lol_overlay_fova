namespace Overlay.Core.Flags;

/// <summary>
/// Pure rules for whether a gated feature runs and whether the HUD overrides its config toggle,
/// for beta and non-beta builds (lolF05). <c>isOff</c> is the build's remote kill —
/// <see cref="RemoteFlags.IsOff"/>: <c>beta_kill</c> in the beta, <c>forceOff</c> otherwise.
///
/// <para>User decisions (lolF05 interview): round 4 Q1 — <see cref="BetaAlwaysOn"/> features always
/// run in the beta and show their cards whatever the saved value; only <c>beta_kill</c> turns them
/// off. Round 4 Q2 — <c>beta_kill</c> also accepts the capturers <see cref="MinimapVision"/> and
/// <see cref="BuffStackVision"/>.</para>
/// </summary>
public static class FeatureGate
{
    /// <summary>beta_kill name of the minimap capture.</summary>
    public const string MinimapVision = "minimapVision";

    /// <summary>beta_kill name of the buff-stack vision.</summary>
    public const string BuffStackVision = "buffStackVision";

    /// <summary>Features that run in the beta regardless of the config toggle (round 4 Q1).
    /// Lookups are case-insensitive.</summary>
    public static IReadOnlyCollection<string> BetaAlwaysOn { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "enemyJunglerSpotted", "enemyItemAlert" };

    /// <summary>A killed feature never runs; otherwise the config toggle decides, except that a
    /// <see cref="BetaAlwaysOn"/> feature runs in the beta even when toggled off.</summary>
    public static bool ShouldRun(string feature, bool configEnabled, bool isBeta, bool isOff)
        => !isOff && (configEnabled || (isBeta && BetaAlwaysOn.Contains(feature)));

    /// <summary>The value the HUD shows instead of the saved toggle for <paramref name="key"/>:
    /// in the beta a <see cref="BetaAlwaysOn"/> key shows on unless killed; null = use the saved value.</summary>
    public static bool? HudOverride(string key, bool isBeta, bool isOff)
        => isBeta && BetaAlwaysOn.Contains(key) ? !isOff : null;
}
