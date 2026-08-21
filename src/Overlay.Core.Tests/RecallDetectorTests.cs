namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for <see cref="RecallDetector"/> (DA-002) — the item-purchase-as-
/// fountain-proxy recall inference. Verifies the three gates (item-added, death != recall,
/// visible-activity confidence penalty), the confidence floor, the ETA formula
/// (channel + distance/moveSpeed), the active-player-vs-default move-speed branch, and the
/// HasData/IsInitialSync fast-exits.
/// </summary>
public class RecallDetectorTests
{
    private const string ConfigJson = """
    {
      "recallChannelSeconds": 8,
      "defaultMoveSpeed": 335,
      "lanes": { "ORDER": { "default": 5000 }, "CHAOS": { "default": 5000 } },
      "detection": {
        "treatItemChangeWhileFarmingAsVisibleShop": true,
        "minConfidence": 0.3,
        "visibleActivityConfidencePenalty": 0.4
      }
    }
    """;

    private static RecallDetector NewDetector(string? json = null)
        => new(RecallConfigLoader.LoadFromJson(json ?? ConfigJson));

    private static GameSnapshot Snapshot(string activeSummoner = "Me", double moveSpeed = 0, params (string name, string team)[] players)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = activeSummoner,
            Stats = new ActivePlayerStats { MoveSpeed = moveSpeed },
            PlayerCount = players.Length,
        };
        for (int i = 0; i < players.Length; i++)
        {
            snap.Players[i].SummonerName = players[i].name;
            snap.Players[i].Team = players[i].team;
        }
        return snap;
    }

    private static SnapshotDiff Diff(double gameTime, params PlayerChange[] changes) => new()
    {
        GameTime = gameTime,
        IsInitialSync = false,
        PlayerChanges = changes,
    };

    // ── Recall-started: item purchase, no counter-signal ───────────────────────

    [Fact]
    public void Detect_ItemAddedNoActivityNoDeath_EmitsFullConfidenceRecallEvent()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(players: ("Bot", "CHAOS"));
        var diff = Diff(100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 1 });

        var events = detector.Detect(diff, snapshot);

        var ev = Assert.Single(events);
        Assert.Equal("Bot", ev.SummonerName);
        Assert.Equal(1.0, ev.Confidence);
        Assert.False(ev.HasVisibleActivity);
    }

    // ── Gate: no item change -> no event (also covers "recall cancelled"/undo,
    //    which the diff shape reports as ItemsAdded == 0) ────────────────────────

    [Fact]
    public void Detect_NoItemsAdded_EmitsNoEvent()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(players: ("Bot", "CHAOS"));
        var diff = Diff(100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 0, CreepScoreDelta = 3 });

        Assert.Empty(detector.Detect(diff, snapshot));
    }

    // ── Gate: death != recall ───────────────────────────────────────────────────

    [Fact]
    public void Detect_PlayerIsDead_EmitsNoEvent_EvenWithItemsAdded()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(players: ("Bot", "CHAOS"));
        var diff = Diff(100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 1, IsDead = true });

        Assert.Empty(detector.Detect(diff, snapshot));
    }

    [Fact]
    public void Detect_DeathStateChangedThisTick_EmitsNoEvent()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(players: ("Bot", "CHAOS"));
        var diff = Diff(100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 1, DeathStateChanged = true });

        Assert.Empty(detector.Detect(diff, snapshot));
    }

    // ── Visible-activity confidence penalty ─────────────────────────────────────

    [Fact]
    public void Detect_ItemAddedWithVisibleActivity_LowersConfidence_ButStillEmitsAboveFloor()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(players: ("Bot", "CHAOS"));
        var diff = Diff(100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 1, CreepScoreDelta = 2 });

        var ev = Assert.Single(detector.Detect(diff, snapshot));

        Assert.Equal(0.4, ev.Confidence); // visibleActivityConfidencePenalty
        Assert.True(ev.HasVisibleActivity);
    }

    [Fact]
    public void Detect_ConfidenceBelowMinConfidence_IsSuppressed()
    {
        // minConfidence raised above the visible-activity penalty (0.4) -> dropped.
        const string json = """
        {
          "recallChannelSeconds": 8, "defaultMoveSpeed": 335,
          "lanes": { "ORDER": { "default": 5000 }, "CHAOS": { "default": 5000 } },
          "detection": { "treatItemChangeWhileFarmingAsVisibleShop": true, "minConfidence": 0.5, "visibleActivityConfidencePenalty": 0.4 }
        }
        """;
        var detector = NewDetector(json);
        var snapshot = Snapshot(players: ("Bot", "CHAOS"));
        var diff = Diff(100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 1, KillsDelta = 1 });

        Assert.Empty(detector.Detect(diff, snapshot));
    }

    // ── ETA formula: channel + distance / moveSpeed ─────────────────────────────

    [Fact]
    public void Detect_EstimatedReturn_UsesChannelPlusDistanceOverDefaultMoveSpeed_ForNonActivePlayer()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(activeSummoner: "Me", moveSpeed: 400, players: ("Bot", "CHAOS"));
        var diff = Diff(gameTime: 100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 1 });

        var ev = Assert.Single(detector.Detect(diff, snapshot));

        // travel = 5000 / 335 (defaultMoveSpeed, since "Bot" isn't the active player)
        double expectedTravel = 5000.0 / 335.0;
        Assert.Equal(100.0 + 8.0 + expectedTravel, ev.EstimatedReturnSeconds, precision: 4);
        Assert.True(ev.UsedDefaultMoveSpeed);
        Assert.Equal("CHAOS", ev.Team);
    }

    [Fact]
    public void Detect_ActivePlayerRecall_UsesLiveMoveSpeed_NotDefault()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(activeSummoner: "Me", moveSpeed: 500, players: ("Me", "ORDER"));
        var diff = Diff(gameTime: 200.0, new PlayerChange { SummonerName = "Me", ItemsAdded = 1 });

        var ev = Assert.Single(detector.Detect(diff, snapshot));

        double expectedTravel = 5000.0 / 500.0; // live MoveSpeed, not defaultMoveSpeed(335)
        Assert.Equal(200.0 + 8.0 + expectedTravel, ev.EstimatedReturnSeconds, precision: 4);
        Assert.False(ev.UsedDefaultMoveSpeed);
    }

    [Fact]
    public void Detect_PlayerNotFoundInScoreboard_TeamIsNull_ButEventStillEmitted()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(players: ("SomeoneElse", "CHAOS")); // "Bot" absent
        var diff = Diff(100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 1 });

        var ev = Assert.Single(detector.Detect(diff, snapshot));

        Assert.Null(ev.Team); // falls back to the 6000-unit neutral distance internally
    }

    // ── Fast-exits ───────────────────────────────────────────────────────────────

    [Fact]
    public void Detect_SnapshotHasNoData_ReturnsEmpty()
    {
        var detector = NewDetector();
        var snapshot = new GameSnapshot { HasData = false };
        var diff = Diff(100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 1 });

        Assert.Empty(detector.Detect(diff, snapshot));
    }

    [Fact]
    public void Detect_InitialSync_ReturnsEmpty_TreatedAsBaselineNotChangeEvent()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(players: ("Bot", "CHAOS"));
        var diff = new SnapshotDiff
        {
            GameTime = 100.0,
            IsInitialSync = true,
            PlayerChanges = new[] { new PlayerChange { SummonerName = "Bot", ItemsAdded = 1 } },
        };

        Assert.Empty(detector.Detect(diff, snapshot));
    }

    [Fact]
    public void Detect_NullDiffOrSnapshot_Throws()
    {
        var detector = NewDetector();
        var snapshot = Snapshot(players: ("Bot", "CHAOS"));
        var diff = Diff(100.0, new PlayerChange { SummonerName = "Bot", ItemsAdded = 1 });

        Assert.Throws<ArgumentNullException>(() => detector.Detect(null!, snapshot));
        Assert.Throws<ArgumentNullException>(() => detector.Detect(diff, null!));
    }
}
