using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;

namespace Overlay.Core.Runes;

/// <summary>
/// M06 spec's own damage-type enum for <see cref="RuneEffect.DamageType"/>. Distinct
/// from M05's <c>Damage.DamageType</c> (which M06 does not reference — see Dependencies:
/// M06 lists only M11/M01, not M05's internal types).
/// </summary>
public enum RuneDamageType
{
    PHYSICAL,
    MAGIC,
    TRUE,
}

/// <summary>
/// M06 Data Model's real <c>RuneEffect</c> shape (spec "Data Model" section) — richer
/// than the single-field placeholder M05 defined for itself (`Damage.RuneEffect(string
/// Id)`) before M06 existed. M06 does NOT touch M05's placeholder or `DamageCalcInput`;
/// wiring this richer type into M05's pipeline is an integration gap left for Lead /
/// whichever module assembles `DamageCalcInput` next (see M06 Agent Report Notes for
/// Reviewer).
/// </summary>
public sealed record RuneEffect(
    string RuneId,
    string Name,
    bool IsApiTrackable,
    int? CurrentStack,
    bool? IsManuallyActive,
    double? DamageBonus,
    RuneDamageType? DamageType);

/// <summary>
/// Minimal shape for "the champion's selected rune settings" (spec Internal Logic #1),
/// invented here because M04 (Combo Editor, the module that would normally produce
/// this) doesn't exist yet. Deliberately just a list of selected rune ids — no
/// build-import format, no rune-page-slot-structure precision beyond what M06 needs
/// (which rune ids are selected for this champion).
/// </summary>
public sealed record UserRuneConfig(IReadOnlyList<string> SelectedRuneIds);

/// <summary>
/// The caster's live stats needed to evaluate a manual rune's damage formula (see
/// <see cref="RuneEffectDb.Evaluate"/>). Invented here for the same reason
/// <see cref="UserRuneConfig"/> was: no upstream module publishes this exact shape.
/// <see cref="BonusAd"/>/<see cref="Ap"/> mirror M05's <see cref="Damage.AttackerStat"/> fields;
/// <see cref="MaxHealth"/>/<see cref="IsMelee"/> are NOT part of <c>AttackerStat</c> (M05 has no
/// health/range fields), so the caller (M03's <c>ComboEngine</c>) sources them from its own
/// <c>ExecutionContext</c> additions — see ComboEngine.cs's <c>ExecutionContext.CasterMaxHealth</c>/
/// <c>CasterIsMelee</c> doc comments for exactly where those come from.
/// </summary>
public sealed record RuneCasterStats(int Level, double BonusAd, double Ap, double MaxHealth, bool IsMelee);

/// <summary>
/// M06 Rune Engine: classifies a champion's selected runes into an API-trackable
/// ("auto") group and a non-trackable ("manual") group via <see cref="RuneData.ApiTrackable"/>,
/// approximately updates the auto group's stacks off <c>GAME.*</c> events, and lets a
/// caller (eventually M02/M04's checkbox UI) set the manual group's active flags
/// directly. <see cref="GetActiveEffects"/> merges both into the final <see cref="RuneEffect"/>[]
/// that M05 Damage Engine eventually consumes.
///
/// Policy Compliance Checklist item 1: a non-API-trackable rune is NEVER defaulted to
/// active — <see cref="_manualFlags"/> only ever contains an entry once
/// <see cref="SetManualFlag"/> is explicitly called with `true`; an unset manual rune
/// reads back `false` (see <see cref="GetActiveEffects"/>'s manual branch).
///
/// Policy Compliance Checklist item 2: the only data source for auto-group stack
/// updates is <see cref="EventBus"/>'s already-published <c>GAME.*</c> events (which
/// M01 itself sources exclusively from the official Live Client API — see
/// LiveClientEventPublisher.cs). RuneEngine performs no memory read, no OCR, no other
/// channel of any kind.
/// </summary>
public sealed class RuneEngine : IDisposable
{
    /// <summary>Arbitrary safety cap on the approximate stack counter (see
    /// <see cref="OnGameEvent"/>). Not a real per-rune max-stack value from the game —
    /// this is a near-recent-combat heuristic, not a stack simulator, so an unbounded
    /// counter would misrepresent precision it doesn't have. Documented honestly in
    /// the Agent Report as a heuristic choice, not a game-accurate constant.</summary>
    private const int MaxApproxStack = 5;

