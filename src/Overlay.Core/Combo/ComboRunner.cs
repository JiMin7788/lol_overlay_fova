using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Overlay.Core.ChampionDb;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Items;
using Overlay.Core.Overlay;
using Overlay.Core.Runes;
using Overlay.Core.Summoners;

namespace Overlay.Core.Combo;

/// <summary>
/// Closes the hotkey -> combo -> HUD loop (FINAL_INSPECTION FINDING 1): M13 publishes
/// <c>COMBO.TRIGGER{comboId}</c> and M02's <see cref="OverlayCoordinator"/> already maps
/// <c>UI.COMBO_RESULT</c> to the combo-result HUD, but nothing sat in the middle. This
/// runner is that middle link — it subscribes to <c>COMBO.TRIGGER</c>, resolves the saved
/// combo, assembles an M03 <see cref="ExecutionContext"/> from the latest live game
/// snapshot, runs <see cref="ComboEngine.Execute"/>, and publishes the resulting
/// <see cref="ComboResult"/> on <c>UI.COMBO_RESULT</c>.
///
/// ─── NO WPF / FULLY TESTABLE ─────────────────────────────────────────────────────────
/// The latest snapshot is reached through an injected <c>Func&lt;GameSnapshot?&gt;</c>
/// (in production wired to the M01 poller's latest tick; in tests a lambda returning a
/// synthetic snapshot), so the whole loop is unit-testable without a live game.
///
/// ─── HONEST API LIMITATIONS (M06/M12 established) ────────────────────────────────────
/// The Live Client API does NOT expose enemy live HP/armor/MR, the player's current
/// target, the base-vs-bonus AD split, crit/lifesteal, or rune data. So most of the
/// <see cref="ExecutionContext"/> is assembled from what IS available plus honest,
/// clearly-documented estimation (see <see cref="BuildContext"/>). The published
/// <see cref="ComboResult"/> is therefore an APPROXIMATION — real target stats would need
/// item-aware estimation and a real target-selection signal neither of which the API
/// gives us. Full end-to-end validation requires a live game.
///
/// ─── NO INPUT AUTOMATION (P4) ────────────────────────────────────────────────────────
/// This runner only computes and publishes inert display data. It sends no keypress and
/// touches no OS input API.
/// </summary>
public sealed class ComboRunner : IDisposable
{
    /// <summary>Same key prefix <see cref="ComboEditor"/> persists combos under
    /// (<c>combos.saved.{id}</c>). Reused, not redefined, so a saved combo round-trips.</summary>
    private const string SavedComboKeyPrefix = "combos.saved.";

    /// <summary>The event type published below AND the combo-result card's stable id in
    /// <see cref="OverlayCoordinator"/> (spec v1.3 changelog: a UI.*-mapped card's id is its
    /// event type). Named once here so the toggle-off check and the publish call cannot drift
    /// apart.</summary>
    private const string HudEventType = "UI.COMBO_RESULT";

    private static readonly JsonSerializerOptions SavedComboOptions = new();

    /// <summary>Documented fallback defender used when no enemy champion can be resolved
    /// from the snapshot — i.e. there is no living enemy on the scoreboard, or the chosen
    /// enemy's champion is outside M11's cached set. Zeroed resists + a nominal 1000 HP
    /// keep the result computable while making it obvious the defender was not measured.
    /// This is an estimate of last resort, never presented as real target stats.</summary>
    private static readonly DefenderStat FallbackDefender =
        new(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);

    private readonly ComboEngine _engine;
    private readonly ConfigManager _config;
    private readonly Func<GameSnapshot?> _currentSnapshot;
    private readonly IClock _clock;

    /// <summary>The SAME <see cref="RuneEngine"/> instance <see cref="_engine"/> was built with
    /// (see AppComposition.StartSubsystems) — needed here so a manual rune's ON/OFF checkbox state
    /// (<see cref="RuneEngine.SetManualFlag"/>) can be armed on it right before
    /// <see cref="ComboEngine.Execute"/> internally calls <see cref="RuneEngine.GetActiveEffects"/>
    /// on that same instance. Null for older/test call sites that don't care about rune wiring —
    /// those keep the pre-existing empty-<see cref="UserRuneConfig"/> behavior unchanged.</summary>
    private readonly RuneEngine? _runeEngine;

    /// <summary>Shared honest lower-bound missing-HP estimator (see <see cref="TargetHealthTracker"/>'s
    /// class doc comment) — anchors each tracked enemy's HP to 100% at their last known respawn, then
    /// accumulates OUR OWN calculated combo damage against them. Null for older/test call sites that
    /// don't care about missing-HP estimation — those keep the pre-existing "CurrentHP == MaxHP"
    /// behavior unchanged (see <see cref="BuildDefenderFor"/>).</summary>
    private readonly TargetHealthTracker? _targetHealthTracker;

    /// <summary>M02 <see cref="OverlayCoordinator"/> instance, attached post-construction (see
    /// <see cref="AttachOverlayCoordinator"/>) once the WPF host has created it — this runner is
    /// built long before that (spec "NO WPF / FULLY TESTABLE"), but <see cref="OverlayCoordinator"/>
    /// itself has no WPF dependency, so holding an optional reference here does not break that.
    /// Null (the default, e.g. every existing unit test) simply disables the toggle-off check
    /// below and preserves the pre-existing "always trigger" behavior.</summary>
    private OverlayCoordinator? _overlayCoordinator;

    /// <summary>The comboId whose card is the currently-shown <c>UI.COMBO_RESULT</c> HUD, or null
    /// if none/cleared. Needed because <see cref="ComboHudResult"/>/<see cref="HUDPayload"/> carry
    /// no comboId (the card's stable id is the constant event type, shared by every combo — see
    /// M02 v1.3 changelog), so identity has to be tracked at the publisher.</summary>
    private string? _shownComboId;

    private string? _subscriptionId;

    /// <summary>(loop 117) EventBus/config subscription ids for LIVE refresh (see
    /// <see cref="RefreshShownCombo"/>) — stat-change + targeting events that re-run the shown
    /// combo. Held so <see cref="Dispose"/> can detach them alongside <see cref="_subscriptionId"/>.</summary>
    private readonly List<string> _refreshSubscriptionIds = new();

    /// <summary>(loop 117) Serializes <see cref="ComputeAndPublish"/>: it can now be entered from the
    /// hotkey thread (OnComboTrigger) AND the poll thread (RefreshShownCombo), and it arms manual-rune
    /// flags on the SHARED <see cref="_runeEngine"/> right before Execute reads them — so two overlapping
    /// computes would race that shared state. The lock makes a refresh and a real trigger take turns.</summary>
    private readonly object _computeGate = new();

    /// <summary>(loop 119) Last-seen defender stats for the designated (Manual) pin, keyed by the pinned
    /// name (champion). Updated every time the pin resolves to a real row (alive OR dead — the scoreboard
    /// keeps level/items even while dead/out-of-vision), and reused when the pinned champion is ABSENT
    /// from the snapshot (left the game) so the card keeps computing from the last-known stats instead of
    /// a 0/0 fallback. Implements the user's "죽어도/안 보여도 저장된 스텟 기준 계산" (no "적 사망 초기화").
    /// In-memory only (a fresh game starts clean); OrdinalIgnoreCase to match ReadTargeting's name.
    ///
    /// <para>(loop 498) CONCURRENT, and it has to be. <see cref="_computeGate"/> serialises the two
    /// threads its own doc comment names — the hotkey and the poll — but the always-on skill panel
    /// added a THIRD caller of <see cref="BuildContext"/> that never takes it:
    /// <see cref="ComputeSkillPanel"/> runs on the render thread roughly four times a second, and
    /// BuildContext WRITES this cache. A lock one of two writers takes protects nothing, and
    /// concurrent writes to a plain Dictionary corrupt its bucket chain — an intermittent hang or
    /// IndexOutOfRangeException, not a wrong number. Per-key atomicity is all this needs: it is a
    /// last-seen cache with no invariant spanning keys, so a concurrent dictionary is the fix rather
    /// than widening the lock, which would stall the render thread behind a combo computation.</para>
    ///
    /// <para>HOW LIKELY WAS IT, HONESTLY: not very, and that is worth writing down rather than
    /// leaving as an implied emergency. A dictionary corrupts most readily on concurrent INSERT,
    /// and this one is keyed by the PINNED target — one key, changed by hand, rewritten in place —
    /// so in practice it overwrites a single entry and never resizes. A stress test hammering both
    /// entry points from eight threads could not reproduce a failure against the plain Dictionary.
    /// The race is real by inspection and the fix costs nothing; the crash was unlikely.</para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Level, double MaxHp, double Armor, double Mr)> _lastSeenTargets
        = new(StringComparer.OrdinalIgnoreCase);

    public ComboRunner(
        ComboEngine engine, ConfigManager config, Func<GameSnapshot?> currentSnapshot,
        IClock? clock = null, RuneEngine? runeEngine = null, TargetHealthTracker? targetHealthTracker = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _currentSnapshot = currentSnapshot ?? throw new ArgumentNullException(nameof(currentSnapshot));
        _clock = clock ?? new SystemClock();
        _runeEngine = runeEngine;
        _targetHealthTracker = targetHealthTracker;
    }

    /// <summary>Attaches the M02 <see cref="OverlayCoordinator"/> so the toggle-off check in
    /// <see cref="OnComboTrigger"/> can see whether the combo card is still on screen. Called once
    /// the WPF host has composed it (<c>MainWindow.OnLoaded</c>, via <c>AppComposition</c>); safe
    /// to leave unattached (toggle-off then simply never fires).</summary>
    public void AttachOverlayCoordinator(OverlayCoordinator coordinator)
        => _overlayCoordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <summary>Subscribe to <c>COMBO.TRIGGER</c>. Idempotent — a second call is a no-op.</summary>
    public void Start()
    {
        if (_subscriptionId is not null) return;
        _subscriptionId = EventBus.EventBus.Subscribe("COMBO.TRIGGER", OnComboTrigger);

        // (loop 117) LIVE refresh: when the attacker's or target's stats change, re-run the shown
        // combo so the card updates without a new hotkey press. Level-up / item change fire for ANY
        // player (mine + enemy), so both my-stat and enemy-stat changes are covered; death/respawn
        // drives the designated-target reset; the targeting.* config change makes a ⇄ re-pin apply
        // immediately. All route to RefreshShownCombo (a no-op when no card is shown).
        void OnStatEvent(Event _) => RefreshShownCombo();
        _refreshSubscriptionIds.Add(EventBus.EventBus.Subscribe("GAME.PLAYER_LEVEL_UP", OnStatEvent));
        _refreshSubscriptionIds.Add(EventBus.EventBus.Subscribe("GAME.ITEM_CHANGED", OnStatEvent));
        _refreshSubscriptionIds.Add(EventBus.EventBus.Subscribe("GAME.CHAMPION_DIED", OnStatEvent));
        _refreshSubscriptionIds.Add(EventBus.EventBus.Subscribe("GAME.CHAMPION_RESPAWNED", OnStatEvent));
        _refreshSubscriptionIds.Add(_config.OnChange("targeting.manualTarget", _ => RefreshShownCombo()));
        _refreshSubscriptionIds.Add(_config.OnChange("targeting.mode", _ => RefreshShownCombo()));
    }

    /// <summary>
    /// The "타겟 스탯 복사" (copy target stats) UI action — loop 38 continuation 19's defender-side
    /// "virtual model": re-runs the SAME live target resolution <see cref="OnComboTrigger"/> uses
    /// (<see cref="ReadTargeting"/> → <see cref="ResolveActive"/> → <see cref="ResolveTarget"/> →
    /// <see cref="BuildDefenderFor"/>) for <paramref name="comboId"/>'s currently-configured target
    /// preference, and persists it via <see cref="TargetSnapshotStore"/> ONLY when a REAL
    /// (non-fallback) target is currently resolved. Returns false — and saves nothing — when there
    /// is no active game, no living enemy, or the resolved target's resistances could not be looked
    /// up (<see cref="BuildDefenderFor"/> fell back), so a fake/zeroed snapshot is never persisted.
    /// </summary>
    public bool CaptureTargetSnapshot(string comboId)
    {
        if (string.IsNullOrWhiteSpace(comboId)) return false;

        var snap = _currentSnapshot();
        if (snap is null || !snap.HasData) return false;

        var (mode, manualTarget) = ReadTargeting();
        var active = ResolveActive(snap);
        var target = ResolveTarget(snap, active, mode, manualTarget);
        var defender = BuildDefenderFor(target, out bool usedFallback);
        if (target is null || usedFallback) return false; // no real target resolved — never save a fake snapshot

        var snapshot = new TargetSnapshot(target.ChampionName, defender.Armor, defender.Mr, defender.MaxHP, _clock.NowMs);
        TargetSnapshotStore.Save(_config, comboId, snapshot);
        return true;
    }

    /// <summary>
    /// Handles one <c>COMBO.TRIGGER</c> (payload = <c>comboId</c> string published by
    /// AppComposition/M13). Resolves the saved combo, builds the context from the latest
    /// snapshot, runs the engine, and publishes <c>UI.COMBO_RESULT</c>. Every failure mode
    /// (unknown/corrupt combo, no active game, bad snapshot) logs and returns without
    /// publishing a bogus result; the whole body is wrapped so a bad combo/snapshot can
    /// never crash the app.
    /// </summary>
    private void OnComboTrigger(Event evt)
    {
        if (evt.Payload is not string comboId || string.IsNullOrWhiteSpace(comboId))
        {
            Log("COMBO.TRIGGER payload was not a comboId string; ignored.");
            return;
        }

        // Toggle-off (M02/M04/M13 "press the same combo's hotkey again to hide it"):
        // if THIS combo's card is the one currently shown, clear it instead of re-triggering.
        // Also checks coordinator.IsActive (not just comboId equality) so a card cleared by
        // something else in the meantime doesn't leave this runner's toggle state stale.
        if (comboId == _shownComboId && _overlayCoordinator is { } coordinator
            && coordinator.IsActive(HudEventType))
        {
            coordinator.ClearHud(HudEventType);
            _shownComboId = null;
            return;
        }

        ComputeAndPublish(comboId, recordDamage: true);
    }

