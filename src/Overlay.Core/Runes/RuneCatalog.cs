using System.Text.Json;

namespace Overlay.Core.Runes;

/// <summary>One rune style (tree) as the client presents it: the keystone row plus three minor
/// rows, the three stat-shard rows, and which styles may serve as its secondary.</summary>
public sealed class RuneStyleInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Data-Dragon-relative icon path ("perk-images/Styles/....png").</summary>
    public string IconPath { get; init; } = "";
    public IReadOnlyList<int> AllowedSubStyles { get; init; } = Array.Empty<int>();
    /// <summary>[0] = keystone row, [1..3] = minor rows, each a list of perk ids in display order.</summary>
    public IReadOnlyList<IReadOnlyList<int>> PerkRows { get; init; } = Array.Empty<IReadOnlyList<int>>();
    /// <summary>The three stat-shard rows (kStatMod slots), in display order.</summary>
    public IReadOnlyList<IReadOnlyList<int>> StatRows { get; init; } = Array.Empty<IReadOnlyList<int>>();
}

/// <summary>
/// The full rune catalog for the rune-page UI (M33 dashboard rune editor): styles/slot layout
/// from the CommunityDragon <c>perkstyles.json</c> cache and per-perk display data (localized
/// name + icon path) from <c>perks.json</c> — both under <c>Data/communitydragon/</c>, fetched
/// by <c>tools/fetch_rune_catalog.py</c>. Patch-dependent layout (including the stat-shard rows)
/// is DATA here, never hardcoded (CLAUDE.md hard rule; the shard rows are exactly the
/// <c>kStatMod</c> slots the client itself renders).
///
/// <para>Static lazy-load with <see cref="ResetForTests"/>, mirroring <c>SkillDamageDb</c>.</para>
/// </summary>
public static class RuneCatalog
{
    private static readonly object Gate = new();
    private static IReadOnlyList<RuneStyleInfo>? _styles;
    private static Dictionary<int, (string Name, string IconPath, string Desc)>? _perks;

    public static IReadOnlyList<RuneStyleInfo> Styles
    {
        get { EnsureLoaded(); return _styles!; }
    }

    public static RuneStyleInfo? GetStyle(int id)
    {
        EnsureLoaded();
        foreach (var s in _styles!)
            if (s.Id == id) return s;
        return null;
    }

    /// <summary>Localized name + normalized icon path + plain-text short description (markup
    /// stripped) for any perk id (runes AND stat shards); null for an unknown id.</summary>
    public static (string Name, string IconPath, string Desc)? GetPerk(int id)
    {
        EnsureLoaded();
        return _perks!.TryGetValue(id, out var p) ? p : null;
    }

    public static bool IsLoaded
    {
        get { EnsureLoaded(); return _styles!.Count > 0; }
    }

    public static void ResetForTests()
    {
        lock (Gate) { _styles = null; _perks = null; }
    }

    private static void EnsureLoaded()
    {
        if (_styles is not null) return;
        lock (Gate)
        {
            if (_styles is not null) return;
            var dir = Path.Combine(AppContext.BaseDirectory, "Data", "communitydragon");
            if (!Directory.Exists(dir))
                dir = Path.Combine(AppContext.BaseDirectory, "data", "communitydragon");
            try
            {
                _perks = LoadPerks(Path.Combine(dir, "perks.json"));
                _styles = LoadStyles(Path.Combine(dir, "perkstyles.json"));
            }
            catch
            {
                // Missing/corrupt catalog: the rune editor simply doesn't render (empty catalog),
                // nothing else in the app depends on it.
                _perks ??= new Dictionary<int, (string, string, string)>();
                _styles ??= Array.Empty<RuneStyleInfo>();
            }
        }
    }

    /// <summary>CommunityDragon icon paths come as "/lol-game-data/assets/v1/perk-images/..." —
    /// normalize to the Data-Dragon-relative "perk-images/..." form the icon loader expects.</summary>
    private static string NormalizeIconPath(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        int i = raw.IndexOf("perk-images/", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? raw[i..] : raw.TrimStart('/');
    }

    private static Dictionary<int, (string, string, string)> LoadPerks(string path)
    {
        var perks = new Dictionary<int, (string, string, string)>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            if (!p.TryGetProperty("id", out var idEl)) continue;
            perks[idEl.GetInt32()] = (
                p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                NormalizeIconPath(p.TryGetProperty("iconPath", out var ip) ? ip.GetString() : null),
                // longDesc carries the actual NUMBERS (damage arrays, cooldowns, ranged
                // modifiers) the 2026-07-26 request asked for; shortDesc is the fallback.
                StripMarkup(p.TryGetProperty("longDesc", out var ld)
                            && ld.GetString() is { Length: > 0 } longDesc ? longDesc
                    : p.TryGetProperty("shortDesc", out var sd) ? sd.GetString() : null));
        }
        return perks;
    }

    /// <summary>The catalog descriptions carry client UI markup (&lt;b&gt;,
    /// &lt;lol-uikit-...&gt;, &lt;br&gt;, &lt;li&gt;) — reduce to plain tooltip text; list items
    /// become bulleted lines.</summary>
    private static string StripMarkup(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string s = raw.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                      .Replace("<li>", "\n· ", StringComparison.OrdinalIgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, "\n{3,}", "\n\n");
        return System.Text.RegularExpressions.Regex.Replace(s, "[ \t]{2,}", " ").Trim();
    }

    private static IReadOnlyList<RuneStyleInfo> LoadStyles(string path)
    {
        var styles = new List<RuneStyleInfo>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var s in doc.RootElement.GetProperty("styles").EnumerateArray())
        {
            var perkRows = new List<IReadOnlyList<int>>();
            var statRows = new List<IReadOnlyList<int>>();
            foreach (var slot in s.GetProperty("slots").EnumerateArray())
            {
                var ids = slot.GetProperty("perks").EnumerateArray().Select(e => e.GetInt32()).ToList();
                string type = slot.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (type == "kStatMod") statRows.Add(ids);
                else perkRows.Add(ids);   // kKeyStone first, then the regular rows, in file order
            }
            styles.Add(new RuneStyleInfo
            {
                Id = s.GetProperty("id").GetInt32(),
                Name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                IconPath = NormalizeIconPath(s.TryGetProperty("iconPath", out var ip) ? ip.GetString() : null),
                AllowedSubStyles = s.TryGetProperty("allowedSubStyles", out var sub)
                    ? sub.EnumerateArray().Select(e => e.GetInt32()).ToList()
                    : new List<int>(),
                PerkRows = perkRows,
                StatRows = statRows,
            });
        }
        // Stable display order: the client shows Precision/Domination/Sorcery/Resolve/Inspiration;
        // the file is alphabetical. Order by the conventional style-id order instead of guessing:
        // 8000, 8100, 8200, 8400, 8300 — but derive nothing beyond ORDER from these ids.
        int[] order = { 8000, 8100, 8200, 8400, 8300 };
        return styles.OrderBy(s => { int i = Array.IndexOf(order, s.Id); return i < 0 ? int.MaxValue : i; })
                     .ToList();
    }
}
