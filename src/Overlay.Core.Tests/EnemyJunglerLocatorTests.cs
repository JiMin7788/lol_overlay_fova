namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for <see cref="EnemyJunglerLocator"/> (M30 step 1): finds the enemy JUNGLE
/// row via <see cref="ScoreboardEntry.Position"/>, resolves "active player" tolerantly (riotId vs
/// summonerName), and — critically per M30's Policy Compliance Checklist — never guesses a
/// jungler when the position field is empty (practice tool/ARAM/normal-blind).
/// </summary>
public class EnemyJunglerLocatorTests
{
    private static GameSnapshot Snapshot(
        string activeRiotId, string activeTeam,
        params (string championName, string riotId, string team, string position)[] players)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerRiotId = activeRiotId,
            PlayerCount = players.Length + 1,
        };
        snap.Players[0].RiotId = activeRiotId;
        snap.Players[0].Team = activeTeam;
        snap.Players[0].ChampionName = "Me";
        for (int i = 0; i < players.Length; i++)
        {
            snap.Players[i + 1].ChampionName = players[i].championName;
            snap.Players[i + 1].RiotId = players[i].riotId;
            snap.Players[i + 1].Team = players[i].team;
            snap.Players[i + 1].Position = players[i].position;
        }
        return snap;
    }

    [Fact]
    public void Find_EnemyJunglePositionPresent_ReturnsThatRow()
    {
        var snap = Snapshot("Me#KR1", "ORDER",
            ("EnemyTop", "Top#KR1", "CHAOS", "TOP"),
            ("EnemyJungler", "Jgl#KR1", "CHAOS", "JUNGLE"),
            ("AllyJungler", "AllyJgl#KR1", "ORDER", "JUNGLE"));

        var found = EnemyJunglerLocator.Find(snap);

        Assert.NotNull(found);
        Assert.Equal("EnemyJungler", found!.ChampionName);
    }

    [Fact]
    public void Find_NoEnemyRowHasJunglePosition_ReturnsNull()
    {
        // Practice tool / ARAM / normal-blind: Live Client leaves position "" — must NOT guess.
        var snap = Snapshot("Me#KR1", "ORDER",
            ("EnemyTop", "Top#KR1", "CHAOS", ""),
            ("EnemyMid", "Mid#KR1", "CHAOS", ""));

        Assert.Null(EnemyJunglerLocator.Find(snap));
    }

    [Fact]
    public void Find_AllyReportsJungle_IsNotReturnedAsEnemy()
    {
        var snap = Snapshot("Me#KR1", "ORDER",
            ("AllyJungler", "AllyJgl#KR1", "ORDER", "JUNGLE"));

        Assert.Null(EnemyJunglerLocator.Find(snap));
    }

    [Fact]
    public void Find_NoActiveGame_ReturnsNull()
    {
        var snap = new GameSnapshot { HasData = false };
        Assert.Null(EnemyJunglerLocator.Find(snap));
    }

    [Fact]
    public void Find_NullSnapshot_ReturnsNull()
    {
        Assert.Null(EnemyJunglerLocator.Find(null));
    }

    [Fact]
    public void Find_ActivePlayerUnresolvable_ReturnsNull()
    {
        // ActivePlayerRiotId doesn't match any row's RiotId/SummonerName — cannot determine "my team".
        var snap = Snapshot("Ghost#KR1", "ORDER",
            ("EnemyJungler", "Jgl#KR1", "CHAOS", "JUNGLE"));
        snap.Players[0].RiotId = "SomeoneElse#KR1"; // active row itself doesn't match ActivePlayerRiotId

        Assert.Null(EnemyJunglerLocator.Find(snap));
    }
}
