namespace Overlay.Core.ChampionDb;

/// <summary>
/// Which Data Dragon version this process runs on and where its files live. Every reader of
/// <c>champion.json</c> / <c>item.json</c> / per-champion JSON and every icon path goes through
/// here, so the engine, the icons and the prefetcher always agree on one version.
///
/// <para>The app ships one version (<see cref="BundledVersion"/>) and downloads newer ones at
/// runtime (Client <c>DDragonUpdater</c>) into the first of <see cref="SetRoots"/>' roots. The
/// newest COMPLETE version across all roots wins, resolved once per process: a version that
/// finishes downloading mid-session is used from the next launch, so paths never change under a
/// running session.</para>
/// </summary>
public static class DDragonData
{
    /// <summary>The version bundled with the app (always present, offline fallback).</summary>
    public const string BundledVersion = "16.13.1";

    /// <summary>Files a version folder must hold to be used. champion.json is written last by the
    /// downloader, so a half-downloaded folder never qualifies.</summary>
    private static readonly string[] RequiredFiles = { "item.json", "runesReforged.json", "champion.json" };

    private static string[] _roots = { Path.Combine(AppContext.BaseDirectory, "data", "ddragon") };
    private static string? _currentDir;
    private static bool _resolved;
    private static readonly object Gate = new();

    /// <summary>Directories holding <c>{version}/</c> folders. The first is where downloads go.
    /// Call before anything reads <see cref="CurrentDir"/>.</summary>
    public static void SetRoots(params string[] roots)
    {
        lock (Gate)
        {
            _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _resolved = false;
            _currentDir = null;
        }
    }

    public static string DownloadRoot => _roots[0];

    public static IReadOnlyList<string> Roots => _roots;

    /// <summary>The newest complete version folder, or null when none exists.</summary>
    public static string? CurrentDir
    {
        get
        {
            lock (Gate)
            {
                if (!_resolved) { _currentDir = FindNewest(_roots); _resolved = true; }
                return _currentDir;
            }
        }
    }

    public static string CurrentVersion => CurrentDir is { } d ? Path.GetFileName(d) : BundledVersion;

    public static bool IsComplete(string versionDir)
        => RequiredFiles.All(f => File.Exists(Path.Combine(versionDir, f)));

    /// <summary>Numeric version order ("16.9.1" &lt; "16.13.1"), null for a non-version name.</summary>
    public static Version? ParseVersion(string name) => Version.TryParse(name, out var v) ? v : null;

    /// <summary>The newest complete version folder across <paramref name="roots"/> (numeric order).</summary>
    internal static string? FindNewest(IEnumerable<string> roots)
    {
        string? best = null;
        Version? bestVersion = null;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var v = ParseVersion(Path.GetFileName(dir));
                if (v is null || !IsComplete(dir)) continue;
                if (bestVersion is null || v > bestVersion) { best = dir; bestVersion = v; }
            }
        }
        return best;
    }
}
