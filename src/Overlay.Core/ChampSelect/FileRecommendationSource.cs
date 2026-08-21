using System.Text.Json;
using Overlay.Core.Lcu;
using Overlay.Core.Stats;

namespace Overlay.Core.ChampSelect;

/// <summary>
/// M33 D4 phase-2 (local form) — recommendation presets read from the aggregation pipeline's
/// output directory (<c>tools/aggregate_runes.py</c> → <c>rec/{patch}/{bracket}/{championKey}.json</c>,
/// already in the client <see cref="RunePreset"/> shape with <c>source:"remote"</c>).
///
/// <para>The bracket (see <see cref="RecBrackets"/>) selects which tier sample
/// the recommendations come from. Changing <see cref="Bracket"/> drops the cache so the next read
/// answers from the new sample; a rec directory written before brackets existed has no bracket
/// subdirectories and is read exactly as before.</para>
///
/// <para>This is the same seam the future HTTP source will use: the panel consumes
/// <see cref="IRunePresetSource"/> and cannot tell the difference. Patch selection picks the
/// latest patch directory that is not a coverage regression (see
/// <see cref="FileRecommendationSource.CoverageFloorRatio"/>) once per app run; per-champion files are
/// cached after first read. Every failure — missing dir/file, corrupt JSON — degrades to an
/// empty list (M33 failure posture), so an unset or stale rec dir simply means "no
/// recommendations", never an error.</para>
/// </summary>
public sealed class FileRecommendationSource : IRunePresetSource
{
    private readonly string _recRoot;
    private readonly object _gate = new();
    private readonly Dictionary<int, IReadOnlyList<RunePreset>> _cache = new();
    private string? _patchDir;   // resolved lazily; null until first successful resolve
    private bool _patchResolved;
    private string _bracket;

    /// <param name="recRoot">Directory containing per-patch subdirs (e.g. <c>…/rec</c>).
    /// Empty/nonexistent → the source lists nothing.</param>
    /// <param name="bracket">Tier bracket slug (<see cref="RecBrackets"/>);
    /// empty reads the legacy bracket-less layout.</param>
    public FileRecommendationSource(string recRoot, string bracket = "")
    {
        _recRoot = recRoot ?? "";
        _bracket = bracket ?? "";
    }

    /// <summary>Tier bracket read from. Setting it discards the cached presets and the resolved
    /// directory, so the next <see cref="List"/> reflects the new sample.</summary>
    public string Bracket
    {
        get { lock (_gate) return _bracket; }
        set
        {
            lock (_gate)
            {
                string next = value ?? "";
                if (next == _bracket) return;
                _bracket = next;
                _cache.Clear();
                _patchResolved = false;
                _patchDir = null;
            }
        }
    }

    public IReadOnlyList<RunePreset> List(int championKey)
    {
        if (championKey <= 0 || _recRoot.Length == 0) return Array.Empty<RunePreset>();
        lock (_gate)
        {
            if (_cache.TryGetValue(championKey, out var cached)) return cached;

            var result = LoadChampion(championKey);
            _cache[championKey] = result;
            return result;
        }
    }

    private IReadOnlyList<RunePreset> LoadChampion(int championKey)
    {
        try
        {
            if (!_patchResolved)
            {
                _patchResolved = true;
                _patchDir = ResolveLatestPatchDir(_recRoot) is { } patch
                    ? ContentDir(patch, _bracket)
                    : null;
            }
            if (_patchDir is null) return Array.Empty<RunePreset>();

            string path = Path.Combine(_patchDir, $"{championKey}.json");
            if (!File.Exists(path)) return Array.Empty<RunePreset>();

            var presets = JsonSerializer.Deserialize<List<RunePreset>>(
                File.ReadAllText(path));
            if (presets is null) return Array.Empty<RunePreset>();
            // Defensive: whatever the file claims, presets from this source are remote —
            // the P4 auto-apply gate must never fire on them.
            foreach (var p in presets) p.Source = "remote";
            return presets;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<RunePreset>();
        }
    }

    /// <summary>A patch directory must cover at least this fraction of the best-covered patch's
    /// champions to be preferred over it. A patch that just went live has only a few hours of
    /// collected games, so its aggregation covers a fraction of the roster: 16.15 measured 43
    /// champions against 16.14's 166 (a 74% coverage loss) while it was one day old. Picking the
    /// newest directory unconditionally would serve that thin set the moment anyone ran the
    /// aggregation, so a new patch only takes over once it has caught up.</summary>
    internal const double CoverageFloorRatio = 0.7;

