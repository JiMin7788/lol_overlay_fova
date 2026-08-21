using System.Text.Json;
using Overlay.Core.Config;

namespace Overlay.Core.Items;

/// <summary>
/// Persists a champion's "hypothetical build" item selection from the M04 combo editor's
/// search-to-add item picker, following the EXACT precedent
/// <see cref="Runes.RuneSelectionStore"/> already established for runes: one JSON-string value
/// per champion under <c>items.builds.{championId}</c> (schema home: <see cref="ItemsConfig"/>),
/// so it survives the typed <see cref="ConfigSchema"/> round-trip instead of being dropped as an
/// unknown key.
///
/// Scoped per CHAMPION, not per combo — same rationale as runes: a champion has one
/// hypothetical build the user is theory-crafting on top of their real live stats, and every
/// combo built for that champion should read the same selection. This is what finally wires the
/// previously UI-only "hypothetical build" chip row (loop 38 item 2) into
/// <see cref="Combo.ComboRunner"/>'s damage calculation — see <c>ComboRunner.BuildAttacker</c>.
/// </summary>
public static class ItemBuildStore
{
    private const string KeyPrefix = "items.builds.";
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>Loads the persisted selection for <paramref name="championId"/>, or null when
    /// none was ever saved (or the stored JSON is corrupt) — never a fabricated default
    /// build.</summary>
    public static ItemBuildSelection? Load(ConfigManager config, string championId)
    {
        if (config.Get(KeyPrefix + championId) is not string raw) return null;
        try { return JsonSerializer.Deserialize<ItemBuildSelection>(raw, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>Persists <paramref name="itemIds"/> as <paramref name="championId"/>'s
    /// hypothetical build.</summary>
    public static void Save(ConfigManager config, string championId, IReadOnlyList<string> itemIds)
        => config.Set(KeyPrefix + championId,
            JsonSerializer.Serialize(new ItemBuildSelection(championId, itemIds), Options));
}

/// <summary>Persisted shape (stored as a JSON string under <c>items.builds.{championId}</c>).</summary>
public sealed record ItemBuildSelection(string ChampionId, IReadOnlyList<string> ItemIds);
