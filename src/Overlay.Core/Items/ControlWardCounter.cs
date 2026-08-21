namespace Overlay.Core.Items;

/// <summary>
/// Enemy control-ward inventory counts (2026-07-26 request): how many 제어 와드 each ENEMY is
/// currently carrying, read from the Live Client scoreboard items — public data the game shows
/// every player (P1/P2: nothing inferred; wards PLACED are not tracked, only the ones in bags).
/// Pure function over a snapshot; the HUD re-reads per frame so item changes show immediately.
/// </summary>
public static class ControlWardCounter
{
    /// <summary>Control Ward's item id — a stable Riot API contract identifier (like spell ids),
    /// not a patch-tuned value.</summary>
    public const int ControlWardItemId = 2055;

    /// <summary>Per-enemy carried control-ward count, keyed by scoreboard champion name
    /// (case-insensitive). Enemies with zero wards are INCLUDED at 0 so the overlay can render a
    /// stable five-row panel. Empty when the snapshot is missing or the active team can't be
    /// resolved (no guessing).</summary>
    public static IReadOnlyDictionary<string, int> CountEnemies(GameSnapshot? snap)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (snap is not { HasData: true }) return result;

        string? myTeam = ResolveActiveTeam(snap);
        if (string.IsNullOrEmpty(myTeam)) return result;

        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (string.IsNullOrEmpty(p.ChampionName)) continue;
            if (string.Equals(p.Team, myTeam, StringComparison.Ordinal)) continue;

            int count = 0;
            for (int s = 0; s < p.ItemCount; s++)
                if (p.ItemIds[s] == ControlWardItemId)
                    count += Math.Max(1, p.ItemCounts[s]); // stack quantity; 0/legacy → at least 1
            result[p.ChampionName] = count;
        }
        return result;
    }

    private static string? ResolveActiveTeam(GameSnapshot snap)
    {
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (Same(p.RiotId, snap.ActivePlayerRiotId) || Same(p.SummonerName, snap.ActivePlayerSummonerName)
                || Same(p.RiotId, snap.ActivePlayerSummonerName) || Same(p.SummonerName, snap.ActivePlayerRiotId))
                return string.IsNullOrEmpty(p.Team) ? null : p.Team;
        }
        return null;
    }

    private static bool Same(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
           && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
