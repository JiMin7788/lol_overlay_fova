namespace Overlay.Core.Render;

/// <summary>
/// Kind of primitive a <see cref="DrawCommand"/> describes. Mirrors the M16 spec's
/// <c>DrawCommand.type</c> enum ({ TEXT, RECT, ICON, PROGRESS_BAR, LINE }).
/// </summary>
public enum DrawCommandType
{
    Text,
    Rect,
    Icon,
    ProgressBar,
    Line,
    /// <summary>A filled ellipse inscribed in <see cref="RenderBounds"/> — used by the combo overlay's
    /// circular enemy portraits (swatch fill + ring). Added for the §40 combo-overlay redesign; the
    /// rounded <see cref="Rect"/> can't render a true circle (its corner radius is capped small).</summary>
    Ellipse,
}

/// <summary>
/// Axis-aligned draw region in overlay-window DIP coordinates (spec's
/// <c>bounds { x, y, width, height }</c>). Pure value type — no allocation.
///
/// For <see cref="DrawCommandType.Line"/> the region is interpreted as the segment
/// from (X, Y) to (X + Width, Y + Height); this is the renderer's convention, not a
/// separate data shape.
/// </summary>
public readonly record struct RenderBounds(double X, double Y, double Width, double Height);

/// <summary>
/// Visual style for a <see cref="DrawCommand"/> (spec's <c>style { color, opacity,
/// fontSize, ... }</c>). Kept intentionally minimal — only the three fields the spec
/// names explicitly.
///
/// <see cref="Color"/> is a packed 32-bit ARGB value (0xAARRGGBB); the rendering
/// layer maps it to its platform colour type. This keeps the Core model free of any
/// WPF/Direct2D dependency (spec: Core render logic must be testable in isolation).
/// <see cref="Opacity"/> is a 0..1 multiplier applied on top of the colour's alpha.
/// </summary>
public readonly record struct RenderStyle(uint Color, double Opacity = 1.0, double FontSize = 12.0);

/// <summary>
/// One immutable, per-frame draw instruction produced by M02 (Overlay Engine) and
/// consumed by the render layer. Matches the M16 spec Data Model: the queue of these
/// is a volatile, frame-scoped structure with no persistence.
///
/// This module never decides <em>what</em> to draw (that is M02's job) — a
/// <see cref="DrawCommand"/> is a passive record of a single drawing primitive.
///
/// <para><b>Content union.</b> The spec's <c>content: string | ImageRef | number</c>
/// is modelled with two optional fields rather than a boxed <c>object</c>, so the
/// record stays allocation-free and cheap to compare:
/// <list type="bullet">
///   <item><see cref="Content"/> — the string payload: the display text for
///   <see cref="DrawCommandType.Text"/>, or the image reference (path/key) for
///   <see cref="DrawCommandType.Icon"/>.</item>
///   <item><see cref="Value"/> — the numeric payload: the 0..1 fill fraction for
///   <see cref="DrawCommandType.ProgressBar"/> (or any numeric content).</item>
/// </list>
/// Primitives that need neither (e.g. <see cref="DrawCommandType.Rect"/>,
/// <see cref="DrawCommandType.Line"/>) leave both null.</para>
/// </summary>
public readonly record struct DrawCommand(
    DrawCommandType Type,
    RenderBounds Bounds,
    RenderStyle Style,
    string? Content = null,
    double? Value = null);
