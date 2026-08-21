using System.Text;
using Overlay.Core.ChampionDb;
using Overlay.Core.Gold;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M19 §3.3 Global Gold Compare's <see cref="GoldEstimate"/> formula.
/// Verifies the pure per-player formula against hand-computed values (documented sourcing
/// in <see cref="GoldEstimate"/>'s own doc comment: 500 starting gold, 2.004 g/s passive
/// after the 1:50 ramp, 20.5 g/CS blended average, 300/150 kill/assist bounty), then the
/// team-split aggregation via a hand-built Live Client payload. Also covers the M07
/// "Pending User-Reported Changes" v2 items-driven signal (<see cref="ItemRepository"/>).
/// </summary>
public class GoldEstimateTests : IDisposable
{
    public GoldEstimateTests() => ItemRepository.ResetForTests();

    public void Dispose() => ItemRepository.ResetForTests();

    private static void SeedItems() => ItemRepository.Initialize(new[]
    {
        new ItemData { Id = "3089", Name = "Rabadon's Deathcap", GoldTotal = 3500 },
        new ItemData { Id = "1058", Name = "Needlessly Large Rod", GoldTotal = 1200 },
    });

    [Fact]
    public void EstimatePlayerGold_HandComputedValue_AtKnownCsKillsAssistsTime()
    {
        // 10:00 game time, 100 CS, 3 kills, 5 assists.
        // passive = 2.004 * (600 - 110) = 981.96
        // cs      = 100 * 20.5          = 2050
        // combat  = 3*300 + 5*150       = 1650
        // total   = 500 + 981.96 + 2050 + 1650 = 5181.96
        double result = GoldEstimate.EstimatePlayerGold(creepScore: 100, kills: 3, assists: 5, gameTimeSeconds: 600);

        Assert.Equal(5181.96, result, precision: 2);
    }

    [Fact]
    public void EstimatePlayerGold_BeforePassiveRampEnd_OnlyStartingGold()
    {
        // 1:00 game time (before the 1:50 ramp end), no CS/kills/assists.
        double result = GoldEstimate.EstimatePlayerGold(creepScore: 0, kills: 0, assists: 0, gameTimeSeconds: 60);

        Assert.Equal(GoldEstimate.StartingGold, result);
    }

    [Fact]
    public void EstimatePlayerGold_ExactlyAtRampEnd_StillJustStartingGold()
    {
        double result = GoldEstimate.EstimatePlayerGold(
            creepScore: 0, kills: 0, assists: 0, gameTimeSeconds: GoldEstimate.PassiveGoldRampEndSeconds);

        Assert.Equal(GoldEstimate.StartingGold, result);
    }

    [Fact]
    public void TryCompute_NoData_ReturnsFalse()
    {
        var snap = new GameSnapshot();
        Assert.False(GoldEstimate.TryCompute(snap, out _));
    }

    [Fact]
    public void TryCompute_SplitsByActivePlayersTeam_AndSumsBothSides()
    {
        // Two ORDER players (active player's team) and two CHAOS players, distinct
        // CS/kills/assists per player so the per-team sum is unambiguous.
        var json = BuildPayload(gameTime: 300);
        var snap = new GameSnapshot();
        bool ok = LiveDataParser.Parse(Encoding.UTF8.GetBytes(json), snap);
        Assert.True(ok);

        Assert.True(GoldEstimate.TryCompute(snap, out var result));
        Assert.True(result.IsEstimate);

        // ally = P1(cs 50, k1 a1) + P2(cs 20, k0 a0)
        double ally = GoldEstimate.EstimatePlayerGold(50, 1, 1, 300)
                    + GoldEstimate.EstimatePlayerGold(20, 0, 0, 300);
        // enemy = P3(cs 60, k2 a0) + P4(cs 10, k0 a3)
        double enemy = GoldEstimate.EstimatePlayerGold(60, 2, 0, 300)
                     + GoldEstimate.EstimatePlayerGold(10, 0, 3, 300);

        Assert.Equal(ally, result.AllyGold, precision: 6);
        Assert.Equal(enemy, result.EnemyGold, precision: 6);
        Assert.Equal(ally - enemy, result.Diff, precision: 6);
    }

    [Fact]
    public void EstimatePlayerGold_ItemsEstimateExceedsEarnedEstimate_ReturnsItemsEstimate()
    {
        SeedItems();
        // Early game (60s, before the passive ramp end) so the EARNED estimate is just
        // StartingGold (500), but the player already has a full Rabadon's built
        // (3500g total) — the ITEMS estimate (500 + 3500 = 4000) must win via Math.Max.
        double result = GoldEstimate.EstimatePlayerGold(
            creepScore: 0, kills: 0, assists: 0, gameTimeSeconds: 60,
            itemIds: new[] { 3089 }, itemCount: 1);

        Assert.Equal(GoldEstimate.StartingGold + 3500, result);
    }

