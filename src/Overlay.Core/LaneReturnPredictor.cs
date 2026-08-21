using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;
using Overlay.Core.Overlay;
using Overlay.Core.Recall;

namespace Overlay.Core;

/// <summary>
/// (loop 125) Predicts when a SAME-LANE enemy will be back in lane after you kill them, for the
/// minimap-left return-timer HUD. When an enemy who shares the active player's lane dies, the estimate is
/// <c>respawnTimer + laneDistance / enemyMoveSpeed</c>:
/// <list type="bullet">
/// <item><b>respawnTimer</b> — the ACTUAL server value carried on <see cref="ChampionDiedPayload.RespawnTimer"/>
///   (P1, exact — level-scaled death timer). Not re-derived from level.</item>
/// <item><b>laneDistance</b> — the fountain→lane preset from <see cref="MapConstants"/> (public map geometry).</item>
/// <item><b>enemyMoveSpeed</b> — reconstructed from the enemy's champion base MS + visible items via
///   <see cref="MoveSpeedEstimator"/> (their items are public), then boosted for the fountain-departure
///   ("민병대") burst that speeds the first stretch of the trip. Move speed is the ESTIMATED axis here
///   (Live Client exposes only the active player's live MS), so the whole timer is shown as an estimate.</item>
/// </list>
///
/// <para>Mirrors the lifecycle of <see cref="Inhibitor.InhibitorTimer"/> (Start/Dispose subscribe,
/// pure clock-driven <see cref="GetActive"/> query so the countdown is testable) and the ETA math of
/// <see cref="RecallDetector"/>/<see cref="RecallTimer"/>. Only fires when the dead champion is an ENEMY
/// sharing the active player's <see cref="ScoreboardEntry.Position"/> — so it needs positions, which the
/// Live Client only reports in ranked/draft; in other modes it simply never triggers (honest, no guess).</para>
/// </summary>
public sealed class LaneReturnPredictor : IDisposable
{
    private const string Source = "LaneReturnPredictor";

    // ── Respawn Homeguard (the real "민병대" mechanic — LoL wiki, verified) ──────────────────────
    // On respawn a champion gets a decaying MOVEMENT-SPEED PERCENT boost from the fountain. It is
    // time-based (decays linearly to 0 over a few seconds), NOT distance/turret-based, so the trip is
    // split into a boosted early phase + steady-state remainder. Percent boost stacks additively in the
    // MS percent term (see MoveSpeedEstimator.MoveSpeedBreakdown.WithBonusPercent).

    /// <summary>Game time (seconds) at which Homeguard upgrades from the basic respawn buff to the
    /// empowered spawn buff (wiki: "Homeguard timer reduced to 14:00").</summary>
    private const double HomeguardUpgradeSeconds = 14 * 60;

    /// <summary>Basic respawn Homeguard (before 14:00): +66.67% MS, decays over 8s.</summary>
    private const double BasicHomeguardPercent = 0.6667;
    private const double BasicHomeguardDecaySeconds = 8.0;

    /// <summary>Empowered spawn Homeguard (after 14:00): +75%–150% MS "based on minutes", decays over 7s.
    /// The exact minute→percent curve isn't published; modeled as a linear ramp from 75% at 14:00 to 150%
    /// at <see cref="EmpoweredHomeguardMaxMinute"/> (APPROXIMATION — tunable).</summary>
    private const double EmpoweredHomeguardMinPercent = 0.75;
    private const double EmpoweredHomeguardMaxPercent = 1.50;
    private const double EmpoweredHomeguardMinMinute = 14.0;
    private const double EmpoweredHomeguardMaxMinute = 30.0; // APPROX — where the 150% cap is reached
    private const double EmpoweredHomeguardDecaySeconds = 7.0;

    /// <summary>Homeguard peak bonus percent + decay duration for a respawn at <paramref name="gameSeconds"/>.</summary>
    private static (double PeakPercent, double DecaySeconds) HomeguardAt(double gameSeconds)
    {
        if (gameSeconds < HomeguardUpgradeSeconds)
            return (BasicHomeguardPercent, BasicHomeguardDecaySeconds);
        double t = Math.Clamp((gameSeconds / 60.0 - EmpoweredHomeguardMinMinute)
                              / (EmpoweredHomeguardMaxMinute - EmpoweredHomeguardMinMinute), 0.0, 1.0);
        double pct = EmpoweredHomeguardMinPercent + (EmpoweredHomeguardMaxPercent - EmpoweredHomeguardMinPercent) * t;
        return (pct, EmpoweredHomeguardDecaySeconds);
    }

