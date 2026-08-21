using Overlay.Core.EventBus;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M30 Enemy Jungler Spotted Alert (docs/modules/M30_ENEMY_JUNGLER_SPOTTED.md)
/// Internal Logic #2-5 — <see cref="EnemyJunglerSpottedDetector"/>. Verifies the alert fires only
/// for the enemy JUNGLE-position row (never an ally, never a non-jungler enemy), never fires when
/// `position` data is unavailable (P2 — no guessing), and dedupes CS+item changing in the same
/// polling tick down to exactly one alert while still re-alerting on a later, distinct tick.
/// </summary>
public class EnemyJunglerSpottedDetectorTests : IDisposable
{
    public EnemyJunglerSpottedDetectorTests() => EventBus.EventBus.ResetForTests();
    public void Dispose() => EventBus.EventBus.ResetForTests();

    private sealed class FakeClock : IClock
    {
        public long NowMs { get; set; }
    }

    private static GameSnapshot Snapshot(double gameTime, params (string championName, string riotId, string team, string position)[] enemyAndAllies)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            GameTime = gameTime,
            ActivePlayerRiotId = "Me#KR1",
            PlayerCount = enemyAndAllies.Length + 1,
        };
        snap.Players[0].RiotId = "Me#KR1";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].ChampionName = "Me";
        for (int i = 0; i < enemyAndAllies.Length; i++)
        {
            snap.Players[i + 1].ChampionName = enemyAndAllies[i].championName;
            snap.Players[i + 1].RiotId = enemyAndAllies[i].riotId;
            snap.Players[i + 1].Team = enemyAndAllies[i].team;
            snap.Players[i + 1].Position = enemyAndAllies[i].position;
        }
        return snap;
    }

    private static (List<Event> ui, List<Event> voice) Subscribe()
    {
        var ui = new List<Event>();
        var voice = new List<Event>();
        EventBus.EventBus.Subscribe("UI.ENEMY_JUNGLER_SPOTTED_ALERT", e => { lock (ui) ui.Add(e); });
        EventBus.EventBus.Subscribe("VOICE.SPEAK", e => { lock (voice) voice.Add(e); });
        return (ui, voice);
    }

    /// <summary>UI.* is always dispatched async by M15 (EventBus.cs), so a positive assertion on
    /// <paramref name="ui"/> must wait for the delivery instead of racing the dispatch thread.</summary>
    private static void WaitForUiCount(List<Event> ui, int expectedCount)
    {
        SpinWait.SpinUntil(() => { lock (ui) return ui.Count >= expectedCount; }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ItemChange_OnEnemyJungler_RaisesAlert()
    {
        GameSnapshot snap = Snapshot(100.0, ("EnemyJungler", "Jgl#KR1", "CHAOS", "JUNGLE"));
        var (ui, voice) = Subscribe();
        using var detector = new EnemyJunglerSpottedDetector(() => snap, new FakeClock { NowMs = 1000 });
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED",
            new ItemChangedPayload("EnemyJungler", Array.Empty<ItemSlot>()), "Test");

        WaitForUiCount(ui, 1);
        var uiAlert = Assert.Single(ui);
        Assert.Equal("적 정글이 감지되었습니다 (EnemyJungler)", uiAlert.Payload);
        var speech = (SpeechRequest)Assert.Single(voice).Payload!;
        Assert.Equal("적 정글이 감지되었습니다 (EnemyJungler)", speech.Text);
    }

    [Fact]
    public void CsChange_OnEnemyJungler_RaisesAlert()
    {
        GameSnapshot snap = Snapshot(100.0, ("EnemyJungler", "Jgl#KR1", "CHAOS", "JUNGLE"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyJunglerSpottedDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.CS_CHANGED",
            new CreepScoreChangedPayload("EnemyJungler", 10), "Test");

        WaitForUiCount(ui, 1);
        Assert.Single(ui);
    }

    [Fact]
    public void ItemChange_OnEnemyNonJungler_NoAlert()
    {
        GameSnapshot snap = Snapshot(100.0,
            ("EnemyJungler", "Jgl#KR1", "CHAOS", "JUNGLE"),
            ("EnemyTop", "Top#KR1", "CHAOS", "TOP"));
        var (ui, voice) = Subscribe();
        using var detector = new EnemyJunglerSpottedDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED",
            new ItemChangedPayload("EnemyTop", Array.Empty<ItemSlot>()), "Test");

        Assert.Empty(ui);
        Assert.Empty(voice);
    }

    [Fact]
    public void ItemChange_OnAllyJungler_NoAlert()
    {
        GameSnapshot snap = Snapshot(100.0, ("AllyJungler", "AllyJgl#KR1", "ORDER", "JUNGLE"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyJunglerSpottedDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED",
            new ItemChangedPayload("AllyJungler", Array.Empty<ItemSlot>()), "Test");

        Assert.Empty(ui);
    }

    [Fact]
    public void NoEnemyPositionData_NoAlert_EvenIfChampionNameHappensToMatch()
    {
        // practice tool / ARAM / normal-blind: Live Client leaves position "" for everyone.
        GameSnapshot snap = Snapshot(100.0, ("Kayn", "Jgl#KR1", "CHAOS", ""));
        var (ui, _) = Subscribe();
        using var detector = new EnemyJunglerSpottedDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED",
            new ItemChangedPayload("Kayn", Array.Empty<ItemSlot>()), "Test");

        Assert.Empty(ui);
    }

    [Fact]
    public void SameTick_CsAndItemBothChange_OnlyOneAlert()
    {
        GameSnapshot snap = Snapshot(100.0, ("EnemyJungler", "Jgl#KR1", "CHAOS", "JUNGLE"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyJunglerSpottedDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED",
            new ItemChangedPayload("EnemyJungler", Array.Empty<ItemSlot>()), "Test");
        EventBus.EventBus.Publish("GAME.CS_CHANGED",
            new CreepScoreChangedPayload("EnemyJungler", 10), "Test");

        WaitForUiCount(ui, 1);
        Assert.Single(ui);
    }

    [Fact]
    public void LaterDistinctTick_RaisesItsOwnAlert()
    {
        GameSnapshot snap = Snapshot(100.0, ("EnemyJungler", "Jgl#KR1", "CHAOS", "JUNGLE"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyJunglerSpottedDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.CS_CHANGED",
            new CreepScoreChangedPayload("EnemyJungler", 10), "Test");

        snap.GameTime = 130.0; // next real polling tick
        EventBus.EventBus.Publish("GAME.CS_CHANGED",
            new CreepScoreChangedPayload("EnemyJungler", 14), "Test");

        WaitForUiCount(ui, 2);
        Assert.Equal(2, ui.Count);
    }

    [Fact]
    public void LevelUp_OnEnemyJungler_RaisesAlert()
    {
        GameSnapshot snap = Snapshot(100.0, ("EnemyJungler", "Jgl#KR1", "CHAOS", "JUNGLE"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyJunglerSpottedDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.PLAYER_LEVEL_UP",
            new PlayerLevelUpPayload("EnemyJungler", 6), "Test");

        WaitForUiCount(ui, 1);
        Assert.Single(ui);
    }

    [Fact]
    public void LevelUp_OnEnemyNonJungler_NoAlert()
    {
        GameSnapshot snap = Snapshot(100.0,
            ("EnemyJungler", "Jgl#KR1", "CHAOS", "JUNGLE"),
            ("EnemyTop", "Top#KR1", "CHAOS", "TOP"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyJunglerSpottedDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.PLAYER_LEVEL_UP",
            new PlayerLevelUpPayload("EnemyTop", 6), "Test");

        Assert.Empty(ui);
    }
}
