using Overlay.Core.EventBus;

namespace Overlay.Core.Combo;

/// <summary>
/// Honest LOWER-BOUND missing-HP estimator (loop 38+N — see M05_DAMAGE_ENGINE.md changelog).
///
/// ─── WHY THIS EXISTS ──────────────────────────────────────────────────────────────────────
/// The Live Client API never exposes an enemy's current HP (see
/// <see cref="ComboRunner.BuildDefenderFor"/>'s prior "CurrentHP = MaxHP" default). "Perfectly"
/// back-calculating it is impossible without a real HP feed. What IS knowable for certain: the
/// instant a champion RESPAWNS (<c>GAME.CHAMPION_RESPAWNED</c>) they are at 100% HP. From that
/// anchor, this tracker accumulates OUR OWN calculated combo damage dealt to that champion (via
/// <see cref="ComboRunner"/>'s real, non-preview, non-fallback, non-snapshot triggers) as
/// "confirmed damage taken since last respawn".
///
/// ─── WHY THIS IS A LOWER BOUND, NEVER THE REAL NUMBER ────────────────────────────────────
/// This estimate can only ever UNDERSTATE real missing HP, never overstate it: it has no
/// visibility into damage from minions/turrets/other laners/jungle monsters/DoTs, nor into
/// healing from potions/elixirs/regen/lifesteal/heals (none of which the Live Client API
/// exposes at all — see CLAUDE.md history: "no potion/elixir/buff event exists"). Every one of
/// those either deals damage we didn't see (understating missing HP further, harmless — our
/// number still undershoots truth) or heals HP back (making our accumulated-damage number an
/// OVERESTIMATE of missing HP for that specific case) — so in the presence of untracked healing
/// this can occasionally overstate missing HP for an individual query, but never overstate it
/// relative to what WE can prove; it must never be presented as authoritative or "perfect".
///
/// ─── DEATH-VS-RESPAWN RESET TIMING (judgment call, documented per task) ──────────────────
/// On <see cref="OnChampionDied"/>, accumulated damage is set to a sentinel (<see cref="DepletedSentinel"/>,
/// effectively "unbounded") rather than left alone or zeroed: a champion is provably at 0 HP the
/// instant they die, i.e. 100% missing, and <see cref="GetAccumulatedDamage"/>'s clamp already
/// turns any value &gt;= maxHp into exactly maxHp — so a sentinel var needs no maxHp lookup at
/// death time (this tracker holds no per-champion maxHp) and is always correct regardless of that
/// champion's actual max HP. <see cref="OnChampionRespawned"/> then resets to 0 (full HP) exactly
/// as before. In practice <see cref="ComboRunner.ResolveTarget"/> already excludes dead
/// champions from ever being targeted, so this sentinel mostly never surfaces in a live combo —
/// it exists as a correctness backstop, not because dead targets are commonly queried.
///
/// ─── KEYING ───────────────────────────────────────────────────────────────────────────────
/// Keyed by champion name (<c>ScoreboardEntry.ChampionName</c>) — the same identity
/// <see cref="ComboRunner.ResolveTarget"/>, <see cref="ComboRunner.NextLivingEnemy"/>, and
/// <see cref="TargetSnapshotStore"/> already key by (case-insensitively), so this tracker's key
/// matches the rest of the combo pipeline without introducing a second identity scheme.
///
/// ─── LIFECYCLE ────────────────────────────────────────────────────────────────────────────
/// Mirrors <see cref="Inhibitor.InhibitorTimer"/>/<see cref="Recall.RecallTimer"/>'s shape:
/// idempotent <see cref="Start"/>, subscription ids released on <see cref="Dispose"/>.
/// </summary>
public sealed class TargetHealthTracker : IDisposable
{
    /// <summary>Sentinel accumulated-damage value written on death: larger than any real champion's
    /// max HP could ever be, so <see cref="GetAccumulatedDamage"/>'s clamp always yields exactly
    /// <c>maxHp</c> (fully depleted) regardless of which champion died or their real max HP.</summary>
    private const double DepletedSentinel = double.MaxValue;

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _accumulatedDamage = new(StringComparer.OrdinalIgnoreCase);

