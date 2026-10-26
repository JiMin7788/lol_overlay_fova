using System.Globalization;
using System.Text.RegularExpressions;
using Overlay.Client.UiTests.M45;

namespace Overlay.Client.UiTests;

/// <summary>
/// The M45 case file itself (plain facts, run on every <c>dotnet test</c>): coverage of HC:104-122, 124-145, 147 and
/// 167, the shape of every claim, engine evidence that names something real, the compare arithmetic and the
/// card line reading.
/// </summary>
public sealed class M45CasesTests
{
    private static readonly string[] CheckTypes = { "badge", "noBadge", "sliderMax", "paletteHas", "compare", "oracle", "card" };
    private static readonly string[] KnobKinds = { "attackCount", "hitDuration", "distance", "stackCount", "condition", "fx" };

    private static readonly M45CaseFile File = M45CaseFile.Load();

    [Fact]
    public void Hc104To122_HaveOneCaseEach_EveryClaimChecksTheOracle()
    {
        foreach (int hc in Enumerable.Range(104, 19))
        {
            var cases = File.Cases.Where(c => c.Hc == hc).ToList();
            Assert.True(cases.Count == 1, $"HC:{hc} has {cases.Count} cases");
            Assert.NotEmpty(cases[0].Claims);
            foreach (var claim in cases[0].Claims)
                Assert.True(claim.Checks.Any(c => c.Type == "oracle"), $"claim {claim.Id} has no oracle check");
        }
        Assert.Equal(File.Cases.Count, File.Cases.Select(c => c.Hc).Distinct().Count());
        var ids = File.Cases.SelectMany(c => c.Claims).Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Hc124To135_HaveOneCaseEach_EveryClaimChecksTheOracle()
    {
        // lolF02 t6a: HC:123 is not an editor case (practice-mode measurements).
        foreach (int hc in Enumerable.Range(124, 12))
        {
            var cases = File.Cases.Where(c => c.Hc == hc).ToList();
            Assert.True(cases.Count == 1, $"HC:{hc} has {cases.Count} cases");
            Assert.NotEmpty(cases[0].Claims);
            foreach (var claim in cases[0].Claims)
                Assert.True(claim.Checks.Any(c => c.Type == "oracle"), $"claim {claim.Id} has no oracle check");
        }
    }

    [Fact]
    public void Hc136To145_147_167_HaveOneCaseEach_EveryClaimChecksTheOracle()
    {
        // lolF02 t6b: HC:146 is a decision line (already [x]); 148-166 are practice-mode items.
        foreach (int hc in Enumerable.Range(136, 10).Append(147).Append(167))
        {
            var cases = File.Cases.Where(c => c.Hc == hc).ToList();
            Assert.True(cases.Count == 1, $"HC:{hc} has {cases.Count} cases");
            Assert.NotEmpty(cases[0].Claims);
            foreach (var claim in cases[0].Claims)
                Assert.True(claim.Checks.Any(c => c.Type == "oracle"), $"claim {claim.Id} has no oracle check");
        }
    }

    [Fact]
    public void Hc142_ReadsMelsKillLineOnTheCard_ShortAgainstLonger()
    {
        var cards = File.Case(142).Claims.SelectMany(c => c.Checks.Where(k => k.Type == "card").Select(k => (Claim: c, Check: k))).ToList();
        Assert.Contains(cards, t => t.Check.Label == "멜 P(압도)" && t.Check.Op == ">"
            && t.Claim.SequenceOf(t.Claim.States.Single(s => s.Name == t.Check.A)).Count
               > t.Claim.SequenceOf(t.Claim.States.Single(s => s.Name == t.Check.B)).Count);
    }

    [Theory]
    [InlineData("≤ 412 HP 확정킬 (멜 P(압도))", 412.0)]
    [InlineData("킬각 상한 900 (R Q)\n< 1,204 HP 확정 킬 ( 멜 P ( 압도 ) )", 1204.0)]
    [InlineData("≤ 412 HP 확정킬 (우르곳 R)", null)]
    public void CardLine_ReadsTheNumberBeforeHp_OnTheLabelsLine(string ocr, double? expected) =>
        Assert.Equal(expected, M45CardLine.Value(ocr, "멜 P(압도)"));

    [Fact]
    public void EveryClaim_IsWellFormed_AndChecksEveryKnobItSets()
    {
        foreach (var item in File.Cases)
            foreach (var claim in item.Claims)
            {
                string at = $"HC:{item.Hc} claim {claim.Id}";
                Assert.False(string.IsNullOrWhiteSpace(claim.Champion), at);
                Assert.False(string.IsNullOrWhiteSpace(claim.ChampionKo), at);
                Assert.NotEmpty(claim.States);
                Assert.Equal(claim.States.Count, claim.States.Select(s => s.Name).Distinct().Count());
                Assert.All(claim.States, s => Assert.InRange(claim.SequenceOf(s).Count, 1, 5)); // one row of entries

                foreach (var state in claim.States)
                    foreach (var knob in state.Knobs)
                    {
                        Assert.Contains(knob.Kind, KnobKinds);
                        Assert.InRange(knob.Node, 0, claim.SequenceOf(state).Count - 1);
                        // AC2: a knob the claim turns is a control it states, so its field is checked.
                        Assert.True(claim.Checks.Any(c => c.Type is "badge" or "sliderMax" && c.Kind == knob.Kind && c.Node == knob.Node),
                            $"{at}: state {state.Name} sets {knob.Kind} on node {knob.Node} without a badge/sliderMax check");
                    }

                var first = claim.SequenceOf(claim.States[0]);
                var readings = claim.States.ToDictionary(s => s.Name, _ => new M45Reading("1", 1, 1, false));
                foreach (var check in claim.Checks)
                {
                    Assert.Contains(check.Type, CheckTypes);
                    switch (check.Type)
                    {
                        case "badge" or "noBadge" or "sliderMax":
                            Assert.Contains(check.Kind, KnobKinds);
                            Assert.InRange(check.Node ?? -1, 0, first.Count - 1);
                            if (check.Type == "sliderMax") Assert.NotNull(check.Max);
                            break;
                        case "paletteHas":
                            Assert.False(string.IsNullOrEmpty(check.Slot), at);
                            break;
                        case "compare":
                            Assert.Contains(check.Op, new[] { ">", "<", "=", "ratio" });
                            if (check.Op == "ratio") Assert.NotNull(check.Value);
                            M45Compare.Evaluate(check.A!, readings); // throws on an unknown state or field
                            M45Compare.Evaluate(check.B!, readings);
                            break;
                        case "oracle":
                            Assert.All(check.States ?? Array.Empty<string>(), s => Assert.Contains(s, readings.Keys));
                            break;
                        case "card":
                            Assert.False(string.IsNullOrEmpty(check.Label), at);
                            Assert.Contains(check.Op, new[] { ">", "<" });
                            Assert.Contains(check.A, readings.Keys);
                            Assert.Contains(check.B, readings.Keys);
                            // The runner tells the two cards apart by their chip count.
                            Assert.NotEqual(claim.SequenceOf(claim.States.Single(s => s.Name == check.A)).Count,
                                claim.SequenceOf(claim.States.Single(s => s.Name == check.B)).Count);
                            break;
                    }
                }
            }
    }

    [Fact]
    public void EngineEvidence_IsTheM45Audit_OrAnExistingCoreTest()
    {
        string tests = Path.Combine(RepoRoot(), "src", "Overlay.Core.Tests");
        var sources = Directory.GetFiles(tests, "*.cs", SearchOption.AllDirectories).Select(System.IO.File.ReadAllText).ToList();
        foreach (var claim in File.Cases.SelectMany(c => c.Claims))
        {
            if (claim.EngineEvidence is null or M45CaseFile.M45Fidelity) continue;
            var parts = claim.EngineEvidence.Split('.');
            Assert.True(parts.Length == 2, $"claim {claim.Id}: engine evidence '{claim.EngineEvidence}' is not Class.Method");
            Assert.True(sources.Any(s => Regex.IsMatch(s, $@"\bclass {parts[0]}\b") && Regex.IsMatch(s, $@"\bvoid {parts[1]}\(")),
                $"claim {claim.Id}: no test {claim.EngineEvidence} under src/Overlay.Core.Tests");
        }
    }

    [Fact]
    public void EachLine_IsThePrefixOfItsChecklistLine()
    {
        var lines = System.IO.File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "reports", "HUMAN_CHECKLIST.md"));
        foreach (var item in File.Cases)
        {
            string line = lines[item.Hc - 1];
            Assert.True(line.StartsWith("- [ ] " + item.Line) || line.StartsWith("- [x] " + item.Line),
                $"HUMAN_CHECKLIST.md:{item.Hc} does not start with '{item.Line}': {line}");
        }
    }

    [Theory]
    [InlineData("1,120", "ko-KR", 1120, 1120, false)]
    [InlineData("480 ~ 1,120", "ko-KR", 480, 1120, true)]
    [InlineData("0", "ko-KR", 0, 0, false)]
    public void PreviewText_ParsesInTheAppCulture(string text, string culture, double min, double max, bool range)
    {
        var reading = M45Reading.Parse(text, CultureInfo.GetCultureInfo(culture));
        Assert.Equal((min, max, range), (reading.Min, reading.Max, reading.IsRange));
        Assert.Equal(min, reading.Resolved);
    }

    [Fact]
    public void Compare_EvaluatesTermsWithRoundingSlack()
    {
        var r = new Dictionary<string, M45Reading>
        {
            ["one"] = new("109 ~ 384", 109, 384, true),
            ["three"] = new("328 ~ 759", 328, 759, true),
            ["four"] = new("634 ~ 947", 634, 947, true),
            ["five"] = new("821 ~ 1,057", 821, 1057, true),
        };
        Assert.Null(M45Compare.Check(new M45Check("compare", A: "three.resolved", Op: "ratio", B: "one.resolved", Value: 3), r));
        Assert.Null(M45Compare.Check(new M45Check("compare", A: "five.resolved - four.resolved", Op: ">", B: "one.resolved"), r));
        Assert.Null(M45Compare.Check(new M45Check("compare", A: "one.max", Op: "=", B: "384"), r));
        Assert.NotNull(M45Compare.Check(new M45Check("compare", A: "one.max", Op: "=", B: "390"), r));
        Assert.NotNull(M45Compare.Check(new M45Check("compare", A: "four.resolved", Op: "ratio", B: "one.resolved", Value: 4), r));
        Assert.NotNull(M45Compare.Check(new M45Check("compare", A: "one.resolved", Op: ">", B: "three.resolved"), r));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "LolOverlay.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LolOverlay.sln not found above the test assembly");
    }
}
