// GameSnapshot / ScoreboardEntry / EnemyJunglerLocator all live in the root Overlay.Core
// namespace; this file's own namespace (Overlay.Core.Jungle) is a CHILD namespace and does not
// see them without this using.
using Overlay.Core;

namespace Overlay.Core.Jungle;

/// <summary>
/// M31 P3 §9 decision 1 (user-confirmed, docs/modules/M31_MINIMAP_VISION.md): who counts as "the"
/// enemy jungler for the (jungler-only) APPEAR alert. Resolution order, first match wins:
/// <list type="number">
///   <item><b>Position field</b> (<see cref="EnemyJunglerLocator"/>, M30) — ranked/draft only,
///   per the loop-125 finding; reused as-is rather than duplicated.</item>
///   <item><b>Smite summoner spell</b> — works in every game mode. Requires the M31 M01 extension
///   (<see cref="ScoreboardEntry.Spell1RawName"/>/<see cref="ScoreboardEntry.Spell2RawName"/>,
///   parsed from <c>allPlayers[].summonerSpells</c>) added alongside this class.</item>
///   <item><b>Settings override</b> — last resort, a user-supplied champion name (e.g. from a
///   future settings UI); the caller passes it in, this class does not read config itself.</item>
/// </list>
/// Returns null if none resolve — no guess beyond these three explicit signals (P2: no inference).
/// </summary>
public static class EnemyJunglerIdentifier
{
    private const string SmiteRawName = "SummonerSmite";

    public static string? Find(GameSnapshot? snap, string? settingsOverrideChampionName)
    {
        if (snap is null || !snap.HasData) return null;

        var byPosition = EnemyJunglerLocator.Find(snap);
        if (byPosition is not null) return byPosition.ChampionName;

        var active = ResolveActive(snap);
        if (active is null) return null;
        string myTeam = active.Team;

        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (string.Equals(p.Team, myTeam, StringComparison.Ordinal)) continue; // ally, skip
            if (string.Equals(p.Spell1RawName, SmiteRawName, StringComparison.Ordinal) ||
                string.Equals(p.Spell2RawName, SmiteRawName, StringComparison.Ordinal))
                return p.ChampionName;
        }

        if (!string.IsNullOrWhiteSpace(settingsOverrideChampionName))
        {
            for (int i = 0; i < snap.PlayerCount; i++)
            {
                var p = snap.Players[i];
                if (string.Equals(p.Team, myTeam, StringComparison.Ordinal)) continue;
                if (string.Equals(p.ChampionName, settingsOverrideChampionName, StringComparison.OrdinalIgnoreCase))
                    return p.ChampionName;
            }
        }

        return null;
    }

    /// <summary>Tolerant active-player identity match (riotId "Name#TAG" ↔ summonerName, tag/case)
    /// — duplicated from <see cref="EnemyJunglerLocator.ResolveActive"/> (private there) rather
    /// than exposed as shared internal API, same convention that method's own doc comment cites
    /// (already duplicated in <c>ComboRunner</c>/<c>LaneReturnPredictor</c>).</summary>
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

    private static bool SamePlayer(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        string na = a.Split('#')[0].Trim();
        string nb = b.Split('#')[0].Trim();
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }
}
