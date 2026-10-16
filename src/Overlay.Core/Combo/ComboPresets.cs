using System.Text.Json;

namespace Overlay.Core.Combo;

/// <summary>
/// Built-in combos per champion (<c>data/combo_presets.json</c>), offered in the combo editor so a
/// new user has something to bind before building their own (1.0 plan, must-add 4). Each preset is
/// only a name and a slot sequence; <see cref="ComboEditor.AddPreset"/> turns it into a normal
/// saved combo built from the champion's live palette, so presets carry no damage numbers of their
/// own and follow patches like any user combo.
///
/// <para>The file records where each sequence came from (<c>sources</c>, community guides found by
/// web search) — the sequences are play advice, not game data, and are reviewed like any other
/// curated file. Never throws: a missing or malformed file means no presets.</para>
/// </summary>
public static class ComboPresets
{
    public sealed record Preset(string Kind, string Name, IReadOnlyList<string> Sequence);

    private static IReadOnlyDictionary<string, IReadOnlyList<Preset>>? _all;
    private static readonly object Gate = new();

    private static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "combo_presets.json");

    /// <summary>Presets for <paramref name="championId"/> (Data Dragon id, case-insensitive), or empty.</summary>
    public static IReadOnlyList<Preset> For(string championId)
        => All.TryGetValue(championId, out var list) ? list : Array.Empty<Preset>();

    public static IReadOnlyDictionary<string, IReadOnlyList<Preset>> All
    {
        get
        {
            lock (Gate)
            {
                if (_all is not null) return _all;
                try { _all = Parse(File.ReadAllText(DefaultPath)); }
                catch { _all = new Dictionary<string, IReadOnlyList<Preset>>(StringComparer.OrdinalIgnoreCase); }
                return _all;
            }
        }
    }

    /// <summary>Parses the file shape <c>{ "Zed": [ {"kind","nameKo","sequence":[...]} ] }</c>;
    /// keys starting with "_" are notes. Entries missing a name or sequence are skipped.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<Preset>> Parse(string json)
    {
        var result = new Dictionary<string, IReadOnlyList<Preset>>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (var champ in doc.RootElement.EnumerateObject())
        {
            if (champ.Name.StartsWith('_') || champ.Value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<Preset>();
            foreach (var p in champ.Value.EnumerateArray())
            {
                var name = p.TryGetProperty("nameKo", out var n) ? n.GetString() : null;
                var kind = p.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name) || !p.TryGetProperty("sequence", out var seq)
                    || seq.ValueKind != JsonValueKind.Array) continue;
                var slots = seq.EnumerateArray().Select(s => s.GetString() ?? "").Where(s => s.Length > 0).ToList();
                if (slots.Count > 0) list.Add(new Preset(kind, name!, slots));
            }
            if (list.Count > 0) result[champ.Name] = list;
        }
        return result;
    }
}
