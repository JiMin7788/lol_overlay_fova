using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Overlay.Client;

/// <summary>
/// One-time move from the Inno Setup installs (0.3.3 and earlier) to the Velopack install, so a
/// user ends up with one Fova and their settings. Runs on every installed start and does nothing
/// once the old uninstall entry is gone.
///
/// <para>The old install folder is whatever the user picked (the default is
/// <c>%LocalAppData%\Programs\Fova</c>, but real installs live elsewhere), so it is read from the
/// uninstall entry rather than assumed.</para>
///
/// <para>Order matters. The old uninstaller deletes its <c>Config\</c> folder AND all of
/// <c>%LocalAppData%\Fova</c> (fova.iss [UninstallDelete]) — the very folder settings now live in —
/// and removes <c>Desktop\Fova.lnk</c>, the same path as the new shortcut. So: read the old
/// settings into memory, uninstall and wait, then write them and restore the shortcut.</para>
/// </summary>
internal static class LegacyInstall
{
    // fova.iss AppId, as Inno writes it under the per-user uninstall key.
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7F3A1C64-5B2E-4E7B-9D14-2C6A8F0B51D3}_is1";

    public static void Migrate()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string legacyDir) return;
            var uninstaller = Path.Combine(legacyDir, "unins000.exe");
            if (!File.Exists(uninstaller)) return;

            var oldConfig = Path.Combine(legacyDir, "config", "user_config.json");
            byte[]? settings = File.Exists(oldConfig) ? File.ReadAllBytes(oldConfig) : null;

            var desktopLink = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Fova.lnk");
            bool hadDesktopLink = File.Exists(desktopLink);

            using (var p = Process.Start(new ProcessStartInfo(uninstaller,
                       "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = false }))
                p?.WaitForExit(60_000);
            // Inno's uninstaller re-launches itself from %TEMP%, so the process above can exit
            // before the work is done; its own exe is the last thing it removes.
            for (int i = 0; i < 120 && File.Exists(uninstaller); i++) Thread.Sleep(500);

            var newConfig = Path.Combine(AppPaths.InstalledDataDir, "config", "user_config.json");
            if (settings is not null && !File.Exists(newConfig))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newConfig)!);
                File.WriteAllBytes(newConfig, settings);
            }

            if (hadDesktopLink && !File.Exists(desktopLink) && Environment.ProcessPath is { } exe)
                CreateShortcut(desktopLink, exe);
        }
        catch
        {
            // A failed migration leaves the old install in place; it must never block startup.
        }
    }

    private static void CreateShortcut(string linkPath, string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(linkPath);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.Save();
    }
}
