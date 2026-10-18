using Overlay.Core.ChampionDb;

namespace Overlay.Core.Combo;

/// <summary>
/// (M40) A <see cref="GameSnapshot"/> in which another champion is the active player, so the combo
/// pipeline — which always computes "the active player's combo" — can compute an ENEMY's combo
/// against me without a second damage model.
///
/// <para>Everything comes from what the scoreboard shows (champion, level, items, keystone) plus
/// Data Dragon base stats; what the scoreboard does not show is stated, never guessed silently:
/// no rune shards or minor runes, no stacks, base mana only, crit chance 0 (item crit is not in
/// the item stat source). Skill ranks are not exposed for other players and come from
/// <see cref="AssumedRanks"/>.</para>
/// </summary>
public static class SyntheticAttacker
{
    /// <summary>The summoner name the synthetic active player is filed under.</summary>
    public const string ActiveName = "__synthetic";

    /// <summary>Assumed ability ranks at <paramref name="level"/>: R at 6/11/16; one point in each
    /// basic ability by level 3, in <paramref name="maxOrder"/> order; then <paramref name="maxOrder"/>[0]
    /// to 5, then [1], then [2]. <paramref name="maxOrder"/> is a permutation of "QWE".</summary>
    public static (int Q, int W, int E, int R) AssumedRanks(int level, string maxOrder)
    {
        level = Math.Clamp(level, 1, 18);
        int r = level >= 16 ? 3 : level >= 11 ? 2 : level >= 6 ? 1 : 0;
        int points = level - r;
        var ranks = new Dictionary<char, int> { ['Q'] = 0, ['W'] = 0, ['E'] = 0 };
        foreach (char slot in maxOrder)
            if (points > 0) { ranks[slot] = 1; points--; }
        foreach (char slot in maxOrder)
        {
            int add = Math.Min(points, 5 - ranks[slot]);
            ranks[slot] += add;
            points -= add;
        }
        return (ranks['Q'], ranks['W'], ranks['E'], r);
    }

    /// <summary>The snapshot with <paramref name="enemy"/> as the active player at the given ranks,
    /// or null for a champion the repository does not know.</summary>
    public static GameSnapshot? Build(ScoreboardEntry enemy, (int Q, int W, int E, int R) ranks)
    {
        string championId = ChampionSummary.ResolveKoreanName(enemy.ChampionName) ?? enemy.ChampionName;
        if (ChampionRepository.Get(championId) is not { } champion) return null;

        int level = Math.Clamp(enemy.Level, 1, 18);
        double ad = LevelGrowth.Stat(champion.BaseStats.Ad, champion.StatsPerLevel.Ad, level);
        double hp = LevelGrowth.Stat(champion.BaseStats.Hp, champion.StatsPerLevel.Hp, level);
        double armor = LevelGrowth.Stat(champion.BaseStats.Armor, champion.StatsPerLevel.Armor, level);
        double mr = LevelGrowth.Stat(champion.BaseStats.Mr, champion.StatsPerLevel.Mr, level);
        double ap = 0;
        for (int i = 0; i < enemy.ItemCount && i < enemy.ItemIds.Length; i++)
        {
            if (ItemRepository.Get(enemy.ItemIds[i].ToString()) is not { } item) continue;
            ad += item.Stats.Ad;
            ap += item.Stats.Ap;
            hp += item.Stats.Hp;
            armor += item.Stats.Armor;
            mr += item.Stats.Mr;
        }

        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = ActiveName,
            Level = level,
            PlayerCount = 1,
            Stats = new ActivePlayerStats
            {
                AttackDamage = ad,
                AbilityPower = ap,
                MaxHealth = hp,
                CurrentHealth = hp,
                Armor = armor,
                MagicResist = mr,
                ResourceMax = champion.BaseStats.Mp,
                ResourceValue = champion.BaseStats.Mp,
                AbilityQ = ranks.Q,
                AbilityW = ranks.W,
                AbilityE = ranks.E,
                AbilityR = ranks.R,
                EquippedRuneIds = enemy.KeystoneId > 0 ? new[] { enemy.KeystoneId } : Array.Empty<int>(),
            },
        };
        var row = snap.Players[0];
        enemy.CopyTo(row);
        row.SummonerName = ActiveName;
        row.RiotId = string.Empty;
        row.ChampionName = championId;
        row.Level = level;
        return snap;
    }
}
