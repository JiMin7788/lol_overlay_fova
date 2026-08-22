using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Overlay.Core.Render;

namespace Overlay.Client.Render;

/// <summary>
/// Thin WPF display layer for the M16 Render Pipeline. Consumes an ordered
/// <see cref="DrawCommand"/> list (produced by <see cref="RenderQueue.EndFrame"/>) and
/// draws each primitive onto a WPF <see cref="DrawingContext"/>.
///
/// <para><b>Scope / policy.</b> This is the ONLY rendering code in the module and it is
/// deliberately WPF-based, not raw Direct2D/DXGI. WPF's retained-mode compositor is
/// GPU-accelerated and renders into the overlay's OWN independent top-level window via
/// the OS compositor — it never touches the game's DirectX swapchain. That inherently
/// satisfies the M16 Policy Compliance Checklist ("독립 윈도우 렌더링", no
/// <c>IDXGISwapChain</c>/<c>Present</c> hooking). There is no DirectX interop,
/// injection, or swapchain interception anywhere in this file.</para>
///
/// <para><b>Software fallback (spec Internal Logic step 4).</b> No hand-rolled second
/// renderer is needed: WPF automatically falls back to software rasterisation when no
/// GPU acceleration is available (<c>RenderCapability.Tier == 0</c>). The same
/// <see cref="DrawingContext"/> calls below execute unchanged on the software path, so
/// minimum functionality is guaranteed on GPU-less environments for free.</para>
///
/// Stateless except for a small icon cache; all state lives in the passed
/// <see cref="DrawingContext"/>.
/// </summary>
internal static class DrawCommandRenderer
{
    /// <summary>
    /// Icon <see cref="ImageSource"/> cache keyed by the command's image reference.
    /// Without this, an Icon command would reload its bitmap from disk every frame
    /// (60x/s) — the cache keeps the hot render path allocation-light. Bounded only by
    /// the number of distinct icon refs the overlay uses (small, fixed HUD icon set).
    /// </summary>
    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new();

    private static readonly Typeface DefaultTypeface = new("Segoe UI");

    /// <summary>Monospace face for the combo overlay's damage numbers (§40 mockup uses Roboto Mono;
    /// Consolas is the always-present Windows equivalent). Selected per-command when a Text command's
    /// <see cref="DrawCommand.Value"/> is 1.</summary>
    private static readonly Typeface MonoTypeface = new("Consolas");

    /// <summary>Grayscale variants of already-decoded icons (dead-enemy portraits, §40). Keyed by the
    /// same image reference as <see cref="IconCache"/>, so a portrait is desaturated at most once.</summary>
    private static readonly ConcurrentDictionary<string, ImageSource?> GrayIconCache = new();

    /// <summary>The basic-attack (auto-attack) sword icon, built once from the approved
    /// <c>assets/basic-attack.svg</c> path data (24×24 viewBox, rotated −45° like the mockup). Drawn
    /// tinted by the command's style colour when an Icon command's reference is <c>"@basic-attack"</c>.</summary>
    private static readonly Geometry BasicAttackGeometry = BuildBasicAttackGeometry();

    private const string BasicAttackRef = "@basic-attack";

    private static Geometry BuildBasicAttackGeometry()
    {
        var g = new GeometryGroup();
        g.Children.Add(Geometry.Parse("M12 2.2c1.7 1.9 2.5 3.9 2.5 6.1v5.2h-5V8.3c0-2.2.8-4.2 2.5-6.1Z"));
        g.Children.Add(new RectangleGeometry(new Rect(6.4, 13.5, 11.2, 2.7), 1.35, 1.35));
        g.Children.Add(new RectangleGeometry(new Rect(10.8, 16.2, 2.4, 4.2), 1.1, 1.1));
        g.Children.Add(new EllipseGeometry(new Point(12, 21.2), 1.75, 1.75));
        g.Transform = new RotateTransform(-45, 12, 12);
        g.Freeze();
        return g;
    }

    /// <summary>
    /// Draw every command, in the order given, onto <paramref name="dc"/>. Callers pass
    /// the already-zOrder-sorted list from <see cref="RenderQueue.EndFrame"/>, so this
    /// method just iterates — the painter's-algorithm ordering is the queue's
    /// responsibility, not this layer's.
    /// </summary>
    public static void Render(DrawingContext dc, IReadOnlyList<DrawCommand> commands, double pixelsPerDip = 1.0)
    {
        for (int i = 0; i < commands.Count; i++)
            Draw(dc, commands[i], pixelsPerDip);
    }

