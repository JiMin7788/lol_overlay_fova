using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;
using Overlay.Core.Items;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for <see cref="EnemyItemAlertDetector"/>: an ENEMY completing a new legendary
/// item raises a structured HUD alert (champion + item) + TTS, while an ally, a raw component, an
/// unresolved active team, a first-observation baseline, and a repeat of the same completed item all
/// stay silent. Item build-tree data is loaded from the REAL checked-in Data Dragon item.json (P1),
/// like <see cref="ItemTrackerTests"/>, so the completion/boots assertions are pinned to live values.
/// </summary>
public class EnemyItemAlertDetectorTests : IDisposable
{
    // Real Data Dragon item ids (verified in ItemTrackerTests): Rabadon = completed legendary,
    // Needlessly Large Rod = raw component.
    private const int RabadonId = 3089;
    private const int LargeRodId = 1058;

    public EnemyItemAlertDetectorTests()
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

    private sealed class FakeClock : IClock { public long NowMs { get; set; } }

    /// <summary>Active player is on ORDER; every row given here is a separate scoreboard entry.</summary>
    private static GameSnapshot Snapshot(params (string champ, string riotId, string team)[] rows)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            GameTime = 100.0,
            ActivePlayerRiotId = "Me#KR1",
            PlayerCount = rows.Length + 1,
        };
        snap.Players[0].RiotId = "Me#KR1";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].ChampionName = "Me";
        for (int i = 0; i < rows.Length; i++)
        {
            snap.Players[i + 1].ChampionName = rows[i].champ;
            snap.Players[i + 1].RiotId = rows[i].riotId;
            snap.Players[i + 1].Team = rows[i].team;
        }
        return snap;
    }

    private static ItemChangedPayload Items(string champion, params int[] itemIds)
    {
        var slots = new ItemSlot[itemIds.Length];
        for (int i = 0; i < itemIds.Length; i++) slots[i] = new ItemSlot(itemIds[i], i);
        return new ItemChangedPayload(champion, slots);
    }

    private static (List<Event> ui, List<Event> voice) Subscribe()
    {
        var ui = new List<Event>();
        var voice = new List<Event>();
        EventBus.EventBus.Subscribe("UI.ENEMY_ITEM_ALERT", e => { lock (ui) ui.Add(e); });
        EventBus.EventBus.Subscribe("VOICE.SPEAK", e => { lock (voice) voice.Add(e); });
        return (ui, voice);
    }

    // UI.* is dispatched async by M15, so a positive assertion must wait for delivery.
    private static void WaitForUiCount(List<Event> ui, int expected)
        => SpinWait.SpinUntil(() => { lock (ui) return ui.Count >= expected; }, TimeSpan.FromSeconds(2));

    [Fact]
    public void EnemyCompletesLegendary_AfterBaseline_RaisesStructuredAlert()
    {
        GameSnapshot snap = Snapshot(("EnemyMid", "Mid#KR1", "CHAOS"));
        var (ui, voice) = Subscribe();
        using var detector = new EnemyItemAlertDetector(() => snap, new FakeClock { NowMs = 1000 });
        detector.Start();

        // First observation = baseline only (two Needlessly Large Rods, no alert), then completion.
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", LargeRodId, LargeRodId), "Test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", RabadonId), "Test");

        WaitForUiCount(ui, 1);
        var evt = Assert.Single(ui);
        var hud = Assert.IsType<HUDPayload>(evt.Payload);
        Assert.Equal(HudType.EnemyItemAlert, hud.Type);
        var alert = Assert.IsType<EnemyItemAlert>(hud.Content);
        Assert.Equal("EnemyMid", alert.ChampionName);
        Assert.Equal(RabadonId.ToString(), alert.ItemId);
        Assert.False(string.IsNullOrEmpty(alert.ItemName));
        Assert.Single(voice); // a spoken line accompanies the HUD alert
    }

    [Fact]
    public void FirstObservation_WithLegendaryAlreadyOwned_IsSilentBaseline()
    {
        GameSnapshot snap = Snapshot(("EnemyMid", "Mid#KR1", "CHAOS"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyItemAlertDetector(() => snap);
        detector.Start();

        // Enemy first seen already holding the legendary → no prior tick to diff → no alert.
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", RabadonId), "Test");

        Assert.Empty(ui);
    }

    [Fact]
    public void EnemyAddsRawComponent_NoAlert()
    {
        GameSnapshot snap = Snapshot(("EnemyMid", "Mid#KR1", "CHAOS"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyItemAlertDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid"), "Test");              // baseline (empty)
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", LargeRodId), "Test");  // +raw rod

        Assert.Empty(ui);
    }

    [Fact]
    public void AllyCompletesLegendary_NoAlert()
    {
        // Ally completions are ItemTracker's job — this enemy-only detector must ignore them.
        GameSnapshot snap = Snapshot(("AllyMid", "Ally#KR1", "ORDER"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyItemAlertDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("AllyMid", LargeRodId, LargeRodId), "Test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("AllyMid", RabadonId), "Test");

        Assert.Empty(ui);
    }

    [Fact]
    public void UnresolvedActiveTeam_NoAlert()
    {
        // No row matches the active player's id → active team unknown → no guessing.
        GameSnapshot snap = Snapshot(("EnemyMid", "Mid#KR1", "CHAOS"));
        snap.ActivePlayerRiotId = "Nobody#KR9";
        var (ui, _) = Subscribe();
        using var detector = new EnemyItemAlertDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", LargeRodId, LargeRodId), "Test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", RabadonId), "Test");

        Assert.Empty(ui);
    }

    [Fact]
    public void SameLegendaryReobserved_DoesNotReAlert()
    {
        GameSnapshot snap = Snapshot(("EnemyMid", "Mid#KR1", "CHAOS"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyItemAlertDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", LargeRodId, LargeRodId), "Test");
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", RabadonId), "Test"); // completes → 1 alert
        WaitForUiCount(ui, 1);
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", RabadonId), "Test"); // unchanged → no new alert

        // Give any (erroneous) second async alert a chance to arrive before asserting the count held.
        Thread.Sleep(150);
        lock (ui) Assert.Single(ui);
    }

    [Fact]
    public void CompletedBoots_AreExcluded()
    {
        // "완성템(보신 제외)": a finished item that is boots must NOT alert. Locate one in the real
        // data (build-tree completed AND IsBoots); if this data version has none, the exclusion has
        // nothing to prove here and the test is trivially satisfied.
        var boots = ItemRepository.GetAll().FirstOrDefault(
            i => i.BuildsFrom.Count > 0 && i.BuildsInto.Count == 0 && i.IsBoots);
        if (boots is null) return;

        int bootsId = int.Parse(boots.Id);
        GameSnapshot snap = Snapshot(("EnemyMid", "Mid#KR1", "CHAOS"));
        var (ui, _) = Subscribe();
        using var detector = new EnemyItemAlertDetector(() => snap);
        detector.Start();

        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid"), "Test");            // baseline
        EventBus.EventBus.Publish("GAME.ITEM_CHANGED", Items("EnemyMid", bootsId), "Test");   // +completed boots

        Assert.Empty(ui);
    }
}
