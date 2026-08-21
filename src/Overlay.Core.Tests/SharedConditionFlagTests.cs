using System.Text.Json;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 490, docs/RECURRING_TRAPS.md 1.4) A combo node carries ONE condition flag. Two conditional
/// hits on the same slot therefore share the same checkbox and fire together — which is correct when
/// both gates are the same fact, and an overcount when they are not.
///
/// <para>This is the guard for a trap whose only previous defence was remembering it. Every slot with
/// more than one conditional hit is listed below with the decision that was made about it; a new one
/// fails this test, which is the point — it forces the question to be answered on the record rather
/// than discovered later as a number that is too big.</para>
/// </summary>
public class SharedConditionFlagTests
{
    /// <summary>Slot → why more than one conditional hit is correct here. Adding a row is a decision,
    /// not a formality: say which fact the shared checkbox is asserting.</summary>
    private static readonly Dictionary<string, string> Adjudicated = new(StringComparer.Ordinal)
    {
        ["AurelionSol.R"] =
            "Both hits are the SAME gate — 75 Stardust. The impact is replaced and the shockwave is "
            + "added, and neither exists without the upgrade, so one checkbox is the whole upgrade.",
        ["Shaco.E"] =
            "Both amplifications are the SAME 30%-health threshold (the dagger's own 1.5x and the "
            + "backstab payload's), so ticking once yields the real maximum. The payload additionally "
            + "needs him behind the target, which is what makes the box a best-case assertion.",
    };

    private static string DataDir =>
        Path.Combine(AppContext.BaseDirectory, "data", "skill_damage");

    /// <summary>Reads conditionType straight off the JSON rather than deserialising into
    /// <c>SkillHit</c>: this asks a question about the FILES, and binding them to the current schema
    /// would make the guard fail for reasons that have nothing to do with what it is guarding.</summary>
    private static IEnumerable<string> ConditionTypes(JsonElement slot)
    {
        foreach (var hit in Hits(slot))
            if (hit.TryGetProperty("conditionType", out var c) && c.ValueKind == JsonValueKind.String
                && c.GetString() is { Length: > 0 } type)
                yield return type;

        static IEnumerable<JsonElement> Hits(JsonElement slot)
        {
            if (slot.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
                foreach (var h in hits.EnumerateArray()) yield return h;
            if (slot.TryGetProperty("bonusEffects", out var effects) && effects.ValueKind == JsonValueKind.Array)
                foreach (var e in effects.EnumerateArray())
                    if (e.TryGetProperty("hits", out var eh) && eh.ValueKind == JsonValueKind.Array)
                        foreach (var h in eh.EnumerateArray()) yield return h;
        }
    }

    [Fact]
    public void EverySlotSharingOneCheckboxAcrossTwoConditionsIsOnTheRecord()
    {
        var found = new List<(string Slot, string Types)>();

        foreach (var file in Directory.GetFiles(DataDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string champion = Path.GetFileNameWithoutExtension(file);
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var slot in doc.RootElement.EnumerateObject())
            {
                if (slot.Name.StartsWith('_') || slot.Value.ValueKind != JsonValueKind.Object) continue;

                var conditions = ConditionTypes(slot.Value).ToList();
                if (conditions.Count >= 2)
                    found.Add(($"{champion}.{slot.Name}", string.Join("+", conditions)));
            }
        }

        var undeclared = found.Where(f => !Adjudicated.ContainsKey(f.Slot)).ToList();
        Assert.True(undeclared.Count == 0,
            "slot(s) with more than one conditional hit and no recorded decision — they share ONE "
            + "checkbox and will fire together, so say whether that is the same gate or an overcount: "
            + string.Join(", ", undeclared.Select(u => $"{u.Slot} [{u.Types}]")));

        // …and the list does not rot: a slot that stops needing an entry should lose it, or the next
        // reader learns a rule from an example that no longer exists.
        var stale = Adjudicated.Keys.Where(k => found.All(f => f.Slot != k)).ToList();
        Assert.True(stale.Count == 0,
            "recorded decision(s) for slot(s) that no longer have multiple conditional hits: "
            + string.Join(", ", stale));
    }
}
