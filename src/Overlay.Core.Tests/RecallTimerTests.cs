using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;
using Overlay.Core.Overlay;
using Overlay.Core.Recall;
using Overlay.Core.Tts;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for the testable heart of M08 Recall Timer
/// (docs/modules/M08_RECALL_TIMER.md) — <see cref="RecallTimer"/> + <see cref="MapConstants"/>.
/// Verifies the Death Timer uses the RAW API respawnTimer with no correction, that the return
/// ETA is pure arithmetic labelled an estimate, and the alert fan-out (callbacks + bus).
/// </summary>
public class RecallTimerTests : IDisposable
{
    public RecallTimerTests()
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.ResetForTests();
    }

    public void Dispose()
    {
        EventBus.EventBus.ResetForTests();
        ChampionRepository.ResetForTests();
    }

    private sealed class FakeClock : IClock
    {
        public long NowMs { get; set; }
    }

    private static void PublishDeath(string champion, double respawnTimer)
        => EventBus.EventBus.Publish(
            "GAME.CHAMPION_DIED",
            new ChampionDiedPayload(champion, KillerName: "Killer", Timestamp: 100.0, RespawnTimer: respawnTimer),
            "TestSource");

    // ── Death Timer: raw API value, no correction (Reviewer Checklist #2) ─────────

    [Fact]
    public void OnDeath_CarriesRawRespawnTimer_Unchanged()
    {
        DeathEvent? received = null;
        using var timer = new RecallTimer(new FakeClock { NowMs = 4242 });
        timer.Start();
        timer.OnDeath(d => received = d);

        PublishDeath("Zed", 27.5);

        Assert.NotNull(received);
        Assert.Equal("Zed", received!.ChampionName);
        Assert.Equal(27.5, received.RespawnTimer); // EXACT — no rounding/correction
        Assert.Equal(4242, received.DeathTimestamp);
    }

    [Fact]
    public void Unsubscribe_StopsCallbackDelivery()
    {
        int calls = 0;
        using var timer = new RecallTimer();
        timer.Start();
        var id = timer.OnDeath(_ => calls++);

        PublishDeath("Ashe", 10);
        timer.Unsubscribe(id);
        PublishDeath("Ashe", 10);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Death_PublishesExactCountdown_ToUiRecallTimer_NotAnEstimate()
    {
        string? hud = null;
        using var delivered = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.RECALL_TIMER", e => { hud = e.Payload as string; delivered.Set(); });

        using var timer = new RecallTimer();
        timer.Start();
        PublishDeath("Zed", 27.5);

        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)), "UI.RECALL_TIMER was not delivered");
        Assert.Contains("27.5", hud);
        Assert.DoesNotContain("estimate", hud); // Death Timer is exact, never labelled an estimate
        Assert.DoesNotContain("~", hud);
    }

    [Fact]
    public void Death_PublishesSpeechRequest_ToVoiceSpeak()
    {
        SpeechRequest? spoken = null;
        EventBus.EventBus.Subscribe("VOICE.SPEAK", e => spoken = e.Payload as SpeechRequest); // VOICE.* is sync

        using var timer = new RecallTimer();
        timer.Start();
        PublishDeath("Zed", 27.5);

        Assert.NotNull(spoken);
        Assert.Contains("Zed", spoken!.Text);
        Assert.Equal("recall-death:Zed", spoken.CooldownKey);
        Assert.Equal(SpeechPriority.Normal, spoken.Priority);
    }

    // ── ETA: pure arithmetic, guarded, labelled an estimate ──────────────────────

    [Fact]
    public void GetETA_TravelTime_IsDistanceOverMoveSpeed()
    {
        using var timer = new RecallTimer();
        var eta = timer.GetETA("Zed", averageMoveSpeed: 400, distanceToLane: 2000);

        Assert.Equal(5.0, eta.EstimatedArrivalSeconds); // 2000 / 400
        Assert.Equal(EtaBasis.RespawnTimerOnly, eta.Basis); // no respawn folded in
    }

    [Fact]
    public void GetETA_FoldsRemainingRespawn_SetsRespawnPlusTravel()
    {
        using var timer = new RecallTimer();
        var eta = timer.GetETA("Zed", averageMoveSpeed: 400, distanceToLane: 2000, remainingRespawnSeconds: 10);

        Assert.Equal(15.0, eta.EstimatedArrivalSeconds); // 10 + 2000/400
        Assert.Equal(EtaBasis.RespawnPlusTravel, eta.Basis);
    }

    [Fact]
    public void GetETA_GuardsNonPositiveMoveSpeed_WithNoFallback_Throws()
    {
        using var timer = new RecallTimer();
        // No champion loaded in M11 → no base-MS fallback available.
        Assert.Throws<ArgumentException>(() => timer.GetETA("Zed", averageMoveSpeed: 0, distanceToLane: 2000));
    }

    [Fact]
    public void GetETA_FallsBackToM11BaseMoveSpeed_WhenAverageNotSupplied()
    {
        ChampionRepository.Initialize(new[]
        {
            new ChampionData { Id = "Zed", Name = "Zed", BaseStats = new ChampionBaseStats { Ms = 400 } },
        });

        using var timer = new RecallTimer();
        var eta = timer.GetETA("Zed", averageMoveSpeed: 0, distanceToLane: 2000);

        Assert.Equal(5.0, eta.EstimatedArrivalSeconds); // 2000 / base MS 400
    }

    [Fact]
    public void ReturnETA_Message_IsLabelledAnEstimate()
    {
        using var timer = new RecallTimer();
        var eta = timer.GetETA("Zed", averageMoveSpeed: 400, distanceToLane: 2000);

        Assert.StartsWith("~", eta.Message);
        Assert.Contains("estimate", eta.Message);
    }

    // ── Map-constants loader (Agent Implementation Notes) ────────────────────────

    [Fact]
    public void MapConstants_LoadFromJson_ReturnsLanePresets()
    {
        const string json = """
        { "schemaVersion": 1, "laneDistances": { "mid": 4700, "top": 12500 } }
        """;
        var map = MapConstantsLoader.LoadFromJson(json);

        Assert.Equal(4700, map.GetLaneDistance("mid"));
        Assert.Equal(12500, map.GetLaneDistance("TOP")); // case-insensitive
        Assert.Null(map.GetLaneDistance("nexus")); // unknown lane
    }

    [Fact]
    public void MapConstants_Load_FromBundledFile_HasLanePresets()
    {
        var map = MapConstantsLoader.Load();
        Assert.NotNull(map.GetLaneDistance("mid"));
        Assert.True(map.GetLaneDistance("mid") > 0);
    }
}
