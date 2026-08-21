using System.Text.Json;

namespace Overlay.Core.Tests;

/// <summary>
/// M24 P8 — completeness audit for the new shapes/effects, extending the M23 coverage guard. Loads
/// the ACTUAL curated corpus and fails on a well-formedness gap, so a future patch that half-curates
/// a distance-scaled hit, or adds an execute rule that can never be selected, goes red instead of
/// silently degrading:
///  1. Every distance-scaled hit has BOTH minCalc and maxCalc (IsDistanceScaled needs both; a hit
///     with only one is a curation mistake — it silently falls back to a plain single-calc hit).
///  2. Every execute rule carries a real threshold expression AND (unless it's a Buff source) an
///     attacker association (champion or itemId) — an unassociated rule can never arm (dormant).
/// </summary>
public class M24CoverageAuditTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    private static string SkillDir => Path.Combine(DataDir, "skill_damage");

    [Fact]
    public void EveryDistanceScaledHit_HasBothMinAndMaxCalc()
    {
        Assert.True(Directory.Exists(SkillDir), $"skill_damage dir missing: {SkillDir}");
        var files = Directory.GetFiles(SkillDir, "*.json");
        Assert.NotEmpty(files);

        int checkedHits = 0, distanceHits = 0;
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var slotProp in doc.RootElement.EnumerateObject())
            {
                if (slotProp.Value.ValueKind != JsonValueKind.Object) continue; // skip "_note*"
                if (!slotProp.Value.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;
                foreach (var hit in hits.EnumerateArray())
                {
                    checkedHits++;
                    bool hasMin = hit.TryGetProperty("minCalc", out var mn) && mn.ValueKind == JsonValueKind.String;
                    bool hasMax = hit.TryGetProperty("maxCalc", out var mx) && mx.ValueKind == JsonValueKind.String;
                    if (hasMin || hasMax) distanceHits++;
                    Assert.True(hasMin == hasMax,
                        $"{Path.GetFileName(file)} slot '{slotProp.Name}': a distance-scaled hit must have BOTH " +
                        $"minCalc and maxCalc (has minCalc={hasMin}, maxCalc={hasMax}). One alone silently degrades " +
                        "to a plain single-calc hit.");
                }
            }
        }
        Assert.True(checkedHits > 0, "audit found no hits to check — corpus/parse regression");
        Assert.True(distanceHits > 0, "audit found no distance-scaled hits — expected at least Hecarim/Fizz/Nidalee");
    }

    [Fact]
    public void EveryExecuteRule_HasThreshold_AndSelectableAssociation()
    {
        var file = Path.Combine(DataDir, "execute_effects.json");
        Assert.True(File.Exists(file), $"execute_effects.json missing: {file}");

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        Assert.True(doc.RootElement.TryGetProperty("rules", out var rules), "execute_effects.json has no 'rules' array");

        int checkedRules = 0;
        string[] thresholdTerms = { "base", "perLevel", "perRank", "perStack", "perAp", "perBonusAd", "perLethality" };
        foreach (var rule in rules.EnumerateArray())
        {
            var id = rule.GetProperty("id").GetString()!;
            var source = rule.GetProperty("source").GetString()!;

            // (1) a real threshold expression: at least one non-zero scalar term OR a per-level table.
            bool hasScalarTerm = thresholdTerms.Any(t =>
                rule.TryGetProperty(t, out var v) && v.ValueKind == JsonValueKind.Number && Math.Abs(v.GetDouble()) > 0);
            bool hasBaseByLevel = rule.TryGetProperty("baseByLevel", out var bbl)
                && bbl.ValueKind == JsonValueKind.Array && bbl.GetArrayLength() > 0;
            Assert.True(hasScalarTerm || hasBaseByLevel,
                $"execute rule '{id}': no non-zero threshold term — it can never set a kill-line.");

            // (2) an attacker association so it can actually be selected — champion (ability/passive) or
            // itemId (item). A Buff execute (Elder) legitimately has no in-combo attacker signal.
            if (!string.Equals(source, "Buff", StringComparison.OrdinalIgnoreCase))
            {
                bool hasChampion = rule.TryGetProperty("champion", out var c) && c.ValueKind == JsonValueKind.String;
                bool hasItemId = rule.TryGetProperty("itemId", out var it) && it.ValueKind == JsonValueKind.Number;
                Assert.True(hasChampion || hasItemId,
                    $"execute rule '{id}' (source {source}): no champion/itemId association — it can never arm " +
                    "for any attacker (dormant). Buff-source rules are the only ones exempt.");
            }
            checkedRules++;
        }
        Assert.True(checkedRules > 0, "audit found no execute rules — corpus/parse regression");
    }

    /// <summary>
    /// (TODO §11.B honesty guard) A PercentMaxHp EXECUTE threshold is a kill-line fraction of the
    /// target's max HP — a small number (Collector 5% … Urgot 25%). It is NOT a damage-scaling
    /// MULTIPLIER (Evelynn R = ×2.4 bonus damage below 30% HP; Akali R = smooth low-HP scaling). The
    /// two share a "below X% HP" trigger in the BIN, which is exactly why they get mixed up. Guard:
    /// no PercentMaxHp rule's base fraction may exceed 0.5 — a real execute never kills from above
    /// half HP off its base line, and a mis-tagged multiplier (2.4) trips the ceiling immediately.
    /// FlatHp rules are exempt (their base is an absolute HP amount, legitimately hundreds).
    /// </summary>
    [Fact]
    public void PercentMaxHpExecuteRule_BaseIsAKillLineFraction_NotADamageMultiplier()
    {
        var file = Path.Combine(DataDir, "execute_effects.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var rules = doc.RootElement.GetProperty("rules");

        int checkedPercent = 0;
        foreach (var rule in rules.EnumerateArray())
        {
            var kind = rule.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (!string.Equals(kind, "PercentMaxHp", StringComparison.OrdinalIgnoreCase)) continue;

            var id = rule.GetProperty("id").GetString()!;
            double @base = rule.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 0;
            Assert.True(@base > 0 && @base <= 0.5,
                $"execute rule '{id}': PercentMaxHp base {@base} is not a plausible kill-line fraction (0,0.5]. " +
                "A value near/above 1 means a damage-scaling multiplier (e.g. Evelynn R ×2.4) was mis-tagged as an execute.");
            checkedPercent++;
        }
        Assert.True(checkedPercent > 0, "audit found no PercentMaxHp execute rules — corpus/parse regression");
    }
}