    private static void Draw(DrawingContext dc, in DrawCommand cmd, double pixelsPerDip)
    {
        var b = cmd.Bounds;
        switch (cmd.Type)
        {
            case DrawCommandType.Text:
                DrawText(dc, cmd, pixelsPerDip);
                break;

            case DrawCommandType.Rect:
            {
                // Rounded corners give the layered card backgrounds/stripes a soft HUD look.
                // Radius is clamped to half the shorter side so thin accent stripes still
                // render sanely. Value == 1 opts OUT of rounding (RectSharp) so combo-sequence
                // ability chips render as true squares.
                double r = cmd.Value == 1
                    ? 0.0
                    : Math.Min(6.0, Math.Min(b.Width, b.Height) / 2.0);
                dc.DrawRoundedRectangle(Brush(cmd.Style), pen: null,
                    new Rect(b.X, b.Y, b.Width, b.Height), r, r);
                break;
            }

            case DrawCommandType.Line:
                // Bounds convention: segment from (X,Y) to (X+Width, Y+Height).
                dc.DrawLine(LinePen(cmd.Style),
                            new Point(b.X, b.Y),
                            new Point(b.X + b.Width, b.Y + b.Height));
                break;

            case DrawCommandType.Ellipse:
                // Filled ellipse inscribed in the bounds — combo-overlay circular portraits (§40).
                dc.DrawEllipse(Brush(cmd.Style), pen: null,
                    new Point(b.X + b.Width / 2, b.Y + b.Height / 2), b.Width / 2, b.Height / 2);
                break;

            case DrawCommandType.ProgressBar:
                DrawProgressBar(dc, cmd);
                break;

            case DrawCommandType.Icon:
                DrawIcon(dc, cmd);
                break;
        }
    }

    private static void DrawText(DrawingContext dc, in DrawCommand cmd, double pixelsPerDip)
    {
        if (string.IsNullOrEmpty(cmd.Content)) return;
        var b = cmd.Bounds;
        // Value == 1 selects the monospace face (§40 combo-overlay damage numbers); otherwise Segoe UI.
        var typeface = cmd.Value == 1 ? MonoTypeface : DefaultTypeface;
        var text = new FormattedText(
            cmd.Content,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            cmd.Style.FontSize > 0 ? cmd.Style.FontSize : 12.0,
            Brush(cmd.Style),
            pixelsPerDip > 0 ? pixelsPerDip : 1.0);
        dc.DrawText(text, new Point(b.X, b.Y));
    }

    private static void DrawProgressBar(DrawingContext dc, in DrawCommand cmd)
    {
        var b = cmd.Bounds;
        double fraction = Math.Clamp(cmd.Value ?? 0.0, 0.0, 1.0);

        // Track (background at reduced opacity) + fill (Style colour, Value fraction).
        var track = Brush(cmd.Style with { Opacity = cmd.Style.Opacity * 0.35 });
        dc.DrawRectangle(track, pen: null, new Rect(b.X, b.Y, b.Width, b.Height));
        dc.DrawRectangle(Brush(cmd.Style), pen: null, new Rect(b.X, b.Y, b.Width * fraction, b.Height));
    }

