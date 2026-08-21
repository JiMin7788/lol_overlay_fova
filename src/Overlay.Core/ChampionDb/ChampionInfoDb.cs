using System.Text.Json;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Riot's own per-champion play-style scores from the cached Data Dragon
/// <c>champion.json</c> (<c>info.attack</c>/<c>info.magic</c>, 0-10) plus tags — the P1 source
/// behind the champ-select team-composition analysis (2026-07-25 request). Static lazy-load
/// from the NEWEST cached ddragon version, mirroring <see cref="ChampionSummary"/>.
/// </summary>
public static class ChampionInfoDb
{
    public sealed record Info(int Key, string Id, string Name, int Attack, int Magic,
        IReadOnlyList<string> Tags);

    private static readonly object Gate = new();
    private static Dictionary<int, Info>? _byKey;

    public static Info? GetByKey(int championKey)
    {
        EnsureLoaded();
        return _byKey!.TryGetValue(championKey, out var i) ? i : null;
    }

    public static void ResetForTests()
    {
        lock (Gate) _byKey = null;
    }

    private static void EnsureLoaded()
    {
        if (_byKey is not null) return;
        lock (Gate)
        {
            if (_byKey is not null) return;
            var map = new Dictionary<int, Info>();
            try
            {
                var root = Path.Combine(AppContext.BaseDirectory, "data", "ddragon");
                var file = Directory.GetFiles(root, "champion.json", SearchOption.AllDirectories)
                    .OrderBy(p => p, StringComparer.Ordinal).LastOrDefault();
                if (file is not null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var champ in doc.RootElement.GetProperty("data").EnumerateObject())
                    {
                        var v = champ.Value;
                        if (!int.TryParse(v.GetProperty("key").GetString(), out int key)) continue;
                        var info = v.GetProperty("info");
                        map[key] = new Info(
                            key,
                            champ.Name,
                            v.TryGetProperty("name", out var n) ? n.GetString() ?? champ.Name : champ.Name,
                            info.GetProperty("attack").GetInt32(),
                            info.GetProperty("magic").GetInt32(),
                            v.TryGetProperty("tags", out var tags)
                                ? tags.EnumerateArray().Select(t => t.GetString() ?? "").ToList()
                                : new List<string>());
                    }
                }
            }
            catch { /* missing cache → empty db; the comp section simply doesn't render */ }
            _byKey = map;
        }
    }
}
