using System.Text.Json;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 511) Checks the curated numbers against the WIKI, not just against the BIN.
///
/// <para>Everything else in this repo verifies the curation against Riot's own data —
/// <c>validate_skill_damage</c> proves the calc names resolve, <c>sweep_unreferenced_damage</c>
/// proves no damage term was left behind, <c>audit_cdragon_drift</c> proves the snapshot matches the
/// live patch. All of those would stay green if a slot pointed at a real calc that happens to be the
/// WRONG one. The wiki is the independent witness that catches that.</para>
///
/// <para>Method: evaluate the curated calc at ZERO bonus stats, which deletes every ratio term and
/// leaves the flat base — exactly what a wiki line like "80 / 130 / 180 / 230 / 280" lists — and
/// compare per rank. Only facts whose <c>field</c> NAMES the curated calc are compared; a first pass
/// without that filter produced 94 "failures" that were all the wiki quoting a different quantity
/// (Brand R's three-bounce total against the per-bounce number, Draven R's out-and-back against one
/// pass), which is noise, not signal.</para>
/// </summary>
public class WikiBaseDamageAgreementTests
{
    private static readonly string[] Slots = { "Q", "W", "E", "R" };

    /// <summary>Champion+slot pairs where the wiki and the BIN legitimately disagree, with the
    /// factor and the reason. Both are the same shape: the wiki publishes an AGGREGATE over several
    /// damage instances while the BIN value is per instance, and the factor lands on the AP ratio as
    /// well as the flat base — which is what proves it is an aggregate rather than a stale number.
    ///
    /// <para>The curation deliberately keeps the per-instance value: landing every instance is
    /// positional, and this project's convention is the conservative floor.</para></summary>
    private static readonly Dictionary<string, string> Explained = new()
    {
        ["Hecarim/W"] = "wiki total = per-tick x5, BIN = per-tick x4 (BuffDuration 4.0). The wiki "
                      + "counts an initial tick plus four; Riot's own aggregate counts four. Both "
                      + "flat and AP ratio differ by the same 1.25.",
        ["Lulu/Q"]    = "wiki total = both bolts (Lulu + Pix, the second at 50%), BIN = one bolt. "
                      + "Flat 60->90 and AP 0.5->0.75 are both exactly 1.5x.",
    };

    private static ActivePlayerStats Zero(int rank) => new()
    {
        AttackDamage = 0, AbilityPower = 0, MaxHealth = 0, ResourceMax = 0, Armor = 0, MagicResist = 0,
        AbilityQ = rank, AbilityW = rank, AbilityE = rank, AbilityR = rank,
    };

    private static string EvidenceDir()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null &&
               !Directory.Exists(Path.Combine(root, "src", "Overlay.Core", "Data", "curation_evidence")))
            root = Path.GetDirectoryName(root);
        return root is null ? "" : Path.Combine(root, "src", "Overlay.Core", "Data", "curation_evidence");
    }

    [Fact]
    public void CuratedBaseDamageMatchesTheWiki()
    {
        string evDir = EvidenceDir();
        if (!Directory.Exists(evDir)) return;   // evidence is not shipped; skip outside the repo

        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var summary = Directory.GetFiles(Path.Combine(dataDir, "ddragon"), "champion.json",
            SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Last();
        ChampionRepository.ResetForTests();
        SkillDamageDb.ResetForTests();
        ChampionRepository.InitializeFromCache(
            Path.GetDirectoryName(summary)!, Path.Combine(dataDir, "communitydragon"));

        var disagreed = new List<string>();
        var staleExemptions = new List<string>(Explained.Keys);
        int compared = 0;

        foreach (var file in Directory.GetFiles(evDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string champId = Path.GetFileNameWithoutExtension(file);
            var champ = ChampionRepository.Get(champId);
            if (champ is null) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("slots", out var slots)) continue;

            foreach (var slotProp in slots.EnumerateObject())
            {
                string slot = slotProp.Name;
                if (!Slots.Contains(slot)) continue;
                if (!slotProp.Value.TryGetProperty("facts", out var facts)) continue;

                var hits = SkillDamageDb.GetHits(champId, slot);
                if (hits is null || hits.Length == 0) continue;
                string? calc = hits[0].Calc ?? hits[0].MinCalc ?? hits[0].MetCalc ?? hits[0].PerSecondCalc;
                if (string.IsNullOrEmpty(calc)) continue;

                foreach (var fact in facts.EnumerateArray())
                {
                    if (fact.GetProperty("kind").GetString() != "flat") continue;
                    if (!fact.TryGetProperty("values", out var vals)) continue;
                    if (!string.Equals(fact.GetProperty("field").GetString(), calc,
                                       StringComparison.OrdinalIgnoreCase)) continue;

                    var wiki = vals.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    if (wiki.Length is < 3 or > 6) continue;

                    var got = new double?[wiki.Length];
                    for (int r = 1; r <= wiki.Length; r++)
                        got[r - 1] = SkillDamage.ComputeCalcDamage(champ, slot, calc!, Zero(r), 18);
                    if (got.Any(g => g is null) || got.All(g => g!.Value == 0)) continue;

                    compared++;
                    bool ok = !wiki.Where((w, i) =>
                        Math.Abs(got[i]!.Value - w) > Math.Max(0.51, w * 0.02)).Any();

                    string key = $"{champId}/{slot}";
                    if (Explained.ContainsKey(key)) { staleExemptions.Remove(key); continue; }
                    if (!ok)
                        disagreed.Add($"{champId} {slot} calc={calc} "
                            + $"wiki=[{string.Join(",", wiki)}] got=[{string.Join(",", got.Select(g => g!.Value))}]");
                }
            }
        }

        Assert.True(compared >= 10,
            $"only {compared} slots could be compared (expected 12+) — the evidence files or the "
            + "field-name convention changed and this test has stopped checking anything");

        Assert.True(disagreed.Count == 0,
            "curated base damage disagrees with the wiki:" + Environment.NewLine
            + string.Join(Environment.NewLine, disagreed) + Environment.NewLine
            + "Either the curation points at the wrong calc, or the wiki is quoting an aggregate — "
            + "check whether the SAME factor applies to the ratio as well as the flat base. If it "
            + "does, it is an aggregate: document it in Explained rather than changing the curation.");

        Assert.True(staleExemptions.Count == 0,
            "these entries agree with the wiki now and their exemption should be deleted: "
            + string.Join(", ", staleExemptions));
    }
}
