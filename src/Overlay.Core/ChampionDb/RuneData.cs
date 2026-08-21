namespace Overlay.Core.ChampionDb;

/// <summary>
/// M11 rune static data — see docs/modules/M11_CHAMPION_DATABASE.md "Data Model".
/// Sourced exclusively from Data Dragon's runesReforged.json (P1); never hand-typed.
/// </summary>
public sealed class RuneData
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>Owning rune tree name, e.g. "Domination", "Sorcery".</summary>
    public string Tree { get; init; } = string.Empty;

    /// <summary>Null when the rune's real-time effect cannot be confirmed via any
    /// public API (<see cref="ApiTrackable"/> == false); M06 Rune Engine falls back
    /// to manual checkbox handling for those runes.</summary>
    public string? EffectFormula { get; init; }

    public bool ApiTrackable { get; init; } = true;

    /// <summary>Relative Data Dragon icon path (e.g. "perk-images/Styles/Domination/CheapShot/
    /// CheapShot.png"), served un-versioned at <c>cdn/img/{icon}</c>. Empty if the source JSON had
    /// no icon field. Added for the M04 icon-tile rune picker (loop 38 pending change item 5).</summary>
    public string Icon { get; init; } = string.Empty;
}