    private readonly Dictionary<string, int> _stacks = new();       // runeId -> currentStack, auto-tracked runes only
    private readonly Dictionary<string, bool> _manualFlags = new(); // runeId -> isActive, manual runes only; absent == false
    private readonly string _subscriptionId;

    private string _trackedChampionId = string.Empty;

    public RuneEngine()
    {
        _subscriptionId = EventBus.EventBus.Subscribe("GAME.*", OnGameEvent);
    }

    /// <summary>
    /// Looks up each selected rune id via <see cref="RuneRepository"/>, classifies it
    /// auto/manual off <see cref="RuneData.ApiTrackable"/>, and returns the merged
    /// <see cref="RuneEffect"/>[] (spec Internal Logic #1 and #4). Unknown rune ids
    /// (not present in the repository) are silently skipped — there is nothing
    /// meaningful to report for a rune M11 doesn't know about, and inventing a
    /// placeholder entry would risk exactly the "assumed active" failure mode the
    /// Policy Compliance Checklist forbids.
    /// </summary>
    /// <param name="casterStats">Optional — needed only to resolve a manual rune's
    /// <see cref="RuneEffect.DamageBonus"/> from <see cref="RuneEffectDb"/> (see
    /// <see cref="BuildManualEffect"/>). Defaults to <c>null</c> so every pre-existing 2-arg call
    /// site (tests, and any caller with no live stats available) keeps compiling unchanged and
    /// keeps getting <c>DamageBonus: null</c> — never a fabricated number when stats aren't
    /// supplied.</param>
    public RuneEffect[] GetActiveEffects(string championId, UserRuneConfig userRuneConfig, RuneCasterStats? casterStats = null)
    {
        _trackedChampionId = championId ?? string.Empty;

        var result = new List<RuneEffect>();
        foreach (var runeId in userRuneConfig.SelectedRuneIds)
        {
            RuneData? data = RuneRepository.Get(runeId);
            if (data is null) continue;

            result.Add(data.ApiTrackable ? BuildAutoEffect(data) : BuildManualEffect(data, casterStats));
        }
        return result.ToArray();
    }

    private RuneEffect BuildAutoEffect(RuneData data)
    {
        if (!_stacks.ContainsKey(data.Id))
            _stacks[data.Id] = 0; // first time this rune is classified auto in this session

        return new RuneEffect(
            RuneId: data.Id,
            Name: data.Name,
            IsApiTrackable: true,
            CurrentStack: _stacks[data.Id],
            IsManuallyActive: null,
            // No damageBonus/damageType number is produced: unlike M11's spellCalculations
            // (which FormulaInterpreter.Evaluate can resolve), RuneData.EffectFormula has no
            // published evaluable grammar in the M11 spec's Data Model — it is not paired
            // with an interpreter the way spell coefficients are. Computing a numeric bonus
            // here would mean inventing a formula grammar M11 never specified. Left null;
            // see Agent Report Notes for Reviewer.
            DamageBonus: null,
            DamageType: null);
    }

