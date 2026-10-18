using Overlay.Core;
using Overlay.Core.Combo;
using Overlay.Core.Review;

namespace Overlay.Core.Tests;

/// <summary>(M42) The post-game review counts only what is observable: my HP against the threat
/// line, kills and deaths from the event list, and combo checks.</summary>
public class KillReviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "KillReview_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private readonly List<(string Killer, string Victim, double Time)> _kills = new();

    private GameSnapshot Snap(double time, double hp, bool ended = false, string result = "")
    {
        var s = new GameSnapshot
        {
            HasData = true, GameTime = time, ActivePlayerSummonerName = "Me", ActivePlayerRiotId = "Me#KR1",
            PlayerCount = 2, GameEnded = ended, GameResult = result,
            Stats = new ActivePlayerStats { CurrentHealth = hp, MaxHealth = 1000 },
        };
        s.Players[0].SummonerName = "Me"; s.Players[0].RiotId = "Me#KR1"; s.Players[0].ChampionName = "Ahri";
        s.Players[0].Team = "ORDER"; s.Players[0].Level = 11; s.Players[0].Kills = 2; s.Players[0].Deaths = 1;
        s.Players[1].SummonerName = "Foe"; s.Players[1].ChampionName = "Zed"; s.Players[1].Team = "CHAOS";
        for (int i = 0; i < _kills.Count; i++)
        {
            var k = s.KillEvents[i];
            k.EventId = i + 1; k.IsChampionKill = true;
            k.KillerName = _kills[i].Killer; k.VictimName = _kills[i].Victim; k.EventTime = _kills[i].Time;
        }
        s.KillEventCount = _kills.Count;
        return s;
    }

    private static readonly IReadOnlyList<EnemyThreat> ZedThreat =
        new[] { new EnemyThreat("Zed", new ThreatEstimate("Zed", 11, 600, "x")) };

    [Fact]
    public void ADeathInsideADangerWindow_AndAKillAfterACheck_AreCounted()
    {
        var rec = new KillReviewRecorder(() => new DateTime(2026, 10, 17, 21, 0, 0));
        Assert.Null(rec.Sample(Snap(100, 1000), ZedThreat));
        rec.Sample(Snap(101, 500), ZedThreat);           // 500 <= 600: danger opens
        rec.Sample(Snap(102, 400), ZedThreat);
        _kills.Add(("Foe", "Me", 103));                  // Zed kills me while in danger
        rec.Sample(Snap(104, 0), ZedThreat);              // dead: window closes

        rec.Sample(Snap(200, 1000), ZedThreat);
        rec.OnComboCheck("Zed", 120);
        rec.OnComboCheck("Zed", 120);                     // live refresh: same check, not counted twice
        _kills.Add(("Me#KR1", "Foe", 210));              // checked kill (Riot id form)
        _kills.Add(("Me", "Foe", 400));                   // unchecked kill
        rec.Sample(Snap(400, 1000), ZedThreat);

        var review = rec.Sample(Snap(1500, 1000, ended: true, result: "Win"), ZedThreat);

        Assert.NotNull(review);
        Assert.Equal("Ahri", review!.Champion);
        Assert.Equal("Win", review.Result);
        Assert.Single(review.DangerWindows);
        Assert.Equal("Zed", review.DangerWindows[0].EnemyChampion);
        Assert.Equal(1, review.DeathsInDanger);
        Assert.Equal("Zed", review.DeathEntries.Single().KillerChampion);
        Assert.Equal(2, review.KillEntries.Count);
        Assert.Equal(1, review.CheckedKills);
        Assert.Single(review.ComboChecks);
    }

    [Fact]
    public void ADeathWithFullHpBeforeIt_IsNotInDanger()
    {
        var rec = new KillReviewRecorder();
        rec.Sample(Snap(300, 1000), ZedThreat);
        _kills.Add(("Foe", "Me", 301));
        var review = rec.Sample(Snap(1000, 1000, ended: true), ZedThreat);
        Assert.False(review!.DeathEntries.Single().InDanger);
    }

    [Fact]
    public void AShortGameIsNotKept_AndADisconnectFinishesTheGame()
    {
        var rec = new KillReviewRecorder();
        rec.Sample(Snap(60, 1000), ZedThreat);
        Assert.Null(rec.Sample(null, ZedThreat));          // ended at 60s: not kept

        rec.Sample(Snap(900, 1000), ZedThreat);
        Assert.NotNull(rec.Sample(null, ZedThreat));       // the game vanished: finished
        Assert.Null(rec.Sample(null, ZedThreat));          // and only once
    }

    /// <summary>Live bug (2026-10-17): the end screen keeps serving GameEnd for several seconds and
    /// each sample started and saved the game again — 14 copies of one game.</summary>
    [Fact]
    public void TheEndScreen_IsNotANewGame()
    {
        var rec = new KillReviewRecorder();
        rec.Sample(Snap(300, 1000), ZedThreat);
        Assert.NotNull(rec.Sample(Snap(1500, 1000, ended: true, result: "Win"), ZedThreat));
        for (int i = 1; i <= 15; i++)
            Assert.Null(rec.Sample(Snap(1500 + i, 1000, ended: true, result: "Win"), ZedThreat));
        Assert.Null(rec.Sample(null, ZedThreat));
    }

    [Fact]
    public void Store_RoundTrips_NewestFirst_AndKeepsFifty()
    {
        var store = new KillReviewStore(_dir);
        for (int i = 0; i < KillReviewStore.Keep + 3; i++)
            store.Save(new GameReview(new DateTime(2026, 10, 1).AddHours(i), "Ahri", 11, i, 0, 0, 1500, "Win",
                Array.Empty<DangerWindow>(), Array.Empty<DeathEntry>(), Array.Empty<KillEntry>(), Array.Empty<ComboCheck>()));
        var all = store.LoadAll();
        Assert.Equal(KillReviewStore.Keep, all.Count);
        Assert.Equal(KillReviewStore.Keep + 2, all[0].Kills);
    }
}
