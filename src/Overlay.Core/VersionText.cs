namespace Overlay.Core;

/// <summary>
/// The version text shown in Help and the app.log header: the Velopack release version plus the
/// assembly's InformationalVersion (build stamp), with the release version appearing once.
/// </summary>
public static class VersionText
{
    public static string Compose(string? release, string informational)
    {
        if (string.IsNullOrEmpty(release)) return informational;
        // AI-NOTE: release builds already lead the stamp with the version (BuildStamp.targets
        // _ReleasePrefix), so repeating it would read "0.4.1 (0.4.1 abc123def ...)". The trailing space
        // keeps "0.4.10 ..." from counting as a "0.4.1" prefix.
        if (informational.StartsWith(release + " ", StringComparison.Ordinal)) return informational;
        return release + " (" + informational + ")";
    }
}