    /// <summary>(loop 140) League's movement-speed SOFT CAP: MS above 415 is progressively less effective
    /// (raw ×0.8 + 83 for 415–490, ×0.5 + 230 above 490). Without it the model over-values a high-MS enemy
    /// (e.g. one who just bought boots) → too-short travel → the enemy arrives LATER than the timer, which
    /// is exactly the "accurate with no boots, slow with T2 boots" report. Below 415 is unchanged (we never
    /// reach the low-MS slow cap here).</summary>
    private static double SoftCap(double raw)
        => raw <= 415 ? raw
         : raw <= 490 ? raw * 0.8 + 83
         : raw * 0.5 + 230;

    private readonly Func<GameSnapshot?> _snapshot;
    private readonly MapConstants _map;
    private readonly Config.ConfigManager _config;
    private readonly IClock _clock;
    private readonly object _gate = new();

    // (loop 141) All currently-dead enemies being tracked, in DEATH ORDER (oldest first = top of the
    // stacked HUD). Which enemies are added depends on the mode (all / designated / same-lane).
    private readonly List<LaneReturnState> _tracked = new();
    private string? _diedSubId;

    public LaneReturnPredictor(Func<GameSnapshot?> snapshot, MapConstants map, Config.ConfigManager config, IClock? clock = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Subscribe to <c>GAME.CHAMPION_DIED</c>. Idempotent.</summary>
    public void Start()
    {
        if (_diedSubId is not null) return;
        _diedSubId = EventBus.EventBus.Subscribe("GAME.CHAMPION_DIED", OnChampionDied);
    }

    /// <summary>(loop 141) All currently-pending enemy return predictions, in DEATH ORDER (oldest first).
    /// Entries whose predicted return time has elapsed as of <paramref name="nowMs"/> are dropped here, so
    /// a finished timer's portrait disappears and the rest of the stack shifts up. Pure/clock-driven.</summary>
    public IReadOnlyList<LaneReturnStatus> GetActive(long nowMs)
    {
        lock (_gate)
        {
            _tracked.RemoveAll(t => t.ReturnAtMs <= nowMs);
            var result = new List<LaneReturnStatus>(_tracked.Count);
            foreach (var t in _tracked)
                result.Add(new LaneReturnStatus(t.ChampionName, (t.ReturnAtMs - nowMs) / 1000.0));
            return result;
        }
    }

    private void OnChampionDied(Event evt)
    {
        if (evt.Payload is not ChampionDiedPayload payload) return;

        var snap = _snapshot();
        if (snap is null || !snap.HasData) return;

        // Resolve the dead champion's scoreboard row + the active player's row.
        ScoreboardEntry? dead = null, active = null;
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (dead is null && string.Equals(p.ChampionName, payload.ChampionName, StringComparison.OrdinalIgnoreCase))
                dead = p;
            // Active-player identity is inconsistent across the Live Client (summonerName often blank;
            // riotId "Name#TAG" is the reliable key) — match tolerantly like ComboRunner.ResolveActive,
            // else `active` never resolves and the timer never fires.
            if (active is null && IsActivePlayer(p, snap))
                active = p;
        }
        if (dead is null || active is null) return;

        // Must be an ENEMY (different team).
        if (string.Equals(dead.Team, active.Team, StringComparison.Ordinal)) return;

        // (loop 136) The DEAD ENEMY's lane = the Live Client POSITION field (the direct signal — assigned
        // by lane, works when present, e.g. ranked/draft), falling back to the JUNGLE-PET / SUPPORT item
        // (normal inventory slots, exposed by the API — the quest TRACKER is NOT) for the two roles the
        // position field misses most, then the user "내 라인" override / default in the distance step.
        string? enemyLane = LaneKeyFor(dead.Position) ?? LaneKeyFromItems(dead);
        string? myLane = LaneKeyFor(active.Position) ?? LaneKeyFromItems(active);
        bool isSameLane = enemyLane is not null && string.Equals(enemyLane, myLane, StringComparison.Ordinal);

        // (loop 141) Mode gate (overlay.laneReturnMode, user option): track ALL enemies, only the
        // DESIGNATED (⇄-pinned) target, or only a SAME-LANE enemy. Default: all.
        bool track = (_config.Get("overlay.laneReturnMode") as string ?? "all").ToLowerInvariant() switch
        {
            "designated" => IsDesignatedTarget(dead),
            "samelane" => isSameLane,
            _ => true, // "all"
        };
        if (!track) return;

        // (loop 137) Destination = the enemy's OUTER TURRET (T1). Uses EMPIRICALLY-CALIBRATED fountain→T1
        // distances per lane (FountainToT1) — top/bot 7000 were ~1s accurate; mid recalibrated to 5100
        // after arriving ~5s late at the map_constants-derived 2632 (the map_constants mid preset, 4700,
        // is too short for this). Lanes not in the table fall back to map_constants × fraction, then default.
        string? lane = enemyLane ?? ConfigLaneKey();
        double destDistance =
            lane is not null && FountainToT1.TryGetValue(lane, out var t1) ? t1
            : lane is not null && _map.GetLaneDistance(lane) is { } ld && ld > 0 ? ld * T1DistanceFraction
            : DefaultLaneDistance * T1DistanceFraction;

        // (loop 128) Travel with the real respawn Homeguard boost. The boost is a MS PERCENT that decays
        // LINEARLY to 0 over ~7-8s (time-based) — so the enemy covers a boosted early phase, then the
        // remainder at steady-state MS. Average bonus percent over a linear decay = peak / 2.
        var bd = EstimateEnemyMoveSpeed(dead);
        double steadyMs = SoftCap(bd.Steady);
        var (peakPercent, decaySeconds) = HomeguardAt(snap.GameTime);
        double avgBoostMs = SoftCap(bd.WithBonusPercent(peakPercent / 2.0));
        double boostDist = avgBoostMs * decaySeconds; // distance covered while the boost is active
        double travelSeconds = boostDist >= destDistance
            ? destDistance / avgBoostMs                                   // reaches T1 before the boost ends
            : decaySeconds + (destDistance - boostDist) / steadyMs;       // boost ends mid-trip
        double totalSeconds = Math.Max(0, payload.RespawnTimer) + travelSeconds;

        long returnAtMs = _clock.NowMs + (long)(totalSeconds * 1000);
        lock (_gate)
        {
            // Re-death: drop any stale entry for this champion, then append so this fresh death sits at
            // the BOTTOM (newest) — the stack stays in death order (oldest on top).
            _tracked.RemoveAll(t => string.Equals(t.ChampionName, dead.ChampionName, StringComparison.OrdinalIgnoreCase));
            _tracked.Add(new LaneReturnState(dead.ChampionName, returnAtMs));
        }
    }

