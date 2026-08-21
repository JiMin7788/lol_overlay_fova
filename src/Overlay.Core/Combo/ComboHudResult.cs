using System.Collections.Generic;
using System.Linq;

namespace Overlay.Core.Combo;

/// <summary>
/// HUD-facing wrapper published by <see cref="ComboRunner"/> on <c>UI.COMBO_RESULT</c>:
/// the computed <see cref="ComboResult"/> plus two display-only fields the combo card
/// needs but the engine result does not carry — the resolved target champion (used for its
/// portrait) and a human-readable command string of the combo's ability sequence.
///
/// <para>Introduced so the richer combo card can be rendered WITHOUT adding presentation
/// fields to the engine's <see cref="ComboResult"/> (which stays a pure damage result).</para>
/// </summary>
/// <param name="Result">The M03 engine result (total damage, lethality, node breakdown).</param>
/// <param name="TargetChampion">Display name of the enemy the combo was computed against
/// (the first living enemy <see cref="ComboRunner"/> resolves), or empty when there is no
/// living-enemy target.</param>
/// <param name="CommandLabel">The ability sequence in node order, e.g. "Q-Q-E-A-W-R"
/// (A = auto-attack); empty when the combo has no ability/AA nodes.</param>
/// <param name="TargetArmor">The resolved target's Armor used for this trigger's mitigation.
/// Display-only — added (loop 38) so the combo card can prove, on screen, whether a real
/// non-zero resistance was actually used for this damage number, instead of requiring a log
/// file to answer "was armor/MR reflected at all?".</param>
/// <param name="TargetMr">Same as <paramref name="TargetArmor"/> for magic resist.</param>
/// <param name="DefenderIsFallback">True when <c>ComboRunner.BuildDefenderFor</c> could not
/// resolve the target's real resistances and fell back to the zeroed <c>FallbackDefender</c>
/// (Armor=0, Mr=0). <b>Loop-38 continuation fix</b>: this is intentionally a SEPARATE signal
/// from <paramref name="TargetChampion"/> being empty — a target row can resolve fine (a real
/// name/portrait, <paramref name="TargetChampion"/> non-empty) while the champion's base
/// resistance lookup still independently fails, which is exactly the bug a user hit (visible
/// target, no warning, but 0/0 armor/MR) when the on-screen warning was wrongly keyed off
/// <paramref name="TargetChampion"/> instead of this field.</param>
/// <param name="DamageTypesSummary">Distinct <c>ComboDamageType</c> names ("Physical"/"Magic"/"True")
/// across the executed graph's nodes, joined with "+" (e.g. "Magic" for a pure-magic single-skill
/// combo). Display-only diagnostic (loop 38) — added because a user-reported combo showed a damage
/// number that exactly matched the raw, unmitigated ability tooltip despite a confirmed-nonzero target
/// MR, and every other link in the pipeline traced correctly by reading the source; this proves ON
/// SCREEN what type each node actually carried AT EXECUTION TIME, the one remaining unverified link.</param>
/// <param name="UsingSnapshotTarget">True when this trigger's defender came from a captured
/// <see cref="TargetSnapshotStore"/> snapshot instead of the live-resolved target (loop 38
/// continuation 19's defender "virtual model" toggle). CLAUDE.md Policy P2/UX-honesty requirement:
/// the HUD must always make it obvious which mode produced this number — see
/// <c>OverlayHost.BuildComboCard</c>'s "가상 타겟 사용 중" line.</param>
/// <param name="TargetMaxHp">The resolved target's MAX HP (base+level+items), used to scale the
/// combo card's LoL-style enemy health bar (loop 113). 0 when no target/fallback.</param>
/// <param name="TargetLevel">The resolved target's champion level (from the scoreboard entry), shown
/// in the health bar's level box. 0 when unknown.</param>
/// <param name="DesignatedCleared">(loop 115) True when a HARD Manual pin's champion is dead/absent so
/// the designated target was RESET (ResolveTarget returned null rather than retargeting). The card then
/// shows a cleared "지정 적 사망/시야밖 — 초기화" state — armor/mr/HP blanked, no health bar — with
/// <see cref="TargetChampion"/> carrying the pinned champion's name for the label. Distinct from
/// <see cref="DefenderIsFallback"/> (a lookup failure): this is an intentional, user-pinned reset.
/// Policy P2: vision state is not in the Live Client API, so death is the reset trigger, not vision.</param>
/// <param name="Sequence">(M28 §3) The combo's ability/AA sequence as STRUCTURED tokens (parallel to
/// the flat <paramref name="CommandLabel"/> string, same order and same skip rules), each carrying
/// which per-node KNOB — if any — the user set above its floor. Lets the overlay decorate each
/// sequence chip with the M28 §3 visual language (max-damage ▲, charge/duration fill-bar, ×n count)
/// with NO champion-specific branching. Empty/every-token-None ⇒ the overlay renders exactly the
/// plain chips it did before this field existed (the §4 "no knob above floor = byte-identical"
/// regression guard).</param>
public sealed record ComboHudResult(
    ComboResult Result, string TargetChampion, string CommandLabel,
    double TargetArmor = 0, double TargetMr = 0, bool DefenderIsFallback = false,
    string DamageTypesSummary = "", bool UsingSnapshotTarget = false,
    double TargetMaxHp = 0, int TargetLevel = 0, bool DesignatedCleared = false,
    IReadOnlyList<ComboSequenceToken>? Sequence = null,
    // (issue: overlay skill icons) The combo's champion (Data Dragon English id, resolved live from
    // the active player, else the saved combo's champion) — lets the overlay draw each ability chip's
    // REAL P/Q/W/E/R icon instead of a bare key letter. Empty ⇒ the chip falls back to its letter.
    string CasterChampion = "",
    // (issue: combo name field) The saved combo's user-given name (SavedCombo.Name), shown at the
    // combo card's bottom-right. Empty ⇒ nothing drawn.
    string ComboName = "",
    // (§76) True when the range FLOOR was pushed down by the user's assumed ENEMY DEFENSIVE runes
    // (M24 P5, `combo.assumedEnemyDefensiveRunes`) — the min box is then showing damage against a
    // hypothetically tankier target than the one actually observed. Determined by comparison in
    // ComboRunner (the floor is resolved with and without the assumption), not inferred from the
    // setting being non-empty, so a rune that happens not to move the number does not label it.
    bool FloorFromAssumedEnemyRunes = false,
    // (§76) True when the range CEILING was multiplied up by an equipped conditional AMPLIFIER rune's
    // best case (M24 P4 — Coup de Grace / Cut Down / Last Stand / PtA / First Strike). The rune's
    // condition is not reliably observable, so the ceiling assumes it holds.
    bool CeilingFromAmplifierRunes = false)
{
    /// <summary>(M28 §3.3) How many sequence tokens carry an above-floor knob — the "가정 N"
    /// assumption count shown next to the total, and (when &gt; 0) the signal that the kill verdict is
    /// assumption-dependent and must render its marker HOLLOW rather than solid. 0 ⇒ nothing assumed.</summary>
    public int AssumptionCount => Sequence?.Count(t => t.Knob != ComboKnobShape.None) ?? 0;
}

