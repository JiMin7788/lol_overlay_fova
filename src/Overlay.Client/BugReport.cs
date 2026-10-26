using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Overlay.Client;

/// <summary>
/// In-app bug report (1.0 must-add 7): packs the version and recent logs into a zip, shows it in
/// Explorer, and opens a pre-filled GitHub issue the user drags the zip into. Nothing is uploaded by
/// the app itself — the user sees the file and decides what to post on a public issue.
/// </summary>
internal static class BugReport
{
    private const string IssueUrl = "https://github.com/JiMin7788/lol_overlay_fova/issues/new";

    /// <summary>Logs older than this are from some other session and only make the zip bigger.</summary>
    private static readonly TimeSpan LogAge = TimeSpan.FromDays(3);

    /// <summary>Per-file cap. Some debug logs reach tens of MB; the end is the part that matters.</summary>
    private const int TailBytes = 512 * 1024;

    public static string Version
    {
        get
        {
            var informational = typeof(BugReport).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "unknown";
            return Overlay.Core.VersionText.Compose(AppUpdater.CurrentVersion, informational);
        }
    }

    /// <summary>Writes the zip, selects it in Explorer and opens the issue form. Returns the zip path.
    /// Throws on I/O failure so the caller can tell the user.</summary>
    public static string CreateAndOpen()
    {
        string zip = CreateZip();
        Open("explorer.exe", $"/select,\"{zip}\"");
        Open(BuildIssueUrl(), null);
        return zip;
    }

    private static string CreateZip()
    {
        string dir = Path.Combine(AppPaths.UserDataDir, "bugreports");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir,
            "fova-bugreport-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddText(archive, "info.txt", SystemInfo());

        var cutoff = DateTime.Now - LogAge;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var logDir in new[] { AppPaths.LogDir, Path.Combine(AppPaths.UserDataDir, "logs") })
        {
            if (!Directory.Exists(logDir)) continue;
            foreach (var file in Directory.EnumerateFiles(logDir, "*.log"))
            {
                var full = Path.GetFullPath(file);
                if (!seen.Add(full) || File.GetLastWriteTime(full) < cutoff) continue;
                AddText(archive, "logs/" + Path.GetFileName(full), Tail(full));
            }
        }
        return path;
    }

    private static string SystemInfo() =>
        $"version: {Version}\n" +
        $"installed: {AppUpdater.IsInstalled}\n" +
        $"os: {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})\n" +
        $"language: {Localization.CurrentLanguage}\n" +
        $"created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";

    private static string Tail(string path)
    {
        // Shared read: the app is still writing several of these.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > TailBytes) fs.Seek(-TailBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string text = reader.ReadToEnd();
        return fs.Length > TailBytes ? "...(truncated)\n" + text : text;
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string BuildIssueUrl()
    {
        string body = Localization.F("bugreport.issue.body", Version, Environment.OSVersion.VersionString);
        return IssueUrl
               + "?title=" + Uri.EscapeDataString(Localization.L("bugreport.issue.title"))
               + "&body=" + Uri.EscapeDataString(body);
    }

    private static void Open(string target, string? args)
    {
        var psi = args is null
            ? new ProcessStartInfo(target) { UseShellExecute = true }
            : new ProcessStartInfo(target, args) { UseShellExecute = true };
        Process.Start(psi)?.Dispose();
    }
}
