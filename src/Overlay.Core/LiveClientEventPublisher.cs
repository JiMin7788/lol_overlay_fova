using Overlay.Core.EventBus;

namespace Overlay.Core;

/// <summary>
/// M01 Internal Logic #2: subscribes to a <see cref="LiveClientPoller"/> and performs
/// this module's own field-level diff (keyed by <c>championName</c>, the identity
/// field the M01 spec's Output section uses for every payload) to publish the eight
/// GAME.* events through the M15 Event Bus.
///
/// Deliberately independent of the legacy <see cref="SnapshotDiff"/>/
/// <see cref="PlayerChange"/> model: that model is keyed by SummonerName, tracks only
/// deltas (not the raw current values a payload like ITEM_CHANGED needs), and its
/// <see cref="SnapshotDiff.HasChanges"/> gate ignores GameTime — so reusing it would
/// either miss GAME.GAME_TIME_UPDATED on most ticks or require changing
/// SnapshotDiffer's HasChanges semantics, which several other legacy modules
/// (ObjectiveTimer/RecallDetector/KillableCalculator) already depend on.
/// This class instead consumes <see cref="LiveClientPoller.SnapshotAvailable"/> (raw
/// previous/current snapshot pair, every successful tick) and does its own compare.
/// </summary>
public sealed class LiveClientEventPublisher : IDisposable
{
    private const string Source = "M01.LiveClient";

    private readonly LiveClientPoller _poller;
    private bool _subscribed;

