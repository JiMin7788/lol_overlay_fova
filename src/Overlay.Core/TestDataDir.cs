using System.Security.Cryptography;
using System.Text;

namespace Overlay.Core;

/// <summary>
/// Test mode: a non-blank <see cref="EnvVar"/> moves all user state (config, logs, history, caches)
/// into that directory and gives the single instance its own name, so UI tests can run a Fova copy
/// next to the user's running Fova without touching it. Unset = normal behaviour.
/// </summary>
public static class TestDataDir
{
    public const string EnvVar = "FOVA_USER_DATA_DIR";

    /// <summary>The test data dir as a full path, or null when <paramref name="raw"/> is blank
    /// (not test mode).</summary>
    public static string? Resolve(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : Path.GetFullPath(raw.Trim());

    /// <summary>"." + 8 lowercase hex chars identifying <paramref name="fullDir"/>, appended to the
    /// single-instance names. AI-NOTE: case and trailing separators are normalised away because
    /// Windows paths are case-insensitive — two spellings of one dir must share one instance.</summary>
    public static string InstanceSuffix(string fullDir)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullDir.TrimEnd('\\', '/').ToUpperInvariant()));
        return "." + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>Where downloaded icons live: <c>testDir\cache</c> for an installed test copy,
    /// <c>installedDataDir\cache</c> for an installed app, <c>baseDir\data</c> otherwise.
    /// AI-NOTE: only installed copies move — a non-installed test copy keeps <c>data\</c> next to its
    /// exe, which is already its own; an installed one would otherwise share the user's cache.</summary>
    public static string AssetCacheDir(bool installed, string? testDir, string installedDataDir, string baseDir)
        => installed && testDir != null ? Path.Combine(testDir, "cache")
        : installed ? Path.Combine(installedDataDir, "cache")
        : Path.Combine(baseDir, "data");
}
