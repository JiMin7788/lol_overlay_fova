namespace Overlay.Core.Logging;

/// <summary>
/// Where Core's file logs go. Defaults to <c>logs/</c> next to the exe. AI-NOTE: Core cannot see the
/// Client's AppPaths, so Program.Main sets this before anything logs (test mode moves it under
/// FOVA_USER_DATA_DIR).
/// </summary>
public static class LogDirectory
{
    public static string Current { get; set; } = Path.Combine(AppContext.BaseDirectory, "logs");
}