    [Fact]
    public void EstimatePlayerGold_EarnedEstimateExceedsItemsEstimate_ReturnsEarnedEstimate()
    {
        SeedItems();
        // Late game with high CS/kills but only a single cheap component held (1200g) —
        // the EARNED estimate should dominate, proving the supplementary formula still
        // fills the gap BETWEEN purchases rather than being discarded outright.
        double earned = GoldEstimate.EstimatePlayerGold(creepScore: 100, kills: 3, assists: 5, gameTimeSeconds: 600);
        double result = GoldEstimate.EstimatePlayerGold(
            creepScore: 100, kills: 3, assists: 5, gameTimeSeconds: 600,
            itemIds: new[] { 1058 }, itemCount: 1);

        Assert.Equal(earned, result, precision: 6);
    }

    [Fact]
    public void EstimatePlayerGold_UnknownItemId_SkippedWithoutThrowing()
    {
        ItemRepository.ResetForTests(); // no items loaded at all
        double result = GoldEstimate.EstimatePlayerGold(
            creepScore: 0, kills: 0, assists: 0, gameTimeSeconds: 60,
            itemIds: new[] { 999999 }, itemCount: 1);

        Assert.Equal(GoldEstimate.StartingGold, result);
    }

    [Fact]
    public void TryCompute_UsesPerPlayerItems_WhenItemsEstimateIsLarger()
    {
        SeedItems();
        // Same scoreboard as TryCompute_SplitsByActivePlayersTeam_AndSumsBothSides, but
        // early game time (60s) and the active player already holds a Rabadon's — the
        // items estimate for that one player should dominate their EARNED estimate and
        // flow through into the team total.
        var json = BuildPayload(gameTime: 60, activeItemId: 3089);
        var snap = new GameSnapshot();
        bool ok = LiveDataParser.Parse(Encoding.UTF8.GetBytes(json), snap);
        Assert.True(ok);

        Assert.True(GoldEstimate.TryCompute(snap, out var result));

        double allyP1 = GoldEstimate.EstimatePlayerGold(50, 1, 1, 60, itemIds: new[] { 3089 }, itemCount: 1);
        double allyP2 = GoldEstimate.EstimatePlayerGold(20, 0, 0, 60);
        Assert.Equal(allyP1 + allyP2, result.AllyGold, precision: 6);
        Assert.True(result.AllyGold > GoldEstimate.EstimatePlayerGold(50, 1, 1, 60) + allyP2,
            "the items estimate should have raised the ally total above the plain EARNED-only sum");
    }

    private static string BuildPayload(double gameTime, int? activeItemId = null)
    {
        string activeItemsJson = activeItemId is null ? "[]" : $"[{{\"itemID\":{activeItemId},\"slot\":0}}]";
        return $$"""
    {
      "activePlayer": {
        "summonerName": "Me",
        "currentGold": 500,
        "level": 1,
        "championStats": { "currentHealth": 500, "maxHealth": 500, "resourceValue": 100, "resourceMax": 100, "attackDamage": 60, "abilityPower": 0, "armor": 30, "magicResist": 30, "moveSpeed": 340 }
      },
      "allPlayers": [
        { "summonerName": "Me", "championName": "Ahri", "team": "ORDER", "level": 5, "isDead": false, "respawnTimer": 0,
          "scores": { "kills": 1, "deaths": 0, "assists": 1, "creepScore": 50 },
          "items": {{activeItemsJson}} },
        { "summonerName": "Ally2", "championName": "Jinx", "team": "ORDER", "level": 4, "isDead": false, "respawnTimer": 0,
          "scores": { "kills": 0, "deaths": 0, "assists": 0, "creepScore": 20 }, "items": [] },
        { "summonerName": "Enemy1", "championName": "Zed", "team": "CHAOS", "level": 5, "isDead": false, "respawnTimer": 0,
          "scores": { "kills": 2, "deaths": 0, "assists": 0, "creepScore": 60 }, "items": [] },
        { "summonerName": "Enemy2", "championName": "Lux", "team": "CHAOS", "level": 3, "isDead": false, "respawnTimer": 0,
          "scores": { "kills": 0, "deaths": 0, "assists": 3, "creepScore": 10 }, "items": [] }
      ],
      "events": { "Events": [] },
      "gameData": { "gameTime": {{gameTime}} }
    }
    """;
    }
}