    private static void DrawIcon(DrawingContext dc, in DrawCommand cmd)
    {
        if (string.IsNullOrEmpty(cmd.Content)) return;
        var b = cmd.Bounds;
        var rect = new Rect(b.X, b.Y, b.Width, b.Height);

        // §40 basic-attack (auto) icon: draw the built-in sword geometry tinted by the style colour,
        // scaled from its 24×24 viewBox into the bounds. No bitmap load.
        if (cmd.Content == BasicAttackRef)
        {
            dc.PushTransform(new TranslateTransform(b.X, b.Y));
            dc.PushTransform(new ScaleTransform(b.Width / 24.0, b.Height / 24.0));
            dc.DrawGeometry(Brush(cmd.Style), pen: null, BasicAttackGeometry);
            dc.Pop();
            dc.Pop();
            return;
        }

        // §40 Icon flags packed in Value: bit0 = grayscale (dead enemy), bit1 = circular clip (portrait).
        int flags = (int)(cmd.Value ?? 0);
        bool grayscale = (flags & 1) != 0;
        bool circle = (flags & 2) != 0;

        var image = grayscale
            ? GrayIconCache.GetOrAdd(cmd.Content, TryLoadGray)
            : IconCache.GetOrAdd(cmd.Content, TryLoadImage);
        if (image is null) return;

        double opacity = Math.Clamp(cmd.Style.Opacity, 0.0, 1.0);
        bool pushedOpacity = opacity < 1.0;
        if (pushedOpacity) dc.PushOpacity(opacity);
        if (circle) dc.PushClip(new EllipseGeometry(rect));
        dc.DrawImage(image, rect);
        if (circle) dc.Pop();
        if (pushedOpacity) dc.Pop();
    }

    /// <summary>Loads an icon and returns a frozen GRAYSCALE variant (dead-enemy portrait). Best-effort:
    /// a failed load/convert returns null and the caller skips the image for the frame.</summary>
    private static ImageSource? TryLoadGray(string reference)
    {
        if (TryLoadImage(reference) is not BitmapSource src) return null;
        try
        {
            var gray = new FormatConvertedBitmap(src, PixelFormats.Gray8, destinationPalette: null, alphaThreshold: 0);
            gray.Freeze();
            return gray;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryLoadImage(string reference)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(reference, UriKind.RelativeOrAbsolute);
            bmp.EndInit();
            bmp.Freeze(); // cross-thread safe + immutable
            return bmp;
        }
        catch
        {
            // Best-effort: an unresolvable icon ref is skipped rather than crashing the
            // frame. M02 is responsible for supplying valid image references.
            return null;
        }
    }

    /// <summary>
    /// Frozen-brush cache keyed by packed colour + quantised opacity. A card is many
    /// primitives that reuse a handful of colours, so without this the hot render path
    /// would allocate a fresh <see cref="SolidColorBrush"/> per primitive per frame
    /// (~60x/s). Rendering is single-threaded (the WPF UI/compositor thread), so a plain
    /// dictionary is sufficient; entries are bounded by the small fixed HUD palette.
    /// </summary>
    private static readonly Dictionary<long, Brush> BrushCache = new();

    /// <summary>Frozen width-1 pens keyed the same way as <see cref="BrushCache"/> — the Line path
    /// allocated a fresh <see cref="Pen"/> every primitive every frame (loop 520). Same small
    /// bounded HUD palette, same single render thread.</summary>
    private static readonly Dictionary<long, Pen> LinePenCache = new();

    /// <summary>
    /// Build (or reuse) a frozen solid brush from a <see cref="RenderStyle"/>: unpack the
    /// packed 0xAARRGGBB colour and fold the 0..1 <see cref="RenderStyle.Opacity"/>
    /// multiplier into the brush opacity. Frozen so it is cheap and thread-safe to reuse.
    /// </summary>
    private static Brush Brush(in RenderStyle style)
    {
        double opacity = Math.Clamp(style.Opacity, 0.0, 1.0);
        long key = ((long)style.Color << 8) | (byte)(opacity * 255.0);
        if (BrushCache.TryGetValue(key, out var cached))
            return cached;

        byte a = (byte)((style.Color >> 24) & 0xFF);
        byte r = (byte)((style.Color >> 16) & 0xFF);
        byte g = (byte)((style.Color >> 8) & 0xFF);
        byte bl = (byte)(style.Color & 0xFF);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, bl))
        {
            Opacity = opacity,
        };
        brush.Freeze();
        BrushCache[key] = brush;
        return brush;
    }

    /// <summary>Reuse a frozen width-1 pen for the given style (see <see cref="LinePenCache"/>).</summary>
    private static Pen LinePen(in RenderStyle style)
    {
        double opacity = Math.Clamp(style.Opacity, 0.0, 1.0);
        long key = ((long)style.Color << 8) | (byte)(opacity * 255.0);
        if (LinePenCache.TryGetValue(key, out var cached))
            return cached;

        var pen = new Pen(Brush(style), 1.0);
        pen.Freeze();
        LinePenCache[key] = pen;
        return pen;
    }
}