    /// <summary>Picks the numerically-latest patch subdirectory (16.14 &gt; 16.9 — string sort
    /// would get this wrong) whose champion coverage clears <see cref="CoverageFloorRatio"/> of
    /// the best-covered patch, or null when none exist. Coverage is compared, not trusted: a
    /// freshly-aggregated patch does not silently shrink what the panel can recommend.</summary>
    internal static string? ResolveLatestPatchDir(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            var candidates = new List<((int Major, int Minor) Ver, string Dir, int Covered)>();
            foreach (var dir in Directory.GetDirectories(root))
            {
                var parts = Path.GetFileName(dir).Split('.');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], out int major)
                    || !int.TryParse(parts[1], out int minor)) continue;
                candidates.Add(((major, minor), dir, CountCoverage(dir)));
            }
            if (candidates.Count == 0) return null;

            int bestCovered = candidates.Max(c => c.Covered);
            double floor = bestCovered * CoverageFloorRatio;
            // Newest first, then take the first one that is not a coverage regression. With every
            // directory empty (bestCovered 0) this degrades to "newest wins", as before.
            return candidates
                .OrderByDescending(c => c.Ver)
                .First(c => c.Covered >= floor)
                .Dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Champion-file count for a patch directory. Bracketed output puts those files one
    /// level down, so coverage is the best bracket's count — the patch is as well covered as its
    /// best sample, and comparing a bracketed patch against a legacy one still works.</summary>
    private static int CountCoverage(string patchDir)
    {
        int top = Directory.GetFiles(patchDir, "*.json").Length;
        if (top > 0) return top;
        int best = 0;
        foreach (var sub in Directory.GetDirectories(patchDir))
        {
            int n = Directory.GetFiles(sub, "*.json").Length;
            if (n > best) best = n;
        }
        return best;
    }

    /// <summary>Where a bracket's files live under a patch directory: the bracket subdirectory
    /// when it exists, otherwise the patch directory itself (the pre-bracket layout, and the
    /// path taken whenever no bracket is configured).</summary>
    internal static string ContentDir(string patchDir, string bracket)
    {
        if (string.IsNullOrEmpty(bracket)) return patchDir;
        string dir = Path.Combine(patchDir, bracket);
        return Directory.Exists(dir) ? dir : patchDir;
    }

    /// <summary>Bracket slugs present in the resolved patch directory, highest first, each flagged
    /// as thin or not. Empty when the layout has no bracket subdirectories at all.
    ///
    /// <para>Every bracket that holds any champion at all is offered (loop 469, at the user's
    /// direction): an earlier version hid the thin ones, and hiding a band the user asked for is
    /// worse than showing it honestly. The thin ones are MARKED so nobody picks one blind.</para>
    ///
    /// <para>A newly-collected tier produces a real but nearly-empty directory: the aggregation
    /// creates one for any tier that has rows, while a few hundred matches clear almost no
    /// champion's minimum sample (measured: 2 champions in gold_minus against 147 in
    /// platinum_plus). So the test is the same one <see cref="CoverageFloorRatio"/> already applies
    /// between patches, one level down — a bracket must cover at least that fraction of the
    /// best-covered bracket.</para>
    ///
    /// <para>Roster coverage is the right yardstick here precisely because it SATURATES: every
    /// bracket converges on the full roster once its sample is real, so a narrow bracket is not
    /// penalised for holding fewer tiers than a wide one. Comparing match counts instead would be
    /// self-defeating, since the brackets are nested — platinum_plus can only ever hold six tenths
    /// of what "all" holds, and would fail its own test forever.</para></summary>
    public static IReadOnlyList<(string Slug, bool Thin)> AvailableBrackets(string recRoot)
    {
        var empty = Array.Empty<(string, bool)>();
        try
        {
            if (ResolveLatestPatchDir(recRoot ?? "") is not { } patchDir) return empty;
            var covered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(patchDir))
                covered[Path.GetFileName(dir)] = Directory.GetFiles(dir, "*.json").Length;
            if (covered.Count == 0) return empty;

            int best = 0;
            foreach (int n in covered.Values) if (n > best) best = n;
            double floor = best * CoverageFloorRatio;

            var result = new List<(string, bool)>();
            foreach (var (slug, _) in RecBrackets.All)
                if (covered.TryGetValue(slug, out int n) && n > 0) result.Add((slug, n < floor));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return empty;
        }
    }
}
