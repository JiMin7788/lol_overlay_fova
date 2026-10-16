using System.Globalization;
using System.IO;
using Velopack;
using Velopack.Sources;

namespace Overlay.Client;

/// <summary>
/// Automatic updates (Velopack): on startup, check the public repo's GitHub Releases, download the
/// newest release in the background (a delta package when one exists), and apply it when the user
/// closes Fova. Nothing restarts mid-session. If the app dies before a clean exit, VelopackApp
/// applies the downloaded update on the next launch instead.
///
/// <para>Inert outside a Velopack install (Debug bundle, portable zip from 0.3.3 and earlier,
/// <c>dotnet run</c>): <see cref="IsInstalled"/> is false and nothing is fetched.</para>
///
/// <para>FOVA_UPDATE_SOURCE (a local folder or URL with Velopack release files) replaces GitHub —
/// for testing an update end to end without publishing a release.</para>
/// </summary>
internal static class AppUpdater
{
    private const string RepoUrl = "https://github.com/JiMin7788/lol_overlay_fova";

    private static UpdateManager? _manager;

    /// <summary>True when running from a Velopack install. Set by <see cref="Initialize"/>, which
    /// Program.Main calls before anything reads <see cref="AppPaths"/>.</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>The installed release version ("0.3.6"), or null outside a Velopack install.</summary>
    public static string? CurrentVersion => IsInstalled ? _manager?.CurrentVersion?.ToString() : null;

    public static void Initialize()
    {
        try
        {
            var source = Environment.GetEnvironmentVariable("FOVA_UPDATE_SOURCE");
            _manager = string.IsNullOrWhiteSpace(source)
                ? new UpdateManager(new GithubSource(RepoUrl, null, false))
                : new UpdateManager(source);
            IsInstalled = _manager.IsInstalled;
        }
        catch (Exception ex)
        {
            Log("init failed: " + ex.Message);
        }
    }

    /// <summary>Checks for and downloads a newer release. Never throws.</summary>
    public static async Task CheckAndDownloadAsync(CancellationToken ct)
    {
        var mgr = _manager;
        if (mgr is null || !IsInstalled) return;
        try
        {
            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                Log($"up to date ({mgr.CurrentVersion})");
                return;
            }
            await mgr.DownloadUpdatesAsync(info, cancelToken: ct).ConfigureAwait(false);
            Log($"downloaded {info.TargetFullRelease.Version} (current {mgr.CurrentVersion}); applies on exit");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log("check/download failed: " + ex.Message);
        }
    }

    /// <summary>Hands a downloaded update to the Velopack updater, which waits for this process to
    /// exit and then applies it silently. Call on the way out.</summary>
    public static void ApplyPendingOnExit()
    {
        try
        {
            if (_manager?.UpdatePendingRestart is { } pending)
                _manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            Log("apply failed: " + ex.Message);
        }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(AppPaths.UserDataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "update.log"),
                "[" + DateTime.Now.ToString("O", CultureInfo.InvariantCulture) + "] " + message + "\n");
        }
        catch
        {
            // Update logging must never affect the app.
        }
    }
}
