using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Items;
using Overlay.Core.Overlay;

namespace Overlay.Core.Tests;

/// <summary>
/// (2026-10-19) The overlay preview's sample game. Every overlay element that reads a snapshot
/// draws from it, so its shape is what makes the preview complete: me plus five enemies the portrait
/// row can list, one of them dead (a respawn countdown), Control Wards for the ward panel, and me
/// below full HP for the kill-threat line. Uses in-memory champions — no Data Dragon on disk.
/// </summary>
public class PreviewGameStateTests : IDisposable
{
    public PreviewGameStateTests() => ChampionRepository.ResetForTests();
    public void Dispose() => ChampionRepository.ResetForTests();

    private static ChampionData Champ(string id) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = 600, Armor = 30, Mr = 30, Ad = 60 },
        StatsPerLevel = new ChampionStatsPerLevel { Hp = 100, Armor = 4, Mr = 2, Ad = 3 },
    };

    [Fact]
    public void Build_MeAndFiveEnemies_ForTheChosenChampion()
    {
        ChampionRepository.Initialize(new[] { Champ("Zed"), Champ("Ahri") });

        var snap = PreviewGameState.Build("Zed");

        Assert.NotNull(snap);
        Assert.True(snap!.HasData);
        Assert.Equal(PreviewGameState.PlayerName, snap.ActivePlayerSummonerName);
        Assert.Equal("Zed", snap.Players[0].ChampionName);
        Assert.Equal(PreviewGameState.Level, snap.Level);
        // Real growth, and below full so the threat line has something to say.
        Assert.True(snap.Stats.MaxHealth > 600);
        Assert.True(snap.Stats.CurrentHealth < snap.Stats.MaxHealth);

        var roster = ComboRunner.EnemyRoster(snap);
        Assert.Equal(5, roster.Count);
        Assert.Contains(roster, e => e.IsDead && e.RespawnTimer > 0);

        var wards = ControlWardCounter.CountEnemies(snap);
        Assert.Contains(wards.Values, c => c > 0);
    }

    [Fact]
    public void Build_UnknownChampion_FallsBackToAKnownOne()
    {
        ChampionRepository.Initialize(new[] { Champ("Ahri") });

        var snap = PreviewGameState.Build("NotAChampion");

        Assert.NotNull(snap);
        Assert.Equal("Ahri", snap!.Players[0].ChampionName);
    }

    [Fact]
    public void Build_EmptyRepository_IsNull()
    {
        ChampionRepository.Initialize(Array.Empty<ChampionData>());
        Assert.Null(PreviewGameState.Build("Zed"));
    }
}