    private string? _diedSubId;
    private string? _respawnedSubId;

    /// <summary>Subscribe to <c>GAME.CHAMPION_DIED</c>/<c>GAME.CHAMPION_RESPAWNED</c>. Idempotent.</summary>
    public void Start()
    {
        if (_diedSubId is not null) return;
        _diedSubId = EventBus.EventBus.Subscribe("GAME.CHAMPION_DIED", HandleChampionDiedEvent);
        _respawnedSubId = EventBus.EventBus.Subscribe("GAME.CHAMPION_RESPAWNED", HandleChampionRespawnedEvent);
    }

    /// <summary>A champion just respawned — they are at 100% HP for certain. Resets accumulated
    /// damage for <paramref name="championKey"/> to 0.</summary>
    public void OnChampionRespawned(string championKey)
    {
        if (string.IsNullOrEmpty(championKey)) return;
        lock (_gate) { _accumulatedDamage[championKey] = 0; }
    }

    /// <summary>A champion just died — they are at 0% HP (100% missing) for certain. See the class
    /// doc comment's "DEATH-VS-RESPAWN RESET TIMING" section for why this writes a sentinel rather
    /// than 0/maxHp directly.</summary>
    public void OnChampionDied(string championKey)
    {
        if (string.IsNullOrEmpty(championKey)) return;
        lock (_gate) { _accumulatedDamage[championKey] = DepletedSentinel; }
    }

    /// <summary>Adds <paramref name="damage"/> to <paramref name="championKey"/>'s accumulated
    /// damage-since-respawn. Never lets the running total go negative (a negative/zero
    /// <paramref name="damage"/> is ignored — this only ever tracks damage WE confirmed was dealt).</summary>
    public void RecordDamageDealt(string championKey, double damage)
    {
        if (string.IsNullOrEmpty(championKey) || damage <= 0) return;
        lock (_gate)
        {
            _accumulatedDamage.TryGetValue(championKey, out double current);
            double next = current + damage; // may saturate to +Infinity from the death sentinel — harmless, still clamps correctly below
            _accumulatedDamage[championKey] = Math.Max(0, next);
        }
    }

    /// <summary>The accumulated "confirmed damage taken since last respawn" for
    /// <paramref name="championKey"/>, clamped to <c>[0, maxHp]</c> — this can never report more
    /// than 100% missing, since that is a mathematical impossibility. Returns 0 for an unknown/
    /// untracked champion (never having died/respawned/been damaged) or a non-positive
    /// <paramref name="maxHp"/>.</summary>
    public double GetAccumulatedDamage(string championKey, double maxHp)
    {
        if (string.IsNullOrEmpty(championKey) || maxHp <= 0) return 0;
        lock (_gate)
        {
            double acc = _accumulatedDamage.TryGetValue(championKey, out double v) ? v : 0;
            return Math.Clamp(acc, 0, maxHp);
        }
    }

    private void HandleChampionDiedEvent(Event evt)
    {
        if (evt.Payload is not ChampionDiedPayload payload) return;
        OnChampionDied(payload.ChampionName);
    }

    private void HandleChampionRespawnedEvent(Event evt)
    {
        if (evt.Payload is not ChampionRespawnedPayload payload) return;
        OnChampionRespawned(payload.ChampionName);
    }

    public void Dispose()
    {
        if (_diedSubId is not null)
        {
            EventBus.EventBus.Unsubscribe(_diedSubId);
            _diedSubId = null;
        }
        if (_respawnedSubId is not null)
        {
            EventBus.EventBus.Unsubscribe(_respawnedSubId);
            _respawnedSubId = null;
        }
    }
}
