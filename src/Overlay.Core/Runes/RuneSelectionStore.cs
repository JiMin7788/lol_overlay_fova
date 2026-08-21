using System.Text.Json;
using Overlay.Core.Config;

namespace Overlay.Core.Runes;

/// <summary>
/// Persists a champion's rune selection (feeds <see cref="RuneEngine.GetActiveEffects"/>'s
/// <see cref="UserRuneConfig"/>) plus the manual-activation checkbox state for the non-API-
/// trackable runes (<see cref="RuneApiTrackability.NonTrackableRuneIds"/>), following the exact
/// <c>combos.saved.{id}</c> precedent M04's <c>ComboEditor.SaveCombo</c>/<c>LoadCombo</c> already
/// established: one JSON-string value per champion under <c>runes.selections.{championId}</c>
/// (schema home: <see cref="RunesConfig"/>), so it survives the typed <see cref="ConfigSchema"/>
/// round-trip instead of being dropped as an unknown key.
///
/// Scoped per CHAMPION, not per combo: <see cref="RuneEngine.GetActiveEffects"/> itself takes a
/// championId (not a comboId), and a champion has one rune page — every combo built for that
/// champion should read the same selection.
/// </summary>
public static class RuneSelectionStore
{
    private const string KeyPrefix = "runes.selections.";
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>Loads the persisted selection for <paramref name="championId"/>, or null when
    /// none was ever saved (or the stored JSON is corrupt) — never a fabricated default
    /// selection.</summary>
    public static RuneSelection? Load(ConfigManager config, string championId)
    {
        if (config.Get(KeyPrefix + championId) is not string raw) return null;
        try { return JsonSerializer.Deserialize<RuneSelection>(raw, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Persists <paramref name="selection"/> under its own champion's key.</summary>
    public static void Save(ConfigManager config, RuneSelection selection)
        => config.Set(KeyPrefix + selection.ChampionId, JsonSerializer.Serialize(selection, Options));

    /// <summary>Builds the <see cref="UserRuneConfig"/> <see cref="RuneEngine.GetActiveEffects"/>
    /// needs from a persisted selection (empty when <paramref name="selection"/> is null — the
    /// pre-existing dormant behavior for a champion with no saved rune selection yet).</summary>
    public static UserRuneConfig ToUserRuneConfig(RuneSelection? selection)
        => new(selection?.SelectedRuneIds ?? Array.Empty<string>());
}

/// <summary>Persisted shape (stored as a JSON string under <c>runes.selections.{championId}</c>).
/// <see cref="ManualFlags"/> mirrors <see cref="RuneEngine.SetManualFlag"/>'s own semantics —
/// absent/false == inactive, NEVER defaulted true (CLAUDE.md Policy P2 / RuneEngine's own Policy
/// Compliance Checklist item 1) — a UI checkbox writes <c>true</c> here only on an explicit user
/// check; an unset entry (a selected-but-never-toggled non-trackable rune) reads back false.</summary>
public sealed record RuneSelection(
    string ChampionId,
    IReadOnlyList<string> SelectedRuneIds,
    IReadOnlyDictionary<string, bool> ManualFlags);
