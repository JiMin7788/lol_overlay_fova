using Overlay.Core.Items;

namespace Overlay.Core.Tests;

/// <summary>Enemy control-ward counting (2026-07-26 overlay): pure snapshot → counts.</summary>
public class ControlWardCounterTests
{
    private static GameSnapshot Snap(params (string champ, string team, (int id, int count)[] items)[] players)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerRiotId = "Me#KR1",
            PlayerCount = players.Length + 1,
        };
        snap.Players[0].RiotId = "Me#KR1";
        snap.Players[0].ChampionName = "Me";
        snap.Players[0].Team = "ORDER";
        for (int i = 0; i < players.Length; i++)
        {
            var p = snap.Players[i + 1];
            p.ChampionName = players[i].champ;
            p.Team = players[i].team;
            foreach (var (id, count) in players[i].items)
                p.TryAddItem(id, count);
        }
        return snap;
    }

    [Fact]
    public void CountsStackedWards_PerEnemy_AndIncludesZeroRows()
    {
        var counts = ControlWardCounter.CountEnemies(Snap(
            ("Galio", "CHAOS", new[] { (ControlWardCounter.ControlWardItemId, 2), (1055, 1) }),
            ("Twitch", "CHAOS", new[] { (ControlWardCounter.ControlWardItemId, 1) }),
            ("Braum", "CHAOS", System.Array.Empty<(int, int)>()),
            ("Sivir", "ORDER", new[] { (ControlWardCounter.ControlWardItemId, 2) }))); // ally: excluded

        Assert.Equal(2, counts["Galio"]);
        Assert.Equal(1, counts["Twitch"]);
        Assert.Equal(0, counts["Braum"]);   // zero row kept for a stable panel
        Assert.False(counts.ContainsKey("Sivir"));
        Assert.False(counts.ContainsKey("Me"));
    }

    [Fact]
    public void LegacyZeroCountSlot_CountsAsOne()
    {
        var counts = ControlWardCounter.CountEnemies(Snap(
            ("Galio", "CHAOS", new[] { (ControlWardCounter.ControlWardItemId, 0) })));
        Assert.Equal(1, counts["Galio"]);
    }

    [Fact]
    public void NoSnapshotOrNoActiveTeam_YieldsEmpty_NoGuessing()
    {
        Assert.Empty(ControlWardCounter.CountEnemies(null));

        var snap = Snap(("Galio", "CHAOS", new[] { (ControlWardCounter.ControlWardItemId, 1) }));
        snap.ActivePlayerRiotId = "Unknown#XX";
        snap.Players[0].RiotId = "Someone#Else";
        Assert.Empty(ControlWardCounter.CountEnemies(snap));
    }
}