    /// <summary>Reconstructs the dead enemy's move-speed breakdown (flat + percent) from champion base MS
    /// (cached-5 only; others use <see cref="MoveSpeedEstimator.DefaultBaseMoveSpeed"/>) + visible item MS
    /// + curated conditional bonus. Returns the breakdown so the caller can layer the Homeguard percent.</summary>
    private static MoveSpeedEstimator.MoveSpeedBreakdown EstimateEnemyMoveSpeed(ScoreboardEntry dead)
    {
        string id = ChampionSummary.ResolveKoreanName(dead.ChampionName) ?? dead.ChampionName;
        double baseMs = ChampionRepository.Get(id)?.BaseStats.Ms ?? 0;

        var itemStats = new List<ItemStats>();
        var itemIds = new List<int>();
        for (int j = 0; j < dead.ItemCount && j < dead.ItemIds.Length; j++)
        {
            int itemId = dead.ItemIds[j];
            itemIds.Add(itemId);
            if (ItemRepository.Get(itemId.ToString()) is { } item) itemStats.Add(item.Stats);
        }

        bool conditional = MoveSpeedEstimator.HasConditionalMsItem(itemIds);
        return MoveSpeedEstimator.Breakdown(baseMs, itemStats, conditional);
    }

    /// <summary>Fallback fountain→lane distance (game units) when no quest item, position, or user
    /// override is available. A side-lane value; the respawn part stays exact, only travel approximates.</summary>
    private const double DefaultLaneDistance = 12500.0;

    /// <summary>Destination is the enemy's OUTER TURRET (T1), not the far lane end — fountain→T1 as a
    /// fraction of the fountain→lane preset. Calibrated from a Nasus top L1 test (~0.56); tunable, and may
    /// vary slightly per lane.</summary>
    private const double T1DistanceFraction = 0.56;

    /// <summary>(loop 136) ROLE item id → lane key. The 2026 quest TRACKER (1090–1094) is in a separate
    /// slot the Live Client does NOT expose (HUD diagnostic confirmed), but the JUNGLE PET (1101–1107) and
    /// SUPPORT items (World Atlas line) sit in normal inventory slots and ARE exposed — so they reliably
    /// identify the two roles whose lane the position field is least reliable for. Support → bot lane.
    /// Patch-dependent ids.</summary>
    private static readonly Dictionary<int, string> RoleItemToLaneKey = new()
    {
        // Jungle pet companions (Gustwalker / Mosstomper / Scorchclaw, base + upgraded)
        [1101] = "jungle", [1102] = "jungle", [1103] = "jungle",
        [1104] = "jungle", [1105] = "jungle", [1106] = "jungle", [1107] = "jungle",
        // Support items (World Atlas → Runic Compass → Bounty of Worlds → Zaz'Zak's Realmspike) → bot
        [3865] = "bot", [3866] = "bot", [3867] = "bot", [3871] = "bot",
    };

