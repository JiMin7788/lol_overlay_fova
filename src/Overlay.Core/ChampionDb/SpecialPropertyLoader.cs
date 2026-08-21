using System.Text.Json;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Loads Special Property override files from data/special_properties/*.json and merges
/// them into already-loaded <see cref="ChampionData"/> instances (M11 Internal Logic
/// step 2). Each file is a JSON array of <see cref="ChampionSpecialProperty"/> entries;
/// this is the sole extension point for adding a champion's special-stack mechanic
/// (e.g. a passive stack counter) without changing any calculation-engine code.
/// </summary>
public static class SpecialPropertyLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads every *.json file directly under <paramref name="directory"/> and
    /// merges each entry into the matching champion in <paramref name="champions"/> by
    /// <see cref="ChampionSpecialProperty.ChampionId"/>. Entries for unknown champion
    /// ids are skipped (the champion may not be in the currently-loaded sample set).
    /// Missing directory is not an error — override files are optional.</summary>
    public static void LoadAndMerge(string directory, IReadOnlyDictionary<string, ChampionData> champions)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<ChampionSpecialProperty>>(json, Options)
                ?? throw new InvalidDataException($"special property file '{path}' deserialized to null");

            foreach (var entry in entries)
            {
                if (champions.TryGetValue(entry.ChampionId, out var champion))
                {
                    champion.SpecialProperties[entry.Key] = entry;
                }
            }
        }
    }
}
