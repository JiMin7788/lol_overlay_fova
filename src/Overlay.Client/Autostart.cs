using Microsoft.Win32;

namespace Overlay.Client;

/// <summary>
/// "Start with League" (<c>general.autoStartWithGame</c>). Windows has no unprivileged way to start
/// a program when ANOTHER program starts, so this is the usual companion-app shape: a per-user Run
/// entry starts Fova at sign-in with <see cref="BackgroundArg"/>, it waits in the tray, and
/// <see cref="TrayHost"/> brings the dashboard up when the League client appears.
/// </summary>
internal static class Autostart
{
    public const string BackgroundArg = "--background";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Fova";

    /// <summary>Writes or removes the Run entry. Called at every start (so the entry follows the
    /// exe if the install moved) and when the setting changes. Never throws.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" {BackgroundArg}");
            else if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // A locked-down registry only costs the autostart, never the app.
        }
    }

    /// <summary>Velopack uninstall hook: the Run entry would otherwise outlive the app.</summary>
    public static void Remove() => Apply(false);
}