    public LiveClientEventPublisher(LiveClientPoller poller)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _poller.DiffAvailable += OnDiffAvailable;
        _poller.SnapshotAvailable += OnSnapshotAvailable;
        _subscribed = true;
    }

    /// <summary>GAME.CONNECTED / GAME.DISCONNECTED: driven off the legacy diff's
    /// availability-transition flag, which already implements the 3-consecutive-
    /// failure threshold (M01 Internal Logic #3) via LiveClientPoller.</summary>
    private void OnDiffAvailable(SnapshotDiff diff)
    {
        if (!diff.GameAvailabilityChanged) return;

        if (diff.GameIsActive)
            EventBus.EventBus.Publish("GAME.CONNECTED", null, Source);
        else
            EventBus.EventBus.Publish("GAME.DISCONNECTED", null, Source);
    }

    /// <summary>Everything else: field-level diff of the raw snapshot pair.</summary>
    private void OnSnapshotAvailable(GameSnapshot previous, GameSnapshot current, bool isInitialSync)
    {
        if (!current.HasData) return;

        // GAME_TIME_UPDATED fires whenever the clock actually advances (including the
        // very first synced tick, since that first value is itself meaningful data).
        // It is NOT spam: gameTime genuinely changes essentially every tick while
        // connected, so publishing it each time it changes is a correct field-level
        // diff, not a violation of "no event when nothing changed" (Acceptance
        // Criteria #3) — the field DID change.
        if (isInitialSync || current.GameTime != previous.GameTime)
            EventBus.EventBus.Publish("GAME.GAME_TIME_UPDATED", new GameTimeUpdatedPayload(current.GameTime), Source);

        // Per-player/gold diffs need a real previous-tick baseline. On initial sync
        // `previous` has no meaningful prior state, so emitting deltas here would
        // flood every connected player's level/items/gold the instant the game or
        // app connects — suppressed the same way the legacy SnapshotDiffer suppresses
        // its deltas on IsInitialSync.
        if (isInitialSync) return;

        PublishActivePlayerGold(previous, current);
        PublishPerPlayerChanges(previous, current);
        PublishInhibitorEvents(previous, current);
        PublishNexusTurretEvents(previous, current);
    }

    private void PublishActivePlayerGold(GameSnapshot previous, GameSnapshot current)
    {
        // currentGold is only ever available for the LOCAL (active) player — the Live
        // Client API does not expose other players' gold, matching what the game's
        // own HUD shows. Publishing it for anyone else would be exactly the kind of
        // "beyond what the API/screen provides" over-claim the M01 Policy Compliance
        // Checklist forbids.
        if (current.CurrentGold == previous.CurrentGold) return;

        string championName = FindChampionName(current, current.ActivePlayerSummonerName);
        if (championName.Length == 0) return; // scoreboard entry for the active player not resolved yet

        EventBus.EventBus.Publish("GAME.GOLD_UPDATED", new GoldUpdatedPayload(championName, current.CurrentGold), Source);
    }

    private void PublishPerPlayerChanges(GameSnapshot previous, GameSnapshot current)
    {
        for (int i = 0; i < current.PlayerCount; i++)
        {
            ScoreboardEntry cur = current.Players[i];
            if (cur.ChampionName.Length == 0) continue;

            ScoreboardEntry? prev = FindBySummonerName(previous, cur.SummonerName);
            if (prev is null) continue; // new player mid-stream (rare); will sync from next tick

            if (cur.Level > prev.Level)
                EventBus.EventBus.Publish("GAME.PLAYER_LEVEL_UP", new PlayerLevelUpPayload(cur.ChampionName, cur.Level), Source);

            if (ItemsChanged(prev, cur))
                EventBus.EventBus.Publish("GAME.ITEM_CHANGED", new ItemChangedPayload(cur.ChampionName, BuildItemSlots(cur)), Source);

            if (cur.CreepScore != prev.CreepScore)
                EventBus.EventBus.Publish("GAME.CS_CHANGED", new CreepScoreChangedPayload(cur.ChampionName, cur.CreepScore), Source);

            if (!prev.IsDead && cur.IsDead)
            {
                (string killerName, double timestamp) = ResolveKillerInfo(current, cur.SummonerName);
                EventBus.EventBus.Publish("GAME.CHAMPION_DIED", new ChampionDiedPayload(cur.ChampionName, killerName, timestamp, cur.RespawnTimer), Source);
            }
            else if (prev.IsDead && !cur.IsDead)
            {
                EventBus.EventBus.Publish("GAME.CHAMPION_RESPAWNED", new ChampionRespawnedPayload(cur.ChampionName), Source);
            }
        }
    }

    /// <summary>M19 §3.2: InhibKilled events, like ChampionKill, are append-only and never
    /// reordered in the Live Client's <c>events</c> list (see <see cref="GameSnapshot.InhibEvents"/>),
    /// so any slot beyond <c>previous.InhibEventCount</c> is one newly seen this tick — the same
    /// "new tail entries" diff <see cref="OnSnapshotAvailable"/>'s isInitialSync guard already
    /// protects against replaying pre-existing history on connect.</summary>
    private void PublishInhibitorEvents(GameSnapshot previous, GameSnapshot current)
    {
        for (int i = previous.InhibEventCount; i < current.InhibEventCount; i++)
        {
            InhibEventRecord ev = current.InhibEvents[i];
            if (ev.IsInhibKilled)
            {
                LogStructureId($"InhibKilled raw=\"{ev.InhibId}\" norm=\"{NormalizeInhibitorId(ev.InhibId)}\" t={ev.EventTime}");
                EventBus.EventBus.Publish(
                    "GAME.INHIBITOR_DESTROYED",
                    new InhibitorDestroyedPayload(NormalizeInhibitorId(ev.InhibId), ev.EventTime), Source);
            }
            else if (ev.IsInhibRespawned)
            {
                LogStructureId($"InhibRespawned raw=\"{ev.InhibId}\" norm=\"{NormalizeInhibitorId(ev.InhibId)}\" t={ev.EventTime}");
                EventBus.EventBus.Publish(
                    "GAME.INHIBITOR_RESPAWNED",
                    new InhibitorRespawnedPayload(NormalizeInhibitorId(ev.InhibId), ev.EventTime), Source);
            }
        }
    }

    /// <summary>Patch 15.1 Nexus-turret respawn timer: TurretKilled events are append-only
    /// (same tail-diff as <see cref="PublishInhibitorEvents"/>), but only the two Nexus turrets
    /// per base respawn (3:00), so this filters via <see cref="IsNexusTurretId"/> before
    /// publishing GAME.NEXUS_TURRET_DESTROYED. Every non-Nexus turret kill is ignored here.</summary>
    private void PublishNexusTurretEvents(GameSnapshot previous, GameSnapshot current)
    {
        for (int i = previous.TurretEventCount; i < current.TurretEventCount; i++)
        {
            TurretEventRecord ev = current.TurretEvents[i];
            if (!ev.IsTurretKilled) continue;

            bool isNexus = IsNexusTurretId(ev.TurretId);
            // Log EVERY turret kill + its nexus classification (and the normalized key when it IS a nexus),
            // so a single game reveals the real id format and confirms no tier-3/other turret is
            // misclassified as a Nexus turret — and which turret is/isn't a twin turret.
            LogStructureId($"TurretKilled raw=\"{ev.TurretId}\" t={ev.EventTime} nexus={isNexus}"
                + (isNexus ? $" norm=\"{NormalizeNexusTurretId(ev.TurretId)}\"" : ""));

            if (!isNexus) continue;
            EventBus.EventBus.Publish(
                "GAME.NEXUS_TURRET_DESTROYED",
                new NexusTurretDestroyedPayload(NormalizeNexusTurretId(ev.TurretId), ev.EventTime), Source);
        }
    }

    /// <summary>Classifier for the two Nexus ("twin") turrets from a raw TurretKilled id.
    ///
    /// <para><b>LIVE-CONFIRMED format (2026-07-16, user game capture):</b> real Live Client ids are
    /// <c>Turret_{TOrder|TChaos}_L{lane}_P{pos}_{instance}_0</c>, and the two twin turrets are the
    /// MID-lane (<c>_L1_</c>) positions <c>P4</c> (top-side twin) and <c>P5</c> (bot-side twin):
    /// observed <c>Turret_TChaos_L1_P4_392430785_0</c> (top) and
    /// <c>Turret_TChaos_L1_P5_342097928_0</c> (bot). The numeric tail is a per-game/per-instance
    /// suffix — never match on it.</para>
    ///
    /// <para>The legacy datamined shape (<c>Turret_T{1,2}_C_0{1,2}_A</c>: <c>_C_01</c> bot-nexus,
    /// <c>_C_02</c> top-nexus) is still accepted for fixtures/back-compat. Regular turrets are
    /// <c>P1..P3</c> per lane, so none of them match. Wrong matches only affect the Nexus-turret
    /// overlay, never damage/policy paths; <see cref="LogStructureId"/> keeps capturing every raw id
    /// for further correction.</para></summary>
    internal static bool IsNexusTurretId(string turretId)
    {
        if (string.IsNullOrEmpty(turretId)) return false;
        return turretId.Contains("_L1_P4", StringComparison.OrdinalIgnoreCase)  // live: top-side twin
            || turretId.Contains("_L1_P5", StringComparison.OrdinalIgnoreCase)  // live: bot-side twin
            || turretId.Contains("_C_01", StringComparison.OrdinalIgnoreCase)   // legacy: bot-nexus
            || turretId.Contains("_C_02", StringComparison.OrdinalIgnoreCase);  // legacy: top-nexus
    }

    // ── Structure-id normalization (live-confirmed 2026-07-16) ──────────────────────
    //
    // Real Live Client structure ids carry a volatile numeric tail that differs per game AND per
    // structure instance (e.g. "Inhib_TChaos_L2_P1_2351107073_0",
    // "Turret_TChaos_L1_P4_392430785_0"). Publishing the RAW id would break the timers two ways:
    // (1) an InhibRespawned id may not string-match the InhibKilled id, so the respawn would never
    // clear the timer; (2) the timers' NO-INTERFERENCE dedup would key on the instance, not the
    // structure. So the publisher normalizes to a stable structural key before publishing.
    // Team token: "TOrder"/"TChaos" live, "_T1_"/"_T2_" legacy. Lane token "_L{n}": L2=top is
    // LIVE-CONFIRMED (user capture); L0=bot/L1=mid by elimination — flagged for live verification
    // in CLAUDE_CODE_TODO (the structure log captures every raw id to confirm).

    /// <summary>"Inhib_TChaos_L2_P1_2351107073_0" → "Chaos_L2"; "Barracks_T1_L1" → "Order_L1".
    /// Unparseable ids pass through raw (timer still works, minimap placement may be skipped).</summary>
    internal static string NormalizeInhibitorId(string raw)
    {
        string? team = StructureTeam(raw);
        int lane = StructureLane(raw);
        return (team is null || lane < 0) ? raw : team + "_L" + lane;
    }

    /// <summary>"Turret_TChaos_L1_P4_392430785_0" → "Chaos_NexusTop"; legacy "Turret_T1_C_01_A" →
    /// "Order_NexusBot". Unparseable ids pass through raw.</summary>
    internal static string NormalizeNexusTurretId(string raw)
    {
        string? team = StructureTeam(raw);
        if (team is null) return raw;
        if (raw.Contains("_L1_P4", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("_C_02", StringComparison.OrdinalIgnoreCase)) return team + "_NexusTop";
        if (raw.Contains("_L1_P5", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("_C_01", StringComparison.OrdinalIgnoreCase)) return team + "_NexusBot";
        return raw;
    }

    private static string? StructureTeam(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.Contains("TOrder", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("_T1_", StringComparison.OrdinalIgnoreCase)) return "Order";
        if (raw.Contains("TChaos", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("_T2_", StringComparison.OrdinalIgnoreCase)) return "Chaos";
        return null;
    }

    /// <summary>First "_L{digit}" token's digit, or -1. L0=bot, L1=mid, L2=top (see block comment).</summary>
    private static int StructureLane(string raw)
    {
        for (int i = 0; i + 2 < raw.Length; i++)
        {
            if (raw[i] == '_' && (raw[i + 1] == 'L' || raw[i + 1] == 'l') && char.IsAsciiDigit(raw[i + 2]))
                return raw[i + 2] - '0';
        }
        return -1;
    }

    /// <summary>Best-effort diagnostic: append a raw destroyed-structure id (and how it was classified)
    /// to <c>logs/structure_ids.log</c> so the REAL live id format can be read off a single game and the
    /// minimap decoders corrected. Never throws (best-effort file append). Set
    /// <see cref="StructureLogPathOverride"/> in tests.</summary>
    internal static string? StructureLogPathOverride;
    private static readonly object StructureLogGate = new();
    private static void LogStructureId(string line)
    {
        try
        {
            string path = StructureLogPathOverride
                ?? System.IO.Path.Combine(AppContext.BaseDirectory, "logs", "structure_ids.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            lock (StructureLogGate)
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
        }
        catch { /* diagnostics must never break the event path */ }
    }

    /// <summary>True on ANY slot-level change: an item appearing/disappearing (id differs) OR a
    /// stackable consumable's quantity changing (same id, count differs — e.g. drinking a Health
    /// Potion: 2 -> 1). The count check is what makes consumable USE a real, detectable field-level
    /// change rather than invisible noise (previously only id presence was compared).</summary>
    private static bool ItemsChanged(ScoreboardEntry prev, ScoreboardEntry cur)
    {
        if (prev.ItemCount != cur.ItemCount) return true;
        for (int i = 0; i < cur.ItemCount; i++)
        {
            if (prev.ItemIds[i] != cur.ItemIds[i]) return true;
            if (prev.ItemCounts[i] != cur.ItemCounts[i]) return true;
        }
        return false;
    }

    private static ItemSlot[] BuildItemSlots(ScoreboardEntry entry)
    {
        if (entry.ItemCount == 0) return Array.Empty<ItemSlot>();

        var slots = new ItemSlot[entry.ItemCount];
        for (int i = 0; i < entry.ItemCount; i++)
            slots[i] = new ItemSlot(entry.ItemIds[i], i, entry.ItemCounts[i]);
        return slots;
    }

    /// <summary>Resolves the killer of <paramref name="victimSummonerName"/>'s death
    /// this tick (and that kill's own timestamp) from the parsed ChampionKill events,
    /// then maps the killer's raw identity to a championName via the scoreboard.
    /// Falls back to the raw identity string when the killer isn't a tracked player
    /// (turret/minion/jungle monster kills report a non-player identity there), and
    /// to the current tick's gameTime when no matching kill event is found at all
    /// (defensive — should not normally happen since isDead only flips alongside a
    /// ChampionKill event) — see M01_report.md Notes for Reviewer.</summary>
    private static (string KillerName, double Timestamp) ResolveKillerInfo(GameSnapshot current, string victimSummonerName)
    {
        KillEventRecord? match = null;
        for (int i = 0; i < current.KillEventCount; i++)
        {
            KillEventRecord ev = current.KillEvents[i];
            if (!ev.IsChampionKill) continue;
            if (!string.Equals(ev.VictimName, victimSummonerName, StringComparison.Ordinal)) continue;
            if (match is null || ev.EventId > match.EventId) match = ev;
        }

        if (match is null) return (string.Empty, current.GameTime);

        string championName = FindChampionName(current, match.KillerName);
        string killerName = championName.Length > 0 ? championName : match.KillerName;
        return (killerName, match.EventTime);
    }

    private static string FindChampionName(GameSnapshot snapshot, string summonerName)
    {
        if (summonerName.Length == 0) return string.Empty;
        return FindBySummonerName(snapshot, summonerName)?.ChampionName ?? string.Empty;
    }

    private static ScoreboardEntry? FindBySummonerName(GameSnapshot snapshot, string summonerName)
    {
        for (int i = 0; i < snapshot.PlayerCount; i++)
        {
            if (string.Equals(snapshot.Players[i].SummonerName, summonerName, StringComparison.Ordinal))
                return snapshot.Players[i];
        }
        return null;
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        _poller.DiffAvailable -= OnDiffAvailable;
        _poller.SnapshotAvailable -= OnSnapshotAvailable;
        _subscribed = false;
    }
}

// ---- GAME.* event payloads (M01 spec Interfaces/Output) ----

public readonly record struct GameTimeUpdatedPayload(double GameTime);

public readonly record struct GoldUpdatedPayload(string ChampionName, double CurrentGold);

public readonly record struct PlayerLevelUpPayload(string ChampionName, int NewLevel);

/// <summary><see cref="RespawnTimer"/> is the raw Live Client <c>respawnTimer</c> value
/// (seconds remaining until respawn) for the victim, carried unchanged so M08 can present
/// the exact in-game Death Timer without any correction.</summary>
public readonly record struct ChampionDiedPayload(string ChampionName, string KillerName, double Timestamp, double RespawnTimer);

public readonly record struct ChampionRespawnedPayload(string ChampionName);

public readonly record struct ItemChangedPayload(string ChampionName, ItemSlot[] Items);

/// <summary>M30 (Enemy Jungler Spotted Alert) addition: fired whenever ANY scoreboard player's
/// <c>creepScore</c> changes tick-to-tick, mirroring the existing <see cref="ItemChangedPayload"/>
/// field-level diff (every visible scoreboard player, not just the active player — ally/enemy
/// filtering is the subscriber's responsibility, same convention M07's Changelog v1.1 already
/// established for item events).</summary>
public readonly record struct CreepScoreChangedPayload(string ChampionName, int CreepScore);

/// <summary>One equipped item slot, per the M01 Data Model's <c>ItemSlot[]</c>.
/// <see cref="Slot"/> is the item's position (0-6) as returned by the Live Client
/// API's <c>items</c> array, which is already slot-ordered. <see cref="Count"/> is the raw
/// <c>items[].count</c> stack quantity (defaults to 1 for the non-stackable case, keeping
/// pre-existing 2-arg call sites source-compatible).</summary>
public readonly record struct ItemSlot(int ItemId, int Slot, int Count = 1);

/// <summary>M19 §3.2 Data Model addition: <see cref="InhibitorId"/> is the raw Live
/// Client identifier for the destroyed inhibitor (e.g. <c>"Barracks_T1_L1"</c>), which
/// already encodes the lane/side per the spec's "lane/side id" field — carried verbatim
/// rather than decoded (see <see cref="InhibEventRecord.InhibId"/> for why). This is REAL
/// Live Client data (InhibKilled is an actual events-feed entry), not an inference — never
/// label it an estimate (unlike Jungle Camp / Global Gold, M19 §3.1/§3.3).</summary>
public readonly record struct InhibitorDestroyedPayload(string InhibitorId, double GameTime);

/// <summary>M19 §3.2 / patch-15.1 timer removal: the game reported an inhibitor RESPAWNED
/// (Live Client <c>InhibRespawned</c> event). <see cref="GameTime"/> is its in-game respawn
/// time. Consumed by <see cref="Inhibitor.InhibitorTimer"/> to end that inhibitor's countdown
/// on the authoritative event (the one non-timeout removal path).</summary>
public readonly record struct InhibitorRespawnedPayload(string InhibitorId, double GameTime);

/// <summary>Patch 15.1 Nexus-turret respawn timer. <see cref="TurretId"/> is the raw Live
/// Client identifier for the destroyed Nexus turret (format unconfirmed — see
/// <see cref="TurretEventRecord"/>). REAL Live Client data (TurretKilled is an actual
/// events-feed entry), not an inference. The 3:00 respawn is a fixed patch-15.1 game rule
/// the API does not report per-tick, so <see cref="NexusTurret.NexusTurretTimer"/> computes it.</summary>
public readonly record struct NexusTurretDestroyedPayload(string TurretId, double GameTime);
