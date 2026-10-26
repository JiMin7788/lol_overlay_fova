using System.Text;
using Overlay.Core.Combo;

namespace Overlay.Core.Items;

/// <summary>
/// (M46, lolF01) The key the next-item card is re-ranked on: the ranking is redone only when the key
/// changes. Damage mode: owned items, level, mode and pinned target (unchanged since M46). Tank mode: what
/// <see cref="ComboRunner.RankTankItems"/> reads - owned items, level, my max HP / armor / MR (bucketed) and
/// every enemy's champion, level, keystone, spells and items.
/// </summary>
public static class NextItemCacheKey
{
    public static string For(GameSnapshot snap, ScoreboardEntry me, bool tank, string? manualTarget)
    {
        var owned = me.ItemIds.Take(me.ItemCount);
        if (!tank) return string.Join(",", owned) + "|" + me.Level + "|" + tank + "|" + manualTarget;

        // AI-NOTE: no CurrentHealth and no IsDead - the tank ranking runs at my max HP whether I am alive
        // or not, so a key holding either would re-rank on every regen tick and death for the same rows.
        var sb = new StringBuilder();
        sb.Append(string.Join(",", owned)).Append('|').Append(me.Level).Append("|True|")
          .Append(EnemyThreatMonitor.MySignature(snap));
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (string.Equals(p.Team, me.Team, StringComparison.Ordinal)) continue;
            sb.Append('|').Append(p.ChampionName).Append(EnemyThreatMonitor.EnemySignature(p));
        }
        return sb.ToString();
    }
}
