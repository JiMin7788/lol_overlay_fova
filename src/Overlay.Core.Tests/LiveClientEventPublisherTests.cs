using Overlay.Core.EventBus;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M01 LiveClient API's EventBus publishing contract (Interfaces
/// Output section + Internal Logic #2 + Acceptance Criteria #2/#3):
///  - Exactly the 8 documented GAME.* event types are published, with the documented
///    payload shapes (championName-keyed).
///  - A tick where nothing actually changed publishes nothing at all (field-level
///    diff, no spam).
///  - killerName in GAME.CHAMPION_DIED is resolved to a championName via the
///    scoreboard, not left as the raw Live Client identity.
///
/// Drives a real <see cref="LiveClientPoller"/> + <see cref="LiveClientEventPublisher"/>
/// pair against <see cref="LiveClientPollerTests.FakeHttpMessageHandler"/> canned
/// responses (no real game process), and observes the real M15 EventBus.
/// </summary>
public class LiveClientEventPublisherTests
{
    public LiveClientEventPublisherTests() => EventBus.EventBus.ResetForTests();

    [Fact]
    public async Task FullTickSequence_PublishesExactlyTheExpectedEvents_AndNothingOnAnUnchangedTick()
    {
        var events = new List<Event>();
        EventBus.EventBus.Subscribe("GAME.*", e => { lock (events) events.Add(e); });

        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n => n switch
        {
            // Tick 1: initial sync (gameTime 10).
            1 => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 10)),
            // Tick 2: identical to tick 1 — nothing changed at all.
            2 => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 10)),
            // Tick 3: level up + item purchase + gold change + Zed dies to the active player.
            3 => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(
                    gameTime: 20,
                    activeGold: 600,
                    alliedLevel: 2,
                    alliedItemIds: new[] { 1001 },
                    enemyDead: true,
                    killEventsJson: $"[{LiveClientPollerTests.ChampionKillEvent(1, 19.5, "Hide on bush", "Enemy1")}]")),
            // Tick 4: Zed respawns; nothing else changes besides the clock.
            _ => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(
                    gameTime: 25,
                    activeGold: 600,
                    alliedLevel: 2,
                    alliedItemIds: new[] { 1001 },
                    enemyDead: false,
                    killEventsJson: $"[{LiveClientPollerTests.ChampionKillEvent(1, 19.5, "Hide on bush", "Enemy1")}]")),
        });
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);
        using var publisher = new LiveClientEventPublisher(poller);

        poller.Start();
        await Task.Delay(650); // ticks 1-4 (and possibly a repeat of tick 4's unchanged data)
        await poller.StopAsync();

        List<Event> snapshot;
        lock (events) snapshot = new List<Event>(events);

        Assert.Contains(snapshot, e => e.Type == "GAME.CONNECTED");

        var gameTimeEvents = snapshot.Where(e => e.Type == "GAME.GAME_TIME_UPDATED")
            .Select(e => ((GameTimeUpdatedPayload)e.Payload!).GameTime)
            .ToList();
        // 10 (initial sync), 20, 25 — NOT a duplicate 10 for tick 2, since gameTime
        // did not change between tick 1 and tick 2 (Acceptance Criteria #3).
        Assert.Equal(new[] { 10d, 20d, 25d }, gameTimeEvents);

        var levelUp = Assert.Single(snapshot, e => e.Type == "GAME.PLAYER_LEVEL_UP");
        var levelPayload = (PlayerLevelUpPayload)levelUp.Payload!;
        Assert.Equal("Ahri", levelPayload.ChampionName);
        Assert.Equal(2, levelPayload.NewLevel);

        var itemChanged = Assert.Single(snapshot, e => e.Type == "GAME.ITEM_CHANGED");
        var itemPayload = (ItemChangedPayload)itemChanged.Payload!;
        Assert.Equal("Ahri", itemPayload.ChampionName);
        Assert.Equal(new[] { new ItemSlot(1001, 0) }, itemPayload.Items);

        var gold = Assert.Single(snapshot, e => e.Type == "GAME.GOLD_UPDATED");
        var goldPayload = (GoldUpdatedPayload)gold.Payload!;
        Assert.Equal("Ahri", goldPayload.ChampionName);
        Assert.Equal(600, goldPayload.CurrentGold);

        var died = Assert.Single(snapshot, e => e.Type == "GAME.CHAMPION_DIED");
        var diedPayload = (ChampionDiedPayload)died.Payload!;
        Assert.Equal("Zed", diedPayload.ChampionName);
        Assert.Equal("Ahri", diedPayload.KillerName); // resolved from raw summoner identity to championName
        Assert.Equal(19.5, diedPayload.Timestamp);

        var respawned = Assert.Single(snapshot, e => e.Type == "GAME.CHAMPION_RESPAWNED");
        Assert.Equal("Zed", ((ChampionRespawnedPayload)respawned.Payload!).ChampionName);

        // Tick 2 (fully unchanged tick) must not have produced ANY event.
        // Everything asserted above already accounts for ticks 1/3/4's events, so if
        // the total count matches that accounting exactly, tick 2 contributed zero.
        int expectedFromTicks1And3And4 = 1 /*CONNECTED*/ + 3 /*GAME_TIME_UPDATED*/ + 1 + 1 + 1 + 1 + 1;
        Assert.Equal(expectedFromTicks1And3And4, snapshot.Count);

        foreach (var e in snapshot)
            Assert.Equal("M01.LiveClient", e.Source);
    }

    [Fact]
    public async Task InhibKilled_PublishesInhibitorDestroyed_WithIdAndTime_NotOnInitialSync()
    {
        var events = new List<Event>();
        EventBus.EventBus.Subscribe("GAME.INHIBITOR_DESTROYED", e => { lock (events) events.Add(e); });

        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n => n switch
        {
            // Tick 1: initial sync already carries a historical InhibKilled entry — must
            // NOT be (re)published, same isInitialSync suppression as GAME.CHAMPION_DIED.
            1 => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(
                    gameTime: 10,
                    killEventsJson: $"[{LiveClientPollerTests.InhibKilledEvent(1, 9.0, "Hide on bush", "Barracks_T1_L1")}]")),
            // Tick 2: a second, NEW InhibKilled event appended to the (append-only) list.
            _ => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(
                    gameTime: 20,
                    killEventsJson: $"""
                    [{LiveClientPollerTests.InhibKilledEvent(1, 9.0, "Hide on bush", "Barracks_T1_L1")},
                     {LiveClientPollerTests.InhibKilledEvent(2, 19.5, "Enemy1", "Barracks_T2_C1")}]
                    """)),
        });
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);
        using var publisher = new LiveClientEventPublisher(poller);

        poller.Start();
        await Task.Delay(400);
        await poller.StopAsync();

        List<Event> snapshot;
        lock (events) snapshot = new List<Event>(events);

        var destroyed = Assert.Single(snapshot); // tick 1's entry suppressed, only tick 2's new one fires
        var payload = (InhibitorDestroyedPayload)destroyed.Payload!;
        Assert.Equal("Barracks_T2_C1", payload.InhibitorId);
        Assert.Equal(19.5, payload.GameTime);
        Assert.Equal("M01.LiveClient", destroyed.Source);
    }

    [Fact]
    public async Task CreepScoreChange_PublishesCsChanged_AndNotOnAnUnchangedTick()
    {
        // M30 depends on this event firing on a real creepScore diff, and NOT firing when
        // creepScore is unchanged (same field-level-diff discipline as GAME.ITEM_CHANGED).
        var events = new List<Event>();
        EventBus.EventBus.Subscribe("GAME.CS_CHANGED", e => { lock (events) events.Add(e); });

        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n => n switch
        {
            1 => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 10, alliedCreepScore: 20)),
            // Tick 2: unchanged — must NOT publish.
            2 => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 20, alliedCreepScore: 20)),
            // Tick 3: creepScore actually changes.
            _ => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 30, alliedCreepScore: 24)),
        });
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);
        using var publisher = new LiveClientEventPublisher(poller);

        poller.Start();
        await Task.Delay(500);
        await poller.StopAsync();

        List<Event> snapshot;
        lock (events) snapshot = new List<Event>(events);

        var changed = Assert.Single(snapshot); // tick 2 (unchanged) must not have fired
        var payload = (CreepScoreChangedPayload)changed.Payload!;
        Assert.Equal("Ahri", payload.ChampionName);
        Assert.Equal(24, payload.CreepScore);
    }

    [Fact]
    public async Task ConsumableCountDecrease_SameItemId_StillPublishesItemChanged()
    {
        // Drinking a Health Potion (2 -> 1) keeps the same itemId/slot — only "count" changes.
        // Before ItemSlot/ItemsChanged tracked count, this was invisible (id-only compare).
        var events = new List<Event>();
        EventBus.EventBus.Subscribe("GAME.ITEM_CHANGED", e => { lock (events) events.Add(e); });

        string Payload(double gameTime, int potionCount) => $$"""
        {
          "activePlayer": {
            "summonerName": "Hide on bush", "currentGold": 500, "level": 1,
            "championStats": { "currentHealth": 500, "maxHealth": 500, "resourceValue": 100, "resourceMax": 100, "attackDamage": 60, "abilityPower": 0, "armor": 30, "magicResist": 30, "moveSpeed": 340 }
          },
          "allPlayers": [
            {
              "summonerName": "Hide on bush", "championName": "Ahri", "team": "ORDER", "level": 1,
              "isDead": false, "respawnTimer": 0,
              "scores": { "kills": 0, "deaths": 0, "assists": 0, "creepScore": 0 },
              "items": [{"itemID": 2003, "slot": 0, "count": {{potionCount}}}]
            }
          ],
          "events": { "Events": [] },
          "gameData": { "gameTime": {{gameTime}} }
        }
        """;

        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n => n switch
        {
            1 => LiveClientPollerTests.JsonResponse(Payload(10, potionCount: 2)),
            2 => LiveClientPollerTests.JsonResponse(Payload(20, potionCount: 2)), // unchanged
            _ => LiveClientPollerTests.JsonResponse(Payload(30, potionCount: 1)), // drank one
        });
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);
        using var publisher = new LiveClientEventPublisher(poller);

        poller.Start();
        await Task.Delay(500);
        await poller.StopAsync();

        List<Event> snapshot;
        lock (events) snapshot = new List<Event>(events);

        var changed = Assert.Single(snapshot); // tick 2 (unchanged count) must not have fired
        var payload = (ItemChangedPayload)changed.Payload!;
        Assert.Equal(new[] { new ItemSlot(2003, 0, Count: 1) }, payload.Items);
    }

    [Fact]
    public async Task Gold_IsOnlyPublished_ForTheActivePlayer_NeverForOtherScoreboardEntries()
    {
        // Policy Compliance Checklist: the Live Client API never exposes another
        // player's gold, so GOLD_UPDATED must never carry any identity but the local
        // (active) player's, even though the enemy's championName is available on the
        // scoreboard for other event types.
        var events = new List<Event>();
        EventBus.EventBus.Subscribe("GAME.GOLD_UPDATED", e => { lock (events) events.Add(e); });

        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n => n switch
        {
            1 => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 10, activeGold: 500)),
            _ => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 20, activeGold: 900)),
        });
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);
        using var publisher = new LiveClientEventPublisher(poller);

        poller.Start();
        await Task.Delay(250);
        await poller.StopAsync();

        lock (events)
        {
            var payload = Assert.Single(events);
            Assert.Equal("Ahri", ((GoldUpdatedPayload)payload.Payload!).ChampionName); // the active player's champion, not "Zed"
        }
    }

    // ── Live-captured structure id formats (logs/structure_ids.log, 2026-07-26 §68 round) ──
    // Every raw below was recorded from real games; these pin the decoders against the ACTUAL
    // client output so a Riot format change fails loudly instead of silently misplacing timers.

    [Theory]
    [InlineData("Inhib_TOrder_L0_P1_478290356_0", "Order_L0")]
    [InlineData("Inhib_TOrder_L1_P1_1242677625_0", "Order_L1")]
    [InlineData("Inhib_TOrder_L2_P1_1509986696_0", "Order_L2")]
    [InlineData("Inhib_TChaos_L0_P1_2116220407_0", "Chaos_L0")]
    [InlineData("Inhib_TChaos_L1_P1_1931666598_0", "Chaos_L1")]
    [InlineData("Inhib_TChaos_L2_P1_2351107073_0", "Chaos_L2")]
    [InlineData("Barracks_T1_L1", "Order_L1")] // legacy shape stays supported
    public void NormalizeInhibitorId_LiveCapturedFormats(string raw, string expected)
        => Assert.Equal(expected, LiveClientEventPublisher.NormalizeInhibitorId(raw));

    [Theory]
    [InlineData("Turret_TChaos_L1_P4_392430785_0", "Chaos_NexusTop")]
    [InlineData("Turret_TChaos_L1_P5_342097928_0", "Chaos_NexusBot")]
    [InlineData("Turret_TOrder_L1_P4_1252041473_0", "Order_NexusTop")]
    [InlineData("Turret_TOrder_L1_P5_1201671996_0", "Order_NexusBot")]
    public void NormalizeNexusTurretId_LiveCapturedFormats(string raw, string expected)
        => Assert.Equal(expected, LiveClientEventPublisher.NormalizeNexusTurretId(raw));

    [Fact]
    public async Task TwoInhibKills_InOneTick_PublishBothWithDistinctNormalizedIds()
    {
        // §516 (pinned with the 02:55 live capture: Chaos L1 and L0 fell 7s apart, and a single
        // poll tick can carry both as NEW entries): one tick with two fresh InhibKilled events
        // must publish TWO GAME.INHIBITOR_DESTROYED events with distinct normalized ids.
        var events = new List<Event>();
        EventBus.EventBus.Subscribe("GAME.INHIBITOR_DESTROYED", e => { lock (events) events.Add(e); });

        var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n => n switch
        {
            1 => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(
                    gameTime: 2940, killEventsJson: "[]")),
            _ => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(
                    gameTime: 2961,
                    killEventsJson: $"""
                    [{LiveClientPollerTests.InhibKilledEvent(1, 2946.4, "Enemy1", "Inhib_TChaos_L1_P1_1931666598_0")},
                     {LiveClientPollerTests.InhibKilledEvent(2, 2960.5, "Enemy2", "Inhib_TChaos_L0_P1_2116220407_0")}]
                    """)),
        });
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);
        using var publisher = new LiveClientEventPublisher(poller);

        poller.Start();
        await Task.Delay(400);
        await poller.StopAsync();

        List<Event> snapshot;
        lock (events) snapshot = new List<Event>(events);

        Assert.Equal(2, snapshot.Count);
        var ids = snapshot.Select(e => ((InhibitorDestroyedPayload)e.Payload!).InhibitorId).ToList();
        Assert.Contains("Chaos_L1", ids);
        Assert.Contains("Chaos_L0", ids);
    }
}
