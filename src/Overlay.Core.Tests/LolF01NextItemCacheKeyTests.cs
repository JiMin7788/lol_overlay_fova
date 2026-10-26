using Overlay.Core.Items;

namespace Overlay.Core.Tests;

/// <summary>
/// (lolF01) <see cref="NextItemCacheKey"/>: the tank key holds what the tank ranking reads and nothing that
/// changes on respawn or regen; the damage key is the M46 string unchanged.
/// Docs: docs/modules/M46_KEYS_COMPACT_TANK_ITEMS.md section 3.
/// </summary>
public class LolF01NextItemCacheKeyTests
{
    private static GameSnapshot Game()
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", PlayerCount = 3,
            Stats = new ActivePlayerStats { MaxHealth = 2000, CurrentHealth = 1500, Armor = 80, MagicResist = 50 },
        };
        Player(snap.Players[0], "Me", "Ornn", "ORDER", 11, 3031, 6672);
        Player(snap.Players[1], "Foe", "Zed", "CHAOS", 10, 6692);
        Player(snap.Players[2], "Foe2", "Lux", "CHAOS", 9, 6655);
        return snap;
    }

    private static void Player(ScoreboardEntry p, string name, string champion, string team, int level, params int[] items)
    {
        p.SummonerName = name; p.ChampionName = champion; p.Team = team; p.Level = level;
        p.KeystoneId = 8112; p.Spell1RawName = "SummonerFlash"; p.Spell2RawName = "SummonerDot";
        foreach (int id in items) p.TryAddItem(id);
    }

    private static string Tank(GameSnapshot snap) => NextItemCacheKey.For(snap, snap.Players[0], tank: true, manualTarget: null);

    [Fact]
    public void TankKey_IgnoresMyCurrentHealthAndEveryonesDeadState()
    {
        var snap = Game();
        string key = Tank(snap);

        snap.Stats.CurrentHealth = 1;
        Assert.Equal(key, Tank(snap));
        snap.Players[0].IsDead = true;
        Assert.Equal(key, Tank(snap));
        snap.Players[1].IsDead = true;
        snap.Players[2].IsDead = true;
        Assert.Equal(key, Tank(snap));
    }

    [Fact]
    public void TankKey_ChangesWithEachEnemyInput()
    {
        foreach (var change in new Action<GameSnapshot>[]
        {
            s => s.Players[1].TryAddItem(3071),
            s => s.Players[1].Level = 11,
            s => s.Players[2].KeystoneId = 8229,
            s => s.Players[2].Spell1RawName = "SummonerExhaust",
            s => s.Players[1].Spell2RawName = "SummonerTeleport",
        })
        {
            var snap = Game();
            string before = Tank(snap);
            change(snap);
            Assert.NotEqual(before, Tank(snap));
        }
    }

    [Fact]
    public void TankKey_ChangesWithMyStatsPastTheirBuckets()
    {
        foreach (var change in new Action<GameSnapshot>[]
        {
            s => s.Stats.MaxHealth += 25,
            s => s.Stats.Armor += 2,
            s => s.Stats.MagicResist += 2,
        })
        {
            var snap = Game();
            string before = Tank(snap);
            change(snap);
            Assert.NotEqual(before, Tank(snap));
        }
    }

    [Fact]
    public void DamageKey_IsTheM46String()
    {
        var snap = Game();
        Assert.Equal("3031,6672|11|False|", NextItemCacheKey.For(snap, snap.Players[0], tank: false, manualTarget: null));
        Assert.Equal("3031,6672|11|False|Zed", NextItemCacheKey.For(snap, snap.Players[0], tank: false, manualTarget: "Zed"));
    }
}
