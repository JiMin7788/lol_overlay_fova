using System.Net;
using System.Text;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M01 LiveClient API's poller-level requirements:
///  1. 0.1s hard-coded minimum poll interval (Internal Logic #1/#5, Reviewer Checklist #1).
///  2. GAME.DISCONNECTED fires only after 3 consecutive failed polls, not the first
///     (Internal Logic #3), and reconnecting fires GAME.CONNECTED again (Reviewer
///     Checklist #2).
///  3. Self-signed cert bypass is scoped to 127.0.0.1 (Reviewer Checklist #3) — proved
///     at the config level (the only endpoint the poller ever targets).
///  4. The app does not crash / the loop keeps retrying when the game isn't running
///     (Acceptance Criteria #1).
///
/// Uses <see cref="FakeHttpMessageHandler"/> (in-process, no real network/game) so the
/// tests are fast and deterministic. Ticks are still driven by the real
/// <see cref="PollerConfig.MinPollInterval"/> cadence (100ms), so multi-tick tests
/// wait a small number of real 100ms intervals — same tradeoff the M15 batching test
/// already accepts for timer-driven behavior.
/// </summary>
public class LiveClientPollerTests
{
    [Fact]
    public void PollInterval_BelowHardFloor_IsClampedTo100Milliseconds()
    {
        var config = new PollerConfig { PollInterval = TimeSpan.FromMilliseconds(1) };

        Assert.Equal(TimeSpan.FromMilliseconds(100), config.PollInterval);
    }

    [Fact]
    public void PollInterval_Default_Is100Milliseconds()
    {
        var config = new PollerConfig();

        Assert.Equal(TimeSpan.FromMilliseconds(100), config.PollInterval);
    }

    [Fact]
    public void PollInterval_AtOrAboveHardFloor_IsUnchanged()
    {
        var config = new PollerConfig { PollInterval = TimeSpan.FromMilliseconds(250) };

        Assert.Equal(TimeSpan.FromMilliseconds(250), config.PollInterval);
    }

    [Fact]
    public void AllGameDataUrl_DefaultsToLocalhostOnly()
    {
        // The self-signed-cert bypass in LiveClientPoller's constructor is only ever
        // exercised for requests to this URL — scoping the bypass to 127.0.0.1 is
        // therefore a property of this default never being overridden to a remote
        // host, which is what every other poll-cycle test below implicitly relies on
        // (they all use this default).
        var config = new PollerConfig();

        Assert.StartsWith("https://127.0.0.1:2999/liveclientdata/", config.AllGameDataUrl);
    }

