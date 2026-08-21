using System.Text.Json;
using Overlay.Core.ChampionDb;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 500) Champion BINs ship Arena/Swarm numbers next to Summoner's Rift ones. Miss Fortune's E
/// carries <c>APRatioPerSecond</c> TWICE — 0.6 under <c>DataValues</c> and 0.9 under
/// <c>DataValuesModeOverride/cherry</c> — and a curation note that read the wrong one would overstate
/// the ability by half with nothing to catch it.
///
/// <para>Nothing leaks today, and for a good reason: <c>ParseDataValues</c> looks up
/// <c>mSpell["DataValues"]</c> by direct property name, and the override is a SIBLING key, so it is
/// never walked. That is a one-line property away from changing — a recursive search for a DataValue
/// by name, added by someone chasing a missing value, would silently start preferring whichever copy
/// it found first.</para>
///
/// <para>150 of the 173 shipped BINs carry an override, so this is close to a corpus-wide property
/// rather than a Miss Fortune quirk.</para>
/// </summary>
public class ModeOverrideNeverLeaksTests
{
    private static string BinDir => Path.Combine(AppContext.BaseDirectory, "data", "communitydragon");

    /// <summary>Every (spell, DataValue) whose override disagrees with the base, as
    /// (file, spellPath, dataValue, baseRank1, overrideRank1).</summary>
    private static List<(string File, string Spell, string Name, double Base, double Override)> Conflicts()
    {
        var found = new List<(string, string, string, double, double)>();

        foreach (var path in Directory.GetFiles(BinDir, "*.bin.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var spell in doc.RootElement.EnumerateObject())
            {
                if (spell.Value.ValueKind != JsonValueKind.Object) continue;
                if (!spell.Value.TryGetProperty("mSpell", out var mSpell)) continue;
                if (!mSpell.TryGetProperty("DataValuesModeOverride", out var modes)) continue;
                if (modes.ValueKind != JsonValueKind.Object) continue;

                var baseline = Values(mSpell, "DataValues");
                foreach (var mode in modes.EnumerateObject())
                {
                    foreach (var (name, over) in Values(mode.Value, "SpellDataValues"))
                    {
                        if (!baseline.TryGetValue(name, out double bas)) continue;
                        if (Math.Abs(bas - over) > 1e-9)
                            found.Add((Path.GetFileName(path), spell.Name, name, bas, over));
                    }
                }
            }
        }
        return found;
    }

    private static Dictionary<string, double> Values(JsonElement parent, string prop)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!parent.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
        foreach (var dv in arr.EnumerateArray())
        {
            if (!dv.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
            if (!dv.TryGetProperty("values", out var v) || v.ValueKind != JsonValueKind.Array) continue;
            var first = v.EnumerateArray().Skip(1).FirstOrDefault();   // index 0 is the rank-0 slot
            if (first.ValueKind == JsonValueKind.Number) map[n.GetString()!] = first.GetDouble();
        }
        return map;
    }

    /// <summary>The premise: there really are conflicting copies, so the test below is not vacuous.</summary>
    [Fact]
    public void TheCorpusReallyDoesShipConflictingModeValues()
    {
        var conflicts = Conflicts();
        Assert.True(conflicts.Count > 0,
            "no override disagrees with its base value anywhere — either the BINs changed shape or "
            + "this test stopped finding them, and either way it is no longer proving anything");

        // The one that motivated this: Miss Fortune's E, 0.6 on the Rift and 0.9 in Arena.
        Assert.Contains(conflicts, c =>
            c.File == "missfortune.bin.json" && c.Name == "APRatioPerSecond"
            && Math.Abs(c.Base - 0.6) < 1e-6 && Math.Abs(c.Override - 0.9) < 1e-6);
    }

    /// <summary>What the parser must hand back is the Rift value, every time.</summary>
    [Fact]
    public void TheParserAlwaysReturnsTheBaseValueNotTheOverride()
    {
        var wrong = new List<string>();
        int checkedPairs = 0;

        foreach (var group in Conflicts().GroupBy(c => c.File))
        {
            string champId = group.Key.Substring(0, group.Key.Length - ".bin.json".Length);
            var spells = ChampionBinParser.ParseChampion(
                champId, File.ReadAllText(Path.Combine(BinDir, group.Key)));

            foreach (var c in group)
            {
                foreach (var slot in spells.Values)
                {
                    if (!slot.DataValues.TryGetValue(c.Name, out double[]? values)) continue;
                    if (values.Length < 2) continue;
                    checkedPairs++;
                    if (Math.Abs(values[1] - c.Override) < 1e-9 && Math.Abs(values[1] - c.Base) > 1e-9)
                        wrong.Add($"{group.Key} {c.Spell} {c.Name}: got {values[1]} (the mode override) "
                                  + $"instead of {c.Base}");
                }
            }
        }

        Assert.True(wrong.Count == 0,
            "an Arena/Swarm value reached a Summoner's Rift number:" + Environment.NewLine
            + string.Join(Environment.NewLine, wrong.Take(20)));

        // The corpus holds 866 conflicting values; the parser surfaces 207 of them into a mapped
        // slot, and those 207 are what is actually being asserted above. Pinned so that a parser
        // change which stops exposing them turns this into a visible failure rather than a quiet
        // green — a test that checks nothing passes just as loudly as one that checks everything.
        Assert.True(checkedPairs >= 200,
            $"only {checkedPairs} parsed values were compared (expected ~207) — this test has gone "
            + "vacuous, which is worse than it failing");
    }
}