    /// <summary>The lane key implied by a player's carried JUNGLE/SUPPORT item, or null if none — used only
    /// as a fallback when the (more direct) position field is blank.</summary>
    private static string? LaneKeyFromItems(ScoreboardEntry e)
    {
        for (int j = 0; j < e.ItemCount && j < e.ItemIds.Length; j++)
            if (RoleItemToLaneKey.TryGetValue(e.ItemIds[j], out var key)) return key;
        return null;
    }

    /// <summary>(loop 137) EMPIRICALLY-CALIBRATED fountain→T1 (outer turret) distances per lane, in game
    /// units. top/bot 7000 measured ~1s accurate; mid 5100 recalibrated after arriving ~5s late at the
    /// map_constants-derived value; jungle 4000 is an estimate (camps are near the fountain — tune with
    /// data). Used directly (not map_constants × fraction) since the map_constants presets were
    /// inconsistent across lanes. Patch/map dependent — tunable.</summary>
    private static readonly Dictionary<string, double> FountainToT1 = new()
    {
        // (loop 138) user calibration: top/bot/support −1s (arrived ~1s early), mid +2s (~2s late).
        // Converted at ~370 units/s marginal (steady) MS: top/bot 7000→6600, mid 5100→5850.
        ["top"] = 6600, ["bot"] = 6600, ["mid"] = 5600, ["jungle"] = 4500,
    };

    /// <summary>The lane key from the user's "내 라인" override (<c>overlay.laneReturnLane</c>), or null when
    /// unset / "auto" — the last-resort lane source when neither position nor a role item resolves.</summary>
    private string? ConfigLaneKey()
    {
        if (_config.Get("overlay.laneReturnLane") as string is { Length: > 0 } cfg
            && !string.Equals(cfg, "auto", StringComparison.OrdinalIgnoreCase))
            return LaneKeyFor(cfg);
        return null;
    }

    /// <summary>True when <paramref name="dead"/> is the user's DESIGNATED (Manual-pinned) target — the
    /// reliable "my lane opponent" signal set by the ⇄ picker (<c>targeting.mode</c>/<c>manualTarget</c>).</summary>
    private bool IsDesignatedTarget(ScoreboardEntry dead)
    {
        if (!string.Equals(_config.Get("targeting.mode") as string, "Manual", StringComparison.OrdinalIgnoreCase))
            return false;
        if (_config.Get("targeting.manualTarget") as string is not { Length: > 0 } manual) return false;
        return string.Equals(dead.ChampionName, manual, StringComparison.OrdinalIgnoreCase)
            || SamePlayer(dead.RiotId, manual)
            || SamePlayer(dead.SummonerName, manual);
    }

    /// <summary>Tolerant active-player identity match (riotId "Name#TAG" ↔ summonerName, tag/case), mirroring
    /// ComboRunner.ResolveActive — the Live Client is inconsistent about which identity field is populated.</summary>
    private static bool IsActivePlayer(ScoreboardEntry p, GameSnapshot snap)
        => SamePlayer(p.RiotId, snap.ActivePlayerRiotId)
           || SamePlayer(p.SummonerName, snap.ActivePlayerSummonerName)
           || SamePlayer(p.RiotId, snap.ActivePlayerSummonerName)
           || SamePlayer(p.SummonerName, snap.ActivePlayerRiotId);

    /// <summary>Case-insensitive, "#TAG"-stripped identity comparison.</summary>
    private static bool SamePlayer(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        string na = a.Split('#')[0].Trim();
        string nb = b.Split('#')[0].Trim();
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Maps a Live Client position ("TOP"/"MIDDLE"/"BOTTOM"/"UTILITY"/"JUNGLE") to a
    /// <see cref="MapConstants"/> lane key, or null if unrecognized/empty.</summary>
    private static string? LaneKeyFor(string? position) => (position ?? string.Empty).ToUpperInvariant() switch
    {
        "TOP" => "top",
        "MIDDLE" or "MID" => "mid",
        "BOTTOM" or "BOT" or "UTILITY" or "SUPPORT" => "bot",
        "JUNGLE" => "jungle",
        _ => null,
    };

    public void Dispose()
    {
        if (_diedSubId is not null)
        {
            EventBus.EventBus.Unsubscribe(_diedSubId);
            _diedSubId = null;
        }
    }

    private readonly record struct LaneReturnState(string ChampionName, long ReturnAtMs);
}

/// <summary>Read-only snapshot of the pending same-lane-enemy return prediction, as of the
/// <c>nowMs</c> passed to <see cref="LaneReturnPredictor.GetActive"/>. <see cref="RemainingSeconds"/>
/// is an ESTIMATE (move-speed axis) — the HUD marks it with "~".</summary>
public readonly record struct LaneReturnStatus(string ChampionName, double RemainingSeconds);
