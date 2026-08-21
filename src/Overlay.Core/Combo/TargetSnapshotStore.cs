using System.Text.Json;
using Overlay.Core.Config;

namespace Overlay.Core.Combo;

/// <summary>
/// Persists a captured defender snapshot for the DEFENDER-side "virtual model" theory-crafting
/// feature (loop 38 continuation 19) — the same ability the ATTACKER side already has via
/// <see cref="Items.ItemBuildStore"/>: freeze a target's Armor/Mr/MaxHp so a combo can be tested
/// against it later even when that target isn't live/present.
///
/// Follows <see cref="Items.ItemBuildStore"/>'s exact persistence shape (one JSON-string value per
/// key, schema home <see cref="TargetSnapshotsConfig"/>, so it survives the typed
/// <see cref="ConfigSchema"/> round-trip) with ONE deliberate difference: scoped per COMBO id, not
/// per champion. Runes/items are a property of the CASTER champion (every combo built for that
/// champion shares one build); a target snapshot is a property of ONE specific combo — the user is
/// theory-crafting "can THIS combo kill THIS specific hypothetical target", and different combos
/// for the same champion may legitimately be tested against different captured targets.
///
/// <see cref="Combo.ComboRunner.CaptureTargetSnapshot"/> is the only writer (re-runs the same live
/// target resolution the real trigger path uses and saves it here — never fabricates a value).
/// <see cref="GetUseSnapshot"/> is the paired per-combo toggle: CLAUDE.md Policy P2 requires this
/// default OFF (absent/false) so a combo's damage silently deviating from live target data is
/// impossible without an explicit user opt-in — <see cref="ComboRunner.BuildContext"/> only
/// substitutes the snapshot when this toggle is explicitly true AND a snapshot exists.
/// </summary>
public static class TargetSnapshotStore
{
    private const string CaptureKeyPrefix = "targetSnapshots.captures.";
    private const string ToggleKeyPrefix = "targetSnapshots.useSnapshot.";
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>Loads the captured snapshot for <paramref name="comboId"/>, or null when none was
    /// ever captured (or the stored JSON is corrupt) — never a fabricated default snapshot.</summary>
    public static TargetSnapshot? Load(ConfigManager config, string comboId)
    {
        if (config.Get(CaptureKeyPrefix + comboId) is not string raw) return null;
        try { return JsonSerializer.Deserialize<TargetSnapshot>(raw, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Persists <paramref name="snapshot"/> as <paramref name="comboId"/>'s captured
    /// defender.</summary>
    public static void Save(ConfigManager config, string comboId, TargetSnapshot snapshot)
        => config.Set(CaptureKeyPrefix + comboId, JsonSerializer.Serialize(snapshot, Options));

    /// <summary>True only when the user explicitly checked this combo's "use captured target"
    /// toggle — absent/anything-else-than-literal-true reads false (Policy P2, never defaulted on).</summary>
    public static bool GetUseSnapshot(ConfigManager config, string comboId)
        => config.Get(ToggleKeyPrefix + comboId) is true;

    /// <summary>Persists the per-combo "use captured target" toggle.</summary>
    public static void SetUseSnapshot(ConfigManager config, string comboId, bool value)
        => config.Set(ToggleKeyPrefix + comboId, value);
}

/// <summary>Persisted shape (stored as a JSON string under <c>targetSnapshots.captures.{comboId}</c>).
/// <see cref="Armor"/>/<see cref="Mr"/>/<see cref="MaxHp"/> mirror exactly what
/// <c>ComboRunner.BuildDefenderFor</c>'s result already carries (<see cref="Damage.DefenderStat"/>'s
/// Armor/Mr/MaxHP fields) at the moment of capture. <see cref="CapturedAtUtcMs"/> (Unix epoch
/// milliseconds, matching <see cref="Overlay.Overlay.IClock.NowMs"/>'s own unit) lets the UI show an honest
/// "captured X ago" instead of implying the snapshot is live.</summary>
public sealed record TargetSnapshot(
    string ChampionName, double Armor, double Mr, double MaxHp, long CapturedAtUtcMs);
