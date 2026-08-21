namespace Overlay.Core.Combo;

/// <summary>(§40 combo overlay) One enemy row for the always-on portrait row: champion name (for the
/// portrait + label), whether the enemy is currently dead (grey-out), and its respawn countdown in
/// seconds (0 when alive). Produced by <see cref="ComboRunner.EnemyRoster"/>.</summary>
public readonly record struct EnemyRosterEntry(string ChampionName, bool IsDead, double RespawnTimer);

/// <summary>(§40 skill overlay) One skill-slot damage box: the slot key ("P"/"Q"/"W"/"E"/"R"/"A") and
/// its summed damage for the current combo against the selected target. Produced by
/// <see cref="ComboRunner.SkillDamageBySlot"/>.</summary>
public readonly record struct SkillSlotDamage(string Slot, double Damage);

/// <summary>(user request) Always-on per-skill damage readout vs the CURRENT target, independent of any
/// triggered combo: every ability + basic attack + passive's STANDALONE post-mitigation damage (armor /
/// MR / penetration applied) at the player's real current rank, from <see cref="ComboRunner.ComputeSkillPanel"/>.
/// Carries the target-header fields the skill overlay draws (champion name + resolved armor/MR). Unleveled
/// abilities read 0.</summary>
public sealed record SkillPanelResult(
    string TargetChampion, double TargetArmor, double TargetMr, bool DefenderIsFallback,
    System.Collections.Generic.IReadOnlyList<SkillSlotDamage> Slots,
    // (user request) The caster's Data Dragon champion id, so the skill overlay can draw each slot's REAL
    // P/Q/W/E/R ability icon (like the combo overlay) instead of a bare letter. Empty ⇒ letter fallback.
    string CasterChampion = "");
