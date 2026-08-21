using Overlay.Core.EventBus;
using Overlay.Core.Jungle;
using Overlay.Core.Overlay;
using Overlay.Core.Tts;
using Overlay.Core.Vision;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M31 P3 (docs/modules/M31_MINIMAP_VISION.md §4/§7/§9) —
/// <see cref="JunglePresenceTracker"/>. Per spec §7's P3 test scope ("pure unit tests: sighting
/// sequences -&gt; alert sequence, debounce, death suppression, cooldowns"), every test drives the
/// tracker with synthetic <see cref="MinimapSighting"/>s and explicit <see cref="JunglePresenceTracker.Tick"/>
/// timestamps against a <see cref="FakeClock"/> — no real waiting on
/// <see cref="JunglePresenceTracker.DefaultLostDebounceMs"/> or
/// <see cref="JunglePresenceTracker.AlertCooldownMs"/>.
///
/// <b>Clock discipline:</b> every test that calls <see cref="JunglePresenceTracker.Tick"/> with an
/// explicit timestamp constructs its own zero-based <see cref="FakeClock"/> and passes it to the
/// tracker, so <see cref="JunglePresenceTracker.OnSighting"/>'s internal "now" (read from the
/// clock at call time) and the later <c>Tick(nowMs: ...)</c> values share one timeline. Mixing a
/// real <see cref="SystemClock"/>-driven sighting with a small synthetic <c>Tick</c> timestamp
/// would make every "elapsed" comparison a huge negative number and silently never fire.
///
/// Zone text assertions use real, known coordinates against the shipped
/// <c>data/map_regions.json</c> (via <see cref="MapZoneLookup.Default"/>) rather than a stub
/// lookup, since (0.05,0.05) → "탑" and (0.95,0.95) → "봇" are stable for this grid (see
/// <c>tools/validate_map_regions.py</c>'s classify() rule: y&lt;0.20 → top_lane, y&gt;0.80 → bot_lane).
/// </summary>
public class JunglePresenceTrackerTests : IDisposable
{
    public JunglePresenceTrackerTests() => EventBus.EventBus.ResetForTests();
    public void Dispose() => EventBus.EventBus.ResetForTests();

    private const double TopZoneX = 0.05, TopZoneY = 0.05;   // -> "탑"
    private const double BotZoneX = 0.95, BotZoneY = 0.95;   // -> "봇"

    private sealed class FakeClock : IClock
    {
        public long NowMs { get; set; }
    }

    private static MinimapSighting Sighting(string championId, double x01 = TopZoneX, double y01 = TopZoneY, double confidence = 0.9)
        => new(championId, new MapPosition01(x01, y01), confidence, 0);

    /// <summary>Feeds a sighting enough times for its POSITION to be confirmed.
    ///
    /// <para>(2026-07-20) A disappearance is only reported from a position corroborated by
    /// <see cref="JunglePresenceTracker.MarkerConfirmFrames"/> consecutive nearby sightings, so a
    /// single call no longer stands for "the champion was there". Repeats are harmless to the APPEAR
    /// path: only the first transition to visible raises that alert.</para></summary>
    private static void See(JunglePresenceTracker tracker, MinimapSighting sighting)
    {
        for (int i = 0; i < JunglePresenceTracker.MarkerConfirmFrames; i++)
            tracker.OnSighting(sighting, flipped: false);
    }

