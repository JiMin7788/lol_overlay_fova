using Overlay.Core.ChampionDb;

namespace Overlay.Core;

/// <summary>
/// (loop 125) Estimates an ENEMY champion's movement speed from PUBLIC data — used by the lane-return
/// predictor to turn "fountain → lane" distance into a travel time. The Live Client API does not expose
/// any other player's live move speed (only the active player's), but it DOES expose every player's
/// champion + visible items, so MS is reconstructed the same way <c>ComboRunner.BuildDefenderFor</c>
/// reconstructs armor/MR/HP: champion base + item contributions (P1 — all on-screen/public).
///
/// <para>Formula (League's real MS stacking): <c>ms = (baseMS + Σ flatItemMS) × (1 + Σ %itemMS) +
/// conditionalBonus</c>. Percent MS is additive among items then applied once to the flat total.</para>
///
/// <para><b>Accuracy caveats (honest, P2):</b>
/// <list type="bullet">
/// <item>Data Dragon's item <c>stats</c> block is INCOMPLETE for many modern items (they encode MS in
///   the localized description only), so <see cref="ItemStats.MoveSpeedFlat"/>/<see cref="ItemStats.MoveSpeedPercent"/>
///   can read 0 for items that really give MS. This under-counts; the curated <paramref name="conditionalBonus"/>
///   and the caller's fountain-boost model partly compensate.</item>
/// <item>Conditional actives/stacks (Youmuu's Ghostblade active, Dead Man's Plate stacks) aren't
///   API-observable, so the user's requested flat <see cref="ConditionalItemBonus"/> (+20) is added when
///   any curated conditional-MS item is built — an assumption, not a reading.</item>
/// <item>Live buffs (abilities, Homeguard/fountain boost, honey-fruit, etc.) are not in this steady-state
///   value; the "민병대"/fountain-departure boost is modeled separately by the caller via
///   <see cref="FountainBoostTravelSeconds"/> since it only applies right after leaving the fountain.</item>
/// </list></para>
/// </summary>
public static class MoveSpeedEstimator
{
    /// <summary>Fallback base MS when the champion's real base MS is not resolvable (only the cached-5
    /// champions currently expose base MS via <c>ChampionRepository</c>; most enemies fall back here).
    /// 340 is a mid-range champion base move speed.</summary>
    public const double DefaultBaseMoveSpeed = 340.0;

    /// <summary>User-specified flat bonus (+20) added when the enemy has a conditional-MS item built
    /// (Youmuu's Ghostblade / Dead Man's Plate). Represents their typical in-motion uptime rather than
    /// the peak active value — the enemy is presumed moving (returning to lane) when this is used.</summary>
    public const double ConditionalItemBonus = 20.0;

    /// <summary>Data Dragon item ids whose MS is CONDITIONAL (active/stacks) and therefore under-counted
    /// by the <c>stats</c> block — presence triggers <see cref="ConditionalItemBonus"/>. Curated (small,
    /// patch-dependent) rather than inferred. Youmuu's Ghostblade (3142), Dead Man's Plate (3742).</summary>
    private static readonly HashSet<int> ConditionalMsItemIds = new() { 3142, 3742 };

    /// <summary>Whether any built item id is a curated conditional-MS item.</summary>
    public static bool HasConditionalMsItem(IEnumerable<int> builtItemIds)
    {
        foreach (var id in builtItemIds)
            if (ConditionalMsItemIds.Contains(id)) return true;
        return false;
    }

    /// <summary>The flat + percent components of an enemy's move speed, kept separate so a caller can
    /// layer an ADDITIONAL percent bonus (e.g. the respawn Homeguard boost) onto the same flat base —
    /// League stacks MS as <c>flat × (1 + Σpercent)</c>, so a late percent bonus is additive in the
    /// percent term, NOT a multiply of the already-percented steady value.</summary>
    public readonly record struct MoveSpeedBreakdown(double FlatTotal, double PercentTotal)
    {
        /// <summary>Steady-state MS = flat × (1 + Σpercent).</summary>
        public double Steady => FlatTotal * (1.0 + PercentTotal);

        /// <summary>MS with an extra additive percent bonus applied to the same flat base
        /// (e.g. Homeguard's +66.67%/+75–150%).</summary>
        public double WithBonusPercent(double extraPercent) => FlatTotal * (1.0 + PercentTotal + extraPercent);
    }

    /// <summary>Flat/percent breakdown for an enemy with the given base MS + built items. Base ≤ 0 falls
    /// back to <see cref="DefaultBaseMoveSpeed"/>. The conditional (+20) bonus is folded into the FLAT
    /// total (it is a flat MS buff, added before the percent multiply).</summary>
    public static MoveSpeedBreakdown Breakdown(double baseMoveSpeed, IEnumerable<ItemStats> items, bool hasConditionalMsItem)
    {
        double flat = baseMoveSpeed > 0 ? baseMoveSpeed : DefaultBaseMoveSpeed;
        double percent = 0;
        foreach (var s in items)
        {
            flat += s.MoveSpeedFlat;
            percent += s.MoveSpeedPercent;
        }
        if (hasConditionalMsItem) flat += ConditionalItemBonus;
        return new MoveSpeedBreakdown(flat, percent);
    }

    /// <summary>Estimated steady-state move speed (convenience over <see cref="Breakdown"/>.Steady).</summary>
    public static double Estimate(double baseMoveSpeed, IEnumerable<ItemStats> items, bool hasConditionalMsItem)
        => Breakdown(baseMoveSpeed, items, hasConditionalMsItem).Steady;
}