    /// <summary>(loop 117) Computes the combo against the LATEST snapshot + live config and publishes
    /// UI.COMBO_RESULT. Extracted from <see cref="OnComboTrigger"/> so the SAME computation can be
    /// re-run for LIVE refresh (see <see cref="RefreshShownCombo"/>) when the attacker's or target's
    /// stats change — level-up, item purchase, death/respawn, or a ⇄ target re-pin — WITHOUT a new
    /// hotkey press. Own try/catch: a bad combo/snapshot must never crash the app.
    /// <paramref name="recordDamage"/> is true ONLY for a real hotkey press (OnComboTrigger); a live
    /// refresh passes false so the missing-HP tracker isn't credited the SAME combo's damage
    /// repeatedly (a refresh re-displays the same hypothetical combo, it is not another cast).</summary>
    /// <param name="publish">(loop 487) false = compute and RETURN, touching neither the HUD nor
    /// the toggle state. The combo editor needs the same number the card shows while the user is
    /// still building the combo, and it must not make a card appear over a live game to get it.</param>
    /// <param name="snapshotOverride">(loop 487) Stand in for the live snapshot. Used by
    /// <see cref="ComputePreview"/> out of game, where there is no snapshot to read.</param>
    /// <param name="savedOverride">(loop 487) Skip the config lookup and use this combo directly —
    /// the editor previews a sequence that has not been saved and may never be.</param>
    /// <returns>The card payload, or null when nothing could be computed.</returns>
    private ComboHudResult? ComputeAndPublish(string comboId, bool recordDamage, bool publish = true,
        GameSnapshot? snapshotOverride = null, SavedCombo? savedOverride = null)
    {
        lock (_computeGate)
        {
        try
        {
            // 1. Resolve the combo — from the caller when previewing an unsaved sequence,
            //    otherwise from persisted config (written by M04's ComboEditor).
            SavedCombo? saved;
            ComboGraph graph;
            if (savedOverride is not null)
            {
                saved = savedOverride;
                try { graph = _engine.Deserialize(saved.GraphJson); }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    Log($"preview combo is corrupt: {ex.Message}");
                    return null;
                }
            }
            else
            {
                if (_config.Get(SavedComboKeyPrefix + comboId) is not string rawSaved)
                {
                    Log($"no saved combo '{comboId}'; nothing to compute.");
                    return null;
                }

                try
                {
                    saved = JsonSerializer.Deserialize<SavedCombo>(rawSaved, SavedComboOptions);
                    if (saved is null)
                    {
                        Log($"saved combo '{comboId}' deserialized to null; ignored.");
                        return null;
                    }
                    graph = _engine.Deserialize(saved.GraphJson);
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    Log($"saved combo '{comboId}' is corrupt: {ex.Message}");
                    return null;
                }
            }

            // 2. Get the latest snapshot. No active game => do not publish a bogus result.
            var snap = snapshotOverride ?? _currentSnapshot();
            if (snap is null || !snap.HasData)
            {
                Log("no active game; combo not computed.");
                return null;
            }

            // 3. Assemble the ExecutionContext (attacker real, defender ESTIMATED — see BuildContext).
            //    targetChampion is the resolved enemy's name, surfaced for the card's portrait.
            //    The target-selection preference is read live from config so a UI change applies
            //    without restarting the runner (see ReadTargeting).
            var (targetMode, manualTarget) = ReadTargeting();

            // 2b. Resolve the combo champion + arm this trigger's rune state: the persisted
            //     selection (M04 rune panel) becomes the real UserRuneConfig, and each selected
            //     non-trackable rune's manual checkbox state is (re-)armed on the SHARED RuneEngine
            //     instance right before Execute() reads it — see LoadRuneSelectionAndArmManualFlags.
            string championId = ResolveChampionId(snap, saved);
            var runeConfig = LoadRuneSelectionAndArmManualFlags(championId, snap);

            var context = BuildContext(snap, saved, targetMode, manualTarget, runeConfig, _config, comboId,
                _targetHealthTracker,
                out string targetChampion, out bool defenderIsFallback, out bool usingSnapshotTarget,
                out int targetLevel, out bool designatedCleared);

            // Diagnostic: a silent FallbackDefender (Armor=0/Mr=0 — see FallbackDefender) makes
            // every hit LOOK like unmitigated true damage with no error anywhere, which is exactly
            // how the loop-38 "damage never reflects armor/MR" report could go unnoticed for an
            // entire live test. IMPORTANT (loop-38 continuation): this must be driven by
            // `defenderIsFallback`, NOT by `targetChampion` being empty — a target ROW can resolve
            // fine (real name/portrait) while TryResolveBase still separately fails to find that
            // champion's base resistances, which is exactly the bug a user reported (target visible,
            // no warning shown, but Armor/Mr read 0/0) when this diagnostic incorrectly keyed off
            // `targetChampion` instead. Logging the outcome of this trigger's defender resolution
            // (real target + its armor/mr, or the fallback) turns that ambiguity into something a
            // future live-game log file can prove either way.
            //
            // usingSnapshotTarget (loop 38 continuation 19): a THIRD, explicit outcome — the
            // defender-side "virtual model" toggle (see TargetSnapshotStore) is ON for this combo
            // and a captured snapshot exists, so the defender is the FROZEN snapshot, not this
            // trigger's live resolution. Checked first so it is never confused with the fallback case.
            if (usingSnapshotTarget)
                Log($"defender using CAPTURED SNAPSHOT for combo '{comboId}': {targetChampion} (Armor={context.Defender.Armor:0.##}, Mr={context.Defender.Mr:0.##}, MaxHP={context.Defender.MaxHP:0.##}) — virtual target, NOT live resolution.");
            else if (defenderIsFallback)
                Log($"defender resolution FAILED -> FallbackDefender (Armor=0, Mr=0, MaxHP=1000) even though targetChampion='{targetChampion}'; this trigger's damage does NOT reflect any real target's resistances.");
            else
                Log($"defender resolved: {targetChampion} (Armor={context.Defender.Armor:0.##}, Mr={context.Defender.Mr:0.##}, MaxHP={context.Defender.MaxHP:0.##})");

            // 3b. Derive the command string from the USER-BUILT node order, BEFORE ApplyBinDamage
            //     expands curated skills into per-hit nodes (which would duplicate slots).
            string commandLabel = BuildCommandLabel(graph);
            // (M28 §3) Structured sequence (same order/skip rules as commandLabel) carrying each node's
            // above-floor knob, so the overlay can show the assumption visual language. Built from the
            // pre-expansion user graph, whose nodes still carry the UserConditionMet/UserDistanceFraction/
            // UserHitDurationSeconds/UserAttackCount/UserStackCount knobs.
            var sequence = BuildSequence(graph, championId);

            // 3c. Fill in REAL skill damage from the combo champion's BIN formulas + live
            // stats, replacing the zero-damage palette templates (see ApplyBinDamage).
            // (T1-c, M26 §6 Trace Mode) Begin/End bracket ONLY this main resolution — the M24
            // floor/max exposure re-runs below happen after End() and are therefore excluded
            // (intentional, see CLAUDE_CODE_TODO.md §15 T1-c).
            CalcTrace.Begin(comboId,
                $"attacker={championId} defender={targetChampion} armor={context.Defender.Armor:0.##} " +
                $"mr={context.Defender.Mr:0.##} maxHp={context.Defender.MaxHP:0.##} " +
                $"atkArmorPenFlat={context.Attacker.ArmorPenFlat:0.##} atkArmorPenPct={context.Attacker.ArmorPenPercent:0.####}");
            // (loop 179) armor-pen / lethality diagnostic (computed pre-Begin in BuildContext).
            if (_lethalityDiag is { Length: > 0 } lethalityDiag) CalcTrace.Row(lethalityDiag);
            var originalGraph = graph; // pre-expansion nodes ("Q_0") carrying the user's knob values
            graph = ApplyBinDamage(originalGraph, saved.ChampionId, snap, context.Defender.MaxHP);

            // 4. Run + publish. UI.COMBO_RESULT is a mapped HUD type in M02's TypeMap. The card
            //    needs the target + command string too, so publish the ComboHudResult wrapper.
            var result = _engine.Execute(graph, context);
            if (CalcTrace.End() is { } traceBlock) WriteCalcTrace(traceBlock);

            // 4b. (M24 P2/P3) Widen the uncertainty range for the knobbed axes (적중시간 duration +
            // 거리/충전 distance). The resolved result already carries RangeMin/Max == crit range (P1).
            // Re-resolve a MIN-exposure and a MAX-exposure variant (unset knobs pushed to their floor /
            // ceiling — see BuildExposureGraph) and take that scenario's crit-floor / crit-ceiling. Each
            // direction is skipped when no node qualifies, so a combo without knobbed hits pays nothing
            // and the resolved result is unchanged. (몇 대/per-attack has no curated cap — deferred.)
            double rangeMin = result.RangeMin, rangeMax = result.RangeMax;

            // (M24 P5) Assumed ENEMY DEFENSIVE runes (a user knob — the enemy's runes aren't
            // API-readable) make the target tankier, so they LOWER the floor. Applied to the
            // RangeMin computation's defender only; unset (no assumed runes) = none = no change, so
            // the resolved result and RangeMax stay favorable (the honest best case).
            var enemyDefRunes = ParseAssumedEnemyDefensiveRunes();
            var floorContext = enemyDefRunes.Count > 0
                ? context with { Defender = ComboEngine.ApplyDefensiveRunes(context.Defender, enemyDefRunes) }
                : context;
            var minGraph = BuildExposureGraph(originalGraph, saved.ChampionId, maximize: false);
            // (§76) Which cause moved the floor. The knob floor and the assumed-enemy-rune floor are
            // separate stories and the card has to name the one that actually moved the number, so the
            // knob floor is resolved against the OBSERVED defender first and the assumed-tankier
            // defender is a second, compared run — attribution by measurement, not by guessing. The
            // extra Execute only happens when enemy runes are genuinely assumed. The resulting
            // rangeMin is the min over the same set of candidates as before, so no number changes.
            bool floorFromAssumedEnemyRunes = false;
            if (minGraph is not null || enemyDefRunes.Count > 0)
            {
                var floorResolved = minGraph is null
                    ? graph
                    : ApplyBinDamage(minGraph, saved.ChampionId, snap, context.Defender.MaxHP);
                if (minGraph is not null)
                    rangeMin = Math.Min(rangeMin, _engine.Execute(floorResolved, context).RangeMin);
                if (enemyDefRunes.Count > 0)
                {
                    double withAssumedRunes = _engine.Execute(floorResolved, floorContext).RangeMin;
                    if (withAssumedRunes < rangeMin)
                    {
                        rangeMin = withAssumedRunes;
                        floorFromAssumedEnemyRunes = true;
                    }
                }
            }
            if (BuildExposureGraph(originalGraph, saved.ChampionId, maximize: true) is { } maxGraph)
                rangeMax = Math.Max(rangeMax,
                    _engine.Execute(ApplyBinDamage(maxGraph, saved.ChampionId, snap, context.Defender.MaxHP), context).RangeMax);

            // 4c. (M24 P4) Equipped CONDITIONAL amplifier runes (Coup de Grace / Cut Down / Last Stand /
            // PtA / First Strike) lift only the range CEILING: their ×(1+amp) applies only when the rune's
            // condition holds (target/caster HP, first-hit), which isn't reliably observable, so the
            // best case (all conditions met) multiplies RangeMax while RangeMin/TotalDamage stay
            // unamplified (the conservative floor). No amplifier equipped ⇒ ×1 ⇒ resolved result unchanged.
            double ampCeiling = 1.0;
            foreach (var runeId in snap.Stats.EquippedRuneIds ?? Array.Empty<int>())
            {
                if (ComboEngine.ConditionalAmplifierRuneAmps.TryGetValue(runeId, out double amp))
                    ampCeiling *= 1.0 + amp;
                // (M24 P4) Last Stand's condition (CASTER HP) is live-observable, so use the ACTUAL amp
                // from the caster's current HP rather than always assuming the max — 0 when healthy.
                else if (runeId == ComboEngine.LastStandRuneId)
                    ampCeiling *= 1.0 + ComboEngine.LastStandAmp(snap.Stats.CurrentHealth, snap.Stats.MaxHealth);
            }
            // (§76) Same attribution question as the floor: a ceiling lifted by an equipped amplifier
            // rune's best-case condition is an ASSUMED number and must say so on the card (P4).
            bool ceilingFromAmplifierRunes = ampCeiling > 1.0;
            if (ceilingFromAmplifierRunes)
                rangeMax *= ampCeiling;

            if (rangeMin != result.RangeMin || rangeMax != result.RangeMax)
                result = result with { RangeMin = rangeMin, RangeMax = rangeMax };

            // 4d. (M24 P9 wiring) Execute KILL-LINE — a threshold, SEPARATE from the damage range: if the
            // target's current HP is at/below it, the attacker's execute finishes it regardless of the
            // combo's damage. Rules are armed by the attacker's champion + built items
            // (ExecuteEffectsDb.SelectForAttacker); the highest active threshold wins (the target dies to
            // whichever executes at the higher HP). Honest supply (Appendix C): level+ability rank are
            // live, but champion STACK counts aren't API-readable, so Stacks=0 — a stack-GATED execute
            // (Syndra 100 / Smolder 225) stays OFF by default (conservative), and bonus-AD/lethality terms
            // (Pyke) are supplied 0 (conservative floor) since the base/bonus AD split isn't exposed.
            var activeRow = ResolveActive(snap);
            var builtItems = activeRow is null
                ? (IReadOnlyCollection<int>)Array.Empty<int>()
                : activeRow.ItemIds.Take(activeRow.ItemCount).ToArray();
            var executeRules = ExecuteEffectsDb.SelectForAttacker(saved.ChampionId, builtItems);
            if (executeRules.Count > 0)
            {
                var execCtx = new ExecuteContext { CasterLevel = snap.Level, AbilityRank = snap.Stats.AbilityR, Stacks = 0 };
                double execThr = 0; string? execLabel = null;
                foreach (var rule in executeRules)
                {
                    if (!ExecuteEvaluator.IsActive(rule, execCtx)) continue;
                    double thr = ExecuteEvaluator.ThresholdHp(rule, execCtx, context.Defender.MaxHP,
                        ap: snap.Stats.AbilityPower, bonusAd: 0, lethality: 0);
                    if (thr > execThr) { execThr = thr; execLabel = rule.Label; }
                }
                if (execThr > 0)
                    result = result with { ExecuteThresholdHP = execThr, ExecuteRuleLabel = execLabel };
            }

            // Record this REAL combo's damage against the missing-HP tracker — only for a live,
            // non-fallback, non-snapshot target (a FallbackDefender or a frozen virtual snapshot is
            // not a real champion we can attribute damage-since-respawn to). See TargetHealthTracker's
            // class doc comment for why this is an honest LOWER-BOUND estimate, never "perfect".
            if (recordDamage && _targetHealthTracker is not null && !defenderIsFallback && !usingSnapshotTarget
                && !string.IsNullOrEmpty(targetChampion))
            {
                _targetHealthTracker.RecordDamageDealt(targetChampion, result.TotalDamage);
            }

            // loop-38 continuation 9: the resist values are confirmed real/nonzero and displayed
            // damage still exactly matches the raw ability tooltip (no reduction observed) — the one
            // remaining unverified link is what DamageType each node actually carries AT EXECUTION
            // TIME. Surface it directly (distinct types across the graph's nodes) so the next test is
            // conclusive: "Magic" confirms the type is right and the bug is deeper in DamageEngine's
            // math itself; "Physical"/"True"/empty would catch a type-assignment bug directly.
            string damageTypesSummary = string.Join("+", graph.Nodes.Select(n => n.DamageType.ToString()).Distinct());
            var hud = new ComboHudResult(result, targetChampion, commandLabel,
                TargetArmor: context.Defender.Armor, TargetMr: context.Defender.Mr,
                DefenderIsFallback: defenderIsFallback, DamageTypesSummary: damageTypesSummary,
                UsingSnapshotTarget: usingSnapshotTarget,
                TargetMaxHp: context.Defender.MaxHP, TargetLevel: targetLevel,
                DesignatedCleared: designatedCleared, Sequence: sequence,
                // (issue: overlay skill icons) the combo's champion, so the card can draw the real
                // P/Q/W/E/R ability icons; (issue: combo name field) the saved combo's display name.
                CasterChampion: championId, ComboName: saved.Name,
                // (§76) Why the range is as wide as it is — see ComboHudResult's own doc comment.
                FloorFromAssumedEnemyRunes: floorFromAssumedEnemyRunes,
                CeilingFromAmplifierRunes: ceilingFromAmplifierRunes);
            if (publish)
            {
                EventBus.EventBus.Publish(HudEventType, hud, source: "ComboRunner");
                _shownComboId = comboId; // toggle-off check above compares against this
            }
            return hud;
        }
        catch (Exception ex)
        {
            // A bad combo/snapshot must never crash the app.
            Log($"combo trigger failed: {ex}");
            return null;
        }
        }
    }

    /// <summary>(loop 487) The number the combo card would show, for a sequence the user is still
    /// building. The editor had no damage display at all: every knob added since loop 471 — the
    /// sweet-spot and upgrade toggles, the wall checkbox, the stack and distance and exposure dials —
    /// changed a number nobody could see until they pressed the hotkey in a live game.
    ///
    /// <para>Two bases, and the caller is told which one it got. IN GAME the live snapshot is used
    /// exactly as a real trigger would, so the preview IS the card. OUT OF GAME there is nothing to
    /// read, so a stated REFERENCE stands in: the champion's own base attack damage grown to level 18
    /// (real Data Dragon growth, not a guess), no items, no runes, every ability at max rank, against
    /// the same zero-resistance 1000 HP dummy <see cref="FallbackDefender"/> already defines. Nothing
    /// in it is invented — it is base data and stated zeroes — but it is a reference, not a
    /// prediction, and <see cref="ComboPreview.IsLive"/> says so.</para>
    ///
    /// <para>Never publishes and never touches the toggle state, so previewing over a live game
    /// cannot make a card appear.</para>
    /// </summary>
    public ComboPreview? ComputePreview(string championId, ComboGraph graph)
    {
        if (string.IsNullOrWhiteSpace(championId) || graph is null || graph.Nodes.Count == 0) return null;
        try
        {
            var live = _currentSnapshot();
            bool isLive = live is { HasData: true };
            var snap = isLive ? live : BuildReferenceSnapshot(championId);
            if (snap is null) return null;

            var saved = new SavedCombo(PreviewComboId, championId, string.Empty, _engine.Serialize(graph));
            var hud = ComputeAndPublish(PreviewComboId, recordDamage: false, publish: false,
                snapshotOverride: snap, savedOverride: saved);
            if (hud is null) return null;

            return new ComboPreview(hud.Result.TotalDamage, hud.Result.RangeMin, hud.Result.RangeMax,
                isLive, hud.TargetChampion, ReferenceLevel);
        }
        catch (Exception ex)
        {
            Log($"combo preview failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The combo id previews compute under. Never written to config — it exists so the log
    /// lines a preview produces are identifiable as previews.</summary>
    private const string PreviewComboId = "__preview";

    /// <summary>The level the out-of-game reference is stated at. 18 because it is the one level at
    /// which "every ability at max rank" — which is what the rank fallback already does with no
    /// ability data — is not also a lie about the character sheet.</summary>
    public const int ReferenceLevel = 18;

    /// <summary>The out-of-game stand-in described on <see cref="ComputePreview"/>: this champion at
    /// <see cref="ReferenceLevel"/> with its real base attack damage and nothing else, and no enemy
    /// row at all so defender resolution lands on <see cref="FallbackDefender"/>. Returns null for a
    /// champion the repository does not know.</summary>
    private static GameSnapshot? BuildReferenceSnapshot(string championId)
    {
        if (ChampionRepository.Get(championId) is not { } champion) return null;

        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "preview",
            Level = ReferenceLevel,
            PlayerCount = 1,
            Stats = new ActivePlayerStats
            {
                // Real base growth from Data Dragon. AP and every item stat are ZERO and stated as
                // such — a reference build would be a guess about the user's game.
                AttackDamage = LevelGrowth.Stat(champion.BaseStats.Ad, champion.StatsPerLevel.Ad, ReferenceLevel),
                MaxHealth = LevelGrowth.Stat(champion.BaseStats.Hp, champion.StatsPerLevel.Hp, ReferenceLevel),
                CurrentHealth = LevelGrowth.Stat(champion.BaseStats.Hp, champion.StatsPerLevel.Hp, ReferenceLevel),
                // Ability ranks are deliberately left at 0: HasAbilityData is then false, which is the
                // documented "combo-editor preview / spectator" path where ChooseRank falls back to
                // each skill's MaxRank instead of reading a rank nobody has chosen.
            },
        };
        snap.Players[0].SummonerName = "preview";
        snap.Players[0].ChampionName = championId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = ReferenceLevel;
        return snap;
    }

    /// <summary>(T1-d, M26 §6 Trace Mode) Sink for one CalcTrace block: append to
    /// <c>logs/calc_trace.log</c> next to the assembly (always on, no config flag — combo triggers
    /// are a low-volume user action). Truncates the file once it nears ~1MB rather than growing it
    /// unbounded. try/catch makes any I/O failure harmless — a combo trigger must never fail because
    /// its trace couldn't be written.</summary>
    private static void WriteCalcTrace(string block)
    {
        try
        {
            const long MaxBytes = 1_000_000;
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "calc_trace.log");
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                File.Delete(path);
            File.AppendAllText(path, block + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Trace logging must never crash or block a combo trigger.
        }
    }

    /// <summary>(loop 117) LIVE refresh: re-computes the currently-shown combo against the latest
    /// snapshot and re-publishes it, so the card updates in real time when stats change WITHOUT the
    /// user re-pressing the combo hotkey. Fired by the attacker's/target's <c>GAME.PLAYER_LEVEL_UP</c>,
    /// <c>GAME.ITEM_CHANGED</c>, <c>GAME.CHAMPION_DIED</c>/<c>_RESPAWNED</c> events and a
    /// <c>targeting.*</c> config change (⇄ re-pin). No-op when no card is shown, or when the card was
    /// toggled/cleared off (so a refresh never resurrects a dismissed card). Never re-runs the
    /// toggle-off branch — that stays exclusive to a real hotkey press in <see cref="OnComboTrigger"/>.
    /// These events arrive on the poll thread; <see cref="Overlay.OverlayCoordinator"/> guards the
    /// UI.* handoff with a lock (its Thread-safety note), so re-publishing here is safe.</summary>
    private void RefreshShownCombo()
    {
        var id = _shownComboId;
        if (string.IsNullOrEmpty(id)) return;
        if (_overlayCoordinator is { } c && !c.IsActive(HudEventType)) return; // dismissed → don't resurrect
        ComputeAndPublish(id!, recordDamage: false);
    }

    /// <summary>
    /// Replaces each SKILL node's zero-damage palette template with REAL, correctly-typed,
    /// multi-hit damage computed from the COMBO CHAMPION's curated per-skill map
    /// (<see cref="SkillDamageDb"/>) evaluated against its live BIN spell formulas
    /// (<see cref="SkillDamage.ComputeCalcDamage"/>). This is what turns the combo-result HUD's
    /// "총 데미지" from 0 into an accurate number.
    ///
    /// EXPANSION: a curated skill is expanded into one node PER (hit × count) — each carrying
    /// that hit's own damage <see cref="ComboDamageType"/> and its raw BIN number (with the
    /// ratio fields zeroed, since the number is already fully resolved). The existing M05
    /// <see cref="DamageEngine"/> then mitigates each expanded node by its own type
    /// (Physical→armor, Magic→MR, True→none) and sums them — so a multi-hit or mixed-type
    /// skill (e.g. Ahri Q's magic-out + true-return) is reflected correctly with no
    /// double-mitigation. Expanded node ids stay unique ("{id}#h{n}c{k}").
    ///
    /// Skill nodes are keyed by slot id ("Q"/"W"/"E"/"R"); the editor stores them as
    /// "{slot}_{n}". A champion outside M11's cached set keeps template damage (0); a cached
    /// champion with no curated file for a slot falls back to the legacy single-hit heuristic
    /// (keeping the node's template type) so the HUD still shows something. Non-skill nodes
    /// (AA/item/rune) pass through untouched.
    /// </summary>
    /// <param name="defenderMaxHp">The resolved target's max HP, needed to turn a %-max-HP curated
    /// hit into its flat number at build time (MaxHP is constant across a combo). %current/%missing
    /// hits do not use it — they become M05 execute nodes re-evaluated against live HP.</param>
    private static ComboGraph ApplyBinDamage(ComboGraph graph, string comboChampionId, GameSnapshot snap, double defenderMaxHp)
    {
        var champion = ChampionRepository.Get(comboChampionId);
        if (champion is null) return graph; // not in the cached 5 -> template damage (0)

        int level = Math.Max(1, snap.Level);
        // (GOLDEN #3 round 3) Resolved once — constant across every node in this combo (same active
        // player state regardless of which hit is being expanded). See ResolveCountedAttackSpeedPercent.
        double countedAsPercent = ResolveCountedAttackSpeedPercent(snap, champion, level);
        var rewritten = new List<ComboNode>(graph.Nodes.Count);
        bool changed = false;

        // Gather the champion's intrinsic bonus effects that apply combo-wide (on every AA for
        // onHit, on every ability for onAbility) once. Each is remembered with the slot whose BIN
        // spell holds its calc (an on-hit passive lives under "P"). Self-triggered bonuses stay
        // with their own slot and are applied per-node below.
        var onHitBonuses = new List<BonusSpec>();
        var onAbilityBonuses = new List<BonusSpec>();
        foreach (var bonusSlot in SkillDamageDb.BonusSourceSlots(champion.Id))
        {
            var effects = SkillDamageDb.GetBonusEffects(champion.Id, bonusSlot);
            if (effects is null) continue;
            foreach (var effect in effects)
            {
                foreach (var hit in effect.Hits)
                {
                    if (effect.Trigger == BonusTrigger.OnHit)
                        onHitBonuses.Add(new BonusSpec(bonusSlot, hit, effect.AlwaysOn, effect.MaxProcs));
                    else if (effect.Trigger == BonusTrigger.OnAbility) onAbilityBonuses.Add(new BonusSpec(bonusSlot, hit));
                }
            }
        }

        // Gather the ACTIVE player's built proc items (their visible build, P1). On-hit items add a
        // hit to every AA; spellblade adds ONE shared proc on an ability→AA transition (see below).
        // Numbers resolve live from the item BIN via the same live stats (a dedicated resolver that
        // also maps BASE AD, which spellblade — mStatFormula==1 — scales on; skill resolver has no
        // base-AD case). A non-cached combo champion returned above, so base AD is available here.
        var (onHitItems, spellbladeShot, stackThenConsume) = ResolveBuildProcs(snap, champion, level);
        // ── SPELLBLADE APPROXIMATION RULE (assistive estimate, P4) ───────────────────────────
        // Spellblade (Sheen/Trinity/Lich Bane) is a single SHARED unique passive: at most one proc
        // every ~1.5s. A landed combo lasts ~1-2s, so AT MOST ONE spellblade proc realistically
        // lands per combo. Rule: apply exactly ONE spellblade proc, on the FIRST auto-attack that
        // FOLLOWS an ability cast in the combo sequence (the ability→empowered-AA transition). If
        // several spellblade items are somehow built they do NOT stack in-game (one shared passive),
        // so we still apply only one — the largest single proc (see ResolveBuildProcs). No ability
        // precedes any AA ⇒ no proc.
        bool spellbladeApplied = false;
        bool abilityCastSeen = false;

        // (§10 damage-bug fix) The ABILITY SLOTS (Q/W/E/R) that have already been cast earlier in this
        // combo. A P-slot onHit bonus is an ALWAYS-ON passive (applies to every AA), but a Q/W/E/R-slot
        // onHit bonus is an EMPOWERED auto-attack that only fires AFTER that ability is cast (e.g. Locke's
        // Q "쏘울네일"). Applying an ability-slot onHit to a plain AA that never followed its ability
        // massively overcounts (the reported "평타 1대 243" bug). Populated as ability nodes execute below.
        var castSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── STACK-THEN-CONSUME APPROXIMATION RULE (Kraken Slayer, assistive estimate, P4) ────
        // Kraken Slayer's "Bring It Down" stacks on every on-hit AA (up to 2) for a REAL 3-second
        // window and consumes on the 3rd; this combo calculator has no wall-clock model of the
        // combo's cast/travel timing, only the AA's ORDINAL POSITION within the sequence being
        // built. We therefore count AAs 1-indexed within this single combo and trigger the bonus
        // on every (stacksRequired+1)th one (3rd, 6th, 9th... for Kraken Slayer's stacksRequired=2)
        // — i.e. we ASSUME every AA in the combo lands inside the real 3-second stack window. This
        // is a reasonable approximation for a short burst combo (the same class of simplification
        // as the spellblade "at most one proc per combo" rule above) but would overcount stacks in
        // an unrealistically long/slow combo whose AAs are actually >3s apart. Documented in
        // item_effects.json's per-item _note too.
        int aaOrdinal = 0;

        // (loop 477) How many attacks each capped on-hit rider has already ridden. An
        // empowered-next-attack is spent when it lands: Rengar's Savagery stab is ONE strike,
        // not a buff on every following auto, and Evelynn's mark is three. Keyed by the rider's
        // own slot, since that is what identifies it.
        var onHitProcs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // (M22 Phase 4) K'Sante-style stance: an R node toggles "All Out" for SUBSEQUENT Q/W/E nodes
        // (from an R until the next R), so those nodes resolve their All-Out variant hit
        // (SkillHit.AllOutCalc/AllOutBinSpell) instead of the base one. Inert for every champion whose
        // curated hits don't set All-Out fields — the flag simply has no effect there.
        bool allOut = false;

        foreach (var node in graph.Nodes)
        {
            // Auto-attack node: the palette template carries 0 damage. The Live Client API gives
            // no base/bonus AD split and no crit state, so a basic attack lands total AD as
            // physical damage (M05 then mitigates it by the target's armor). This fills the
            // "기본공격 = 0" gap the user reported.
            if (node.NodeType == ComboNodeType.Aa)
            {
                rewritten.Add(node with
                {
                    Damage = snap.Stats.AttackDamage,
                    RatioAD = 0,
                    RatioBonusAD = 0,
                    RatioAP = 0,
                    DamageType = ComboDamageType.Physical,
                });
                changed = true;
                // (M23 Phase 2 Step 3) ONE per-AA bonus-effect pass: each source below is gated by
                // its classified BonusEffect Condition (see ConditionMet) instead of an ad-hoc
                // inline bool/modulo check, but still dispatches to the SAME leaf resolvers
                // (AppendBonusHits/AppendItemHit) with the SAME arguments/order as before this
                // refactor — a pure control-flow unification (M23 archetypes A5/A6/A7), zero
                // output change. See docs/modules/M23_PHASE2_REFACTOR_SPEC.md.
                //
                // On-hit passives (e.g. Warwick's magic on-hit) add extra, own-typed hits per AA — but
                // ONLY the applicable ones. Always-on passives fire on every AA: a P-slot onHit (the
                // passive slot) or an ability-slot onHit explicitly flagged AlwaysOn (e.g. Varus W
                // Blight, Kayle E passive half, Vi W Denting Blows). A plain (un-flagged) Q/W/E/R-slot
                // onHit is an EMPOWERED auto that fires only after that ability was cast earlier in this
                // combo (castSlots) — this gates e.g. Locke's Q nail / Viktor's Q discharge off a plain AA.
                var applicableOnHit = onHitBonuses.Count == 0
                    ? onHitBonuses
                    : onHitBonuses.Where(b =>
                        b.AlwaysOn || b.CalcSlot.Equals("P", StringComparison.OrdinalIgnoreCase) || castSlots.Contains(b.CalcSlot)).ToList();
                // (loop 477) …and only while it has procs left. A cap applies to the empowered-attack
                // class only: an AlwaysOn or P-slot passive really does fire on every attack.
                if (applicableOnHit.Count > 0)
                    applicableOnHit = applicableOnHit.Where(b =>
                        b.MaxProcs <= 0 || b.AlwaysOn
                        || b.CalcSlot.Equals("P", StringComparison.OrdinalIgnoreCase)
                        || onHitProcs.GetValueOrDefault(b.CalcSlot) < b.MaxProcs).ToList();
                if (applicableOnHit.Count > 0)
                {
                    AppendBonusHits(rewritten, node, champion, applicableOnHit, snap.Stats, level, defenderMaxHp, "onHit");
                    foreach (var b in applicableOnHit)
                        if (b.MaxProcs > 0)
                            onHitProcs[b.CalcSlot] = onHitProcs.GetValueOrDefault(b.CalcSlot) + 1;
                }
                // On-hit ITEMS (Nashor/Guinsoo) add their proc to every auto-attack, each mitigated
                // by its own type (Nashor magic vs MR) independently of the AA's physical mitigation.
                foreach (var item in onHitItems)
                    AppendItemHit(rewritten, node, item);
                // Spellblade (A7 OnHitEmpowered): this AA is empowered only if an ability was cast
                // before it in the combo, and only once (shared unique passive, see
                // ResolveBuildProcs's doc comment).
                if (spellbladeShot is { } shot
                    && ConditionMet(SpellbladeCondition, abilityCastSeen, spellbladeApplied, aaOrdinal: 0))
                {
                    AppendItemHit(rewritten, node, shot);
                    spellbladeApplied = true;
                }
                // Stack-then-consume (A6 EveryNth, Kraken Slayer): every (stacksRequired+1)th AA in
                // the combo's own sequence consumes the stacks — see the approximation rule above
                // this loop.
                aaOrdinal++;
                if (stackThenConsume is { } stc
                    && ConditionMet(new Condition(ConditionType.EveryNth, stc.StacksRequired + 1),
                        abilityCastSeen: false, alreadyApplied: false, aaOrdinal))
                {
                    AppendItemHit(rewritten, node, stc.Proc);
                }
                // User-attached bonus effects on this AA node (manual sub-icons, T3.3/T8).
                AppendBonusHits(rewritten, node, champion, UserBonuses(node), snap.Stats, level, defenderMaxHp);
                continue; // (changed already true)
            }

            // Summoner-spell node (currently only Ignite): level-scaled TRUE damage read from
            // data/summoner_effects.json (DDragon/CommunityDragon expose no numeric value; this mirrors
            // rune_effects.json's hand-authored-from-official precedent). No ratios and TRUE damage, so
            // no mitigation. The saved combo can't store the number (it depends on the player's LIVE
            // champion level), so it is resolved here at trigger time from snap.Level. A summoner name
            // not covered by the map falls through inert (kept as its 0-damage template node).
            if (node.NodeType == ComboNodeType.Summoner)
            {
                if (SummonerEffectDb.DamageAtLevel(node.Name, level) is double summonerDmg)
                {
                    rewritten.Add(node with
                    {
                        Damage = summonerDmg,
                        RatioAD = 0,
                        RatioBonusAD = 0,
                        RatioAP = 0,
                        DamageType = ComboDamageType.True,
                    });
                    changed = true;
                }
                else
                {
                    rewritten.Add(node);
                }
                continue;
            }

            // The combo editor stores sequence-node ids as "{slot}_{n}" (e.g. "Q_0", "W_1") so a
            // skill can appear more than once; recover the bare slot before matching it to data.
            var slot = SlotOf(node.Id);
            // Expand a Skill node when its slot is a canonical ability (Q/W/E/R/P) OR (M22) when it
            // is a curated EXTRA slot (a transform/stance/weapon/sub-spell skill, e.g. Jayce
            // "QCannon") that carries curated hits — the latter has an underscore-free slot key so
            // SlotOf recovers it intact from a "{slot}_{n}" sequence id.
            // A user-placed Passive node (P) expands the same way as a Skill node: the curation's P
            // hits (e.g. Talon Blade's End 'BleedDamage') are dealt directly when the user adds P to
            // the combo (the simple, user-driven model — the 3-stack proc condition is the player's
            // call, not auto-inferred). Fixes P computing 0 (it was gated on NodeType==Skill only).
            // (loop 480) …or an extra slot that carries only RIDERS. Hwei's WE grants an empowered-
            // attack bonus and deals nothing itself, so a hits-only test read it as not a cast at all
            // and it never armed anything.
            bool isExpandableSkill = (node.NodeType == ComboNodeType.Skill || node.NodeType == ComboNodeType.Passive)
                && (IsAbilitySlot(slot) || SkillDamageDb.GetHits(champion.Id, slot) is not null
                    || SkillDamageDb.GetBonusEffects(champion.Id, slot) is not null);
            if (!isExpandableSkill)
            {
                rewritten.Add(node);
                // A non-ability node (item/rune/execute) can still carry user-attached bonus effects.
                changed |= AppendBonusHits(rewritten, node, champion, UserBonuses(node), snap.Stats, level, defenderMaxHp);
                continue;
            }

            // A real ability cast (not the passive) arms the next AA's spellblade proc and any
            // empowered-attack rider that ability grants.
            //
            // (loop 480) A muse/form SUB-CAST counts. Hwei's WE is as much a W cast as Jayce's
            // QCannon is a Q cast, and gating on the canonical five alone meant a rider curated on
            // the sub-cast that actually grants it could never fire.
            //
            // (loop 482) Only the sub-cast's OWN key goes in, not the parent letter as well. The
            // parent was speculative generality and it actively misfires: Camille's Q and Q2 each
            // empower one attack, so letting a Q2 cast also arm Q's rider would empower two attacks
            // for one cast. A rider curated on a base ability is armed by casting that ability.
            if (!slot.Equals("P", StringComparison.OrdinalIgnoreCase))
            {
                abilityCastSeen = true;
                castSlots.Add(slot);
            }

            var hits = SkillDamageDb.GetHits(champion.Id, slot);
            int castCount = SkillDamageDb.GetCastCount(champion.Id, slot);
            var expanded = hits is not null
                ? ExpandCuratedSkill(node, champion, slot, hits, snap.Stats, level, defenderMaxHp, castCount, allOut, countedAsPercent)
                : null;
            if (expanded is not null)
            {
                rewritten.AddRange(expanded);
                changed = true;
            }
            // Legacy fallback: champion cached but slot not curated (or curated hits all
            // failed to resolve, e.g. an unlearned/rank-0 ability during isolated single-skill
            // testing). Loop-38 fix: this used to silently keep the palette template's own
            // DamageType, which is only a HEURISTIC GUESS from tooltip HTML markup
            // (DDragonParser.GuessDamageType) and defaults to Physical for any ambiguous
            // "MIXED" tooltip — a real user report (single-E combo, damage exactly matched the
            // raw tooltip number regardless of the target's real, confirmed-nonzero MR) traced to
            // exactly this: curated hits existed for the slot but failed to evaluate, the raw
            // damage was still computed correctly via the heuristic path below, but the node kept
            // whatever (possibly wrong) type the template had, making a correctly-computed number
            // look unmitigated. Prefer the CURATED hit's type (authoritative, hand-verified data)
            // whenever curated hits exist for this slot at all, even if none of them resolved.
            // (golden #16, Ivern phantom-W fix) A BONUS-ONLY curated slot (entry exists, zero
            // direct hits — Ivern W Brushmaker) is an authoritative "this cast deals no direct
            // damage": the heuristic must not run, because the BIN shell may carry the RIDER's
            // own calc as a plausible candidate (IvernW.TotalDamage IS the brush-attack rider,
            // and the fallback double-counted it as phantom cast damage).
            // (loop 483) …and the same is true of a slot whose every hit is a PURE ON/OFF conditional
            // (a curated condition with no baseline calc). Skarner E deals nothing at all unless the
            // grabbed target hits terrain, so an unticked node resolving to no hits is the correct
            // answer, not a failure to resolve. Without this the heuristic ran and picked PinDamage —
            // the very wall damage the toggle exists to gate — handing back the full number.
            else if (!(hits is null && SkillDamageDb.IsSlotCurated(champion.Id, slot))
                && !(hits is { Length: > 0 } && hits.All(h => h.IsConditional && string.IsNullOrEmpty(h.Calc)))
                && SkillDamage.ComputeNodeDamage(champion, slot, snap.Stats, level) is double damage)
            {
                var fallbackType = hits is { Length: > 0 } ? MapHitType(hits[0].Type) : node.DamageType;
                if (CalcTrace.IsCollecting)
                    CalcTrace.Row($"node={node.Id} branch=legacy-fallback type={fallbackType}");
                rewritten.Add(node with { Damage = damage, DamageType = fallbackType });
                changed = true;
            }
            else
            {
                rewritten.Add(node);
            }

            // Append this node's bonus contributions: the slot's own self-triggered effects,
            // plus (for a real ability cast, not the passive) any on-ability effects.
            var selfBonuses = SelfBonuses(champion.Id, slot);
            if (selfBonuses.Count > 0)
                changed |= AppendBonusHits(rewritten, node, champion, selfBonuses, snap.Stats, level, defenderMaxHp, "self");
            if (!slot.Equals("P", StringComparison.OrdinalIgnoreCase) && onAbilityBonuses.Count > 0)
                changed |= AppendBonusHits(rewritten, node, champion, onAbilityBonuses, snap.Stats, level, defenderMaxHp, "onAbility");

            // User-attached bonus effects on this skill node (manual sub-icons, T3.3/T8). Merged with
            // the curated ones above via the same AppendBonusHits path (no double-count: curated and
            // user lists are disjoint sources), so a manual bonus adds to — never replaces — them.
            var userBonuses = UserBonuses(node);
            if (userBonuses.Count > 0)
                changed |= AppendBonusHits(rewritten, node, champion, userBonuses, snap.Stats, level, defenderMaxHp);

            // (M22 Phase 4) Flip the All-Out stance AFTER resolving this node, so the R cast itself
            // resolves normally and only the nodes that FOLLOW it (until the next R) are All-Out.
            if (slot.Equals("R", StringComparison.OrdinalIgnoreCase)) allOut = !allOut;
        }

        return changed ? graph with { Nodes = rewritten } : graph;
    }

    /// <summary>The self-triggered bonus hits curated on one slot, paired with that slot as the
    /// calc source.</summary>
    private static List<BonusSpec> SelfBonuses(string championId, string slot)
    {
        var result = new List<BonusSpec>();
        var effects = SkillDamageDb.GetBonusEffects(championId, slot);
        if (effects is null) return result;
        foreach (var effect in effects)
            if (effect.Trigger == BonusTrigger.Self)
                foreach (var hit in effect.Hits)
                    result.Add(new BonusSpec(slot, hit));
        return result;
    }

    /// <summary>The bonus hits a user manually attached to <paramref name="node"/> in the editor
    /// (<see cref="ComboNode.UserBonusEffects"/>), each paired with the champion skill slot whose BIN
    /// spell holds its calc so the number resolves live. Empty when the node has no manual effects.</summary>
    private static List<BonusSpec> UserBonuses(ComboNode node)
    {
        var result = new List<BonusSpec>();
        if (node.UserBonusEffects is null) return result;
        foreach (var attached in node.UserBonusEffects)
            foreach (var hit in attached.Effect.Hits)
                result.Add(new BonusSpec(attached.Slot, hit));
        return result;
    }

    /// <summary>Expands each bonus spec into its per-(hit×count) nodes — each with the bonus hit's
    /// own damage type and its raw BIN number (evaluated against the spec's calc-source slot) — and
    /// appends them, tagged "{node.Id}#bonus{n}c{k}" so they stay distinct and per-type mitigated.
    /// Bonus hits whose calc can't be resolved are skipped (documented, never crash).</summary>
    private static bool AppendBonusHits(
        List<ComboNode> into, ComboNode node, ChampionData champion,
        IReadOnlyList<BonusSpec> bonuses, ActivePlayerStats stats, int level, double defenderMaxHp,
        string trigger = "user")
    {
        bool added = false;
        for (int n = 0; n < bonuses.Count; n++)
        {
            var (calcSlot, hit) = (bonuses[n].CalcSlot, bonuses[n].Hit);
            if (!TryBuildHitShape(hit, champion, calcSlot, stats, level, defenderMaxHp, node.UserHitDurationSeconds,
                    node.UserAttackCount, node.UserDistanceFraction, allOut: false, out double dmg, out ComboExecuteType execType, out double execPercent, node.UserConditionMet, node.UserStackCount ?? 0))
                continue;

            // (T1-c, M26 §6 Trace Mode) One row per resolved bonus hit, before it's replicated into
            // `count` per-count nodes below (the count copies are the same calc/damage repeated).
            if (CalcTrace.IsCollecting)
                CalcTrace.Row($"node={node.Id} branch=bonus trigger={trigger} calc={hit.Calc} slot={calcSlot}");

            int count = Math.Max(1, hit.Count);
            var shares = TypeShares(hit, dmg, execPercent);
            for (int c = 0; c < count; c++)
            for (int s = 0; s < shares.Count; s++)
            {
                into.Add(node with
                {
                    Id = shares.Count > 1 ? $"{node.Id}#bonus{n}c{c}s{s}" : $"{node.Id}#bonus{n}c{c}",
                    Damage = shares[s].Damage,
                    RatioAD = 0,
                    RatioBonusAD = 0,
                    RatioAP = 0,
                    DamageType = shares[s].Type,
                    ExecuteType = execType,
                    ExecutePercent = shares[s].ExecutePercent,
                    CanCrit = hit.CanCrit,
                    CritDamageScalar = hit.CritDamageScalar,
                    HpBelowGate = hit.HpBelowGate,
                });
                added = true;
            }
        }
        return added;
    }

    /// <summary>(M23 Phase 2 Step 3) The classified condition for a spellblade-style proc — see
    /// <see cref="BonusEffectNormalizers.FromItemEffect"/>'s <c>ItemTrigger.Spellblade</c> case.
    /// Constructed once since it carries no per-item data (unlike EveryNth's per-item interval).</summary>
    private static readonly Condition SpellbladeCondition = new(ConditionType.OnHitEmpowered, 0);

    /// <summary>(M23 Phase 2 Step 3) Evaluates a normalized <see cref="Condition"/> against the
    /// AA-node dispatch state, replacing the ad-hoc bool-latch/modulo checks that used to live
    /// inline in <see cref="ApplyBinDamage"/>. Null condition = Always (every skill on-hit/
    /// on-ability/self bonus and every plain item on-hit proc). <see cref="ConditionType.OnHitEmpowered"/>
    /// = spellblade: fires only after an ability cast and only once, latched by the caller's own
    /// <paramref name="alreadyApplied"/> flag (the shared unique-passive rule). <see cref="ConditionType.EveryNth"/>
    /// = Kraken Slayer-style stack-then-consume: fires when the already-incremented, 1-indexed
    /// <paramref name="aaOrdinal"/> is exactly divisible by <see cref="Condition.Value"/> (the
    /// StacksRequired+1 interval). <paramref name="abilityCastSeen"/>/<paramref name="aaOrdinal"/>
    /// are unused by conditions that don't need them (pass placeholder <c>false</c>/<c>0</c>).</summary>
    private static bool ConditionMet(Condition? condition, bool abilityCastSeen, bool alreadyApplied, int aaOrdinal)
    {
        if (condition is null) return true;
        return condition.Type switch
        {
            ConditionType.OnHitEmpowered => abilityCastSeen && !alreadyApplied,
            ConditionType.EveryNth => condition.Value > 0 && aaOrdinal % (int)condition.Value == 0,
            _ => true,
        };
    }

    /// <summary>(M25 §11.G) Resolves an AUTO-RESOLVABLE condition from the active player's own live
    /// snapshot (only kinds classified not-UserAssumed reach here). ResourceGte/ManaGte compare the
    /// current resource (fury/ferocity/energy/mana, GameSnapshot.ResourceValue) to the threshold.</summary>
    private static bool ResolveAutoCondition(ConditionType type, double value, ActivePlayerStats stats)
        => type switch
        {
            ConditionType.ResourceGte or ConditionType.ManaGte => stats.ResourceValue >= value,
            _ => false, // conservative unmet — a UserAssumed kind should never be routed here
        };

    /// <summary>A bonus hit plus the skill slot whose BIN spell holds its calc.</summary>
    private readonly record struct BonusSpec(string CalcSlot, SkillHit Hit, bool AlwaysOn = false,
        int MaxProcs = 0);

    /// <summary>Expands one curated skill node into its per-(cast×hit×count) nodes, each with the
    /// hit's curated damage type and resolved raw BIN number. <paramref name="castCount"/> &gt; 1
    /// (see <see cref="SkillDamageDb.GetCastCount"/>) repeats the WHOLE hit set that many times —
    /// one real cast per repetition, each independently computed and mitigated — for a recastable
    /// skill (e.g. Ahri R "Spirit Rush") that appears only once in the combo's node sequence.
    /// Every repetition resolves the same live BIN calc, matching the documented approximation that
    /// each recast lands on the same target. Returns null if NONE of the hits could be evaluated
    /// (so the caller can fall back to the heuristic).</summary>
    private static List<ComboNode>? ExpandCuratedSkill(
        ComboNode node, ChampionData champion, string slot, SkillHit[] hits,
        ActivePlayerStats stats, int level, double defenderMaxHp, int castCount = 1, bool allOut = false,
        double countedAsPercent = 0)
    {
        var result = new List<ComboNode>();
        for (int cast = 0; cast < Math.Max(1, castCount); cast++)
        {
            for (int h = 0; h < hits.Length; h++)
            {
                var hit = hits[h];
                if (!TryBuildHitShape(hit, champion, slot, stats, level, defenderMaxHp, node.UserHitDurationSeconds,
                        node.UserAttackCount, node.UserDistanceFraction, allOut, out double dmg, out ComboExecuteType execType, out double execPercent, node.UserConditionMet, node.UserStackCount ?? 0))
                    continue; // unresolvable calc/DataValue -> drop this hit (documented)

                // (T1-c, M26 §6 Trace Mode) One row per resolved curated hit, before it's replicated
                // into `count` per-count nodes below (the count copies are the same calc repeated).
                // (§19) The All-Out W charge rider is called out as its own branch so its TRUE line is
                // unmistakable in the trace (the requested "TRUE 라이더 별도 행"); the engine row for it
                // additionally carries type=True.
                if (CalcTrace.IsCollecting)
                    CalcTrace.Row(hit.IsChargeScaled
                        ? $"node={node.Id} branch=charge-rider type={hit.Type} slot={slot}"
                        : $"node={node.Id} branch=curated calc={hit.Calc} slot={slot}");

                // (GOLDEN #3 round 3) Attack-speed-scaled strike count (Garen E) — see
                // SkillHit.AttackSpeedStrikeStep's doc comment. Null (the vast majority of hits) ->
                // the static Count, unchanged.
                int count = hit.AttackSpeedStrikeStep is > 0
                    ? Math.Max(1, hit.Count) + (int)Math.Floor(countedAsPercent / hit.AttackSpeedStrikeStep.Value)
                    : Math.Max(1, hit.Count);
                var shares = TypeShares(hit, dmg, execPercent);
                for (int c = 0; c < count; c++)
                for (int s = 0; s < shares.Count; s++)
                {
                    result.Add(node with
                    {
                        Id = shares.Count > 1
                            ? $"{node.Id}#cast{cast}h{h}c{c}s{s}"
                            : $"{node.Id}#cast{cast}h{h}c{c}",
                        Damage = shares[s].Damage,
                        RatioAD = 0,
                        RatioBonusAD = 0,
                        RatioAP = 0,
                        DamageType = shares[s].Type,
                        ExecuteType = execType,
                        ExecutePercent = shares[s].ExecutePercent,
                        CanCrit = hit.CanCrit,
                        CritDamageScalar = hit.CritDamageScalar,
                        HpBelowGate = hit.HpBelowGate,
                    });
                }
            }
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Resolves one curated <see cref="SkillHit"/> into the fields a combo node needs: a flat
    /// <paramref name="damage"/> number plus an optional execute type/percent. Shared by the
    /// direct-skill and bonus-effect expansion paths so both honor %HP hits identically.
    ///
    /// A DURATION-SCALED hit (<see cref="SkillHit.IsDurationScaled"/> — an escapable persistent
    /// zone/DoT, e.g. Malzahar W) is checked FIRST and resolves independently of everything below:
    /// see the branch's own inline comment. <paramref name="userHitDurationSeconds"/> is the node's
    /// <see cref="ComboNode.UserHitDurationSeconds"/>, threaded through from both call sites.
    ///
    /// A %HP hit (it names a <see cref="SkillHit.HpPercentDataValue"/> and/or a
    /// <see cref="SkillHit.HpPercentCalc"/>, or carries a literal <see cref="SkillHit.HpPercent"/>
    /// fallback) resolves its FRACTION live per rank — first from the BIN DataValue via
    /// <see cref="SkillDamage.ResolveHpPercent"/>, else from the BIN GameCalculation via
    /// <see cref="SkillDamage.ResolveHpPercentCalc"/> (for %HP mechanics that live only inside a
    /// formula tree, e.g. Skarner P), else the literal fallback — then is shaped by its
    /// <see cref="SkillHit.HpBasis"/>: <see cref="HpBasis.Max"/>
    /// becomes a flat number (fraction × <paramref name="defenderMaxHp"/>, since max HP is constant
    /// across a combo) with no execute type, so the M05 engine still mitigates it by the hit's own
    /// damage type (a %-max-HP TRUE hit like Vayne W therefore bypasses resist, a MAGIC one like
    /// Brand P is cut by MR); <see cref="HpBasis.Current"/> / <see cref="HpBasis.Missing"/> become
    /// M05 <see cref="ComboExecuteType.CurrentHp"/> / <see cref="ComboExecuteType.MissingHp"/>
    /// execute nodes carrying the fraction, which the engine re-evaluates against the target's live
    /// HP as the combo ticks. A non-%HP hit resolves its flat number from the BIN
    /// <see cref="SkillHit.Calc"/> exactly as before. Returns false — drop the hit — when the %HP
    /// fraction can't be resolved (and no literal fallback), or a non-%HP calc can't be resolved.
    /// </summary>
    private static bool TryBuildHitShape(
        SkillHit hit, ChampionData champion, string calcSlot, ActivePlayerStats stats, int level,
        double defenderMaxHp, double? userHitDurationSeconds, int? userAttackCount,
        double? userDistanceFraction, bool allOut,
        out double damage, out ComboExecuteType execType, out double execPercent,
        bool? userConditionMet = null, int userStackCount = 0)
    {
        if (!TryBuildHitShapeCore(hit, champion, calcSlot, stats, level, defenderMaxHp,
                userHitDurationSeconds, userAttackCount, userDistanceFraction, allOut,
                out damage, out execType, out execPercent, userConditionMet, userStackCount))
            return false;

        // (loop 471) Per-cast ramp: a later cast of the same ability scales whatever shape the
        // branches below produced. Applied HERE, around them, so it composes with every hit shape
        // (plain calc, %HP, conditional) instead of being repeated inside each. An execute hit
        // carries its magnitude in execPercent, so that is what scales for those.
        if (hit.IsRamped)
        {
            string rampSlot = string.IsNullOrEmpty(hit.BinSpell) ? calcSlot : hit.BinSpell!;
            if (SkillDamage.ComputeFlatDataValue(champion, rampSlot, hit.RampDataValue!, stats, level,
                    rankSlot: calcSlot) is not double step)
                return false;   // the ramp is part of the claim; unresolvable means drop the hit
            double factor = 1 + hit.RampSteps * step;
            damage *= factor;
            execPercent *= factor;
        }
        return true;
    }

    private static bool TryBuildHitShapeCore(
        SkillHit hit, ChampionData champion, string calcSlot, ActivePlayerStats stats, int level,
        double defenderMaxHp, double? userHitDurationSeconds, int? userAttackCount,
        double? userDistanceFraction, bool allOut,
        out double damage, out ComboExecuteType execType, out double execPercent,
        bool? userConditionMet = null, int userStackCount = 0)
    {
        damage = 0;
        execType = ComboExecuteType.None;
        execPercent = 0;

        // (§19, GOLDEN_02 §7) An All-Out-ONLY hit (the K'Sante W charge rider) does not exist outside
        // the All-Out stance — drop it so the base-form skill is byte-identical to before this hit was
        // curated (the golden-invariant for every non-All-Out combo). Distinct from AllOutCalc below,
        // which only swaps an existing hit's calc; this whole hit is absent unless allOut is set.
        if (hit.AllOutOnly && !allOut) return false;

        // (§16 P-round) The inverse: a BASE-stance-only hit (K'Sante's base-form P mark) is dropped once
        // All-Out is active, because All-Out REPLACES it with a different rider (an AllOutOnly hit) — the
        // two are mutually exclusive by stance.
        if (hit.BaseStanceOnly && allOut) return false;

        // (M22 Phase 4) While the combo is in the K'Sante All-Out stance (set by a preceding R node —
        // see the node loop), a hit that declares an All-Out variant resolves against THOSE fields
        // instead of its base ones. Inert when allOut is false or the hit has no All-Out override.
        bool useAllOut = allOut && !string.IsNullOrEmpty(hit.AllOutCalc);
        string effCalc = useAllOut ? hit.AllOutCalc! : hit.Calc;

        // (M22) Resolve every calc/DataValue below against the hit's BinSpell override when set
        // (a transform/stance/weapon/sub-spell object exposed by ChampionBinParser.ParseExtraSpells),
        // otherwise against the curated slot exactly as before. One local so all resolvers agree.
        // In All-Out, an AllOutBinSpell (if given) takes precedence over the base BinSpell.
        string calcSlotEff = useAllOut && !string.IsNullOrEmpty(hit.AllOutBinSpell) ? hit.AllOutBinSpell!
            : (string.IsNullOrEmpty(hit.BinSpell) ? calcSlot : hit.BinSpell!);

        // (M25 §11.G) Conditional-bonus hit: the condition switches this hit from its baseline effCalc
        // (UNMET = the conservative RangeMin anchor, P2) to MetCalc (MET = RangeMax). An AutoResolvable
        // condition (the caster's own resource) reads live stats; a UserAssumed one (enemy/positional
        // state the Live Client API can't see) reads the node's UserConditionMet knob and defaults to
        // UNMET. This only SELECTS which of the two calcs to resolve; the picked calc then evaluates via
        // the ordinary ComputeCalcDamage path. Checked first since it resolves from its own calc fields.
        if (hit.IsConditional && Enum.TryParse<ConditionType>(hit.ConditionType, out var condType))
        {
            // (loop 472) An explicit user assertion wins for EITHER class of condition. For a
            // UserAssumed one that is the only source there has ever been. For an AutoResolvable one
            // (own resource) the live reading stays the default — unset behaves exactly as before —
            // but the editor can now say "assume 50 fury" while standing in the shop.
            bool met = userConditionMet ?? (!ConditionResolution.IsUserAssumed(condType)
                && ResolveAutoCondition(condType, hit.ConditionValue, stats));
            // (loop 479) A MET half expressed as a %HP fraction (MetHpPercentCalc) resolves through the
            // %HP shape below instead of being read as flat damage — 15% of missing health is not 0.15
            // damage. The UNMET half keeps the ordinary flat path: every existing conditional hit has a
            // flat baseline or none at all.
            if (met && !string.IsNullOrEmpty(hit.MetHpPercentCalc))
            {
                if (SkillDamage.ResolveHpPercentCalc(champion, calcSlotEff, hit.MetHpPercentCalc!, stats, level, userStackCount, rankSlot: calcSlot) is not double metFrac || metFrac <= 0)
                    return false;
                switch (hit.HpBasis)
                {
                    case HpBasis.Current: execType = ComboExecuteType.CurrentHp; execPercent = metFrac; break;
                    case HpBasis.Missing: execType = ComboExecuteType.MissingHp; execPercent = metFrac; break;
                    default: damage = metFrac * defenderMaxHp; break;
                }
                return true;
            }
            string condCalc = met ? (hit.MetCalc ?? string.Empty) : effCalc;
            if (SkillDamage.ComputeCalcDamage(champion, calcSlotEff, condCalc, stats, level, userStackCount, rankSlot: calcSlot) is double condDmg)
            {
                damage = condDmg;
                return true;
            }
            return false;
        }

        // (loop 484) Tiered stack CONSUMPTION: N stacks land the base calc N times, raised by a
        // per-tier bonus percentage read live from the BIN. Locke's Soul Nails, whose whole point is
        // that saving nails is worth more than spending them one at a time. An unset knob is one
        // stack — the plain base calc, byte-identical to what this hit resolved to before.
        if (hit.IsStackTiered)
        {
            int stacks = Math.Clamp(userStackCount <= 0 ? 1 : userStackCount, 1, hit.MaxStackTier);
            if (SkillDamage.ComputeCalcDamage(champion, calcSlotEff, effCalc, stats, level, userStackCount, rankSlot: calcSlot) is not double perStack)
                return false;
            double bonus = 0;
            if (stacks >= 2
                && SkillDamage.ComputeFlatDataValue(champion, calcSlotEff, hit.StackTierBonusDataValues![stacks - 2], stats, level, rankSlot: calcSlot) is double tierRaw)
                // Same >=1-is-a-percentage reading as the %HP resolvers: TwoMarkBonusPercent is 20.
                bonus = tierRaw >= 1 ? tierRaw / 100.0 : tierRaw;
            damage = perStack * stacks * (1 + bonus);
            return true;
        }

        // (M22 Phase 3) Per-attack SUMMON hit: the user states how many times the pet/summon hits
        // (ComboNode.UserAttackCount), and damage = per-hit BIN number × that count. Unset/0 -> 0, the
        // same honest default as the duration-scaled hit (a summon's total output is player-uptime-
        // dependent, not a fixed cast number). Resolved before the %HP/plain branches from its own field.
        if (!string.IsNullOrEmpty(hit.PerAttackCalc))
        {
            int attacks = Math.Max(0, userAttackCount ?? 0);
            if (attacks == 0) return true; // damage stays 0 (honest unset default)
            if (SkillDamage.ComputeCalcDamage(champion, calcSlotEff, hit.PerAttackCalc!, stats, level, userStackCount, rankSlot: calcSlot) is double perAttack)
            {
                damage = perAttack * attacks;
                return true;
            }
            return false;
        }

        // (§19, GOLDEN_02 §7 — K'Sante All-Out W TRUE rider) Charge-scaled hit: TRUE damage
        // = f × baseRaw, where baseRaw is THIS hit's own flat (FlatDataValue) + %maxHP (HpPercentCalc)
        // terms — a self-contained copy of the base W damage on the pre-All-Out-reduction resists (the
        // live snapshot stats, which the engine never reduces) — and f ramps ChargeMinDataValue(0.10)→
        // ChargeMaxDataValue(0.80) by the charge knob (UserDistanceFraction, the M28 §1 charge slider):
        //   f = lerp(Min, Max, clamp01((t − MinChargeTime)/(TimeToFullCharge − MinChargeTime))).
        // An UNSET knob → t=0 → the Min floor (P2-conservative default, GOLDEN_02 §7); t ≥ TimeToFull
        // → capped at Max. Every ramp constant is a live BIN DataValue lookup (Hard Rule). Checked
        // before the %HP/plain branches: those base terms live on this hit but are consumed HERE, not
        // as a standalone hit. Only reached while allOut (an All-Out-only hit — gated at the top).
        if (hit.IsChargeScaled)
        {
            double baseRaw = 0;
            if (!string.IsNullOrEmpty(hit.FlatDataValue)
                && SkillDamage.ComputeFlatDataValue(champion, calcSlotEff, hit.FlatDataValue!, stats, level, rankSlot: calcSlot) is double chFlat)
                baseRaw += chFlat;
            if (!string.IsNullOrEmpty(hit.HpPercentCalc)
                && SkillDamage.ResolveHpPercentCalc(champion, calcSlotEff, hit.HpPercentCalc!, stats, level, userStackCount, rankSlot: calcSlot) is double chFrac)
                baseRaw += chFrac * defenderMaxHp;
            if (baseRaw <= 0) return false; // base W didn't resolve -> drop the rider rather than emit 0×?

            if (SkillDamage.ComputeFlatDataValue(champion, calcSlotEff, hit.ChargeMinDataValue!, stats, level, rankSlot: calcSlot) is not double chMin
                || SkillDamage.ComputeFlatDataValue(champion, calcSlotEff, hit.ChargeMaxDataValue!, stats, level, rankSlot: calcSlot) is not double chMax
                || SkillDamage.ComputeFlatDataValue(champion, calcSlotEff, hit.ChargeTimeMinDataValue!, stats, level, rankSlot: calcSlot) is not double chTMin
                || SkillDamage.ComputeFlatDataValue(champion, calcSlotEff, hit.ChargeTimeFullDataValue!, stats, level, rankSlot: calcSlot) is not double chTFull)
                return false;

            double t = userDistanceFraction ?? 0.0; // unset knob -> floor (Min), GOLDEN_02 §7
            double window = chTFull - chTMin;
            double chargeFrac = window > 0 ? Math.Clamp((t - chTMin) / window, 0, 1) : (t >= chTFull ? 1 : 0);
            damage = (chMin + chargeFrac * (chMax - chMin)) * baseRaw;
            return true;
        }

        // (M24 P3) Distance/charge-scaled hit (e.g. Hecarim E charge, Fizz R throw, Nidalee Q spear):
        // the real damage interpolates between a MIN-distance calc (SkillHit.MinCalc, the floor) and a
        // MAX-distance calc (SkillHit.MaxCalc, the ceiling) by how far the user says it travelled
        // (UserDistanceFraction 0-1). An UNSET knob resolves to Calc — the RESOLVED ANCHOR, i.e. the end
        // the champion was already curated at (Hecarim E = "MaxDamage" full charge; Fizz R / Nidalee Q =
        // their conservative min end) — so migrating to distance-scaled changes NO resolved number
        // (value identity). The uncertainty RANGE [min, max] is produced separately by ComboRunner's
        // exposure-graph (min-exposure -> knob 0, max-exposure -> knob 1). Resolved before the %HP/plain
        // branches.
        if (hit.IsDistanceScaled)
        {
            if (userDistanceFraction is { } uf)
            {
                if (SkillDamage.ComputeCalcDamage(champion, calcSlotEff, hit.MinCalc!, stats, level, userStackCount, rankSlot: calcSlot) is not double min
                    || SkillDamage.ComputeCalcDamage(champion, calcSlotEff, hit.MaxCalc!, stats, level, userStackCount, rankSlot: calcSlot) is not double max)
                    return false;
                damage = min + Math.Clamp(uf, 0, 1) * (max - min);
                return true;
            }
            // Unset knob -> the resolved anchor (Calc), unchanged from before the migration.
            if (SkillDamage.ComputeCalcDamage(champion, calcSlotEff, effCalc, stats, level, userStackCount, rankSlot: calcSlot) is not double resolved)
                return false;
            damage = resolved;
            return true;
        }

        // Duration-scaled hit (an escapable persistent zone/DoT, e.g. Malzahar W "Null Zone" — see
        // SkillHit.PerSecondHpPercent's doc comment): PARTIAL damage proportional to how many seconds
        // the USER manually says the target was actually exposed, clamped to the ability's real max
        // duration. Unset/0 seconds -> 0 damage, the same honest default as this project's prior
        // convention of omitting such a hit entirely rather than assuming full duration (P2). Checked
        // first and returns before the %HP branch below, since a duration-scaled hit resolves from its
        // own independent fields, not HpPercentDataValue/HpPercentCalc/HpPercent.
        if (hit.IsDurationScaled)
        {
            double seconds = Math.Clamp(userHitDurationSeconds ?? 0, 0, hit.MaxDurationSeconds!.Value);
            // FLAT per-second DoT (PerSecondCalc, e.g. Heimerdinger Q turret): resolve the per-second
            // number LIVE from the BIN so it tracks patch + AP, then scale by exposure seconds. Tried
            // before the %HP rate. If the calc can't resolve, drop the hit (return false) rather than
            // silently emit a %HP number it was never meant to be.
            if (!string.IsNullOrEmpty(hit.PerSecondCalc))
            {
                if (SkillDamage.ComputeCalcDamage(champion, calcSlotEff, hit.PerSecondCalc!, stats, level, userStackCount, rankSlot: calcSlot) is double perSec)
                {
                    damage = perSec * seconds;
                    return true;
                }
                return false;
            }
            // (§27 Nasus R) BIN-sourced, rank-scaled PER-SECOND %maxHP DoT rate, scaled by exposure
            // seconds and target max HP — same shape as the literal PerSecondHpPercent, but the rate is a
            // live rank-scaled BIN lookup (DataValue or calc). Tried before the literal; an unresolved
            // source drops the hit (P2 honest default, never emit a fabricated number).
            if (!string.IsNullOrEmpty(hit.PerSecondHpPercentDataValue) || !string.IsNullOrEmpty(hit.PerSecondHpPercentCalc))
            {
                double? frac = !string.IsNullOrEmpty(hit.PerSecondHpPercentDataValue)
                    ? SkillDamage.ResolveHpPercent(champion, calcSlotEff, hit.PerSecondHpPercentDataValue!, stats, level, rankSlot: calcSlot)
                    : SkillDamage.ResolveHpPercentCalc(champion, calcSlotEff, hit.PerSecondHpPercentCalc!, stats, level, userStackCount, rankSlot: calcSlot);
                if (frac is not > 0)
                    return false;
                damage = frac.Value * defenderMaxHp * seconds;
                return true;
            }
            damage = hit.PerSecondHpPercent!.Value * defenderMaxHp * seconds;
            return true;
        }

        bool isHpHit = !string.IsNullOrEmpty(hit.HpPercentDataValue) || !string.IsNullOrEmpty(hit.HpPercentCalc) || hit.HpPercent > 0;
        if (isHpHit)
        {
            double? fraction = null;
            if (!string.IsNullOrEmpty(hit.HpPercentDataValue))
                fraction = SkillDamage.ResolveHpPercent(champion, calcSlotEff, hit.HpPercentDataValue!, stats, level, rankSlot: calcSlot);
            if (fraction is not > 0 && !string.IsNullOrEmpty(hit.HpPercentCalc))
                fraction = SkillDamage.ResolveHpPercentCalc(champion, calcSlotEff, hit.HpPercentCalc!, stats, level, userStackCount, rankSlot: calcSlot);
            if (fraction is not > 0 && hit.HpPercent > 0)
                fraction = hit.HpPercent; // documented last-resort literal fallback
            if (fraction is not > 0)
                return false; // %HP source unresolved and no usable fallback -> drop the hit

            double frac = fraction.Value;
            switch (hit.HpBasis)
            {
                case HpBasis.Current:
                    execType = ComboExecuteType.CurrentHp;
                    execPercent = frac;
                    break;
                case HpBasis.Missing:
                    execType = ComboExecuteType.MissingHp;
                    execPercent = frac;
                    break;
                default: // Max: a flat-per-cast number, still mitigated by the hit's own type.
                    damage = frac * defenderMaxHp;
                    break;
            }
            return true;
        }

        if (SkillDamage.ComputeCalcDamage(champion, calcSlotEff, effCalc, stats, level, userStackCount, rankSlot: calcSlot) is double raw)
        {
            damage = raw;
            // (loop 480) …and if the curation says this base GROWS with the target's missing health
            // (Hwei QW's BonusDamageMult), hand M05 the Kraken-Slayer shape so it re-reads the target's
            // health where the hit actually lands instead of freezing a pre-combo fraction. A scalar
            // that will not resolve leaves the plain base rather than dropping a real hit.
            if (!string.IsNullOrEmpty(hit.MissingHpBonusDataValue)
                && SkillDamage.ComputeFlatDataValue(champion, calcSlotEff, hit.MissingHpBonusDataValue!, stats, level, rankSlot: calcSlot) is > 0 and double scalar)
            {
                execType = ComboExecuteType.BaseWithMissingHpBonus;
                execPercent = scalar;
            }
            return true;
        }

        // FlatDataValue fallback (see SkillHit.FlatDataValue's doc comment — Garen R's flat base
        // term is the motivating case): a plain rank-indexed BIN DataValue with no separately-
        // resolvable Calc of its own. Tried only when Calc was empty/unresolvable above.
        if (!string.IsNullOrEmpty(hit.FlatDataValue)
            && SkillDamage.ComputeFlatDataValue(champion, calcSlotEff, hit.FlatDataValue!, stats, level, rankSlot: calcSlot) is double flatRaw)
        {
            damage = flatRaw;
            return true;
        }

        return false;
    }

    /// <summary>(loop 497) The type-share(s) one resolved hit contributes. Normally one — the hit's
    /// own type carrying all of it. For a hit that declares a <see cref="SkillHit.SplitType"/> it is
    /// two halves, so each half meets the resistance that actually applies to it; typing the whole
    /// hit as either one mitigates half the damage against the wrong resist.
    ///
    /// <para>An execute-shaped hit carries its magnitude in the percent rather than in the damage, so
    /// both are halved and whichever one is live gets the right split.</para></summary>
    private static List<(double Damage, double ExecutePercent, ComboDamageType Type)> TypeShares(
        SkillHit hit, double damage, double executePercent)
    {
        if (hit.SplitType is not { } split || split == hit.Type)
            return new() { (damage, executePercent, MapHitType(hit.Type)) };
        return new()
        {
            (damage / 2.0, executePercent / 2.0, MapHitType(hit.Type)),
            (damage / 2.0, executePercent / 2.0, MapHitType(split)),
        };
    }

    private static ComboDamageType MapHitType(HitDamageType type) => type switch
    {
        HitDamageType.Physical => ComboDamageType.Physical,
        HitDamageType.Magic => ComboDamageType.Magic,
        HitDamageType.True => ComboDamageType.True,
        _ => ComboDamageType.Magic,
    };

    /// <summary>A resolved item proc: its owning item id, its (curated) damage type, appended as one
    /// extra AA hit mitigated by its own type — plus either a flat <paramref name="Raw"/> damage
    /// number (the ordinary case: on-hit/spellblade items evaluated from the BIN) or an
    /// <see cref="ComboExecuteType.CurrentHp"/> <paramref name="ExecuteType"/>/<paramref name="ExecutePercent"/>
    /// pair (Blade of the Ruined King's Mist's Edge — see <see cref="BuildTargetHpItemProc"/>), which
    /// M05 re-evaluates against the target's LIVE current HP as the combo ticks, exactly like a skill
    /// %-current-HP hit (Vayne W/Brand P/Skarner P). <see cref="ExecuteType"/> defaults to
    /// <see cref="ComboExecuteType.None"/> so every existing (non-%HP) item proc is unaffected.</summary>
    private readonly record struct ItemProc(
        string ItemId, double Raw, HitDamageType Type,
        ComboExecuteType ExecuteType = ComboExecuteType.None, double ExecutePercent = 0);

    /// <summary>Melee/ranged classification threshold for item procs whose value differs by attack
    /// type (Blade of the Ruined King, Titanic Hydra). Data Dragon's <c>attackrange</c> field clusters
    /// melee champions at 125-175 and ranged champions at 400-650+; verified this session against the
    /// cached champion.json summary: Zed 125, Darius/Garen/Aatrox 175 (melee) vs Jinx 525, Ahri 550,
    /// Annie 625, Ashe 600, Caitlyn 650 (ranged). 300 sits cleanly in the gap between the two clusters.
    /// An unresolvable AttackRange (0 — e.g. a champion outside both the cached-detail and Data Dragon
    /// summary sets) classifies as melee, since 0 &lt;= threshold; documented here rather than silently
    /// zeroing the proc, but honestly this is a guess for that edge case, not a measured value.</summary>
    private const double MeleeRangeThreshold = 300.0;

    /// <summary>True if <paramref name="champion"/>'s published Data Dragon attack range places it in
    /// the melee cluster (see <see cref="MeleeRangeThreshold"/>).</summary>
    private static bool IsMelee(ChampionData champion) => champion.BaseStats.AttackRange <= MeleeRangeThreshold;

    /// <summary>Resolves the ACTIVE player's built proc items into ready-to-append hits: the list
    /// of on-hit procs (Nashor/Guinsoo/Wit's End/Blade of the Ruined King/Titanic Hydra — added to
    /// every AA), the single spellblade proc to use (largest, since Sheen-line items share ONE
    /// unique passive), and the stack-then-consume proc to use (Kraken Slayer — Riot's own "Limited
    /// to 1 Kraken Slayer" rule means at most one is ever built, so no largest-wins tiebreak is
    /// needed here). Each ordinary number is evaluated live from the item BIN via
    /// <see cref="ItemEffectDb"/> + <see cref="FormulaInterpreter"/>; a melee/ranged %-HP item
    /// (<see cref="ItemHpPercentBasis"/> != None) instead resolves via
    /// <see cref="BuildTargetHpItemProc"/>/<see cref="BuildCasterHpItemProc"/>; a stack-then-consume
    /// item resolves via <see cref="BuildStackThenConsumeItemProc"/>. An unresolvable proc is
    /// dropped. Empty/null when no active row or no covered proc item is built.</summary>
    private static (List<ItemProc> OnHit, ItemProc? Spellblade, (ItemProc Proc, int StacksRequired)? StackThenConsume) ResolveBuildProcs(
        GameSnapshot snap, ChampionData champion, int level)
    {
        var onHit = new List<ItemProc>();
        ItemProc? spellblade = null;
        (ItemProc Proc, int StacksRequired)? stackThenConsume = null;

        var active = ResolveActive(snap);
        if (active is null) return (onHit, null, null);

        var resolver = BuildItemStatResolver(snap.Stats, champion, level);
        for (int j = 0; j < active.ItemCount && j < active.ItemIds.Length; j++)
        {
            var effect = ItemEffectDb.Get(active.ItemIds[j].ToString());
            if (effect is null) continue;

            if (effect.Trigger == ItemTrigger.StackThenConsume)
            {
                if (stackThenConsume is null && BuildStackThenConsumeItemProc(effect, champion, level) is { } scProc)
                    stackThenConsume = (scProc, effect.StacksRequired!.Value);
                continue;
            }

            ItemProc? proc = effect.HpPercentBasis switch
            {
                ItemHpPercentBasis.TargetCurrent => BuildTargetHpItemProc(effect, champion),
                ItemHpPercentBasis.CasterMax => BuildCasterHpItemProc(effect, champion, snap.Stats),
                _ => ComputeItemProc(effect, resolver, snap.Stats) is double raw
                    ? new ItemProc(effect.ItemId, raw, effect.DamageType)
                    : null,
            };
            if (proc is not { } p) continue;

            if (effect.Trigger == ItemTrigger.OnHit)
                onHit.Add(p);
            else if (spellblade is null || p.Raw > spellblade.Value.Raw) // shared passive: keep the largest
                spellblade = p;
        }
        return (onHit, spellblade, stackThenConsume);
    }

    /// <summary>(GOLDEN #3 round 3, docs/reports/golden/GOLDEN_03_GAREN.md §5) The active player's
    /// ITEM + LEVEL-GROWTH bonus attack speed, in WHOLE PERCENT POINTS (15.0 = +15%) — feeds
    /// <see cref="SkillHit.AttackSpeedStrikeStep"/>. Deliberately EXCLUDES rune/stat-shard attack
    /// speed (see that field's doc comment for the full rationale and the L1 regression this exists
    /// to prevent — the Live Client API has no way to decompose a total bonus-AS reading into its
    /// item/growth/rune parts, so this is reconstructed from two independently-readable sources
    /// instead of trusting any such total):
    /// <list type="bullet">
    /// <item>Item AS: summed from each of the active player's built items' Data Dragon
    /// <see cref="ItemStats.AttackSpeedPercent"/> (a 0-1 fraction, ×100 here to match this method's
    /// whole-percent return unit).</item>
    /// <item>Growth AS: the champion's own per-level growth, via the SAME curve
    /// <see cref="LevelGrowth.Stat"/> already applies to HP/AD/Armor/MR (baseValue=0 — Data Dragon's
    /// <see cref="ChampionStatsPerLevel.AttackSpeed"/> is itself a growth-ONLY percentage, e.g. 3.65,
    /// not a flat-to-base delta like the other stats' per-level fields — the curve's own baseValue=0
    /// input reduces it to exactly the growth term, matching the additive-percentage-points
    /// semantics this whole feature needs).</item>
    /// </list>
    /// Returns 0 (item AS 0, growth AS naturally 0 at level 1) when no active player row resolves.</summary>
    private static double ResolveCountedAttackSpeedPercent(GameSnapshot snap, ChampionData champion, int level)
    {
        var active = ResolveActive(snap);
        if (active is null) return 0;

        double itemAsPercent = 0;
        for (int j = 0; j < active.ItemCount && j < active.ItemIds.Length; j++)
            itemAsPercent += (ItemRepository.Get(active.ItemIds[j].ToString())?.Stats.AttackSpeedPercent ?? 0) * 100.0;

        double growthAsPercent = LevelGrowth.Stat(0, champion.StatsPerLevel.AttackSpeed, level);
        return itemAsPercent + growthAsPercent;
    }

    /// <summary>Kraken Slayer's Bring It Down: a flat melee/ranged BASE number that linearly
    /// interpolates the wiki's level-1/level-18 endpoints by the ACTIVE player's live level — the
    /// same BaseAtLevel1/BaseAtLevel18 shape <see cref="Runes.RuneEffectDb.Evaluate"/> already uses
    /// for manual runes — PLUS, when the item carries a <see cref="ItemEffect.MissingHpBonusScalar"/>
    /// (Kraken Slayer's real "increased by 0%-75% based on target's missing health" clause), an
    /// <see cref="ComboExecuteType.BaseWithMissingHpBonus"/> execute node so M05 scales that base
    /// number up by the LIVE target's missing-health fraction at the moment the proc lands — the
    /// same dynamic-live-HP convention as Blade of the Ruined King's CurrentHp mechanism. Melee/ranged
    /// is the ACTIVE player's own champion, same as every other melee/ranged item proc (see
    /// <see cref="IsMelee"/>). The BASE number resolves once per combo (constant across it, like
    /// Titanic Hydra's Cleave) — the PER-AA gating (every 3rd AA) is applied by the caller
    /// (<see cref="ApplyBinDamage"/>'s <c>aaOrdinal</c> counter), not here.</summary>
    private static ItemProc? BuildStackThenConsumeItemProc(ItemEffect effect, ChampionData champion, int level)
    {
        bool melee = IsMelee(champion);
        double? lvl1 = melee ? effect.MeleeDamageAtLevel1 : effect.RangedDamageAtLevel1;
        double? lvl18 = melee ? effect.MeleeDamageAtLevel18 : effect.RangedDamageAtLevel18;
        if (lvl1 is not > 0 || lvl18 is not > 0) return null;

        double damage = InterpolateByLevel(lvl1.Value, lvl18.Value, level);
        if (effect.MissingHpBonusScalar is > 0)
            return new ItemProc(effect.ItemId, damage, effect.DamageType,
                ComboExecuteType.BaseWithMissingHpBonus, effect.MissingHpBonusScalar.Value);
        return new ItemProc(effect.ItemId, damage, effect.DamageType);
    }

    /// <summary>Linear level-1→level-18 interpolation, the standard shape for a LoL "X (based on
    /// level)" range. Mirrors <see cref="Runes.RuneEffectDb.Evaluate"/>'s levelBase term exactly.</summary>
    private static double InterpolateByLevel(double atLevel1, double atLevel18, int level)
    {
        double clamped = Math.Clamp(level, 1, 18);
        return atLevel1 + (atLevel18 - atLevel1) / 17.0 * (clamped - 1);
    }

    /// <summary>Blade of the Ruined King's Mist's Edge: a fraction (melee/ranged, per the active
    /// player's own champion) of the TARGET's CURRENT hp at the moment of the hit — not a frozen
    /// snapshot. Carries no flat <see cref="ItemProc.Raw"/>; instead sets
    /// <see cref="ComboExecuteType.CurrentHp"/> so M05 re-evaluates it against the defender's live,
    /// decreasing HP exactly like a skill %-current-HP hit (see <see cref="TryBuildHitShape"/>).</summary>
    private static ItemProc? BuildTargetHpItemProc(ItemEffect effect, ChampionData champion)
    {
        double? pct = IsMelee(champion) ? effect.MeleeHpPercent : effect.RangedHpPercent;
        if (pct is not > 0) return null;
        return new ItemProc(effect.ItemId, 0, effect.DamageType, ComboExecuteType.CurrentHp, pct.Value);
    }

    /// <summary>Titanic Hydra's Cleave (on-hit portion only — see item_effects.json's per-item _note
    /// for what is out of scope): a fraction (melee/ranged) of the CASTER's own max HP. Unlike Blade
    /// of the Ruined King this scales off the attacker, not the target, so it is constant across the
    /// combo and resolves to a flat <see cref="ItemProc.Raw"/> once, like any other on-hit item.</summary>
    private static ItemProc? BuildCasterHpItemProc(ItemEffect effect, ChampionData champion, ActivePlayerStats stats)
    {
        double? pct = IsMelee(champion) ? effect.MeleeHpPercent : effect.RangedHpPercent;
        if (pct is not > 0) return null;
        return new ItemProc(effect.ItemId, pct.Value * stats.MaxHealth, effect.DamageType);
    }

    /// <summary>Appends one item proc as an own-typed extra hit on <paramref name="aaNode"/>, tagged
    /// "{aaNode.Id}#item{itemId}" so it stays distinct and per-type mitigated by the M05 engine. A
    /// proc's <see cref="ItemProc.ExecuteType"/>/<see cref="ItemProc.ExecutePercent"/> pass through
    /// unchanged (default None/0 for every ordinary item, so this is a no-op for the 6 pre-existing
    /// items — see <see cref="ItemProc"/>).</summary>
    private static void AppendItemHit(List<ComboNode> into, ComboNode aaNode, ItemProc proc)
    {
        // (T1-c, M26 §6 Trace Mode)
        if (CalcTrace.IsCollecting)
            CalcTrace.Row($"node={aaNode.Id} branch=item id={proc.ItemId}");

        into.Add(aaNode with
        {
            Id = $"{aaNode.Id}#item{proc.ItemId}",
            Damage = proc.Raw,
            RatioAD = 0,
            RatioBonusAD = 0,
            RatioAP = 0,
            DamageType = MapHitType(proc.Type),
            ExecuteType = proc.ExecuteType,
            ExecutePercent = proc.ExecutePercent,
        });
    }

    /// <summary>Evaluates an item proc's raw single-target number from its BIN calc against the live
    /// stats, or null when it references data that can't be resolved (dropped, never crash).</summary>
    private static double? ComputeItemProc(ItemEffect eff, Func<int, int?, double> resolver, ActivePlayerStats stats)
    {
        try { return FormulaInterpreter.Evaluate(eff.Skill, eff.Calc, rank: 1, resolver, stats.ResourceMax); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The stat resolver for ITEM procs. Same shape as <see cref="SkillDamage.BuildStatResolver"/>
    /// (id 0 → AP; id 1 → Armor; id 2 → total AD, or BONUS AD when mStatFormula==2; id 6 → Magic
    /// Resist; id 12 → total Health, or BONUS Health when mStatFormula==2 — see
    /// <see cref="SkillDamage.BuildStatResolver"/>'s remarks for the Tahm Kench citation; no current
    /// <c>item_effects.json</c> entry uses id 12, so this is a parity/forward-compat addition, not a
    /// behavior change for any existing item), PLUS the case the skill resolver has no reason to
    /// model: mStatFormula==1 → BASE AD, which spellblade items scale on (Sheen 100% / Trinity 200%
    /// / Lich Bane 75% BASE AD — BIN mStat=2, mStatFormula=1). Base AD = the combo champion's base +
    /// per-level AD at <paramref name="level"/>; bonus AD = total − base. Any other id is unmapped
    /// and throws <see cref="KeyNotFoundException"/> (see <see cref="SkillDamage.BuildStatResolver"/>
    /// remarks) so an unresolvable item calc is dropped rather than silently computed with 0.</summary>
    private static Func<int, int?, double> BuildItemStatResolver(ActivePlayerStats stats, ChampionData champion, int level)
    {
        double baseAd = champion.BaseStats.Ad + champion.StatsPerLevel.Ad * (Math.Max(1, level) - 1);
        double bonusAd = Math.Max(0.0, stats.AttackDamage - baseAd);
        double baseHp = champion.BaseStats.Hp + champion.StatsPerLevel.Hp * (Math.Max(1, level) - 1);
        double bonusHp = Math.Max(0.0, stats.MaxHealth - baseHp);
        return (statId, statFormula) => statId switch
        {
            0 => stats.AbilityPower,
            1 => stats.Armor,
            2 => statFormula == 1 ? baseAd
               : statFormula == 2 ? bonusAd
               : stats.AttackDamage,
            6 => stats.MagicResist,
            12 => statFormula == 2 ? bonusHp : stats.MaxHealth,
            _ => throw new KeyNotFoundException($"Unmapped BIN mStat id '{statId}' — no live stat available"),
        };
    }

    /// <summary>The bare skill slot from a combo-node id: the editor writes "{slot}_{n}"
    /// (e.g. "Q_0") to allow a skill to repeat; palette/raw ids ("Q") pass through unchanged.</summary>
    private static string SlotOf(string id)
    {
        int u = id.IndexOf('_');
        return u < 0 ? id : id[..u];
    }

    /// <summary>(M24 P5) The assumed ENEMY defensive-rune ids from config
    /// (<c>"combo.assumedEnemyDefensiveRunes"</c>, a comma-separated id list — the user's honest guess
    /// at the target's defensive runes, which the Live Client API can't read). Empty when unset/blank/
    /// absent, so the range floor is unchanged by default. Malformed ids are skipped, never thrown.</summary>
    private IReadOnlyList<int> ParseAssumedEnemyDefensiveRunes()
    {
        if (_config.Get("combo.assumedEnemyDefensiveRunes") is not string raw || string.IsNullOrWhiteSpace(raw))
            return Array.Empty<int>();
        var ids = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out int id)) ids.Add(id);
        return ids;
    }

    /// <summary>(M24 P2/P3) Builds a MIN- or MAX-exposure variant of <paramref name="graph"/> for the
    /// knobbed uncertainty axes (적중시간 duration + 거리/충전 distance), or <c>null</c> when no node
    /// qualifies (caller then skips the extra resolve/execute for that direction). Only UNSET knobs are
    /// touched — a SET knob collapses its axis and must not widen the range.
    /// <list type="bullet">
    /// <item><description><b>Distance</b> is pushed to its end in BOTH directions (max → knob 1, min →
    /// knob 0), because its resolved ANCHOR (<see cref="SkillHit.Calc"/>) can sit at EITHER end per
    /// champion (Hecarim E full-charge vs Fizz/Nidalee min), so both ends can differ from resolved.</description></item>
    /// <item><description><b>Duration</b> is pushed only in the MAX direction (→ MaxDurationSeconds);
    /// its resolved default is always 0 = the floor, so the MIN direction already matches resolved and
    /// needs no pre-fill.</description></item>
    /// </list>
    /// Only knob VALUES on the ORIGINAL pre-expansion graph change, so the unchanged
    /// <see cref="ApplyBinDamage"/> resolves the variant naturally — no hot-path change, resolved result
    /// untouched.</summary>
    private static ComboGraph? BuildExposureGraph(ComboGraph graph, string championId, bool maximize)
    {
        List<ComboNode>? changed = null;
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            var node = graph.Nodes[i];
            var slot = SlotOf(node.Id);
            var next = node;
            bool nodeChanged = false;

            if (maximize && node.UserHitDurationSeconds is null
                && SkillDamageDb.GetDurationScaledHit(championId, slot) is { MaxDurationSeconds: > 0 } dh)
            {
                next = next with { UserHitDurationSeconds = dh.MaxDurationSeconds };
                nodeChanged = true;
            }
            // Distance/charge share the one UserDistanceFraction knob (M28 §1): min-exposure -> 0
            // (distance floor / charge Min), max-exposure -> 1 (full distance / full-charge Max), so a
            // charge rider (K'Sante All-Out W) contributes an honest [floor, full-charge] range end.
            if (node.UserDistanceFraction is null
                && SkillDamageDb.GetHits(championId, slot) is { } hits && hits.Any(h => h.IsDistanceScaled || h.IsChargeScaled))
            {
                next = next with { UserDistanceFraction = maximize ? 1.0 : 0.0 };
                nodeChanged = true;
            }
            // (M25 §11.G) A conditional hit spans [unmet, met]: min-graph assumes the condition
            // fails (baseline Calc), max-graph assumes it holds (MetCalc).
            //
            // (loop 473) This used to be limited to UserAssumed conditions, on the reasoning that an
            // AutoResolvable one is deterministic from live stats and so resolves identically in both
            // graphs. That is true of the number, and wrong about what the range is FOR: a combo is
            // planned ahead of being cast, and the fury or ferocity the caster will hold at that
            // moment is not the value the bar shows while they are still building it. Leaving the
            // empowered variant out of the range hid the whole upper half of a Renekton or Rengar
            // combo. So both classes widen it now, and the checkbox behaves the same either way -
            // untouched means "either could happen, here is the span", ticked (or unticked) means the
            // user has decided and the range collapses to that one value.
            // (loop 479) Asks SkillDamageDb rather than re-scanning GetHits, so a conditional RIDER
            // (Jhin's fourth shot, an on-hit bonus with no hit of its own on any slot) widens the
            // range on the AA node exactly like a conditional hit does on an ability node.
            if (node.UserConditionMet is null
                && SkillDamageDb.GetConditionalHit(championId, slot) is not null)
            {
                next = next with { UserConditionMet = maximize };
                nodeChanged = true;
            }

            if (nodeChanged)
            {
                changed ??= new List<ComboNode>(graph.Nodes);
                changed[i] = next;
            }
        }
        return changed is null ? null : graph with { Nodes = changed };
    }

    /// <summary>True for the slots that carry BIN spell formulas expandable into per-hit damage:
    /// the four abilities plus the champion PASSIVE ("P"), which a combo can include as a node so
    /// its curated passive damage is summed (기본지속효과를 패시브에 병합).</summary>
    private static bool IsAbilitySlot(string id)
        => id is "Q" or "W" or "E" or "R" or "P";

    /// <summary>Human-readable command string of the combo's ability sequence in node order:
    /// each ability slot (Q/W/E/R) uppercased, an auto-attack as "A"; non-ability, non-AA nodes
    /// (item/rune/passive/execute) are skipped so the string stays readable. Joined with '-',
    /// e.g. nodes Q_0, AA_0, W_0 → "Q-A-W". Empty when the combo has no ability/AA nodes.</summary>
    private static string BuildCommandLabel(ComboGraph graph)
    {
        var tokens = new List<string>(graph.Nodes.Count);
        foreach (var node in graph.Nodes)
        {
            if (node.NodeType == ComboNodeType.Aa)
            {
                tokens.Add("A");
                continue;
            }

            if (node.NodeType == ComboNodeType.Summoner)
            {
                var (label, sid) = SummonerToken(node.Name);
                if (sid is not null) tokens.Add(label); // (loop 176) 점화/점멸 shown in the sequence
                continue;
            }

            var slot = SlotOf(node.Id);
            if (IsAbilitySlot(slot))
                tokens.Add(slot.ToUpperInvariant());
            // item/rune/passive/execute nodes carry no keystroke token — skipped.
        }
        return string.Join("-", tokens);
    }

    /// <summary>(loop 176) Maps a summoner-spell node's Name to its overlay (label, Data Dragon spell id)
    /// — Ignite/Flash are the only ones a combo carries; anything else returns a null id (skipped).</summary>
    private static (string Label, string? SpellId) SummonerToken(string name) => name switch
    {
        "Ignite" => ("점화", "SummonerDot"),
        "Flash" => ("점멸", "SummonerFlash"),
        _ => (name, null),
    };

    /// <summary>(M28 §3) The structured sibling of <see cref="BuildCommandLabel"/>: one token per
    /// AA/ability node in the SAME order and with the SAME skip rules (item/rune/execute nodes carry no
    /// keystroke), each tagged with the above-floor knob the user set on that node. Reads the
    /// pre-expansion user graph, whose nodes still carry the User* knobs. NO champion-specific branching
    /// — the knob SHAPE alone drives the overlay glyph (M28 §1's data-driven rule).</summary>
    private static List<ComboSequenceToken> BuildSequence(ComboGraph graph, string championId)
    {
        var tokens = new List<ComboSequenceToken>(graph.Nodes.Count);
        foreach (var node in graph.Nodes)
        {
            if (node.NodeType == ComboNodeType.Aa)
            {
                tokens.Add(new ComboSequenceToken("A", IsAbility: false));
                continue;
            }
            if (node.NodeType == ComboNodeType.Summoner)
            {
                var (label, sid) = SummonerToken(node.Name);
                if (sid is not null) // (loop 176) summoner token carries a spell id → overlay draws its icon
                    tokens.Add(new ComboSequenceToken(label, IsAbility: false, SummonerSpellId: sid));
                continue;
            }
            var slot = SlotOf(node.Id);
            if (!IsAbilitySlot(slot)) continue; // item/rune/passive/execute — no keystroke token
            var (knob, fraction, count) = KnobFor(node, championId, slot);
            tokens.Add(new ComboSequenceToken(slot.ToUpperInvariant(), IsAbility: true, knob, fraction, count));
        }
        return tokens;
    }

    /// <summary>(M28 §1/§3) Maps a node's set USER knobs to the overlay shape class + its display value.
    /// A knob counts only when the user pushed it ABOVE its conservative floor (P2): a UserAssumed
    /// condition assumed MET, a charge/distance or duration slider above 0, or a summon-hit/stack count
    /// above 0. First match wins (a node realistically carries one). None ⇒ a plain chip (no assumption).</summary>
    private static (ComboKnobShape Knob, double Fraction, int Count) KnobFor(ComboNode node, string championId, string slot)
    {
        // Binary max-damage: a wall/debuff/state condition the API can't read, assumed MET by the user.
        if (node.UserConditionMet == true)
            return (ComboKnobShape.MaxDamage, 0, 0);
        // Charge/distance slider (0..1).
        if (node.UserDistanceFraction is double f && f > 0)
            return (ComboKnobShape.Slider, Math.Clamp(f, 0, 1), 0);
        // Duration slider: seconds exposed as a fraction of the curated max duration.
        if (node.UserHitDurationSeconds is double secs && secs > 0
            && SkillDamageDb.GetDurationScaledHit(championId, slot) is { MaxDurationSeconds: > 0 } dh)
            return (ComboKnobShape.Slider, Math.Clamp(secs / dh.MaxDurationSeconds.Value, 0, 1), 0);
        // Summon-hit count / stack count steppers.
        if (node.UserAttackCount is int ac && ac > 0)
            return (ComboKnobShape.Count, 0, ac);
        if (node.UserStackCount is int sc && sc > 0)
            return (ComboKnobShape.Count, 0, sc);
        return (ComboKnobShape.None, 0, 0);
    }

    /// <summary>True if two Live Client identity strings refer to the same player, tolerating
    /// the API's riotId#TAG vs game-name inconsistencies: empty strings never match; compared
    /// case-insensitively; and with the "#TAG" suffix stripped from each side so "Name#KR1"
    /// matches a bare "Name".</summary>
    private static bool SamePlayer(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(BaseName(a), BaseName(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The game-name part of a riotId, i.e. everything before the first '#'.</summary>
    private static string BaseName(string id)
    {
        int h = id.IndexOf('#');
        return h < 0 ? id : id[..h];
    }

    /// <summary>
    /// Builds the M03 <see cref="ExecutionContext"/> from the live snapshot, degrading
    /// honestly for every field the Live Client API does not expose:
    /// <list type="bullet">
    /// <item><b>Attacker</b> — real: total AD / AP / level come straight from
    /// <see cref="ActivePlayerStats"/>. <b>BonusAD</b> is DERIVED (the API gives no
    /// base-vs-bonus split): total AD minus the active champion's base+per-level AD when
    /// that champion is in M11, else 0. <b>CriticalChance</b> (M05 v2.8) is now the REAL
    /// live value from <c>championStats.critChance</c>; <b>LifeSteal</b> is still 0 — the
    /// API does not expose it.</item>
    /// <item><b>Defender</b> — ESTIMATED. The API gives no enemy live stats and no current
    /// target, so the target is the FIRST LIVING ENEMY on the scoreboard (a deterministic,
    /// documented rule — real target selection is not API-exposed) and its stats are the
    /// champion's public base + per-level values at its visible level, with NO item bonus
    /// (bounded + honest for MVP). Falls back to <see cref="FallbackDefender"/> when no
    /// enemy is resolvable or the enemy champion is outside M11's cached set.
    /// <b>CurrentHP</b> is <c>MaxHP</c> minus <paramref name="targetHealthTracker"/>'s honest
    /// LOWER-BOUND missing-HP estimate when a tracker is available (null for older/test call
    /// sites, which keep the prior "CurrentHP == MaxHP" behavior) — see
    /// <see cref="TargetHealthTracker"/>'s class doc comment for why this can only ever
    /// UNDERSTATE real missing HP, never overstate it.</item>
    /// <item><b>Runes</b> — <paramref name="runeConfig"/> is the REAL persisted selection from the
    /// M04 combo editor's rune panel (<see cref="Runes.RuneSelectionStore"/>), resolved by the
    /// caller (<see cref="OnComboTrigger"/>) for the same <c>championId</c> this method computes.
    /// <b>CasterMaxHealth</b>/<b>CasterIsMelee</b> are populated for real (from the live snapshot /
    /// champion base data) so a manual rune's %-max-health damage term (see
    /// <see cref="Runes.RuneEffectDb"/>) resolves correctly.</item>
    /// <item><b>Attacker items</b> — <paramref name="config"/>'s persisted
    /// <see cref="ItemBuildStore"/> selection for <c>championId</c> (the M04 combo editor's
    /// "hypothetical build" search-to-add picker) is added ADDITIVELY on top of the live AD/AP
    /// above, in <see cref="BuildAttacker"/> — theory-crafting on top of the attacker's real
    /// current state, never a replacement for it. See <see cref="BuildAttacker"/>'s own doc
    /// comment for exactly which fields this affects.</item>
    /// <item><b>Defender snapshot override</b> — loop 38 continuation 19's defender-side "virtual
    /// model": when <see cref="TargetSnapshotStore.GetUseSnapshot"/> is explicitly true for
    /// <paramref name="comboId"/> AND a snapshot was actually captured for it, the live-resolved
    /// defender above is REPLACED by the frozen snapshot's Armor/Mr/MaxHp
    /// (<paramref name="usingSnapshotTarget"/> = true). Default OFF and no-snapshot both leave the
    /// live resolution completely unchanged (CLAUDE.md Policy P2) — this is an explicit, always-OFF-
    /// unless-opted-in per-combo toggle, never an automatic substitution.</item>
    /// </list>
    /// </summary>
    // (loop 119) INSTANCE method (was static): it now reads/writes the instance _lastSeenTargets cache
    // (last-known designated-target stats). All helpers it calls (ResolveActive/ResolveTarget/
    // BuildDefenderFor/BuildAttacker/ResolveChampionId) are static, so this transition is call-compatible;
    // the sole caller (ComputeAndPublish) is already an instance method, so its call site is unchanged.
    private ExecutionContext BuildContext(
        GameSnapshot snap, SavedCombo saved, TargetMode mode, string manualTarget,
        UserRuneConfig runeConfig, ConfigManager config, string comboId, TargetHealthTracker? targetHealthTracker,
        out string targetChampion, out bool defenderIsFallback, out bool usingSnapshotTarget,
        out int targetLevel, out bool designatedCleared)
    {
        string championId = ResolveChampionId(snap, saved);

        var attacker = BuildAttacker(snap, championId, config);
        var active = ResolveActive(snap);
        // Resolve the target ONCE (override → same-position → first-living) so the defender the
        // damage is computed against and the champion name shown on the card are the SAME target.
        var target = ResolveTarget(snap, active, mode, manualTarget);
        var defender = BuildDefenderFor(target, out defenderIsFallback, targetHealthTracker);
        targetChampion = target?.ChampionName ?? string.Empty;
        targetLevel = target?.Level ?? 0; // (loop 113) for the card's health-bar level box

        // (loop 178) Recover the attacker's FLAT ARMOR PEN from item LETHALITY. The Live Client API
        // reports championStats.armorPenetrationFlat as 0 for lethality builds, and the combo's item
        // source (ItemRepository/Data Dragon stats) has no lethality field — so mitigation was using the
        // target's FULL armor (no pen), under-counting lethality-build damage (user: raw 154.694 came out
        // 125.8 @ armor 23, but in-game 147 @ effective armor 5 = 23−18 lethality). StaticGameData carries
        // per-item lethality; convert (live-equipped + hypothetical build) to flat pen scaled by target
        // level and add it on top of any API-reported flat pen.
        attacker = attacker with
        {
            ArmorPenFlat = attacker.ArmorPenFlat + LethalityFlatPen(active, config, championId, targetLevel),
            // (loop 178) % armor pen (Last Whisper line): the Live Client API's armorPenetrationPercent
            // MAY already carry it (unlike lethality it's a direct multiplier), so we take the MAX of the
            // API value and the item-computed % — never adding on top (that would double-count when the
            // API already reports it), but still covering the case where the API omits item % pen.
            ArmorPenPercent = Math.Max(attacker.ArmorPenPercent, ItemPercentArmorPen(active, config, championId)),
        };
        usingSnapshotTarget = false;
        designatedCleared = false; // (loop 119) the "적 사망 초기화" state is removed — see last-seen cache below

        // (loop 122, REVERTED — M26 §3/§16) An assumed enemy SCALING HEALTH shard (+10×level MaxHP,
        // "enemy rune shards aren't API-readable so assume the near-universal pick") used to be added
        // here. Removed: GOLDEN #2 (K'Sante W, docs/reports/golden/GOLDEN_02_KSANTE.md) proved it
        // double-counts against a real Practice-Tool measurement whenever the target's resolved MaxHP
        // already IS the true total (the golden's zero-growth Dummy pins the measured real value
        // directly) — the assumption has no way to distinguish "estimate, rune unknown" from "already
        // the true number," and real measured data overrides a speculative inference (P2: no inference
        // beyond Last Seen). See CLAUDE_CODE_TODO.md §16 "W/P %maxHP shard 이중계산" for the diagnosis.

        // (loop 119) Last-seen stat cache for the designated (Manual) pin. When the pinned champion is
        // present (alive OR dead — ResolveTarget no longer drops dead rows) we resolved a real row above,
        // so REMEMBER its stats. When the pinned champion is ABSENT from the snapshot (target == null,
        // e.g. left the game), reuse those saved stats so the card keeps computing from the last-known
        // state instead of a 0/0 fallback — the user's "죽어도/안 보여도 저장된 스텟 기준" requirement.
        // (Level/items stay live on the scoreboard while dead/out-of-vision, so the present-but-dead case
        // already uses fresh numbers; this cache only covers the truly-gone row.)
        if (mode == TargetMode.Manual && !string.IsNullOrWhiteSpace(manualTarget))
        {
            if (target is not null && !defenderIsFallback)
            {
                _lastSeenTargets[manualTarget] = (targetLevel, defender.MaxHP, defender.Armor, defender.Mr);
            }
            else if (target is null && _lastSeenTargets.TryGetValue(manualTarget, out var seen))
            {
                defender = new DefenderStat(CurrentHP: seen.MaxHp, MaxHP: seen.MaxHp,
                    Armor: seen.Armor, Mr: seen.Mr, Shield: 0);
                targetChampion = manualTarget;
                targetLevel = seen.Level;
                defenderIsFallback = false;
            }
        }

        // Defender-side "virtual model" (loop 38 continuation 19): explicit per-combo opt-in only —
        // see the class doc-comment item above and TargetSnapshotStore's own Policy P2 note.
        if (!string.IsNullOrEmpty(comboId)
            && TargetSnapshotStore.GetUseSnapshot(config, comboId)
            && TargetSnapshotStore.Load(config, comboId) is { } snapshot)
        {
            defender = new DefenderStat(
                CurrentHP: snapshot.MaxHp, MaxHP: snapshot.MaxHp,
                Armor: snapshot.Armor, Mr: snapshot.Mr, Shield: 0);
            targetChampion = snapshot.ChampionName;
            defenderIsFallback = false; // an explicit, labeled virtual target — not a failed resolution
            usingSnapshotTarget = true;
            designatedCleared = false; // an explicit virtual target overrides the cleared-pin state
        }

        // CasterMaxHealth/CasterIsMelee: the two fields a manual rune's %-max-health damage term
        // (Grasp of the Undying, Shield Bash — see RuneEffectDb) needs but M05's AttackerStat has
        // no room for. MaxHealth comes straight from the live snapshot (already used elsewhere,
        // e.g. ResolveBuildProcs); IsMelee reuses the same champion-attack-range check item procs
        // already rely on (see IsMelee doc comment) — unresolvable (championId outside M11's cached
        // set) classifies as not-melee here, the opposite default IsMelee itself documents for its
        // own edge case, since this is a fallback affecting a rune bonus rather than an item proc's
        // own melee/ranged split, and 0 is a safer (smaller) ranged-side default for an unknown case.
        var casterChampion = ChampionRepository.Get(championId);
        double casterMaxHealth = snap.Stats.MaxHealth;
        bool casterIsMelee = casterChampion is not null && IsMelee(casterChampion);

        return new ExecutionContext(championId, attacker, defender, runeConfig,
            CasterMaxHealth: casterMaxHealth, CasterIsMelee: casterIsMelee);
    }

    /// <summary>The combo's champion id: the ACTIVE player's live champion when resolvable
    /// (real, from the snapshot), else the champion the combo was authored for
    /// (<see cref="SavedCombo.ChampionId"/>) — same rule <see cref="BuildContext"/> always used,
    /// pulled out so <see cref="OnComboTrigger"/> can resolve it once, before building the context,
    /// to look up that champion's persisted rune selection.
    ///
    /// Same Korean-name handling as <see cref="TryResolveBase"/>: the Live Client API can return
    /// the active player's champion as its Korean client-language display name rather than the
    /// English Data Dragon id every lookup here (rune selection, item build, melee/ranged) is keyed
    /// by, so the resolved name is reverse-translated via <see cref="ChampionSummary.ResolveKoreanName"/>
    /// before use; a no-op for an already-English name.</summary>
    private static string ResolveChampionId(GameSnapshot snap, SavedCombo saved)
    {
        var active = ResolveActive(snap);
        if (active is { ChampionName.Length: > 0 })
            return ChampionSummary.ResolveKoreanName(active.ChampionName) ?? active.ChampionName;
        return saved.ChampionId;
    }

    /// <summary>
    /// Determines <paramref name="championId"/>'s SELECTED runes from two sources — live
    /// auto-detect PREFERRED, the M04 manual chip-picker as FALLBACK — arms this runner's shared
    /// <see cref="RuneEngine"/>'s manual-activation flags (<see cref="RuneEngine.SetManualFlag"/> —
    /// a call SEPARATE from selection; see the <see cref="RuneEngine"/> class doc: a rune being
    /// selected does not mean it is "active" for THIS trigger), and returns the
    /// <see cref="UserRuneConfig"/> for <see cref="RuneEngine.GetActiveEffects"/>.
    ///
    /// Source 1 (preferred) — LIVE AUTO-DETECT: when <paramref name="snap"/>'s
    /// <see cref="ActivePlayerStats.EquippedRuneIds"/> is non-empty (a real game is running and the
    /// Live Client API returned a <c>fullRunes</c> block), the selection is the intersection of the
    /// player's REAL equipped runes with <see cref="RuneApiTrackability.NonTrackableRuneIds"/> (the 8
    /// runes M06 cannot verify via any public API) — i.e. which of those 8 the player actually has on
    /// their page this game, auto-detected instead of hand-picked. <b>Every rune found this way is
    /// armed manually-active TRUE</b> (loop 48 fix — see below), never consulting
    /// <c>persisted.ManualFlags</c>.
    ///
    /// Source 2 (fallback) — MANUAL PICKER: when no live rune data is available (no active game, e.g.
    /// editor preview, or an older/spectator API shape that omits <c>fullRunes</c>), falls back to
    /// <see cref="Runes.RuneSelectionStore"/>'s persisted selection, and each selected rune's
    /// manual-activation flag is armed from the persisted <see cref="RuneSelection.ManualFlags"/> for
    /// that rune id (true only when explicitly stored true, false otherwise) — exactly as before this
    /// change, for whatever legacy checkbox data a pre-loop-40 user may still have persisted.
    ///
    /// <b>Loop 48 fix (user report: a live-equipped manual rune, e.g. Deathfire Touch/죽음불꽃손아귀,
    /// silently never applied):</b> the manual rune-picker checkbox UI (the only thing that ever
    /// wrote a `true` into <c>ManualFlags</c>) was deleted in loop 40, on the documented premise that
    /// live auto-detection alone was sufficient going forward — but this method kept gating Source
    /// 1's runes on that now-permanently-empty/stale `ManualFlags` dictionary, so EVERY ONE of the 8
    /// manual runes silently stopped applying for any live game the moment loop 40 shipped (a genuine
    /// regression, not a design choice — no reachable code path could ever set the flag true again for
    /// a live-detected rune). Fixed by treating live-confirmed-equipped as sufficient evidence on its
    /// own: it is strictly stronger proof of intent than a UI checkbox ever was (a checkbox could be
    /// stale from an old game; a live `fullRunes` read is the player's real, current rune page). This
    /// does not reintroduce the original "unmodeled trigger condition" imprecision each of the 8
    /// runes' own <c>rune_effects.json</c> `_note` already discloses (Cheap Shot's CC requirement,
    /// Sudden Impact's dash requirement, etc. still aren't simulated) — it only restores the
    /// pre-loop-40 "equipped + assumed active → applied once per combo" approximation that was always
    /// this project's baseline for these 8, now sourced from live equip-detection instead of a
    /// deleted checkbox.
    ///
    /// No <see cref="RuneEngine"/> was injected (older/test call sites), or neither source has anything
    /// -&gt; empty selection, the exact pre-existing dormant behavior — never a fabricated default.
    /// </summary>
    private UserRuneConfig LoadRuneSelectionAndArmManualFlags(string championId, GameSnapshot snap)
    {
        if (_runeEngine is null) return new UserRuneConfig(Array.Empty<string>());

        IReadOnlyList<string> selectedRuneIds;
        IReadOnlyDictionary<string, bool> manualFlags;
        bool isLiveAutoDetected;
        var equipped = snap.Stats.EquippedRuneIds;
        if (equipped is { Count: > 0 } && ReadAutoApplyRunes())
        {
            // Live auto-detect (user setting "runes.autoApply" ON): select every equipped rune this
            // engine can actually model — the 8 non-trackable MANUAL runes (armed active, loop 48)
            // AND the 6 API-trackable AUTO-TRIGGER runes (Conqueror/Electrocute/Axiom/... — loop 46).
            // Before this, only the manual 8 were selected, so an equipped auto-trigger rune never
            // reached ComboEngine's SelectedRuneIds.Contains gate and silently never fired live.
            var selected = new List<string>();
            foreach (int id in equipped)
                if (RuneApiTrackability.NonTrackableRuneIds.Contains(id)
                    || ComboEngine.AutoTriggeredRuneIds.Contains(id))
                    selected.Add(id.ToString());
            selectedRuneIds = selected;
            manualFlags = new Dictionary<string, bool>(); // unused below — every live-detected MANUAL rune is forced active instead
            isLiveAutoDetected = true;
        }
        else if (equipped is { Count: > 0 })
        {
            // Live game but auto-apply is OFF: contribute no auto rune. Any per-node
            // ComboNode.AttachedRuneId (Part 3, force-applied) still fires independently in
            // ComboEngine — it does not depend on SelectedRuneIds.
            return new UserRuneConfig(Array.Empty<string>());
        }
        else
        {
            var persisted = RuneSelectionStore.Load(_config, championId);
            if (persisted is null) return new UserRuneConfig(Array.Empty<string>());
            selectedRuneIds = persisted.SelectedRuneIds;
            manualFlags = persisted.ManualFlags;
            isLiveAutoDetected = false;
        }

        foreach (var runeId in selectedRuneIds)
        {
            // Only the 8 non-trackable MANUAL runes carry an additive DamageBonus armed via a manual
            // flag; the 6 auto-trigger runes produce their effect inside ComboEngine (never via
            // RuneEngine.GetActiveEffects, which returns null for them), so arming a flag on them is a
            // harmless no-op — but we skip it to keep the flag set faithful to real manual runes.
            if (!int.TryParse(runeId, out int rid) || !RuneApiTrackability.NonTrackableRuneIds.Contains(rid))
                continue;
            // (loop 170) Condition gating: live equip alone is only sufficient evidence for manual runes
            // whose trigger this app can AUTO-VERIFY against the executed combo — the ability-damage runes
            // (Scorch/Comet/Deathfire), which ComboEngine.Execute additionally gates on a real ability node
            // being present. Runes whose real trigger is an unobservable state (Cheap Shot = target CC'd,
            // Sudden Impact = post-dash, Shield Bash = shield gained, Grasp = melee in-combat stacks) are NO
            // LONGER armed from mere equip — auto-applying them to every combo (regardless of whether the
            // condition holds) was the user-reported bug. They now require an explicit persisted manual flag
            // (a deliberate user assertion that the condition is met), so an unqualified combo never gets them.
            // (loop 174) Dash-trigger runes (Sudden Impact) are also auto-armable: their bonus is
            // COMPUTED when live-equipped, and ComboEngine.Execute then applies it only if the combo
            // actually contains a Flash/dash node (the user's dash signal) — same two-stage pattern as
            // the ability-trigger runes.
            bool autoArmable = ComboEngine.AbilityDamageTriggeredManualRuneIds.Contains(rid)
                || ComboEngine.DashTriggeredManualRuneIds.Contains(rid);
            bool isManualActive = (isLiveAutoDetected && autoArmable)
                || (manualFlags.TryGetValue(runeId, out var flag) && flag);
            _runeEngine.SetManualFlag(runeId, isManualActive);
        }

        return new UserRuneConfig(selectedRuneIds);
    }

    /// <summary>Reads the "auto-apply equipped runes" user setting (<c>runes.autoApply</c>) each
    /// trigger so a Settings-toggle change takes effect without restarting the runner. Defaults to
    /// TRUE (auto-apply on) for any missing/unknown value — the equipped-rune reflection is the
    /// intended default behavior; the toggle exists to let a user turn it OFF and rely solely on
    /// per-node <see cref="ComboNode.AttachedRuneId"/> theorycraft overrides instead.</summary>
    private bool ReadAutoApplyRunes()
        => _config.Get("runes.autoApply") is not bool enabled || enabled;

    /// <summary>Reads the target-selection preference from config each trigger (so a UI change
    /// applies without restarting the runner). Defaults to Auto for any missing/unknown value.</summary>
    private (TargetMode Mode, string ManualTarget) ReadTargeting()
    {
        string manual = _config.Get("targeting.manualTarget") as string ?? string.Empty;
        var mode = string.Equals(_config.Get("targeting.mode") as string, "Manual", StringComparison.OrdinalIgnoreCase)
            ? TargetMode.Manual
            : TargetMode.Auto;
        return (mode, manual);
    }

    /// <summary>Resolves the active player's scoreboard row (for champion name + team + their
    /// built items). The Live Client API is inconsistent about identity between activePlayer and
    /// allPlayers rows — riotId ("Name#TAG") is populated on both, while summonerName is
    /// deprecated and often differs (active="Name#TAG", scoreboard="") — so match on any identity
    /// that lines up across the two, tolerating tag/case differences. Null when none matches.</summary>
    private static ScoreboardEntry? ResolveActive(GameSnapshot snap)
    {
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (SamePlayer(p.RiotId, snap.ActivePlayerRiotId)
                || SamePlayer(p.SummonerName, snap.ActivePlayerSummonerName)
                || SamePlayer(p.RiotId, snap.ActivePlayerSummonerName)
                || SamePlayer(p.SummonerName, snap.ActivePlayerRiotId))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves the combo target from the living ENEMY rows relative to <paramref name="active"/>,
    /// applying the user mandate's precedence (internal so a unit test can assert each tier):
    /// <list type="number">
    /// <item><b>Manual override</b> — when <paramref name="mode"/> is <see cref="TargetMode.Manual"/>
    /// and <paramref name="manualTarget"/> names a LIVING enemy (by champion name, or riotId/summoner
    /// name), use it. This is the always-available path the user asked for.</item>
    /// <item><b>Same-position enemy</b> — the auto default: when the active player's position is known
    /// (best-effort; the Live Client only fills lanes in ranked/draft) and a living enemy shares it.</item>
    /// <item><b>First living enemy</b> — the prior deterministic behavior, used when no override
    /// matches and positions are unavailable (practice tool / ARAM) or none line up.</item>
    /// </list>
    /// A manual target that is dead/absent, or an unknown position, gracefully falls through to the
    /// next tier. Null when there is no active-player row or no living enemy at all (→ FallbackDefender).
    /// </summary>
    internal static ScoreboardEntry? ResolveTarget(
        GameSnapshot snap, ScoreboardEntry? active, TargetMode mode, string manualTarget)
    {
        if (active is null) return null;
        string myTeam = active.Team;

        // 1. Manual override — a HARD pin (loop 119: NO survivor condition). The pinned champion stays
        //    the target whether ALIVE or DEAD; we do NOT retarget another enemy and do NOT show a "적
        //    사망 초기화" state (user request). Its scoreboard row keeps live level/items even while dead
        //    or out of vision (allPlayers always lists it), so the defender stats compute from that
        //    LAST-KNOWN state. Only when the pinned champion is ABSENT from the snapshot entirely (left
        //    the game) do we return null — BuildContext then falls back to the last-seen stat cache.
        //    Auto mode (tiers 2–3 below) keeps the smart same-position/first-living behavior.
        if (mode == TargetMode.Manual && !string.IsNullOrWhiteSpace(manualTarget))
        {
            for (int i = 0; i < snap.PlayerCount; i++)
            {
                var p = snap.Players[i];
                if (IsAlly(p, myTeam)) continue;
                if (MatchesTarget(p, manualTarget)) return p; // alive OR dead — stats from its live row
            }
            return null; // pinned champ not in the snapshot at all → BuildContext uses last-seen cache
        }

        // 2. Same-position enemy (best-effort; positions empty outside ranked/draft).
        if (!string.IsNullOrEmpty(active.Position))
        {
            for (int i = 0; i < snap.PlayerCount; i++)
            {
                var p = snap.Players[i];
                if (p.IsDead || IsAlly(p, myTeam)) continue;
                if (string.Equals(p.Position, active.Position, StringComparison.OrdinalIgnoreCase)) return p;
            }
        }

        // 3. First living enemy (prior behavior).
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (p.IsDead || IsAlly(p, myTeam)) continue;
            return p;
        }
        return null;
    }

    private static bool IsAlly(ScoreboardEntry p, string myTeam)
        => string.Equals(p.Team, myTeam, StringComparison.Ordinal);

    /// <summary>M02 loop 38 continuation 12 — combo-overlay click-to-target. Returns the champion
    /// name of the next LIVING enemy after <paramref name="currentTarget"/>, in scoreboard order,
    /// wrapping around (relative to <see cref="ResolveActive"/>'s team, same "enemy" definition
    /// <see cref="ResolveTarget"/> uses). Pure/static so the overlay's portrait-click handler (which
    /// has no ComboRunner instance — it fires from the WPF render host, not from a combo trigger)
    /// can compute a target cycle directly from the latest snapshot. Null when there is no active
    /// player row or no living enemy at all; when <paramref name="currentTarget"/> does not match any
    /// living enemy (stale/unknown name), starts from the first living enemy rather than failing.</summary>
    public static string? NextLivingEnemy(GameSnapshot snap, string? currentTarget)
    {
        var active = ResolveActive(snap);
        if (active is null) return null;
        string myTeam = active.Team;

        var livingEnemies = new List<string>();
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (p.IsDead || IsAlly(p, myTeam)) continue;
            livingEnemies.Add(p.ChampionName);
        }
        if (livingEnemies.Count == 0) return null;

        int idx = currentTarget is not null ? livingEnemies.IndexOf(currentTarget) : -1;
        int nextIdx = (idx + 1) % livingEnemies.Count;
        return livingEnemies[nextIdx];
    }

    /// <summary>(loop 118) Champion names of ALL living enemies in scoreboard order — the choices the
    /// overlay's ⇄ target picker shows as portraits. Same "enemy" definition as
    /// <see cref="ResolveTarget"/>/<see cref="NextLivingEnemy"/> (relative to <see cref="ResolveActive"/>'s
    /// team, dead rows skipped). Empty when there is no active-player row or no living enemy. Pure/static
    /// so the WPF render host can call it directly from the latest snapshot.</summary>
    public static IReadOnlyList<string> LivingEnemies(GameSnapshot snap)
    {
        var result = new List<string>();
        var active = ResolveActive(snap);
        if (active is null) return result;
        string myTeam = active.Team;
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (p.IsDead || IsAlly(p, myTeam)) continue;
            result.Add(p.ChampionName);
        }
        return result;
    }

    /// <summary>(§40 combo overlay) The full ENEMY roster in scoreboard order — unlike
    /// <see cref="LivingEnemies"/> this KEEPS dead rows and projects each enemy's
    /// <see cref="ScoreboardEntry.IsDead"/> + <see cref="ScoreboardEntry.RespawnTimer"/>, so the
    /// always-on portrait row can grey out a dead enemy and show its respawn countdown. Pure/static
    /// so the WPF render host can build the row directly from the latest snapshot. Empty when there is
    /// no active-player row.</summary>
    public static IReadOnlyList<EnemyRosterEntry> EnemyRoster(GameSnapshot snap)
    {
        var result = new List<EnemyRosterEntry>();
        var active = ResolveActive(snap);
        if (active is null) return result;
        string myTeam = active.Team;
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (IsAlly(p, myTeam) || string.IsNullOrEmpty(p.ChampionName)) continue;
            result.Add(new EnemyRosterEntry(p.ChampionName, p.IsDead, p.RespawnTimer));
        }
        return result;
    }

    /// <summary>(§40 skill overlay) Aggregates a <see cref="ComboResult.NodeBreakdown"/> into the six
    /// canonical skill slots the per-skill overlay shows — <b>P, Q, W, E, R, A</b> in that order — by
    /// grouping each node's damage under <see cref="SlotOf"/> (auto-attack slot "AA" folds into "A").
    /// Nodes that don't belong to a skill slot (rune / item / bonus ids, which have no "{slot}_"
    /// prefix) are excluded — the per-skill view is intentionally "damage by ability + basic attack",
    /// mirroring the approved mockup's six boxes. Always returns exactly six entries (0 damage when a
    /// slot did nothing). Pure/static.</summary>
    public static IReadOnlyList<SkillSlotDamage> SkillDamageBySlot(ComboResult result)
    {
        var sums = new Dictionary<string, double> { ["P"] = 0, ["Q"] = 0, ["W"] = 0, ["E"] = 0, ["R"] = 0, ["A"] = 0 };
        if (result?.NodeBreakdown is { } nodes)
        {
            foreach (var n in nodes)
            {
                string slot = SlotOf(n.NodeId);
                if (slot == "AA") slot = "A";
                if (sums.ContainsKey(slot)) sums[slot] += n.Damage;
            }
        }
        return new[] { "P", "Q", "W", "E", "R", "A" }
            .Select(s => new SkillSlotDamage(s, sums[s]))
            .ToList();
    }

    /// <summary>The six slots the always-on skill panel shows, in display order.</summary>
    private static readonly string[] SkillPanelSlots = { "P", "Q", "W", "E", "R", "A" };

    /// <summary>(user request) Computes each ability + basic-attack + passive's STANDALONE damage vs the
    /// CURRENT target — independent of any saved/triggered combo — so the skill overlay can show "how much
    /// does each of my skills do to this target right now". Reuses the SAME engine + mitigation as the combo
    /// card (so numbers are consistent): for each slot a one-node graph is resolved (curated BIN damage for
    /// P/Q/W/E/R via <see cref="ApplyBinDamage"/>, total-AD for the auto), then run through
    /// <see cref="ComboEngine.Execute"/> against the resolved defender so armor/MR/penetration apply. Target
    /// = the designated (Manual-pinned) enemy first, else the auto-resolved one (same rule the combo card
    /// uses, via <see cref="BuildContext"/>). Rank = the player's REAL current rank (an unleveled ability
    /// resolves to 0). Runes are intentionally EXCLUDED (empty config) so this is the raw per-skill damage,
    /// not a combo's rune procs. Returns null when there is no game/target/champion. Never throws (a bad
    /// resolution yields null / a 0 slot rather than propagating). Safe to call ~4x/s from the overlay.</summary>
    public SkillPanelResult? ComputeSkillPanel(GameSnapshot? snap)
    {
        if (snap is null || !snap.HasData) return null;
        try
        {
            var (mode, manualTarget) = ReadTargeting();
            var saved = new SavedCombo("__skillpanel__", string.Empty, string.Empty, string.Empty);
            string championId = ResolveChampionId(snap, saved);
            if (ChampionRepository.Get(championId) is null) return null;

            // Empty rune config = pure skill/AA/passive damage (no Electrocute/Comet/etc. contamination).
            var runeConfig = new UserRuneConfig(Array.Empty<string>());
            var context = BuildContext(snap, saved, mode, manualTarget, runeConfig, _config, string.Empty,
                _targetHealthTracker,
                out string targetChampion, out bool defenderIsFallback, out _, out int _, out _);
            if (string.IsNullOrEmpty(targetChampion)) return null; // no living/designated target

            var slots = new List<SkillSlotDamage>(SkillPanelSlots.Length);
            foreach (var slot in SkillPanelSlots)
            {
                double dmg = 0;
                try
                {
                    var node = slot == "A"
                        ? new ComboNode("A_0", ComboNodeType.Aa, "AA", 0, 0, 0, ComboDamageType.Physical, 1.0, 0, 0, 0, 0, 0)
                        : new ComboNode(slot + "_0", slot == "P" ? ComboNodeType.Passive : ComboNodeType.Skill, slot,
                                        0, 0, 0, ComboDamageType.Physical, 0, 0, 0, 0, 0, 0);
                    var graph = _engine.BuildGraph(new[] { node });
                    // The auto-attack is a bare AD node (RatioAD=1) resolved by the engine; abilities/passive
                    // get their real curated BIN damage filled in first.
                    var resolved = slot == "A" ? graph : ApplyBinDamage(graph, championId, snap, context.Defender.MaxHP);
                    dmg = _engine.Execute(resolved, context).TotalDamage;
                }
                catch { dmg = 0; }
                slots.Add(new SkillSlotDamage(slot, dmg));
            }

            return new SkillPanelResult(targetChampion, context.Defender.Armor, context.Defender.Mr,
                defenderIsFallback, slots, championId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True if <paramref name="id"/> (the configured manual target — a champion name, or a
    /// riotId/summoner name) identifies scoreboard row <paramref name="p"/>. Champion name is the
    /// primary case (the target selector lists champions); identity strings are also accepted and
    /// matched tolerantly (case-insensitive, "#TAG" stripped) via <see cref="SamePlayer"/>.</summary>
    private static bool MatchesTarget(ScoreboardEntry p, string id)
        => string.Equals(p.ChampionName, id, StringComparison.OrdinalIgnoreCase)
           || SamePlayer(p.RiotId, id)
           || SamePlayer(p.SummonerName, id);

    /// <summary>
    /// Builds the attacker's real live stats, then adds <paramref name="config"/>'s persisted
    /// <see cref="ItemBuildStore"/> "hypothetical build" (M04 combo editor's search-to-add item
    /// picker) ON TOP of them — ADDITIVE theory-crafting, never a replacement for the live
    /// snapshot. This finalizes the item side of the loop-38 "virtual model" decision: runes
    /// already worked this way (<see cref="LoadRuneSelectionAndArmManualFlags"/>), items were
    /// UI-only until now (see M04_COMBO_EDITOR.md changelog).
    ///
    /// Only <see cref="AttackerStat.Ad"/>/<see cref="AttackerStat.BonusAD"/>/
    /// <see cref="AttackerStat.Ap"/> have a home for an item's stats — <see cref="AttackerStat"/>
    /// carries no Armor/Mr/Hp/Haste fields (those only exist on <see cref="DefenderStat"/>, which
    /// is why <see cref="BuildDefenderFor"/>'s equivalent item loop can add them for the target
    /// but this one cannot for the caster), so an item's Armor/Mr/Hp/Haste contribution is
    /// intentionally NOT modeled here. An item's AD is always bonus AD by definition (never
    /// base), so it is added to both <c>Ad</c> and <c>BonusAD</c>.
    ///
    /// A missing <see cref="ItemBuildStore"/> selection (nothing ever saved for this champion) or
    /// an item id <see cref="ItemRepository.Get"/> cannot resolve is skipped gracefully — never a
    /// crash, never a fabricated stat.
    /// </summary>
    private static AttackerStat BuildAttacker(GameSnapshot snap, string championId, ConfigManager? config = null)
    {
        double totalAd = snap.Stats.AttackDamage; // API AttackDamage is TOTAL AD.
        double totalAp = snap.Stats.AbilityPower;

        // BonusAD split is not exposed by the API; derive it from public base+per-level AD
        // when the active champion is known to M11, else leave it at 0 (documented).
        double bonusAd = 0;
        var champ = ChampionRepository.Get(championId);
        if (champ is not null)
        {
            int level = Math.Max(1, snap.Level);
            // Same real-growth-curve fix as TryResolveBase (LevelGrowth.Stat) — a linear baseAd
            // estimate here was over/under-stating it, skewing the total-vs-bonus-AD split that
            // feeds bonus-AD-ratio skills (e.g. Zed Q/E, Jinx R).
            double baseAd = LevelGrowth.Stat(champ.BaseStats.Ad, champ.StatsPerLevel.Ad, level);
            bonusAd = Math.Max(0, totalAd - baseAd);
        }

        if (config is not null)
        {
            var build = ItemBuildStore.Load(config, championId);
            if (build is not null)
            {
                foreach (var itemId in build.ItemIds)
                {
                    var item = ItemRepository.Get(itemId);
                    if (item is null) continue; // unresolvable id -> skip, never crash
                    totalAd += item.Stats.Ad;
                    bonusAd += item.Stats.Ad;
                    totalAp += item.Stats.Ap;
                }
            }
        }

        return new AttackerStat(
            Ad: totalAd,
            BonusAD: bonusAd,
            Ap: totalAp,
            Level: snap.Level,
            // M05 v2.8: real, live-parsed value (was hardcoded 0 — the API exposes
            // championStats.critChance, see LiveDataParser.ReadChampionStats).
            CriticalChance: snap.Stats.CritChance,
            LifeSteal: 0,      // not exposed by the API
            // Penetration comes straight from the player's own championStats (P1). Percent
            // fields are the fraction ignored (see ActivePlayerStats percent-semantics note).
            ArmorPenFlat: snap.Stats.ArmorPenetrationFlat,
            ArmorPenPercent: snap.Stats.ArmorPenetrationPercent,
            MagicPenFlat: snap.Stats.MagicPenetrationFlat,
            MagicPenPercent: snap.Stats.MagicPenetrationPercent,
            // Real value from championStats.critDamage when reported; DamageEngine falls back
            // to its own constant when this is 0 (see AttackerStat.CritDamageMultiplier doc).
            CritDamageMultiplier: snap.Stats.CritDamage);
    }

    // (loop 178) Item LETHALITY isn't in Data Dragon's item stats (ItemRepository) and the Live Client
    // API reports championStats.armorPenetrationFlat as 0 for lethality builds — so the combo's mitigation
    // saw NO flat armor pen. StaticGameData (the same source KillableCalculator reads) DOES carry per-item
    // lethality; lazily load the bundled file (same static-cache pattern as SkillDamageDb/RuneEffectDb),
    // best-effort (null on any failure => 0 pen, unchanged behavior).
    private static StaticGameData? _staticData;
    private static bool _staticDataTried;
    /// <summary>(loop 179) Captured while resolving armour penetration, which happens BEFORE
    /// CalcTrace.Begin, and emitted by the caller once the trace is collecting.
    ///
    /// <para>(loop 498) [ThreadStatic]. It was a plain static, so the skill panel's four-times-a-second
    /// resolution on the render thread overwrote whatever the hotkey thread had just captured, and the
    /// line a combo's trace log attributed to itself could belong to a different computation entirely.
    /// A string assignment cannot corrupt anything, so this was never a crash — it was a log that lies,
    /// which is the failure mode section 43-P exists to prevent. Per-thread storage makes the capture
    /// and the emit, which always happen on one thread inside one call, each other's only writer.</para>
    /// </summary>
    [ThreadStatic]
    private static string? _lethalityDiag;
    private static StaticGameData? StaticData
    {
        get
        {
            if (!_staticDataTried)
            {
                _staticDataTried = true;
                try { _staticData = StaticGameDataLoader.Load(); } catch { _staticData = null; }
            }
            return _staticData;
        }
    }

    /// <summary>Flat armor penetration from the attacker's item LETHALITY (live-equipped items on the
    /// scoreboard + the hypothetical build), scaled by target level per Riot's lethality definition:
    /// <c>lethality × (0.6 + 0.4 × min(targetLevel,18)/18)</c> — mirrors <see cref="KillableCalculator"/>.
    /// Returns 0 when there's no lethality, no static data, or no resolved target. Never throws.</summary>
    private static double LethalityFlatPen(ScoreboardEntry? active, ConfigManager? config, string championId, int targetLevel)
    {
        var data = StaticData;
        if (data is null) return 0;

        double lethality = 0;
        int lethItems = 0; // (loop 179 diag)
        if (active is not null)
            for (int i = 0; i < active.ItemCount && i < active.ItemIds.Length; i++)
            {
                double leth = data.GetItem(active.ItemIds[i])?.Lethality ?? 0;
                lethality += leth;
                if (leth > 0) lethItems++;
            }
        if (config is not null && ItemBuildStore.Load(config, championId) is { } build)
            foreach (var id in build.ItemIds)
                if (int.TryParse(id, out var itemId))
                    lethality += data.GetItem(itemId)?.Lethality ?? 0;

        // (loop 180) Lethality is applied as its FULL value in flat armor pen — NO level scaling. The old
        // "× (0.6 + 0.4·level/18)" curve is stale: the user's in-game measurement showed full 18 lethality
        // reducing a lvl-1 target's armor by the full 18 (raw 154.694 → 147 = ÷(100+5), i.e. 23−18=5), so
        // current-patch lethality no longer scales down against low-level targets. Measured value wins (M26).
        double flat = Math.Max(0, lethality);

        // (loop 179) BuildContext runs BEFORE CalcTrace.Begin, so store the diagnostic and let the caller
        // emit it once the trace is collecting (see the CalcTrace.Begin site).
        _lethalityDiag = $"lethalityPen activeItemCount={active?.ItemCount ?? 0} lethItems={lethItems} " +
            $"itemIds=[{(active is null ? "" : string.Join(",", active.ItemIds.Take(Math.Max(0, active.ItemCount))))}] " +
            $"lethality={lethality:0.##} targetLevel={targetLevel} flatPen={flat:0.##}";

        return flat;
    }

    /// <summary>Combined % armor penetration (Last Whisper line) from the attacker's item
    /// <c>PercentArmorPen</c> (live-equipped + hypothetical build), stacked MULTIPLICATIVELY per the
    /// League rule (mirrors <see cref="KillableCalculator"/>'s SumPercentPen). Returns the fraction of
    /// the target's armor IGNORED (0..1); 0 when no such item / no static data. Never throws.</summary>
    private static double ItemPercentArmorPen(ScoreboardEntry? active, ConfigManager? config, string championId)
    {
        var data = StaticData;
        if (data is null) return 0;

        double remaining = 1.0;
        if (active is not null)
            for (int i = 0; i < active.ItemCount && i < active.ItemIds.Length; i++)
            {
                double pct = data.GetItem(active.ItemIds[i])?.PercentArmorPen ?? 0;
                if (pct > 0) remaining *= 1.0 - pct;
            }
        if (config is not null && ItemBuildStore.Load(config, championId) is { } build)
            foreach (var id in build.ItemIds)
            {
                if (!int.TryParse(id, out var itemId)) continue;
                double pct = data.GetItem(itemId)?.PercentArmorPen ?? 0;
                if (pct > 0) remaining *= 1.0 - pct;
            }
        return 1.0 - remaining;
    }

    /// <summary>Internal (not private) so a unit test can assert resistances resolve for a
    /// NON-cached champion straight from the Data Dragon summary — see ComboRunnerTests.
    /// Uses the Auto target rule; the live trigger path resolves the target once (honoring the
    /// manual override) and calls <see cref="BuildDefenderFor"/> directly.</summary>
    internal static DefenderStat BuildDefender(GameSnapshot snap, ScoreboardEntry? active)
        => BuildDefenderFor(ResolveTarget(snap, active, TargetMode.Auto, string.Empty), out _);

    /// <summary>Builds the ESTIMATED defender for an already-resolved <paramref name="target"/>
    /// row (base + per-level resistances at its visible level plus its VISIBLE item stats), or the
    /// <see cref="FallbackDefender"/> when there is no target / the champion resolves nowhere.
    /// <paramref name="usedFallback"/> is the loop-38 fix: this is a DIFFERENT signal than "is
    /// <paramref name="target"/> null" — the target ROW can resolve fine (a visible enemy, real
    /// champion name, portrait shows) while <see cref="TryResolveBase"/> STILL fails to find that
    /// champion's base resistances (e.g. a name the cache doesn't recognize), silently returning
    /// <see cref="FallbackDefender"/> even though a target was clearly found. Previously nothing
    /// downstream could tell these two cases apart (both left <c>ComboHudResult.TargetChampion</c>
    /// non-empty, since that field is set independently from <c>target.ChampionName</c>), which is
    /// exactly how a user could see a resolved target portrait/name with 0 armor/MR and no warning
    /// at all.</summary>
    /// <param name="targetHealthTracker">Optional shared <see cref="TargetHealthTracker"/> — when
    /// present, <c>CurrentHP</c> is <c>MaxHP</c> minus its honest LOWER-BOUND missing-HP estimate
    /// for this target (see that class's doc comment); null (the default, e.g. every pre-existing
    /// call site) keeps the prior "CurrentHP == MaxHP" behavior unchanged.</param>
    private static DefenderStat BuildDefenderFor(
        ScoreboardEntry? target, out bool usedFallback, TargetHealthTracker? targetHealthTracker = null)
    {
        if (target is not null)
        {
            var p = target;
            // First living enemy = the (documented) target. Its resistances/HP = champion base
            // + per-level at its visible level PLUS the armor/MR/HP from its VISIBLE items (the
            // Live Client API exposes each player's item ids; M11 ItemRepository gives their
            // stats) — all public/on-screen info (P1). This makes the combo's post-mitigation
            // total reflect the target's real build, not just a naked champion.
            int lvl = Math.Max(1, p.Level);
            if (!TryResolveBase(p.ChampionName, lvl, out double maxHp, out double armor, out double mr))
            {
                usedFallback = true;
                return FallbackDefender; // champion name matched neither the cached set nor the summary
            }

            // Add the target's item resistances/health (their visible build).
            for (int j = 0; j < p.ItemCount && j < p.ItemIds.Length; j++)
            {
                var item = ItemRepository.Get(p.ItemIds[j].ToString());
                if (item is null) continue;
                armor += item.Stats.Armor;
                mr += item.Stats.Mr;
                maxHp += item.Stats.Hp;
            }

            // CurrentHP: the Live Client API still does not expose an enemy's real current health
            // directly, but when a TargetHealthTracker is available its honest LOWER-BOUND estimate
            // (100% HP anchored at this champion's last known respawn, minus OUR OWN calculated combo
            // damage dealt to them since) narrows this gap — see TargetHealthTracker's doc comment for
            // why it can only ever understate real missing HP, never overstate it. No tracker (null,
            // e.g. every pre-existing call site) keeps the prior "CurrentHP == MaxHP" (full-HP 100→0,
            // "can this combo kill from full") behavior exactly as before.
            usedFallback = false;
            double currentHp = targetHealthTracker is not null
                ? maxHp - targetHealthTracker.GetAccumulatedDamage(p.ChampionName, maxHp)
                : maxHp;
            return new DefenderStat(CurrentHP: currentHp, MaxHP: maxHp, Armor: armor, Mr: mr, Shield: 0);
        }

        usedFallback = true;
        return FallbackDefender; // no living enemy on the scoreboard
    }

    /// <summary>Resolves an enemy's base HP/armor/MR at <paramref name="level"/> from the most
    /// specific source available: the fully-detailed <see cref="ChampionRepository"/> (the
    /// cached 5, and any test-injected stats) first, then the all-champion Data Dragon summary
    /// (<see cref="ChampionSummary"/>) so ANY champion — bots and non-cached enemies included —
    /// gets real resistances instead of the zeroed fallback. Returns false only when the name
    /// matches neither source.</summary>
    private static bool TryResolveBase(string championName, int level, out double hp, out double armor, out double mr)
    {
        // Defensive trim (loop-38 continuation 4): a live-API string that picked up incidental
        // leading/trailing whitespace would fail both dictionary lookups below even though
        // OrdinalIgnoreCase tolerates casing — cheap, safe, backward-compatible (a no-op for any
        // already-clean name) hardening while the raw value is inspected on-screen (see
        // ComboHudResult.DefenderIsFallback's warning text) to confirm or rule this out.
        championName = championName?.Trim() ?? string.Empty;

        // loop-38 continuation 6: LoadedIds==173 (full roster) while the lookup still failed for a
        // visible, named target rules out "champion missing from the dictionary" — the only remaining
        // explanation is the resolved name itself isn't the English id both tables below are keyed by.
        // If the Live Client API returned the champion's Korean client-language display name instead
        // (e.g. "아리"), translate it back to the English id first; a no-op for any name that isn't a
        // recognized Korean display name (including already-English ids).
        if (ChampionSummary.ResolveKoreanName(championName) is { } englishId)
            championName = englishId;

        var champ = ChampionRepository.Get(championName);
        if (champ is not null)
        {
            // Real League per-level growth (LevelGrowth.Stat), not naive linear interpolation —
            // see LevelGrowth's doc comment for the user-reported overlay-vs-real-game gap this
            // fixes (armor 101 shown vs 97 actual at the same build).
            hp = LevelGrowth.Stat(champ.BaseStats.Hp, champ.StatsPerLevel.Hp, level);
            armor = LevelGrowth.Stat(champ.BaseStats.Armor, champ.StatsPerLevel.Armor, level);
            mr = LevelGrowth.Stat(champ.BaseStats.Mr, champ.StatsPerLevel.Mr, level);
            return true;
        }

        var summary = ChampionSummary.Get(championName);
        if (summary is not null)
        {
            hp = summary.HpAt(level);
            armor = summary.ArmorAt(level);
            mr = summary.MrAt(level);
            return true;
        }

        hp = armor = mr = 0;
        return false;
    }

    private void Log(string message)
        => Debug.WriteLine($"[ComboRunner @{_clock.NowMs}] {message}");

    public void Dispose()
    {
        // (loop 117) detach the live-refresh subscriptions alongside the primary one.
        foreach (var id in _refreshSubscriptionIds)
            EventBus.EventBus.Unsubscribe(id);
        _refreshSubscriptionIds.Clear();

        if (_subscriptionId is null) return;
        EventBus.EventBus.Unsubscribe(_subscriptionId);
        _subscriptionId = null;
    }
}

/// <summary>Combo target-selection mode (user mandate: "대상=같은 포지션 상대+선택 옵션").
/// <see cref="Auto"/> = same-position enemy when lanes are known, else the first living enemy;
/// <see cref="Manual"/> = the user-selected champion (see <see cref="Config.TargetingConfig"/>).</summary>
public enum TargetMode
{
    Auto,
    Manual,
}

/// <summary>(loop 487) What the combo editor shows while a sequence is being built:
/// <paramref name="Resolved"/> with the honest <paramref name="Min"/>-<paramref name="Max"/> span
/// around it, and the BASIS it was computed on. <paramref name="IsLive"/> false means the stated
/// out-of-game reference (see <see cref="ComboRunner.ComputePreview"/>) — the editor must say so
/// rather than let a reference read as a prediction.</summary>
public sealed record ComboPreview(
    double Resolved, double Min, double Max, bool IsLive, string TargetChampion, int ReferenceLevel);
