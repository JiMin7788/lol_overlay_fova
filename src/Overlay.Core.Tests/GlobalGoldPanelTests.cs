using Overlay.Core.EventBus;
using Overlay.Core.Gold;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M19 §3.3's <see cref="GlobalGoldPanel"/> message formatting and
/// wiring: always labelled "(추정)" (P2 — never presented as confirmed gold), correct sign
/// on the gold difference, and — per M07's "Pending User-Reported Changes" item-event-driven
/// recompute — publishes ONLY after a real <c>GAME.ITEM_CHANGED</c> event, never on a plain
/// unchanged poller tick.
/// </summary>
public class GlobalGoldPanelTests
{
    [Fact]
    public void BuildMessage_AlwaysLabelledAsEstimate()
    {
        var result = new TeamGoldEstimate(AllyGold: 5000, EnemyGold: 3800, Diff: 1200, IsEstimate: true);
        string msg = GlobalGoldPanel.BuildMessage(result);

        Assert.Contains("추정", msg);
        Assert.Contains("5000g", msg);
        Assert.Contains("3800g", msg);
        Assert.Contains("+1200g", msg);
    }

    [Fact]
    public void BuildMessage_NegativeDiff_ShowsMinusSign()
    {
        var result = new TeamGoldEstimate(AllyGold: 2000, EnemyGold: 2500, Diff: -500, IsEstimate: true);
        string msg = GlobalGoldPanel.BuildMessage(result);

        Assert.Contains("-500g", msg);
        Assert.DoesNotContain("+-500", msg);
    }

    [Fact]
    public async Task Start_DoesNotPublish_OnPlainUnchangedTicks_WithNoItemChangeEvent()
    {
        // No LiveClientEventPublisher in this test, so nothing ever publishes
        // GAME.ITEM_CHANGED — proves the panel no longer recomputes/publishes on every
        // poll tick by itself (the old continuous-recompute behavior this change removes).
        EventBus.EventBus.ResetForTests();
        try
        {
            var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n =>
                LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 300)));
            using var http = new HttpClient(handler);
            await using var poller = new LiveClientPoller(new PollerConfig(), http);

            using var panel = new GlobalGoldPanel(poller);
            panel.Start();

            bool published = false;
            EventBus.EventBus.Subscribe("UI.GLOBAL_GOLD", e => published = true);

            poller.Start();
            await Task.Delay(350); // several ticks, all identical — no item-change signal
            await poller.StopAsync();

            Assert.False(published, "UI.GLOBAL_GOLD must not publish without a GAME.ITEM_CHANGED event");
        }
        finally
        {
            EventBus.EventBus.ResetForTests();
        }
    }

    [Fact]
    public async Task Start_PublishesAfterRealItemChangedEvent_WithFreshTeamGoldMessage()
    {
        // Real LiveClientEventPublisher wired to the same poller, so GAME.ITEM_CHANGED is
        // the genuine M01/M07 signal (not a hand-published stand-in) — proves the panel's
        // recompute is actually driven by that event, using that same tick's fresh scoreboard.
        EventBus.EventBus.ResetForTests();
        try
        {
            var handler = new LiveClientPollerTests.FakeHttpMessageHandler(n => n switch
            {
                // Tick 1: initial sync — GAME.ITEM_CHANGED never fires on initial sync.
                1 => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(gameTime: 300)),
                // Tick 2+: ally buys an item.
                _ => LiveClientPollerTests.JsonResponse(LiveClientPollerTests.SamplePayload(
                        gameTime: 310, alliedItemIds: new[] { 1001 })),
            });
            using var http = new HttpClient(handler);
            await using var poller = new LiveClientPoller(new PollerConfig(), http);
            using var publisher = new LiveClientEventPublisher(poller);

            using var panel = new GlobalGoldPanel(poller);
            panel.Start();

            string? hud = null;
            using var delivered = new ManualResetEventSlim(false);
            EventBus.EventBus.Subscribe("UI.GLOBAL_GOLD", e => { hud = e.Payload as string; delivered.Set(); });

            poller.Start();
            Assert.True(delivered.Wait(TimeSpan.FromSeconds(3)), "UI.GLOBAL_GOLD was not delivered");
            await poller.StopAsync();

            Assert.NotNull(hud);
            Assert.Contains("추정", hud);
        }
        finally
        {
            EventBus.EventBus.ResetForTests();
        }
    }
}