    /// <summary>
    /// Builds one manual rune's effect. <paramref name="casterStats"/> is consulted ONLY when the
    /// rune is already active (<paramref name="data"/>'s manual flag explicitly set true) — an
    /// inactive manual rune stays fully inert (DamageBonus/DamageType both null) regardless of
    /// whether stats are available, preserving the Policy Compliance invariant that a non-trackable
    /// rune's effect is never surfaced unless the user explicitly toggled it on. When active, the
    /// rune's formula (if <see cref="RuneEffectDb"/> has one — not every manual rune has a damage
    /// component, see rune_effects.json's own "_note") is evaluated live via
    /// <see cref="RuneEffectDb.Evaluate"/>; no formula or no caster stats leaves DamageBonus null,
    /// exactly as before this feature existed (never a fabricated number).
    /// </summary>
    private RuneEffect BuildManualEffect(RuneData data, RuneCasterStats? casterStats)
    {
        bool isActive = _manualFlags.TryGetValue(data.Id, out var flag) && flag; // unset == false, never defaults true

        double? damageBonus = null;
        RuneDamageType? damageType = null;
        if (isActive && casterStats is { } stats)
        {
            var formula = RuneEffectDb.Get(data.Id);
            if (formula is not null)
            {
                var (bonus, type) = RuneEffectDb.Evaluate(formula, stats);
                damageBonus = bonus;
                damageType = type;
            }
        }

        return new RuneEffect(
            RuneId: data.Id,
            Name: data.Name,
            IsApiTrackable: false,
            CurrentStack: null,
            IsManuallyActive: isActive,
            DamageBonus: damageBonus,
            DamageType: damageType);
    }

    /// <summary>Directly sets an approximate stack count for an API-trackable rune
    /// (spec Interfaces: "API 추적 가능한 룬만 해당"). Exposed for callers that want to
    /// push a stack value explicitly (e.g. tests, or a future finer-grained source);
    /// the automatic <c>GAME.*</c>-driven approximation in <see cref="OnGameEvent"/> also
    /// writes through this same backing state.</summary>
    public void UpdateStack(string runeId, int stackCount) => _stacks[runeId] = stackCount;

    /// <summary>Checkbox-linked manual override (spec Interfaces). Setting `false`
    /// (or never calling this at all) is the only way a non-trackable rune's
    /// <c>isManuallyActive</c> reads as inactive — see class doc's Policy Compliance
    /// note.</summary>
    public void SetManualFlag(string runeId, bool isActive) => _manualFlags[runeId] = isActive;

    /// <summary>
    /// Approximate auto-group stack heuristic (spec Internal Logic #2, worked example:
    /// Conqueror / "최근 전투 참여 감지 기반 근사 스택 갱신"). The only recent-combat-shaped
    /// signal among M01's published <c>GAME.*</c> events is <c>GAME.CHAMPION_DIED</c>
    /// (carries both the victim's champion name and the killer's). Heuristic:
    /// - the tracked champion (last `championId` passed to <see cref="GetActiveEffects"/>)
    ///   is the KILLER of a death event -> treat as "recently fought", bump every
    ///   currently-known auto-tracked rune's stack by 1 (capped at <see cref="MaxApproxStack"/>).
    ///   Real Conqueror stacks on landing damage on champions repeatedly, not only on
    ///   kills; a kill is the closest evidence available from these 8 event types, so
    ///   this under-counts real stacking activity that doesn't end in a kill.
    /// - the tracked champion is the VICTIM -> the fight is over for them; reset to 0.
    /// Both directions are explicitly an approximation, never presented as exact stack
    /// truth — see Agent Report Notes for Reviewer for the full honesty/limitations
    /// writeup. This uses only the already-published EventBus payload; no memory read,
    /// no OCR (Policy Compliance Checklist item 2).
    /// </summary>
    private void OnGameEvent(Event evt)
    {
        if (evt.Type != "GAME.CHAMPION_DIED") return;
        if (evt.Payload is not ChampionDiedPayload payload) return;
        if (_trackedChampionId.Length == 0) return;

        if (string.Equals(payload.KillerName, _trackedChampionId, StringComparison.Ordinal))
        {
            var runeIds = _stacks.Keys.ToArray(); // snapshot: bump every known auto-tracked rune
            foreach (var runeId in runeIds)
                _stacks[runeId] = Math.Min(_stacks[runeId] + 1, MaxApproxStack);
        }
        else if (string.Equals(payload.ChampionName, _trackedChampionId, StringComparison.Ordinal))
        {
            var runeIds = _stacks.Keys.ToArray();
            foreach (var runeId in runeIds)
                _stacks[runeId] = 0;
        }
    }

    public void Dispose() => EventBus.EventBus.Unsubscribe(_subscriptionId);
}