    private static GameSnapshot Snapshot(params (string championName, string riotId, string team, string position, string spell1, string spell2)[] enemyAndAllies)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerRiotId = "Me#KR1",
            PlayerCount = enemyAndAllies.Length + 1,
            // Mid-game: the 2026-07-25 opening quiet period (OpeningQuietGameSeconds) suppresses
            // all presence alerts while GameTime < 60 — these tests exercise normal play.
            GameTime = 900,
        };
        snap.Players[0].RiotId = "Me#KR1";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].ChampionName = "Me";
        for (int i = 0; i < enemyAndAllies.Length; i++)
        {
            var e = enemyAndAllies[i];
            snap.Players[i + 1].ChampionName = e.championName;
            snap.Players[i + 1].RiotId = e.riotId;
            snap.Players[i + 1].Team = e.team;
            snap.Players[i + 1].Position = e.position;
            snap.Players[i + 1].Spell1RawName = e.spell1;
            snap.Players[i + 1].Spell2RawName = e.spell2;
        }
        return snap;
    }

    /// <summary>Convenience: a snapshot with one enemy identified as jungler purely by
    /// <c>position</c> (no Smite needed).</summary>
    private static GameSnapshot SnapshotWithJungler(string championName)
        => Snapshot((championName, "Jgl#KR1", "CHAOS", "JUNGLE", "", ""));

    private static (List<Event> ui, List<Event> voice) Subscribe()
    {
        var ui = new List<Event>();
        var voice = new List<Event>();
        EventBus.EventBus.Subscribe("UI.NOTIFICATION", e => { lock (ui) ui.Add(e); });
        EventBus.EventBus.Subscribe("VOICE.SPEAK", e => { lock (voice) voice.Add(e); });
        return (ui, voice);
    }

    /// <summary>Captures the §0/§A structured alerts published on <c>UI.ENEMY_PRESENCE</c>.</summary>
    private static List<Event> SubscribePresence()
    {
        var presence = new List<Event>();
        EventBus.EventBus.Subscribe("UI.ENEMY_PRESENCE", e => { lock (presence) presence.Add(e); });
        return presence;
    }

    /// <summary>UI.* is always dispatched async by M15 (EventBus.cs) — wait instead of racing it.</summary>
    private static void WaitForUiCount(List<Event> ui, int expectedCount)
        => SpinWait.SpinUntil(() => { lock (ui) return ui.Count >= expectedCount; }, TimeSpan.FromSeconds(2));

    /// <summary>The display text of a presence toast. The tracker now publishes it as a HUDPayload
    /// whose Content is an <see cref="EnemyPresenceHud"/> (rich portrait card), so unwrap that; falls
    /// back to a bare string for any other UI.NOTIFICATION payload.</summary>
    private static string PayloadText(Event e) => e.Payload switch
    {
        HUDPayload { Content: EnemyPresenceHud hud } => hud.Message,
        string s => s,
        _ => e.Payload?.ToString() ?? string.Empty,
    };

    // ── APPEAR (jungler-only, §9 decision 1) ───────────────────────────────────────

    [Fact]
    public void Sighting_OnTheJungler_RaisesAppearAlert_WithZoneText()
    {
        var snap = SnapshotWithJungler("Kayn");
        var (ui, voice) = Subscribe();
        var tracker = new JunglePresenceTracker(() => snap, clock: new FakeClock { NowMs = 1000 });
        tracker.Start();

        See(tracker, Sighting("Kayn", TopZoneX, TopZoneY));

        WaitForUiCount(ui, 1);
        var uiAlert = Assert.Single(ui);
        Assert.Equal("적 정글 발견 · 탑", PayloadText(uiAlert));
        var speech = (SpeechRequest)Assert.Single(voice).Payload!;
        Assert.Equal("적 정글 발견 · 탑", speech.Text);
    }

    [Fact]
    public void Sighting_OnNonJunglerEnemy_NoAppearAlert()
    {
        var snap = Snapshot(
            ("Kayn", "Jgl#KR1", "CHAOS", "JUNGLE", "", ""),
            ("Garen", "Top#KR1", "CHAOS", "TOP", "", ""));
        var (ui, _) = Subscribe();
        var tracker = new JunglePresenceTracker(() => snap);
        tracker.Start();

        See(tracker, Sighting("Garen"));

        Assert.Empty(ui);
    }

    [Fact]
    public void Sighting_NoJunglerIdentifiable_NoAppearAlert()
    {
        // position empty (practice tool/ARAM) and no Smite on anyone -> jungler unresolved (P2: no guess).
        var snap = Snapshot(("Kayn", "Jgl#KR1", "CHAOS", "", "", ""));
        var (ui, _) = Subscribe();
        var tracker = new JunglePresenceTracker(() => snap);
        tracker.Start();

        See(tracker, Sighting("Kayn"));

        Assert.Empty(ui);
    }

    [Fact]
    public void Sighting_JunglerIdentifiedBySmite_RaisesAppearAlert()
    {
        // position empty everywhere (normal/ARAM), but the enemy row carries Smite.
        var snap = Snapshot(("Warwick", "Jgl#KR1", "CHAOS", "", "SummonerSmite", "SummonerFlash"));
        var (ui, _) = Subscribe();
        var tracker = new JunglePresenceTracker(() => snap);
        tracker.Start();

        See(tracker, Sighting("Warwick"));

        WaitForUiCount(ui, 1);
        Assert.Single(ui);
    }

    [Fact]
    public void AppearAlert_WithinCooldown_Suppressed_ThenRaisesAgainAfterCooldown()
    {
        var snap = SnapshotWithJungler("Kayn");
        var (ui, _) = Subscribe();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Kayn")); // t=0: first appear
        WaitForUiCount(ui, 1);

        // Disappear toasts now land on the same ui list (grouping was removed and every channel
        // shares one decision), so count APPEARS specifically — this test is about that cooldown.
        int Appears()
        {
            lock (ui)
                return ui.Count(e => (e.Payload as HUDPayload)?.Id?.Contains("Appear") == true);
        }

        // Champion goes LOST, then is re-sighted within the 8s appear cooldown.
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 10); // t=1010
        clock.NowMs = (long)(JunglePresenceTracker.DefaultLostDebounceMs + 20); // t=1020
        See(tracker, Sighting("Kayn"));

        Assert.Equal(1, Appears()); // still within AlertCooldownMs of the first appear (t=0) -> suppressed

        // Advance well past the cooldown, lose it again, and re-sight -> should alert again.
        tracker.Tick(nowMs: JunglePresenceTracker.AlertCooldownMs + JunglePresenceTracker.DefaultLostDebounceMs + 50);
        clock.NowMs = (long)(JunglePresenceTracker.AlertCooldownMs + JunglePresenceTracker.DefaultLostDebounceMs + 100);
        See(tracker, Sighting("Kayn"));

        SpinWait.SpinUntil(() => Appears() >= 2, TimeSpan.FromSeconds(2));
        Assert.Equal(2, Appears());
    }

    // ── DISAPPEAR (all five enemies, §9 decision 1) ────────────────────────────────

    [Fact]
    public void NoSightingForTLost_TransitionsToLost_RaisesDisappearAlert()
    {
        // Non-jungler enemy: DISAPPEAR still applies (disappear is not jungler-gated).
        var snap = Snapshot(("Garen", "Top#KR1", "CHAOS", "TOP", "", ""));
        var (ui, _) = Subscribe();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Garen", BotZoneX, BotZoneY)); // t=0

        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs - 1); // t=999: not yet past T_lost
        Assert.Empty(ui);

        // The Tick that crosses T_lost announces immediately — there is no longer a grouping queue
        // to flush (Tick calls RaiseDisappear inline, GroupCount is always 1). The second Tick is a
        // harmless no-op kept only because the state is already Lost; asserting "still empty"
        // between the two would just be racing UI.*'s async dispatch.
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 50); // t=1050: crosses T_lost -> announces

        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 350); // t=1350: nothing left to do
        WaitForUiCount(ui, 1);
        var uiAlert = Assert.Single(ui);
        // §A bugfix: label is the disappearing champion's ROLE, not the hardcoded "적 정글".
        // Garen is TOP here, so "적 탑 사라짐" (was wrongly "적 정글 사라짐" before the fix).
        Assert.Equal("적 탑 사라짐 · 봇", PayloadText(uiAlert));
    }

    [Fact]
    public void NewSightingBeforeTLost_RefreshesDebounce_NeverGoesLost()
    {
        var snap = Snapshot(("Garen", "Top#KR1", "CHAOS", "TOP", "", ""));
        var (ui, _) = Subscribe();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Garen")); // t=0
        tracker.Tick(nowMs: 500); // well inside the 1000ms debounce -> still Visible

        clock.NowMs = 500;
        See(tracker, Sighting("Garen")); // refreshes LastSightingMs to 500
        tracker.Tick(nowMs: 900); // 900 - 500 = 400 < 1000 -> still Visible
        tracker.Tick(nowMs: 1400); // 1400 - 500 = 900 < 1000 -> still Visible (never re-crossed)

        Assert.Empty(ui); // never went LOST -> no disappear
    }

    /// <summary>
    /// (2026-07-20, user request "이벤트 묶어") Grouping is GONE. It collapsed a simultaneous
    /// multi-champion drop into one toast that carried no champion id, which the afterimage could not
    /// follow — the toast said "3 enemies" while at most one marker appeared. Each champion now
    /// reports itself, so every channel describes the same thing.
    /// </summary>
    [Fact]
    public void SimultaneousLost_AcrossMultipleChampions_EmitsOneAlertPerChampion()
    {
        var snap = SnapshotWithJungler("Kayn");
        var (ui, _) = Subscribe();
        var clock = new FakeClock { NowMs = 1000 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Kayn"));
        See(tracker, Sighting("Garen"));
        See(tracker, Sighting("Warwick"));

        clock.NowMs += (long)JunglePresenceTracker.DefaultLostDebounceMs + 500;
        tracker.Tick(clock.NowMs);

        WaitForUiCount(ui, 4);   // 1 appear (jungler) + 3 disappears
        List<Event> disappears;
        lock (ui)
            disappears = ui.Where(e => (e.Payload as HUDPayload)?.Id?.Contains("Disappear") == true).ToList();
        Assert.Equal(3, disappears.Count);
    }


    [Fact]
    public void DisappearAlert_WithinCooldown_Suppressed()
    {
        var snap = Snapshot(("Garen", "Top#KR1", "CHAOS", "TOP", "", ""));
        var (ui, _) = Subscribe();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Garen")); // t=0
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 50);  // t=1050: crosses T_lost, queues (disappear cooldown now anchored at t=1050)
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 350); // t=1350: window elapsed -> 1st disappear
        WaitForUiCount(ui, 1);

        // Re-seen, then lost again — still inside the 8s disappear cooldown anchored at t=1050.
        clock.NowMs = 1350;
        See(tracker, Sighting("Garen"));
        tracker.Tick(nowMs: 1350 + JunglePresenceTracker.DefaultLostDebounceMs + 50);  // t=2400: crosses T_lost again, but 2400-1050=1350 < 8000 -> suppressed, nothing queued
        tracker.Tick(nowMs: 1350 + JunglePresenceTracker.DefaultLostDebounceMs + 350); // one more Tick — still nothing pending to flush

        Assert.Single(ui); // second LOST suppressed by the disappear cooldown
    }

    // ── DISAPPEAR role labelling + UI.ENEMY_PRESENCE (§A bugfix + §0 contract) ─────

    /// <summary>Drives one non-jungler champion to LOST at (<paramref name="x01"/>,<paramref name="y01"/>)
    /// and returns the disappear toast text + the structured presence alert. Non-jungler snapshots
    /// raise no APPEAR, so both event lists hold exactly one item.</summary>
    private static (string uiText, EnemyPresenceAlert alert) RunSingleDisappear(
        GameSnapshot snap, string championId, double x01, double y01)
    {
        var (ui, _) = Subscribe();
        var presence = SubscribePresence();
        var tracker = new JunglePresenceTracker(() => snap, clock: new FakeClock { NowMs = 0 });
        tracker.Start();

        See(tracker, Sighting(championId, x01, y01)); // t=0
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 50);  // queues
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 350); // flushes

        WaitForUiCount(ui, 1);
        WaitForUiCount(presence, 1);
        return (PayloadText(Assert.Single(ui)), (EnemyPresenceAlert)Assert.Single(presence).Payload!);
    }

    [Theory]
    [InlineData("TOP", "탑", "top")]
    [InlineData("MIDDLE", "미드", "mid")]
    [InlineData("BOTTOM", "원딜", "adc")]
    [InlineData("UTILITY", "서폿", "support")]
    public void DisappearAlert_UsesResolvedRoleLabel_AndPublishesPresence(string position, string label, string roleKey)
    {
        // Regression for the §A bug: previously EVERY disappear said "적 정글 사라짐" regardless of role.
        var snap = Snapshot(("Garen", "E#KR1", "CHAOS", position, "", ""));
        var (uiText, alert) = RunSingleDisappear(snap, "Garen", BotZoneX, BotZoneY);

        Assert.Equal($"적 {label} 사라짐 · 봇", uiText);
        Assert.Equal(EnemyAlertKind.Disappear, alert.Kind);
        Assert.Equal("Garen", alert.ChampionId);
        Assert.Equal(roleKey, alert.RoleKey);
        Assert.Equal("bot_lane", alert.ZoneKey);
        Assert.Equal("봇", alert.ZoneLabel);
        Assert.Equal(1, alert.GroupCount);
    }

    [Fact]
    public void DisappearAlert_PositionEmpty_JunglePetFallback_ResolvesJungle()
    {
        // position blank (practice tool/ARAM) but the row carries a jungle pet -> role-item fallback.
        var snap = Snapshot(("Kayn", "E#KR1", "CHAOS", "", "", ""));
        snap.Players[1].ItemIds[0] = 1101; // Gustwalker (jungle pet)
        snap.Players[1].ItemCount = 1;

        var (uiText, alert) = RunSingleDisappear(snap, "Kayn", BotZoneX, BotZoneY);

        Assert.Equal("적 정글 사라짐 · 봇", uiText); // "적 정글" here is CORRECT — the champ really is jungle
        Assert.Equal("jungle", alert.RoleKey);
    }

    [Fact]
    public void DisappearAlert_PositionEmpty_SupportItemFallback_ResolvesSupport()
    {
        var snap = Snapshot(("Lulu", "E#KR1", "CHAOS", "", "", ""));
        snap.Players[1].ItemIds[0] = 3865; // World Atlas (support line)
        snap.Players[1].ItemCount = 1;

        var (uiText, alert) = RunSingleDisappear(snap, "Lulu", BotZoneX, BotZoneY);

        Assert.Equal("적 서폿 사라짐 · 봇", uiText);
        Assert.Equal("support", alert.RoleKey);
        Assert.True(alert.X01 > 0.5 && alert.Y01 > 0.5, $"afterimage coords should be bot-side, got {alert.X01},{alert.Y01}");
    }

    [Fact]
    public void DisappearAlert_UnknownRole_UsesChampionNameLabel_AndEmptyRoleKey()
    {
        // position blank AND no role item -> role unresolved (P2: no guess). Label = champion name.
        var snap = Snapshot(("Garen", "E#KR1", "CHAOS", "", "", ""));
        var (uiText, alert) = RunSingleDisappear(snap, "Garen", BotZoneX, BotZoneY);

        Assert.Equal("적 Garen 사라짐 · 봇", uiText);
        Assert.Equal("", alert.RoleKey);
        Assert.Equal("Garen", alert.ChampionId);
    }

    [Fact]
    public void AppearAlert_PublishesJunglePresence()
    {
        var snap = SnapshotWithJungler("Kayn");
        var (ui, _) = Subscribe();
        var presence = SubscribePresence();
        var tracker = new JunglePresenceTracker(() => snap, clock: new FakeClock { NowMs = 1000 });
        tracker.Start();

        See(tracker, Sighting("Kayn", TopZoneX, TopZoneY));

        WaitForUiCount(presence, 1);
        var alert = (EnemyPresenceAlert)Assert.Single(presence).Payload!;
        Assert.Equal(EnemyAlertKind.Appear, alert.Kind);
        Assert.Equal("Kayn", alert.ChampionId);
        Assert.Equal("jungle", alert.RoleKey);
        Assert.Equal("top_lane", alert.ZoneKey);
        Assert.Equal("탑", alert.ZoneLabel);
    }

    /// <summary>Every disappearance now carries its own champion id and GroupCount 1 — nothing
    /// emits the id-less group payload any more.</summary>
    [Fact]
    public void EveryDisappear_CarriesItsOwnChampionId()
    {
        var snap = SnapshotWithJungler("Kayn");
        var presence = new List<EnemyPresenceAlert>();
        var sub = EventBus.EventBus.Subscribe("UI.ENEMY_PRESENCE",
            e => { if (e.Payload is EnemyPresenceAlert a) lock (presence) presence.Add(a); });
        try
        {
            var clock = new FakeClock { NowMs = 1000 };
            var tracker = new JunglePresenceTracker(() => snap, clock: clock);
            tracker.Start();

            See(tracker, Sighting("Kayn"));
            See(tracker, Sighting("Garen"));

            clock.NowMs += (long)JunglePresenceTracker.DefaultLostDebounceMs + 500;
            tracker.Tick(clock.NowMs);
            SpinWait.SpinUntil(
                () => { lock (presence) return presence.Count(a => a.Kind == EnemyAlertKind.Disappear) >= 2; },
                TimeSpan.FromSeconds(2));

            lock (presence)
            {
                var gone = presence.Where(a => a.Kind == EnemyAlertKind.Disappear).ToList();
                Assert.Equal(2, gone.Count);
                Assert.All(gone, a => Assert.False(string.IsNullOrEmpty(a.ChampionId)));
                Assert.All(gone, a => Assert.Equal(1, a.GroupCount));
                Assert.DoesNotContain(presence, a => a.Kind == EnemyAlertKind.GroupDisappear);
            }
        }
        finally { EventBus.EventBus.Unsubscribe(sub); }
    }


    // ── Death/respawn cross-check (§4: "dying is not disappearing") ────────────────

    [Fact]
    public void ChampionDied_WhileVisible_SuppressesTheDisappearAlert()
    {
        var snap = Snapshot(("Garen", "Top#KR1", "CHAOS", "TOP", "", ""));
        var (ui, _) = Subscribe();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Garen")); // t=0, Visible
        EventBus.EventBus.Publish("GAME.CHAMPION_DIED",
            new ChampionDiedPayload("Garen", "Me", 100, 30), "Test"); // -> Unseen, no timer pending

        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 300);

        Assert.Empty(ui); // dying, not "disappearing" -> no alert
    }

    [Fact]
    public void ChampionRespawned_ResetsToUnseen_AndCanAppearAgainAfterCooldown()
    {
        var snap = SnapshotWithJungler("Kayn");
        var (ui, _) = Subscribe();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Kayn")); // appear #1 (t=0)
        WaitForUiCount(ui, 1);

        EventBus.EventBus.Publish("GAME.CHAMPION_DIED", new ChampionDiedPayload("Kayn", "Me", 100, 30), "Test");
        EventBus.EventBus.Publish("GAME.CHAMPION_RESPAWNED", new ChampionRespawnedPayload("Kayn"), "Test");

        // Still within the appear cooldown right after respawn -> a re-sighting here would be
        // suppressed regardless of the Unseen reset; advance past AlertCooldownMs first.
        clock.NowMs = (long)JunglePresenceTracker.AlertCooldownMs + 100;
        See(tracker, Sighting("Kayn")); // appear #2

        WaitForUiCount(ui, 2);
        Assert.Equal(2, ui.Count);
    }

    // ── Coordinate transform (flip) ─────────────────────────────────────────────────

    [Fact]
    public void FlippedSighting_MirrorsIntoCanonicalMapSpace_ForZoneNaming()
    {
        // Raw ROI position near (0.95, 0.95) with flipped=true should mirror to (0.05, 0.05) ->
        // "탑" instead of "봇".
        var snap = SnapshotWithJungler("Kayn");
        var (ui, _) = Subscribe();
        var tracker = new JunglePresenceTracker(() => snap);
        tracker.Start();

        tracker.OnSighting(Sighting("Kayn", BotZoneX, BotZoneY), flipped: true);

        WaitForUiCount(ui, 1);
        Assert.Equal("적 정글 발견 · 탑", PayloadText(Assert.Single(ui)));
    }

    [Fact]
    public void GameEnd_ResetsSilently_NoDisappearAlertsForTheWholeRoster()
    {
        // 2026-07-26 user report: when the game ends, sightings stop all at once and the lost
        // debounce turned every visible enemy into a "disappeared" callout + ghost marker.
        // GAME.DISCONNECTED must forget everyone without emitting anything.
        var snap = SnapshotWithJungler("Kayn");
        var presence = SubscribePresence();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Kayn"));
        See(tracker, Sighting("Garen", BotZoneX, BotZoneY));

        EventBus.EventBus.Publish("GAME.DISCONNECTED", null, "Test");
        Thread.Sleep(50); // bus handlers are async

        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 50);
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 350);
        Thread.Sleep(50);

        lock (presence) Assert.DoesNotContain(presence,
            e => e.Payload is EnemyPresenceAlert { Kind: EnemyAlertKind.Disappear });
    }

    // ── Opening quiet period + static structure-lock veto (2026-07-25 live game) ────

    [Fact]
    public void AppearAlert_DuringOpeningQuietPeriod_Suppressed()
    {
        var snap = SnapshotWithJungler("Kayn");
        snap.GameTime = 10; // in-game 0:10 — the false structure-match window
        var (ui, voice) = Subscribe();
        var tracker = new JunglePresenceTracker(() => snap, clock: new FakeClock { NowMs = 1000 });
        tracker.Start();

        See(tracker, Sighting("Kayn"));

        Thread.Sleep(50); // UI.* is async; nothing should ever arrive
        lock (ui) Assert.Empty(ui);
        lock (voice) Assert.Empty(voice);
    }

    [Fact]
    public void DisappearAlert_DuringOpeningQuietPeriod_Suppressed()
    {
        var snap = SnapshotWithJungler("Kayn");
        snap.GameTime = 15; // inside the 20s quiet window
        var presence = SubscribePresence();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Kayn"));
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 50);
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 350);

        Thread.Sleep(50);
        lock (presence) Assert.Empty(presence);
    }

    // ── Invade window (user requirement: 감지·사라짐 must WORK during the opening minute) ──

    [Fact]
    public void InvadeWindow_StrongMatch_AppearFires_AndDisappearFires()
    {
        // GameTime 45s: past the 20s quiet, inside the strict window — a real invade sighting
        // (conf ≥ 0.88) must announce, and its later vanish must announce too.
        var snap = SnapshotWithJungler("Kayn");
        snap.GameTime = 45;
        var (ui, _) = Subscribe();
        var presence = SubscribePresence();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        See(tracker, Sighting("Kayn", confidence: 0.92));
        WaitForUiCount(ui, 1);
        Assert.Contains("적 정글 발견", PayloadText(ui[0]));

        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 50);
        tracker.Tick(nowMs: JunglePresenceTracker.DefaultLostDebounceMs + 350);
        SpinWait.SpinUntil(() =>
        {
            lock (presence)
                return presence.Any(e => e.Payload is EnemyPresenceAlert { Kind: EnemyAlertKind.Disappear });
        }, TimeSpan.FromSeconds(2));
        lock (presence) Assert.Contains(presence,
            e => e.Payload is EnemyPresenceAlert { Kind: EnemyAlertKind.Disappear });
    }

    [Fact]
    public void InvadeWindow_StructureGradeConfidence_AppearSuppressed()
    {
        // conf 0.82 = the measured structure-lock band; during the strict window it must not
        // produce a jungler callout even though tracking continues.
        var snap = SnapshotWithJungler("Kayn");
        snap.GameTime = 45;
        var (ui, voice) = Subscribe();
        var tracker = new JunglePresenceTracker(() => snap, clock: new FakeClock { NowMs = 1000 });
        tracker.Start();

        See(tracker, Sighting("Kayn", confidence: 0.82));

        Thread.Sleep(50);
        lock (ui) Assert.Empty(ui);
        lock (voice) Assert.Empty(voice);
    }

    [Fact]
    public void StaticTrack_BeyondVetoWindow_ForgottenWithoutDisappear_AndRetracted()
    {
        // The 2026-07-25 false track: a red structure icon matched as the jungler, pixel-still
        // at one point for tens of seconds. After StaticVetoMs the track must be voided quietly:
        // no DISAPPEAR when sightings stop, and the afterimage store told to drop the point.
        var snap = SnapshotWithJungler("Kayn");
        var presence = SubscribePresence();
        var retracted = new List<Event>();
        EventBus.EventBus.Subscribe(JunglePresenceTracker.SightingRetractedTopic,
            e => { lock (retracted) retracted.Add(e); });
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        // Same spot every 500ms until past the veto window.
        for (; clock.NowMs <= (long)JunglePresenceTracker.StaticVetoMs + 1000; clock.NowMs += 500)
            tracker.OnSighting(Sighting("Kayn", 0.90, 0.27), flipped: false);

        SpinWait.SpinUntil(() => { lock (retracted) return retracted.Count > 0; }, TimeSpan.FromSeconds(2));
        lock (retracted) Assert.Contains(retracted, e => Equals(e.Payload, "Kayn"));

        // Sightings stop (the veto is swallowing them); crossing the lost debounce must NOT
        // announce a disappearance — the state was quietly reset to Unseen.
        clock.NowMs += (long)JunglePresenceTracker.DefaultLostDebounceMs + 500;
        tracker.Tick(clock.NowMs);
        Thread.Sleep(50);
        lock (presence) Assert.DoesNotContain(presence,
            e => e.Payload is EnemyPresenceAlert { Kind: EnemyAlertKind.Disappear });
    }

    [Fact]
    public void SubFloorConfidence_NotIngested_NoAlert_NoAfterimageFeed()
    {
        // 0.80 < MinIngestConfidence (0.84): the measured false-ID band (self icon 0.75-0.76,
        // structure locks 0.82) must produce NOTHING — no appear, no last-seen feed.
        var snap = SnapshotWithJungler("Kayn");
        var (ui, _) = Subscribe();
        var marks = new List<Event>();
        EventBus.EventBus.Subscribe(JunglePresenceTracker.SightingTopic,
            e => { lock (marks) marks.Add(e); });
        var tracker = new JunglePresenceTracker(() => snap, clock: new FakeClock { NowMs = 1000 });
        tracker.Start();

        See(tracker, Sighting("Kayn", confidence: 0.80));

        Thread.Sleep(50);
        lock (ui) Assert.Empty(ui);
        lock (marks) Assert.Empty(marks);
    }

    [Fact]
    public void StaticVeto_Releases_WhenTheTrackMoves()
    {
        var snap = SnapshotWithJungler("Kayn");
        var (ui, _) = Subscribe();
        var clock = new FakeClock { NowMs = 0 };
        var tracker = new JunglePresenceTracker(() => snap, clock: clock);
        tracker.Start();

        for (; clock.NowMs <= (long)JunglePresenceTracker.StaticVetoMs + 1000; clock.NowMs += 500)
            tracker.OnSighting(Sighting("Kayn", 0.90, 0.27), flipped: false);
        WaitForUiCount(ui, 1); // the appear from the track's FIRST sighting (pre-veto)

        // A genuinely moving champion re-emerges: new position far outside the veto radius but
        // within a plausible jump; appear must fire again after the cooldown.
        clock.NowMs += (long)JunglePresenceTracker.AlertCooldownMs + 500;
        See(tracker, Sighting("Kayn", 0.85, 0.20));

        WaitForUiCount(ui, 2);
        lock (ui) Assert.Equal(2, ui.Count);
    }
}
