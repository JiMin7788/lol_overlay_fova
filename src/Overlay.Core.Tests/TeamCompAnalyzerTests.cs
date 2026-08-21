using System.Text.Json;
using Overlay.Core.ChampionDb;
using Overlay.Core.ChampSelect;
using Overlay.Core.Lcu;

namespace Overlay.Core.Tests;

/// <summary>Team-comp analysis against the REAL bundled data (ddragon info + curated skill
/// damage), plus the pure champ-select board extraction.</summary>
public class TeamCompAnalyzerTests
{
    public TeamCompAnalyzerTests()
    {
        ChampionInfoDb.ResetForTests();
        Combo.SkillDamageDb.ResetForTests();
    }

    [Fact]
    public void Analyze_SharesSumToOne_AndRowsCarryRiotScores()
    {
        // Garen 86 (attack-lean), Annie 1 (magic-lean) — both in every ddragon cache.
        var comp = TeamCompAnalyzer.Analyze(new[] { 86, 1 });

        Assert.Equal(2, comp.Rows.Count);
        Assert.Equal(1.0, comp.AdShare + comp.ApShare, precision: 9);
        var garen = comp.Rows.First(r => r.Key == 86);
        var annie = comp.Rows.First(r => r.Key == 1);
        Assert.True(garen.Attack > garen.Magic, "Garen must lean physical in Riot's own scores");
        Assert.True(annie.Magic > annie.Attack, "Annie must lean magic");
    }

    [Fact]
    public void Analyze_FlagsCuratedTrueDamage()
    {
        // Vayne W (Silver Bolts) is curated as True damage; Annie has none.
        var comp = TeamCompAnalyzer.Analyze(new[] { 67, 1 });
        Assert.True(comp.Rows.First(r => r.Key == 67).HasTrueDamage);
        Assert.False(comp.Rows.First(r => r.Key == 1).HasTrueDamage);
        Assert.Equal(1, comp.TrueCount);
    }

    [Fact]
    public void TypeShares_SumToOne_AndTrueChampionYieldsATrueSlice()
    {
        // Vayne carries curated True hits → the three-way tendency must carve a real true slice.
        var comp = TeamCompAnalyzer.Analyze(new[] { 67, 1 });
        Assert.Equal(1.0, comp.PhysShare + comp.MagicShare + comp.TrueShare, precision: 9);
        Assert.True(comp.TrueShare > 0, "Vayne's curated True damage must produce a true-share slice");
        Assert.True(comp.PhysShare > 0 && comp.MagicShare > 0);
    }

    [Fact]
    public void Analyze_SkipsUnpickedZeroKeys()
    {
        var comp = TeamCompAnalyzer.Analyze(new[] { 0, 86, 0 });
        Assert.Single(comp.Rows);
    }

    [Fact]
    public void ExtractBoard_ReadsPicksAndBans()
    {
        using var doc = JsonDocument.Parse("""
        {
          "myTeam":    [ {"championId": 86}, {"championId": 0} ],
          "theirTeam": [ {"championId": 1},  {"championId": 67} ],
          "bans": { "myTeamBans": [238, 0], "theirTeamBans": [555] }
        }
        """);
        var board = LcuConnector.ExtractBoard(doc.RootElement);

        Assert.Equal(new[] { 86, 0 }, board.MyTeam);
        Assert.Equal(new[] { 1, 67 }, board.TheirTeam);
        Assert.Equal(new[] { 238 }, board.MyBans);   // 0 = no ban, dropped
        Assert.Equal(new[] { 555 }, board.TheirBans);
    }

    [Fact]
    public void ExtractBoard_FallsBackToPickIntent_ForPreLockOwnCell()
    {
        // Pre-lock the local player's cell has championId 0 with the hover in championPickIntent
        // — the ally comp bar must include it (2026-07-26 user report).
        using var doc = JsonDocument.Parse("""
        {
          "myTeam": [ {"championId": 0, "championPickIntent": 119}, {"championId": 86} ]
        }
        """);
        var board = LcuConnector.ExtractBoard(doc.RootElement);
        Assert.Equal(new[] { 119, 86 }, board.MyTeam);
    }
}
