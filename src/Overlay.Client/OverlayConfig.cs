using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Client;

/// <summary>
/// In-memory, deserialized view of the overlay shell configuration JSON.
/// Governs window geometry, opacity, click-through default, target monitor,
/// and per-widget placement / visibility settings.
///
/// Per Hard Rule #4, every visual/positional setting comes from this config —
/// nothing is hardcoded in <see cref="MainWindow"/> or in widget code-behind.
/// Loaded once at startup by <see cref="OverlayConfigLoader"/>; treated as
/// immutable thereafter.
///
/// Pattern mirrors <c>RecallConfigLoader</c> (Overlay.Core) — same
/// <see cref="JsonSerializerOptions"/>, same Load/LoadFromJson surface,
/// same <c>schemaVersion</c> field.
/// </summary>
public sealed class OverlayConfig
{
    /// <summary>Config schema version for forward-compat guards.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Index of the monitor to cover (0 = primary). Used to position the
    /// window over the correct screen when multiple monitors are present.
    /// </summary>
    [JsonPropertyName("targetMonitorIndex")]
    public int TargetMonitorIndex { get; init; } = 0;

    /// <summary>
    /// Overall window opacity in [0.0, 1.0]. 1.0 = fully opaque; values below
    /// 1.0 make the entire window (including widgets) semi-transparent.
    /// The shell itself is transparent (background = Transparent), so this
    /// mainly affects any future non-transparent widget regions.
    /// </summary>
    [JsonPropertyName("windowOpacity")]
    public double WindowOpacity { get; init; } = 1.0;

    /// <summary>
    /// When true (default), the window starts in click-through mode:
    /// Win32 extended style <c>WS_EX_TRANSPARENT | WS_EX_LAYERED</c> is applied
    /// so all mouse input falls through to the game beneath.
    /// Call <see cref="MainWindow.SetClickThrough(bool)"/> to toggle at runtime.
    /// </summary>
    [JsonPropertyName("clickThroughByDefault")]
    public bool ClickThroughByDefault { get; init; } = true;

    /// <summary>
    /// Per-widget display settings: anchor coordinates, font size, and visibility.
    ///
    /// Hard Rule #4: widget placement is config-driven — these values are read once
    /// at startup and applied via <see cref="MainWindow"/> to the Canvas children.
    /// </summary>
    [JsonPropertyName("widgets")]
    public WidgetsConfig Widgets { get; init; } = new();
}

/// <summary>
/// Container for per-widget configuration entries.
///
/// Hard Rule #4: all widget position/font/visibility settings live in this
/// object — never hardcoded in XAML or code-behind.
/// </summary>
public sealed class WidgetsConfig
{
    /// <summary>Objective-timers widget (Dragon, Baron, Rift Herald, etc.).</summary>
    [JsonPropertyName("objectiveTimers")]
    public WidgetConfig ObjectiveTimers { get; init; } = new()
    {
        Visible  = true,
        AnchorX  = 20,
        AnchorY  = 20,
        FontSize = 13,
    };

    /// <summary>Recall / return-countdown widget (per-player ETA).</summary>
    [JsonPropertyName("recall")]
    public WidgetConfig Recall { get; init; } = new()
    {
        Visible  = true,
        AnchorX  = 20,
        AnchorY  = 200,
        FontSize = 13,
    };
}

/// <summary>
/// Placement and appearance settings for a single widget panel.
///
/// Hard Rule #4: every field here maps directly to a Canvas.Left/Canvas.Top
/// or FontSize binding — no hardcoded defaults in widget XAML.
/// </summary>
public sealed class WidgetConfig
{
    /// <summary>
    /// Whether the widget is shown at all. When false the widget's
    /// <c>Visibility</c> is set to <see cref="System.Windows.Visibility.Collapsed"/>.
    /// </summary>
    [JsonPropertyName("visible")]
    public bool Visible { get; init; } = true;

    /// <summary>
    /// Horizontal anchor (Canvas.Left, WPF DIPs from the left edge of the overlay).
    /// </summary>
    [JsonPropertyName("anchorX")]
    public double AnchorX { get; init; } = 20;

    /// <summary>
    /// Vertical anchor (Canvas.Top, WPF DIPs from the top edge of the overlay).
    /// </summary>
    [JsonPropertyName("anchorY")]
    public double AnchorY { get; init; } = 20;

    /// <summary>
    /// Font size (WPF points) used for all text labels inside the widget.
    /// </summary>
    [JsonPropertyName("fontSize")]
    public double FontSize { get; init; } = 13;
}

/// <summary>
/// Loads <see cref="OverlayConfig"/> from the bundled JSON file.
/// Call once at startup and cache the result.
///
/// Mirrors <c>RecallConfigLoader</c> in Overlay.Core:
///   – same <see cref="JsonSerializerOptions"/> (case-insensitive, skip comments,
///     allow trailing commas);
///   – same <c>Load(path?)</c> / <c>LoadFromJson(string)</c> surface;
///   – default path under <see cref="AppContext.BaseDirectory"/>.
/// </summary>
public static class OverlayConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Default on-disk path relative to the app base directory.</summary>
    public static string DefaultConfigPath
        => Path.Combine(AppContext.BaseDirectory, "Config", "overlay-config.json");

    /// <summary>
    /// Load and deserialize from <paramref name="path"/> (defaults to the
    /// bundled file). Throws on missing/malformed — this is a session-startup
    /// operation, so failing loud is correct (mirrors RecallConfigLoader).
    /// </summary>
    public static OverlayConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath;
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<OverlayConfig>(stream, Options);
        return data ?? throw new InvalidDataException(
            $"overlay config at '{path}' deserialized to null");
    }

    /// <summary>Deserialize from an already-read JSON string (testing/embedding).</summary>
    public static OverlayConfig LoadFromJson(string json)
    {
        var data = JsonSerializer.Deserialize<OverlayConfig>(json, Options);
        return data ?? throw new InvalidDataException(
            "overlay config JSON deserialized to null");
    }
}
