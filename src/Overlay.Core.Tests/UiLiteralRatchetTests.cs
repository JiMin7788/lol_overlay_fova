using System.Text.RegularExpressions;

namespace Overlay.Core.Tests;

/// <summary>
/// Design-system ratchet for the desktop app (docs/design/DESIGN_SYSTEM.md). Theme.xaml owns the
/// type ramp, radii, colours and control styles; view code that types its own <c>FontSize = 11</c>,
/// <c>new CornerRadius(3)</c>, <c>Color.FromRgb(...)</c> or <c>"#4C8DFF"</c> is how the app drifted
/// to 12 font sizes and 10 corner radii while the theme claimed views "never use ad-hoc colors".
///
/// <para>Each Overlay.Client file's count of such literals is pinned in <see cref="Baseline"/>
/// (Theme.xaml itself is exempt — it is where the values live). A count that GROWS fails: use a
/// theme style (<c>HintText</c>, <c>CardInset</c>, <c>CompactGhostButton</c> …) or token
/// (<c>FontCaption</c>, <c>RadiusSmall</c>, <c>Ally</c> …) instead. A count that SHRINKS also fails
/// until the baseline is lowered to match, so the debt only ever goes down. A new file starts at 0.</para>
///
/// <para>Source-scanning, not rendering: it reads src/Overlay.Client from the repo checkout, so it
/// needs no WPF and runs in this net8.0 test project.</para>
/// </summary>
public class UiLiteralRatchetTests
{
    private static readonly Regex[] CsPatterns =
    {
        new(@"FontSize\s*=\s*[0-9]"),
        new(@"new\s+CornerRadius\s*\(\s*[0-9]"),
        new(@"Color\.From(?:A?Rgb|ScRgb)\s*\("),
        new("\"#[0-9A-Fa-f]{6,8}\""),
        new(@"\bBrushes\.(?!Transparent\b)[A-Z]"),
        new("new\\s+FontFamily\\s*\\(\\s*\""),
    };

    private static readonly Regex[] XamlPatterns =
    {
        new("FontSize=\"[0-9]"),
        new("CornerRadius=\"[0-9]"),
        new("=\"#[0-9A-Fa-f]{6,8}\""),
        new("FontFamily=\"(?!\\{)"),
    };

    /// <summary>Literal count per file, relative to src/Overlay.Client. Absent = 0.
    /// Set by the 2026-10-19 design pass (148 → 84 outside Theme.xaml; 83 after the enemy-rune checkboxes went).</summary>
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["Ads/AdBanner.cs"] = 2,
        ["ChampSelect/ChampSelectPanel.cs"] = 10,
        ["ChampSelect/KillTimingCard.cs"] = 1,
        ["HomeWindow.xaml"] = 1,
        ["MainWindow.xaml.cs"] = 1,
        ["OverlayConfig.cs"] = 2,
        ["Views/ComboSettingsView.xaml.cs"] = 51,
        ["Views/HelpView.cs"] = 1,
        ["Views/StatsView.cs"] = 10,
        ["Views/UserSearchView.xaml.cs"] = 4,
    };

    [Fact]
    public void Ui_literal_counts_match_the_ratchet_baseline()
    {
        string clientDir = FindClientDir();
        var actual = CountLiterals(clientDir);
        var problems = new List<string>();

        foreach (var (file, hits) in actual)
        {
            int allowed = Baseline.TryGetValue(file, out int b) ? b : 0;
            if (hits.Count > allowed)
                problems.Add($"{file}: {hits.Count} hand-typed UI literals, baseline {allowed}. Use a Theme.xaml " +
                             $"style or token instead (docs/design/DESIGN_SYSTEM.md). Lines:\n    "
                             + string.Join("\n    ", hits));
            else if (hits.Count < allowed)
                problems.Add($"{file}: {hits.Count} literals, baseline {allowed} — lower the baseline in " +
                             $"{nameof(UiLiteralRatchetTests)} to {hits.Count} so it cannot creep back.");
        }
        foreach (var (file, allowed) in Baseline)
            if (!actual.ContainsKey(file))
                problems.Add(File.Exists(Path.Combine(clientDir, file))
                    ? $"{file}: 0 literals, baseline {allowed} — remove its entry from the baseline."
                    : $"{file}: no longer exists — remove its entry from the baseline.");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>file (relative, '/'-separated) → "line N: text" for each literal; files with none
    /// are left out.</summary>
    private static Dictionary<string, List<string>> CountLiterals(string clientDir)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(clientDir, "*.*", SearchOption.AllDirectories))
        {
            bool isXaml = path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            if (!isXaml && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            string rel = Path.GetRelativePath(clientDir, path).Replace('\\', '/');
            if (rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal)) continue;
            if (rel == "Theme/Theme.xaml") continue;

            var patterns = isXaml ? XamlPatterns : CsPatterns;
            var hits = new List<string>();
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
                foreach (var pattern in patterns)
                    foreach (Match _ in pattern.Matches(lines[i]))
                        hits.Add($"line {i + 1}: {lines[i].Trim()}");
            if (hits.Count > 0) result[rel] = hits;
        }
        return result;
    }

    private static string FindClientDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "LolOverlay.sln")))
                return Path.Combine(dir.FullName, "src", "Overlay.Client");
        throw new InvalidOperationException("LolOverlay.sln not found above " + AppContext.BaseDirectory);
    }
}