/// <summary>(M28 §1/§3) The knob "shape class" that decorates a combo sequence chip on the overlay,
/// derived from which per-node user knob is set — never from the champion. <see cref="None"/> = a plain
/// chip (no assumption). <see cref="MaxDamage"/> = a binary wall/debuff/state condition assumed MET
/// (gold border + ▲). <see cref="Slider"/> = a charge/distance or duration knob above 0 (fill-bar
/// underline at <see cref="ComboSequenceToken.SliderFraction"/>). <see cref="Count"/> = a summon-hit
/// or stack count above its floor (×n badge).</summary>
public enum ComboKnobShape { None, MaxDamage, Slider, Count }

/// <summary>(M28 §3) One token of the combo sequence for the overlay's visual language: the keystroke
/// <paramref name="Label"/> ("Q"/"W"/"E"/"R"/"A"), whether it is an ability chip (vs an auto-attack),
/// and the above-floor <paramref name="Knob"/> the user set on that node (with the slider fraction or
/// count it displays). A <see cref="ComboKnobShape.None"/> token decorates nothing — it renders as the
/// same plain chip as before M28 §3.</summary>
public sealed record ComboSequenceToken(
    string Label, bool IsAbility,
    ComboKnobShape Knob = ComboKnobShape.None,
    double SliderFraction = 0, int Count = 0,
    // (loop 176) Non-null for a summoner-spell token (Ignite/Flash) → the Data Dragon spell id
    // ("SummonerDot"/"SummonerFlash"); the overlay draws that spell's ICON instead of the text label.
    string? SummonerSpellId = null);
