using System.Drawing;

namespace Overlay.Client.UiTests;

/// <summary>
/// Copies of the <c>src/Overlay.Client/Theme/Theme.xaml</c> colours the dashboard checks look for
/// (RGB). The test project cannot reference Overlay.Client, so each value cites its source line;
/// <c>HarnessTests.ThemePalette_CitedLinesStillHoldTheValues</c> fails when a cited line no longer
/// holds the value.
/// </summary>
public static class ThemePalette
{
    public const uint Danger = 0xF87171;  // Theme.xaml:23 DangerColor
    public const uint Warning = 0xF2C14E; // Theme.xaml:24 WarningColor

    /// <summary>Every copied value with its Theme.xaml resource key and line, for the citation check.</summary>
    public static IReadOnlyList<(string Key, uint Rgb, int Line)> Citations { get; } = new[]
    {
        ("DangerColor", Danger, 23),
        ("WarningColor", Warning, 24),
    };

    public static Color ToColor(uint rgb) => Color.FromArgb(255, (int)(rgb >> 16) & 0xFF, (int)(rgb >> 8) & 0xFF, (int)rgb & 0xFF);
}