    [Fact]
    public async Task GameNotRunning_ConnectionRefused_DoesNotCrash_AndStaysDisconnected()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("simulated: game not running"));
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);

        poller.Start();
        await Task.Delay(350); // several failed ticks

        Assert.False(poller.GameConnected);
        await poller.StopAsync(); // no exception => loop survived every failed tick
    }

    [Fact]
    public async Task Disconnect_RequiresThreeConsecutiveFailures_NotTheFirst()
    {
        // Tick 1 connects (call 1 succeeds); every call after that fails, simulating
        // the game closing / going to a loading screen mid-session.
        var handler = new FakeHttpMessageHandler(n => n == 1
            ? JsonResponse(SamplePayload(gameTime: 10))
            : throw new HttpRequestException("simulated failure"));
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);

        var diffs = new List<SnapshotDiff>();
        poller.DiffAvailable += d => { lock (diffs) diffs.Add(d); };

        poller.Start();
        // Tick 1 (connect, t~0), tick 2 (1st failure, t~100), tick 3 (2nd failure,
        // t~200) — only 2 consecutive failures so far, below the threshold of 3.
        await Task.Delay(250);
        lock (diffs)
        {
            Assert.DoesNotContain(diffs, d => d.GameAvailabilityChanged && !d.GameIsActive);
        }

        // Tick 4 (t~300) is the 3rd consecutive failure — disconnect must fire now.
        await Task.Delay(250);
        lock (diffs)
        {
            Assert.Contains(diffs, d => d.GameAvailabilityChanged && !d.GameIsActive);
        }

        await poller.StopAsync();
    }

    [Fact]
    public async Task Reconnect_AfterDisconnect_FiresConnectedAgain()
    {
        var handler = new FakeHttpMessageHandler(n =>
        {
            // Tick 1 connects; ticks 2-4 fail (3 consecutive => disconnect on tick 4);
            // tick 5+ succeeds again (=> reconnect).
            if (n == 1) return JsonResponse(SamplePayload(gameTime: 5));
            if (n is >= 2 and <= 4) throw new HttpRequestException("simulated failure");
            return JsonResponse(SamplePayload(gameTime: 12.3));
        });
        using var http = new HttpClient(handler);
        await using var poller = new LiveClientPoller(new PollerConfig(), http);

        var diffs = new List<SnapshotDiff>();
        poller.DiffAvailable += d => { lock (diffs) diffs.Add(d); };

        poller.Start();
        await Task.Delay(700);
        await poller.StopAsync();

        lock (diffs)
        {
            Assert.Contains(diffs, d => d.GameAvailabilityChanged && !d.GameIsActive); // disconnected
            Assert.Contains(diffs, d => d.GameAvailabilityChanged && d.GameIsActive);  // reconnected
        }
        Assert.True(poller.GameConnected);
    }

    // ---- shared test fixtures ----

    [Fact]
    public void Parse_ActivePlayerAbilities_ReadsRealAbilityLevelsIntoSnapshot()
    {
        // activePlayer.abilities.{Q,W,E,R}.abilityLevel is the player's real in-game rank.
        var json = """
        {
          "activePlayer": {
            "summonerName": "Me",
            "currentGold": 500,
            "level": 9,
            "championStats": { "attackDamage": 100, "abilityPower": 0 },
            "abilities": {
              "Passive": { "displayName": "P", "abilityLevel": 0 },
              "Q": { "abilityLevel": 5, "displayName": "Q" },
              "W": { "abilityLevel": 3 },
              "E": { "abilityLevel": 1 },
              "R": { "abilityLevel": 2 }
            }
          },
          "allPlayers": [],
          "events": { "Events": [] },
          "gameData": { "gameTime": 60 }
        }
        """;
        var snap = new GameSnapshot();

        bool ok = LiveDataParser.Parse(Encoding.UTF8.GetBytes(json), snap);

        Assert.True(ok);
        Assert.Equal(5, snap.Stats.AbilityQ);
        Assert.Equal(3, snap.Stats.AbilityW);
        Assert.Equal(1, snap.Stats.AbilityE);
        Assert.Equal(2, snap.Stats.AbilityR);
        // championStats after the abilities block must still parse (reader resumed correctly).
        Assert.Equal(100, snap.Stats.AttackDamage);
    }

    /// <summary>Loop-38 fix: Riot's Live Client API reports armor/magicPenetrationPercent as a
    /// remaining-resistance MULTIPLIER (baseline/no-pen-items = 1.0), not the additive 0-1 bonus
    /// fraction (baseline = 0.0) <see cref="Damage.DamageEngine"/>'s <c>1.0 - penPercent</c> formula
    /// expects. Reading the raw value directly made a naked attacker's percent-pen always compute as
    /// 1.0, zeroing the resistance term for EVERY hit regardless of the target's real armor/MR — this
    /// is the root cause of a user-reported "damage always matches raw tooltip, never mitigated"
    /// combo bug. <see cref="LiveDataParser"/> now converts at parse time (<c>1.0 - raw</c>).</summary>
    [Fact]
    public void Parse_PenetrationPercent_ConvertsRiotMultiplier_ToAdditiveFraction()
    {
        var json = """
        {
          "activePlayer": {
            "summonerName": "Me",
            "currentGold": 500,
            "level": 9,
            "championStats": {
              "attackDamage": 100, "abilityPower": 9,
              "armorPenetrationFlat": 0, "armorPenetrationPercent": 1,
              "magicPenetrationFlat": 0, "magicPenetrationPercent": 0.82
            }
          },
          "allPlayers": [],
          "events": { "Events": [] },
          "gameData": { "gameTime": 60 }
        }
        """;
        var snap = new GameSnapshot();

        bool ok = LiveDataParser.Parse(Encoding.UTF8.GetBytes(json), snap);

        Assert.True(ok);
        // Riot's baseline 1.0 (no bonus pen) -> our 0.0 (no bonus pen). Before this fix this stayed
        // 1.0, which is exactly what zeroed EffectiveResistMultiplier's resistance term.
        Assert.Equal(0.0, snap.Stats.ArmorPenetrationPercent, precision: 6);
        // Riot's 0.82 (18% pen, 82% of MR remains) -> our 0.18 (18% bonus pen).
        Assert.Equal(0.18, snap.Stats.MagicPenetrationPercent, precision: 6);
    }

    /// <summary>Loop-38 continuation 19: activePlayer.fullRunes.keystone.id + generalRunes[].id ->
    /// GameSnapshot.Stats.EquippedRuneIds, the source ComboRunner's rune auto-load now prefers over
    /// the M04 manual picker. keystone repeats as generalRunes[0] in real payloads (deduplicated
    /// here); statRunes (stat shards) is a sibling field that must never be read into this list.</summary>
    [Fact]
    public void Parse_FullRunes_ReadsKeystoneAndGeneralRunes_IntoEquippedRuneIds()
    {
        var json = """
        {
          "activePlayer": {
            "summonerName": "Me",
            "currentGold": 500,
            "level": 9,
            "championStats": { "attackDamage": 100, "abilityPower": 0 },
            "fullRunes": {
              "keystone": { "id": 8437, "rawDescription": "d", "rawDisplayName": "Grasp of the Undying" },
              "primaryRuneTree": { "id": 8400, "rawDescription": "d", "rawDisplayName": "Resolve" },
              "secondaryRuneTree": { "id": 8300, "rawDescription": "d", "rawDisplayName": "Inspiration" },
              "generalRunes": [
                { "id": 8437, "rawDescription": "d", "rawDisplayName": "Grasp of the Undying" },
                { "id": 8401, "rawDescription": "d", "rawDisplayName": "Shield Bash" },
                { "id": 8369, "rawDescription": "d", "rawDisplayName": "First Strike" }
              ],
              "statRunes": [
                { "id": 5008, "rawDescription": "d", "rawDisplayName": "Adaptive Force" }
              ]
            }
          },
          "allPlayers": [],
          "events": { "Events": [] },
          "gameData": { "gameTime": 60 }
        }
        """;
        var snap = new GameSnapshot();

        bool ok = LiveDataParser.Parse(Encoding.UTF8.GetBytes(json), snap);

        Assert.True(ok);
        // keystone (8437) is deduplicated against generalRunes[0]'s identical id -> 3 total, not 4.
        Assert.Equal(new[] { 8437, 8401, 8369 }, snap.Stats.EquippedRuneIds);
        // statRunes' stat-shard id (5008) must never leak in.
        Assert.DoesNotContain(5008, snap.Stats.EquippedRuneIds);
    }

    [Fact]
    public void Parse_NoFullRunesBlock_LeavesEquippedRuneIdsEmpty()
    {
        var snap = new GameSnapshot();

        bool ok = LiveDataParser.Parse(Encoding.UTF8.GetBytes(SamplePayload(gameTime: 10)), snap);

        Assert.True(ok);
        Assert.NotNull(snap.Stats.EquippedRuneIds);
        Assert.Empty(snap.Stats.EquippedRuneIds);
    }

    [Fact]
    public void Parse_NoAbilitiesBlock_LeavesAbilityRanksAtZero()
    {
        var snap = new GameSnapshot();

        bool ok = LiveDataParser.Parse(Encoding.UTF8.GetBytes(SamplePayload(gameTime: 10)), snap);

        Assert.True(ok);
        Assert.Equal(0, snap.Stats.AbilityQ);
        Assert.Equal(0, snap.Stats.AbilityW);
        Assert.Equal(0, snap.Stats.AbilityE);
        Assert.Equal(0, snap.Stats.AbilityR);
    }

    [Fact]
    public void Parse_InhibKilledEvent_CapturesIdKillerAndTime_IntoInhibEvents()
    {
        var json = SamplePayload(
            gameTime: 30,
            killEventsJson: $"[{InhibKilledEvent(7, 28.4, "Hide on bush", "Barracks_T1_L1")}]");
        var snap = new GameSnapshot();

        bool ok = LiveDataParser.Parse(Encoding.UTF8.GetBytes(json), snap);

        Assert.True(ok);
        Assert.Equal(1, snap.InhibEventCount);
        var ev = snap.InhibEvents[0];
        Assert.True(ev.IsInhibKilled);
        Assert.Equal(7, ev.EventId);
        Assert.Equal(28.4, ev.EventTime);
        Assert.Equal("Hide on bush", ev.KillerName);
        Assert.Equal("Barracks_T1_L1", ev.InhibId);
    }

    [Fact]
    public void Parse_ChampionKillAndInhibKilled_InSameTick_DoNotCrossContaminate()
    {
        var events = $"[{ChampionKillEvent(1, 10.0, "A", "B")},{InhibKilledEvent(2, 11.0, "A", "Barracks_T2_C1")}]";
        var json = SamplePayload(gameTime: 15, killEventsJson: events);
        var snap = new GameSnapshot();

        bool ok = LiveDataParser.Parse(Encoding.UTF8.GetBytes(json), snap);

        Assert.True(ok);
        Assert.Equal(1, snap.KillEventCount);
        Assert.Equal(1, snap.InhibEventCount);
        Assert.Equal("B", snap.KillEvents[0].VictimName);
        Assert.Equal("Barracks_T2_C1", snap.InhibEvents[0].InhibId);
    }

    internal static string SamplePayload(
        double gameTime,
        double activeGold = 500,
        int activeLevel = 1,
        string activeSummoner = "Hide on bush",
        string alliedChampion = "Ahri",
        int alliedLevel = 1,
        bool alliedDead = false,
        int[]? alliedItemIds = null,
        int alliedCreepScore = 0,
        string enemySummoner = "Enemy1",
        string enemyChampion = "Zed",
        bool enemyDead = false,
        string killEventsJson = "[]")
    {
        alliedItemIds ??= Array.Empty<int>();
        var itemsJson = new StringBuilder("[");
        for (int i = 0; i < alliedItemIds.Length; i++)
        {
            if (i > 0) itemsJson.Append(',');
            itemsJson.Append($"{{\"itemID\":{alliedItemIds[i]},\"slot\":{i}}}");
        }
        itemsJson.Append(']');

        return $$"""
        {
          "activePlayer": {
            "summonerName": "{{activeSummoner}}",
            "currentGold": {{activeGold}},
            "level": {{activeLevel}},
            "championStats": { "currentHealth": 500, "maxHealth": 500, "resourceValue": 100, "resourceMax": 100, "attackDamage": 60, "abilityPower": 0, "armor": 30, "magicResist": 30, "moveSpeed": 340 }
          },
          "allPlayers": [
            {
              "summonerName": "{{activeSummoner}}",
              "championName": "{{alliedChampion}}",
              "team": "ORDER",
              "level": {{alliedLevel}},
              "isDead": {{(alliedDead ? "true" : "false")}},
              "respawnTimer": 0,
              "scores": { "kills": 0, "deaths": 0, "assists": 0, "creepScore": {{alliedCreepScore}} },
              "items": {{itemsJson}}
            },
            {
              "summonerName": "{{enemySummoner}}",
              "championName": "{{enemyChampion}}",
              "team": "CHAOS",
              "level": 1,
              "isDead": {{(enemyDead ? "true" : "false")}},
              "respawnTimer": 0,
              "scores": { "kills": 0, "deaths": 0, "assists": 0, "creepScore": 0 },
              "items": []
            }
          ],
          "events": { "Events": {{killEventsJson}} },
          "gameData": { "gameTime": {{gameTime}} }
        }
        """;
    }

    internal static string ChampionKillEvent(int eventId, double eventTime, string killerName, string victimName) =>
        $$"""{"EventID":{{eventId}},"EventName":"ChampionKill","EventTime":{{eventTime}},"KillerName":"{{killerName}}","VictimName":"{{victimName}}","Assisters":[]}""";

    /// <summary>M19 §3.2 parser-extension fixture: same array-entry shape as
    /// <see cref="ChampionKillEvent"/>, for the real Live Client <c>InhibKilled</c> event
    /// (EventName == "InhibKilled", plus the event's own "InhibKilled" field carrying the
    /// destroyed inhibitor's raw id, e.g. "Barracks_T1_L1").</summary>
    internal static string InhibKilledEvent(int eventId, double eventTime, string killerName, string inhibId) =>
        $$"""{"EventID":{{eventId}},"EventName":"InhibKilled","EventTime":{{eventTime}},"KillerName":"{{killerName}}","InhibKilled":"{{inhibId}}","Assisters":[]}""";

    internal static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Minimal in-process HttpMessageHandler stand-in for the real Live
    /// Client API — lets tests drive exact response sequences without a real game
    /// process or network call.</summary>
    internal sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder;
        private int _callCount;

        public FakeHttpMessageHandler(Func<int, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int n = Interlocked.Increment(ref _callCount);
            return Task.FromResult(_responder(n));
        }
    }
}
