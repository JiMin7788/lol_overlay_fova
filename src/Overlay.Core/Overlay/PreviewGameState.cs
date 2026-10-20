using Overlay.Core.ChampionDb;

namespace Overlay.Core.Overlay;

/// <summary>
/// (2026-10-19, user request: "미리보기에 콤보카드만 나옴 — 다른 오버레이도 전부 띄우기") The pretend game
/// the overlay preview draws when no game is running, so every element that reads a
/// <see cref="GameSnapshot"/> — status card, enemy portrait row, ward panel, kill-threat line, skill
/// overlay, and a combo hotkey pressed in preview — has something to show and can be positioned and
/// sized before a real game.
///
/// <para>Built from real data only where real data exists: "me" is the chosen champion's own base
/// stats grown to <see cref="Level"/> (Data Dragon, like the combo editor's out-of-game reference), and
/// the enemies are real champions holding real items. Everything about the situation itself — who the
/// enemies are, their items, one being dead, my HP at 70% — is a fixed SAMPLE, labelled as such by the
/// preview banner. Never fed to anything that records or learns (review, health tracker, target
/// snapshots): the composition only hands it to the overlay and to preview-time combo computation.</para>
/// </summary>
public static class PreviewGameState
{
    /// <summary>Summoner name of the preview player row.</summary>
    public const string PlayerName = "preview";

    /// <summary>Everyone is at 18: every ability at max rank — the rank fallback the engine uses when
    /// the snapshot carries no ability data — is then true rather than a stretch (same reason the combo
    /// editor's reference is stated at 18).</summary>
    public const int Level = 18;

    /// <summary>Enemy lineup in scoreboard order: champion id, position, items, respawn seconds
    /// (0 = alive). Real item ids; Control Wards (2055) so the ward panel has counts to show.</summary>
    private static readonly (string Champion, string Position, int[] Items, double Respawn)[] Enemies =
    {
        ("Darius", "TOP", new[] { 6631, 3047, 3071, 2055 }, 0),
        ("LeeSin", "JUNGLE", new[] { 6692, 3111, 6610 }, 0),
        ("Ahri", "MIDDLE", new[] { 6655, 3020, 3157, 2055 }, 0),
        ("Jinx", "BOTTOM", new[] { 3031, 3006, 3094 }, 18),
        ("Thresh", "UTILITY", new[] { 3190, 3047, 2055, 2055 }, 0),
    };

    /// <summary>The preview snapshot with <paramref name="championId"/> as me (middle lane, so the
    /// auto target is the enemy mid). Falls back to a champion the repository knows when
    /// <paramref name="championId"/> is unknown or empty; null only when the repository is empty.</summary>
    public static GameSnapshot? Build(string? championId)
    {
        var me = (championId is { Length: > 0 } ? ChampionRepository.Get(championId) : null)
                 ?? ChampionRepository.Get("Ahri");
        if (me is null) return null;

        double maxHp = LevelGrowth.Stat(me.BaseStats.Hp, me.StatsPerLevel.Hp, Level);
        var snap = new GameSnapshot
        {
            HasData = true,
            GameTime = 30 * 60,
            ActivePlayerSummonerName = PlayerName,
            Level = Level,
            CurrentGold = 1250,
            Stats = new ActivePlayerStats
            {
                MaxHealth = maxHp,
                CurrentHealth = maxHp * 0.7, // below full, so the kill-threat line has something to say
                AttackDamage = LevelGrowth.Stat(me.BaseStats.Ad, me.StatsPerLevel.Ad, Level),
                Armor = LevelGrowth.Stat(me.BaseStats.Armor, me.StatsPerLevel.Armor, Level),
                MagicResist = LevelGrowth.Stat(me.BaseStats.Mr, me.StatsPerLevel.Mr, Level),
            },
            PlayerCount = 1 + Enemies.Length,
        };

        var self = snap.Players[0];
        self.SummonerName = PlayerName;
        self.ChampionName = me.Id;
        self.Team = "ORDER";
        self.Position = "MIDDLE";
        self.Level = Level;
        self.CreepScore = 240;

        for (int i = 0; i < Enemies.Length; i++)
        {
            var (champion, position, items, respawn) = Enemies[i];
            var row = snap.Players[i + 1];
            row.SummonerName = "preview-enemy-" + (i + 1);
            row.ChampionName = champion;
            row.Team = "CHAOS";
            row.Position = position;
            row.Level = Level;
            row.IsDead = respawn > 0;
            row.RespawnTimer = respawn;
            row.ItemCount = items.Length;
            for (int s = 0; s < items.Length; s++)
            {
                row.ItemIds[s] = items[s];
                row.ItemCounts[s] = 1;
            }
        }
        return snap;
    }
}
