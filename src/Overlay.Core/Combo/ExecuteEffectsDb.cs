using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Core;

/// <summary>
/// (M24 P9) Loads the curated hard-execute roster from <c>data/execute_effects.json</c>
/// into <see cref="ExecuteRule"/>s, lazily and once, mirroring the tolerant lazy-cache
/// convention of <see cref="Overlay.Core.Items.ItemEffectDb"/> /
/// <see cref="Overlay.Core.Combo.SkillDamageDb"/>.
///
/// Why a curated file and not pure inference: the BIN exposes execute-ish DataValue names
/// but unreliably (Akali R's "MaxExecuteThreshold" is a damage-scaling cap, NOT an execute),
/// so which effects are TRUE kill-lines — and their threshold expression, shield-pierce and
/// negation semantics — is curated knowledge. Rows flagged <c>needsConfirm</c> in the JSON
/// carry approximate numbers pending BIN/M11 dynamic sourcing; the loader keeps them but the
/// flag is preserved on <see cref="ExecuteRuleEntry.NeedsConfirm"/> for audits.
///
/// Tolerant of a missing/corrupt file: returns an empty roster, so the kill-line simply
/// falls back to pure damage — no execute augmentation. Never throws.
/// </summary>
public static class ExecuteEffectsDb
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static IReadOnlyList<ExecuteRuleEntry>? _cache;
    private static readonly object Gate = new();

    private static string DefaultPath =>
        Path.Combine(AppContext.BaseDirectory, "data", "execute_effects.json");

    /// <summary>All curated execute rules (empty when the file is missing/corrupt).</summary>
    public static IReadOnlyList<ExecuteRuleEntry> All() => Load();

    /// <summary>The rule with the given id, or null.</summary>
    public static ExecuteRuleEntry? Get(string id)
    {
        foreach (var e in Load())
            if (string.Equals(e.Rule.Id, id, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    private static IReadOnlyList<ExecuteRuleEntry> Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;

            var parsed = new List<ExecuteRuleEntry>();
            try
            {
                if (File.Exists(DefaultPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(DefaultPath));
                    if (doc.RootElement.TryGetProperty("rules", out var rules)
                        && rules.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rules.EnumerateArray())
                            if (TryParse(r, out var entry))
                                parsed.Add(entry!);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                parsed.Clear();
            }

            _cache = parsed;
            return _cache;
        }
    }

    private static bool TryParse(JsonElement obj, out ExecuteRuleEntry? entry)
    {
        entry = null;
        try
        {
            var id = obj.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id)) return false;

            var rule = new ExecuteRule
            {
                Id = id!,
                Label = Str(obj, "label"),
                Source = obj.GetProperty("source").Deserialize<ExecuteSource>(JsonOptions),
                Kind = obj.GetProperty("kind").Deserialize<ExecuteThresholdKind>(JsonOptions),
                Base = Num(obj, "base"),
                PerLevel = Num(obj, "perLevel"),
                BaseByLevel = NumArray(obj, "baseByLevel"),
                PerRank = Num(obj, "perRank"),
                PerStack = Num(obj, "perStack"),
                PerAp = Num(obj, "perAp"),
                PerBonusAd = Num(obj, "perBonusAd"),
                PerLethality = Num(obj, "perLethality"),
                // Default true when omitted — all currently-known real executes pierce shields.
                PiercesShield = !obj.TryGetProperty("piercesShield", out var ps) || ps.GetBoolean(),
                GateStacks = (int)Num(obj, "gateStacks"),
                GateNote = Str(obj, "gateNote"),
            };

            bool needsConfirm = obj.TryGetProperty("needsConfirm", out var nc)
                && nc.ValueKind == JsonValueKind.True;

            // (M24 P9 wiring) Attacker association: which champion (ability/passive executes) or built
            // item id (item executes) arms this rule. A Buff execute (Elder Dragon) has neither — the
            // attacker has no in-combo buff signal, so it stays unselectable until a buff knob exists.
            string? champion = Str(obj, "champion");
            int? itemId = obj.TryGetProperty("itemId", out var it) && it.ValueKind == JsonValueKind.Number
                ? it.GetInt32() : null;

            entry = new ExecuteRuleEntry(rule, needsConfirm, champion, itemId);
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static double Num(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;

    /// <summary>Reads a numeric JSON array (e.g. <c>baseByLevel</c>) or null when absent/not an array.
    /// Non-number elements are skipped, keeping the loader tolerant of a malformed row.</summary>
    private static double[]? NumArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Array) return null;
        var list = new List<double>(e.GetArrayLength());
        foreach (var v in e.EnumerateArray())
            if (v.ValueKind == JsonValueKind.Number) list.Add(v.GetDouble());
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>
    /// (M24 P9 wiring) The execute rules armed for a given ATTACKER: ability/passive executes whose
    /// <see cref="ExecuteRuleEntry.Champion"/> matches <paramref name="championId"/>, plus item
    /// executes whose <see cref="ExecuteRuleEntry.ItemId"/> is in <paramref name="builtItemIds"/>.
    /// Buff executes (no attacker signal) are never selected here. Empty when nothing matches — the
    /// caller then shows no kill-line. Case-insensitive champion match.
    /// </summary>
    public static IReadOnlyList<ExecuteRule> SelectForAttacker(string championId, IReadOnlyCollection<int> builtItemIds)
    {
        var result = new List<ExecuteRule>();
        foreach (var e in Load())
        {
            bool byChampion = e.Champion is { } c && string.Equals(c, championId, StringComparison.OrdinalIgnoreCase);
            bool byItem = e.ItemId is { } id && builtItemIds.Contains(id);
            if (byChampion || byItem) result.Add(e.Rule);
        }
        return result;
    }

    /// <summary>Test-only reset so the lazy cache doesn't leak between cases.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _cache = null;
    }
}

/// <summary>A loaded execute rule plus its curation-confidence flag and attacker association
/// (M24 P9 wiring): <see cref="Champion"/> (ability/passive executes) or <see cref="ItemId"/>
/// (item executes) — the signal <see cref="ExecuteEffectsDb.SelectForAttacker"/> matches against.
/// Both null for a Buff execute (no in-combo attacker signal).</summary>
public sealed record ExecuteRuleEntry(ExecuteRule Rule, bool NeedsConfirm, string? Champion = null, int? ItemId = null);
