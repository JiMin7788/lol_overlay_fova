using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;
using Overlay.Core.Items;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for the testable heart of M07 Item Tracker
/// (docs/modules/M07_ITEM_TRACKER.md) — <see cref="ItemTracker"/> — plus the Lead-authorized
/// M11 <see cref="ItemData"/> gold/build-tree extension it depends on. Item cost + build-tree
/// data is loaded from the REAL checked-in Data Dragon item.json cache (P1 source), not fixtures,
/// so the exact-price and completion assertions are pinned to live values.
/// </summary>
public class ItemTrackerTests : IDisposable
{
    // Real Data Dragon item ids used below (values verified against the cached item.json):
    private const string RabadonId = "3089";     // total 3500, from ["1058","1058"], into null  → completed
    private const string LargeRodId = "1058";    // total 1200, from null, into non-empty         → raw component
    private const string SorcShoesId = "3020";   // total 1100, from ["1001"], into non-empty

    public ItemTrackerTests()
    {
        EventBus.EventBus.ResetForTests();
        ItemRepository.ResetForTests();
        ItemRepository.Initialize(LoadRealItems());
    }

    public void Dispose()
    {
        EventBus.EventBus.ResetForTests();
        ItemRepository.ResetForTests();
    }

    private static IReadOnlyList<ItemData> LoadRealItems()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "ddragon", "16.13.1", "item.json");
        return DDragonParser.ParseItems(File.ReadAllText(path));
    }

    private sealed class FakeClock : IClock
    {
        public long NowMs { get; set; }
    }

    private static ItemChangedPayload Items(string champion, params int[] itemIds)
    {
        var slots = new ItemSlot[itemIds.Length];
        for (int i = 0; i < itemIds.Length; i++) slots[i] = new ItemSlot(itemIds[i], i);
        return new ItemChangedPayload(champion, slots);
    }

    // ── M11 extension: gold/build-tree parse against real item.json ──────────────

    [Fact]
    public void ParseItems_PopulatesGoldTotalAndBuildTree_FromRealItemJson()
    {
        var rabadon = ItemRepository.Get(RabadonId)!;
        Assert.Equal(3500, rabadon.GoldTotal);
        Assert.Equal(new[] { "1058", "1058" }, rabadon.BuildsFrom);
        Assert.Empty(rabadon.BuildsInto); // "into" absent in json → empty (finished item)

        var rod = ItemRepository.Get(LargeRodId)!;
        Assert.Equal(1200, rod.GoldTotal);
        Assert.Empty(rod.BuildsFrom);      // "from" absent in json → empty (raw component)
        Assert.NotEmpty(rod.BuildsInto);

        Assert.Equal(1100, ItemRepository.Get(SorcShoesId)!.GoldTotal);
    }

    // ── GetMissingGold: exact Data Dragon price (Acceptance #2) ──────────────────

    [Fact]
    public void GetMissingGold_ReturnsExactRemainder_AgainstRealPrice()
    {
        using var tracker = new ItemTracker();
        Assert.Equal(240, tracker.GetMissingGold("Syndra", RabadonId, 3260)); // 3500 - 3260
    }

    [Fact]
    public void GetMissingGold_FlooredAtZero_WhenAlreadyAffordable()
    {
        using var tracker = new ItemTracker();
        Assert.Equal(0, tracker.GetMissingGold("Syndra", RabadonId, 4000)); // 3500 - 4000 → 0
    }

    [Fact]
    public void GetMissingGold_UnknownItemId_Throws()
    {
        using var tracker = new ItemTracker();
        Assert.Throws<ArgumentException>(() => tracker.GetMissingGold("Syndra", "999999", 0));
    }

    // ── Item-completion detection (Acceptance #1) ────────────────────────────────

    [Fact]
    public void ItemCompleted_Fires_WhenFinishedItemNewlyAppears()
    {
        using var tracker = new ItemTracker(new FakeClock { NowMs = 42 });
        var alerts = new List<ItemAlert>();
        tracker.OnItemChanged(alerts.Add);
        tracker.Start();

        // Baseline: two Needlessly Large Rods (components). Then they combine into Rabadon's.
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 1058, 1058), "test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 3089), "test");

        var alert = Assert.Single(alerts);
        Assert.Equal(ItemAlertType.ItemCompleted, alert.Type);
        Assert.Equal("Rabadon's Deathcap completed", alert.Message);
        Assert.Equal(42, alert.Timestamp);
    }

    [Fact]
    public void ItemCompleted_DoesNotFire_ForRawComponent()
    {
        using var tracker = new ItemTracker();
        var alerts = new List<ItemAlert>();
        tracker.OnItemChanged(alerts.Add);
        tracker.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 1058), "test");       // baseline
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 1058, 1058), "test"); // +raw rod

        Assert.Empty(alerts);
    }

    [Fact]
    public void ItemCompleted_DoesNotFire_OnSlotReSort_ItemIdSetUnchanged()
    {
        using var tracker = new ItemTracker();
        var alerts = new List<ItemAlert>();
        tracker.OnItemChanged(alerts.Add);
        tracker.Start();

        // Same multiset (includes the completed Rabadon's), only slot order differs.
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 3089, 1058), "test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 1058, 3089), "test");

        Assert.Empty(alerts);
    }

    [Fact]
    public void FirstObservation_EstablishesBaseline_WithoutFiring()
    {
        using var tracker = new ItemTracker();
        var alerts = new List<ItemAlert>();
        tracker.OnItemChanged(alerts.Add);
        tracker.Start();

        // Even though a finished item is present, the first tick has no prior baseline to diff.
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 3089), "test");

        Assert.Empty(alerts);
    }

    [Fact]
    public void ItemCompleted_IsPublishedToHudBus_AsUiItemAlert()
    {
        using var tracker = new ItemTracker();
        string? hudPayload = null;
        using var delivered = new ManualResetEventSlim(false);
        // UI.* is always dispatched async by M15, so wait on the delivery signal.
        EventBus.EventBus.Subscribe("UI.ITEM_ALERT", e => { hudPayload = e.Payload as string; delivered.Set(); });
        tracker.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 1058, 1058), "test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 3089), "test");

        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)), "UI.ITEM_ALERT was not delivered");
        Assert.Equal("Rabadon's Deathcap completed", hudPayload);
    }

    // ── No enemy / cross-player inference (P1/P2) ────────────────────────────────

    [Fact]
    public void Diff_IsPerPlayer_NoCrossPlayerInference()
    {
        using var tracker = new ItemTracker();
        var alerts = new List<ItemAlert>();
        tracker.OnItemChanged(alerts.Add);
        tracker.Start();

        // Enemy 'Zed' shows components; the tracker must NOT combine those with the local
        // player's slots or infer a completed item across players.
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Zed", 1058), "test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 1058), "test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Zed", 1058, 1058), "test");

        Assert.Empty(alerts); // only per-payload slots are diffed; no hidden/enemy completion inferred
    }

    // ── Gold milestones routed through M09 (Acceptance #3) ───────────────────────

    [Fact]
    public void GoldMilestone_EmitsSpeechRequest_WithStableCooldownKey()
    {
        using var tracker = new ItemTracker(new FakeClock { NowMs = 7 });
        SpeechRequest? spoken = null;
        EventBus.EventBus.Subscribe("VOICE.SPEAK", e => spoken = e.Payload as SpeechRequest); // VOICE.* is sync
        tracker.SetTarget("Syndra", RabadonId);
        tracker.Start();

        EventBus.EventBus.Publish("GAME.GOLD_UPDATED", new GoldUpdatedPayload("Syndra", 3260), "test");

        Assert.NotNull(spoken);
        Assert.Equal("Need 240 gold for Rabadon's Deathcap", spoken!.Text);
        Assert.Equal($"gold-milestone:{RabadonId}", spoken.CooldownKey);
        Assert.Equal(SpeechPriority.Normal, spoken.Priority);
    }

    [Fact]
    public void GoldMilestone_OnlyReAnnounces_OnCloserBucket()
    {
        using var tracker = new ItemTracker();
        var texts = new List<string>();
        EventBus.EventBus.Subscribe("VOICE.SPEAK",
            e => texts.Add(((SpeechRequest)e.Payload!).Text));
        tracker.SetTarget("Syndra", RabadonId);
        tracker.Start();

        EventBus.EventBus.Publish("GAME.GOLD_UPDATED", new GoldUpdatedPayload("Syndra", 3260), "t"); // missing 240 → bucket 2
        EventBus.EventBus.Publish("GAME.GOLD_UPDATED", new GoldUpdatedPayload("Syndra", 3260), "t"); // same bucket → silent
        EventBus.EventBus.Publish("GAME.GOLD_UPDATED", new GoldUpdatedPayload("Syndra", 3450), "t"); // missing 50  → bucket 0

        Assert.Equal(new[] { "Need 240 gold for Rabadon's Deathcap", "Need 50 gold for Rabadon's Deathcap" }, texts);
    }

    [Fact]
    public void GoldMilestone_Silent_WhenNoTargetSet()
    {
        using var tracker = new ItemTracker();
        var fired = false;
        EventBus.EventBus.Subscribe("VOICE.SPEAK", _ => fired = true);
        tracker.Start();

        EventBus.EventBus.Publish("GAME.GOLD_UPDATED", new GoldUpdatedPayload("Syndra", 100), "t");

        Assert.False(fired);
    }

    // ── OnItemChanged / Unsubscribe ──────────────────────────────────────────────

    [Fact]
    public void Unsubscribe_StopsDeliveringToThatCallback()
    {
        using var tracker = new ItemTracker();
        var alerts = new List<ItemAlert>();
        var id = tracker.OnItemChanged(alerts.Add);
        tracker.Start();

        tracker.Unsubscribe(id);

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 1058, 1058), "test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("Syndra", 3089), "test");

        Assert.Empty(alerts);
    }
}
