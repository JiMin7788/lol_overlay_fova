namespace Overlay.Core;

/// <summary>
/// M30 (Enemy Jungler Spotted Alert) — step 1: identifies the enemy team's JUNGLE-position
/// scoreboard row, so a later trigger (CS/item change) knows which row to watch.
///
/// <para>Uses <see cref="ScoreboardEntry.Position"/> ("TOP"/"JUNGLE"/"MIDDLE"/"BOTTOM"/"UTILITY"),
/// which the Live Client sets once at draft/ranked champion-select and never recomputes — the
/// user-confirmed premise for this feature is that jungle assignment is fixed for the game, unlike
/// e.g. an inferred/heuristic role. BEST-EFFORT per the existing convention
/// (<see cref="ComboRunner.ResolveTarget"/>, <see cref="LaneReturnPredictor"/>): the field is only
/// populated in ranked/draft and is empty in practice tool/ARAM/normal-blind, etc. Per M30's Policy
/// Compliance Checklist, THERE IS NO FALLBACK when it is empty — this returns null rather than
/// guessing a jungler from items/other signals (P2: no inference beyond what the API actually
/// states).</para>
/// </summary>
public static class EnemyJunglerLocator
{
    /// <summary>Returns the enemy scoreboard row whose <see cref="ScoreboardEntry.Position"/> is
    /// "JUNGLE", or null when there is no active game, no active-player row, or no enemy row
    /// reports a JUNGLE position (position unavailable in this game mode).</summary>
    public static ScoreboardEntry? Find(GameSnapshot? snap)
    {
        if (snap is null || !snap.HasData) return null;

        var active = ResolveActive(snap);
        if (active is null) return null;
        string myTeam = active.Team;

        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (string.Equals(p.Team, myTeam, StringComparison.Ordinal)) continue; // ally, skip
            if (string.Equals(p.Position, "JUNGLE", StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null; // no enemy row reports JUNGLE (position empty this game mode) — no guess (P2)
    }

    /// <summary>Tolerant active-player identity match (riotId "Name#TAG" ↔ summonerName, tag/case),
    /// mirroring <see cref="ComboRunner.ResolveActive"/>/<see cref="LaneReturnPredictor.IsActivePlayer"/>
    /// — the Live Client is inconsistent about which identity field is populated.</summary>
    private static ScoreboardEntry? ResolveActive(GameSnapshot snap)
    {
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (SamePlayer(p.RiotId, snap.ActivePlayerRiotId)
                || SamePlayer(p.SummonerName, snap.ActivePlayerSummonerName)
                || SamePlayer(p.RiotId, snap.ActivePlayerSummonerName)
                || SamePlayer(p.SummonerName, snap.ActivePlayerRiotId))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>Case-insensitive, "#TAG"-stripped identity comparison.</summary>
    private static bool SamePlayer(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        string na = a.Split('#')[0].Trim();
        string nb = b.Split('#')[0].Trim();
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }
}
