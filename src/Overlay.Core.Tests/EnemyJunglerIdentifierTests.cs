using Overlay.Core.Jungle;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M31 P3 §9 decision 1's jungler-ID resolution order —
/// <see cref="EnemyJunglerIdentifier"/>: position field (ranked/draft) -&gt; Smite (any mode) ->
/// settings override (last resort) -&gt; null (no guess).
/// </summary>
public class EnemyJunglerIdentifierTests
{
    private static GameSnapshot Snapshot(params (string championName, string riotId, string team, string position, string spell1, string spell2)[] enemyAndAllies)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerRiotId = "Me#KR1",
            PlayerCount = enemyAndAllies.Length + 1,
        };
        snap.Players[0].RiotId = "Me#KR1";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].ChampionName = "Me";
        for (int i = 0; i < enemyAndAllies.Length; i++)
        {
            var e = enemyAndAllies[i];
            snap.Players[i + 1].ChampionName = e.championName;
            snap.Players[i + 1].RiotId = e.riotId;
            snap.Players[i + 1].Team = e.team;
            snap.Players[i + 1].Position = e.position;
            snap.Players[i + 1].Spell1RawName = e.spell1;
            snap.Players[i + 1].Spell2RawName = e.spell2;
        }
        return snap;
    }

    [Fact]
    public void PositionField_TakesPriority_OverSmiteOnADifferentRow()
    {
        var snap = Snapshot(
            ("Kayn", "A#KR1", "CHAOS", "JUNGLE", "", ""),
            ("Warwick", "B#KR1", "CHAOS", "TOP", "SummonerSmite", "")); // has Smite but not JUNGLE position

        Assert.Equal("Kayn", EnemyJunglerIdentifier.Find(snap, null));
    }

    [Fact]
    public void NoPosition_FallsBackToSmite_EitherSpellSlot()
    {
        var snap1 = Snapshot(("Warwick", "A#KR1", "CHAOS", "", "SummonerSmite", "SummonerFlash"));
        Assert.Equal("Warwick", EnemyJunglerIdentifier.Find(snap1, null));

        var snap2 = Snapshot(("Warwick", "A#KR1", "CHAOS", "", "SummonerFlash", "SummonerSmite"));
        Assert.Equal("Warwick", EnemyJunglerIdentifier.Find(snap2, null));
    }

    [Fact]
    public void NoPositionNoSmite_FallsBackToSettingsOverride()
    {
        var snap = Snapshot(
            ("Garen", "A#KR1", "CHAOS", "", "", ""),
            ("Warwick", "B#KR1", "CHAOS", "", "", ""));

        Assert.Equal("Warwick", EnemyJunglerIdentifier.Find(snap, "Warwick"));
    }

    [Fact]
    public void SettingsOverride_IgnoredIfNotAnEnemyChampionThisGame()
    {
        var snap = Snapshot(("Garen", "A#KR1", "CHAOS", "", "", ""));

        Assert.Null(EnemyJunglerIdentifier.Find(snap, "Warwick")); // Warwick isn't in this game
    }

    [Fact]
    public void NoSignalAtAll_ReturnsNull_NoGuess()
    {
        var snap = Snapshot(("Garen", "A#KR1", "CHAOS", "", "", ""));
        Assert.Null(EnemyJunglerIdentifier.Find(snap, null));
    }

    [Fact]
    public void AllySmite_NeverCounted()
    {
        var snap = Snapshot(("AllyWarwick", "A#KR1", "ORDER", "", "SummonerSmite", ""));
        Assert.Null(EnemyJunglerIdentifier.Find(snap, null));
    }

    [Fact]
    public void NullOrEmptySnapshot_ReturnsNull()
    {
        Assert.Null(EnemyJunglerIdentifier.Find(null, null));
        Assert.Null(EnemyJunglerIdentifier.Find(new GameSnapshot { HasData = false }, null));
    }
}
