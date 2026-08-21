using System.Globalization;

namespace Overlay.Core.Vision;

/// <summary>
/// M31 §2 calibration LAYER 0 (the authoritative prior): the two HUD-layout values League
/// persists to <c>(League install)\Config\game.cfg</c>, section <c>[HUD]</c> —
/// <c>MinimapScale</c> (the user's minimap-size slider) and <c>FlipMiniMap</c> (bottom-left vs
/// bottom-right anchor). Reading these makes the minimap rect deterministic per user instead of
/// a pure geometric guess, and makes the flip AUTO-DETECTED (closes M31 §9-Q3).
///
/// <para>This reads a plain local settings FILE the game already wrote — it is NOT game-memory
/// access (P3-safe). Install-path DISCOVERY (locating game.cfg from the tracked process image
/// path via <c>QueryFullProcessImageName</c>) is a native concern deferred to the P1 build slice
/// — see <c>CLAUDE_CODE_TODO.md</c>; this type only parses text and reads a supplied path.</para>
///
/// <para>Both values are OPTIONAL: a missing file, missing <c>[HUD]</c> section, or missing/
/// malformed key yields <c>null</c> for that field, and the calibrator falls through to the
/// geometric prior (M31 §2 layer 1). Tolerant by design — never throws on a bad config.</para>
/// </summary>
public sealed class GameCfgHudSettings
{
    /// <summary>Raw <c>[HUD] MinimapScale</c> value, or null if absent/unparseable. This is the
    /// user's slider value; the mapping from this to an on-screen pixel size is an approximation
    /// whose coefficient must be pinned by live layer-2 auto-calibration (see
    /// <see cref="MinimapCalibrator"/> and the M31 P1 TODO). Range is NOT clamped here.</summary>
    public double? MinimapScale { get; init; }

    /// <summary>Raw <c>[HUD] FlipMiniMap</c> value (1/true → flipped), or null if absent. Unlike
    /// the scale, this is a clean boolean the calibrator can trust directly.</summary>
    public bool? FlipMiniMap { get; init; }

    /// <summary>An all-null instance (no usable config) — the fall-through signal.</summary>
    public static GameCfgHudSettings Empty { get; } = new();

    /// <summary>True when neither value was found — caller should use the geometric prior.</summary>
    public bool IsEmpty => MinimapScale is null && FlipMiniMap is null;
}

/// <summary>Parses <see cref="GameCfgHudSettings"/> out of League's <c>game.cfg</c>.</summary>
public static class GameCfgReader
{
    private const string HudSection = "HUD";
    private const string MinimapScaleKey = "MinimapScale";
    private const string FlipMiniMapKey = "FlipMiniMap";

    /// <summary>Parse the <c>[HUD]</c> minimap keys out of a game.cfg's text. Pure and tolerant:
    /// unknown sections/keys are ignored, malformed values become null. INI shape — sections in
    /// <c>[...]</c>, entries as <c>Key=Value</c>; keys and the section name are case-insensitive.
    /// If a key appears more than once, the LAST occurrence wins (matches how the client rewrites
    /// the file).</summary>
    public static GameCfgHudSettings Parse(string? cfgText)
    {
        if (string.IsNullOrEmpty(cfgText)) return GameCfgHudSettings.Empty;

        double? scale = null;
        bool? flip = null;
        bool inHud = false;

        foreach (var rawLine in cfgText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue; // blank / comment

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close > 1)
                {
                    var section = line.Substring(1, close - 1).Trim();
                    inHud = string.Equals(section, HudSection, StringComparison.OrdinalIgnoreCase);
                }
                continue;
            }

            if (!inHud) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();

            if (string.Equals(key, MinimapScaleKey, StringComparison.OrdinalIgnoreCase))
            {
                scale = TryParseDouble(value);
            }
            else if (string.Equals(key, FlipMiniMapKey, StringComparison.OrdinalIgnoreCase))
            {
                flip = TryParseBool(value);
            }
        }

        return new GameCfgHudSettings { MinimapScale = scale, FlipMiniMap = flip };
    }

    /// <summary>Read and parse the file at <paramref name="path"/>. Any I/O failure (missing
    /// file, locked, unreadable) returns <see cref="GameCfgHudSettings.Empty"/> so calibration
    /// silently falls through to the geometric prior — this is not a hot path but it is on the
    /// GAME.CONNECTED path, so failing loud here would be wrong.</summary>
    public static GameCfgHudSettings Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return GameCfgHudSettings.Empty;
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception)
        {
            return GameCfgHudSettings.Empty;
        }
    }

    private static double? TryParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    private static bool? TryParseBool(string value)
    {
        if (value.Length == 0) return null;
        if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        // Some clients write floats (e.g. "1.0000"); treat != 0 as flipped.
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d != 0.0
            : null;
    }
}
