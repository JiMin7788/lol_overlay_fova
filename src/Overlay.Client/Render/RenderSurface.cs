using System.Windows;
using System.Windows.Media;
using Overlay.Core.Render;

namespace Overlay.Client.Render;

/// <summary>
/// WPF display surface for the M16 Render Pipeline. A lightweight
/// <see cref="FrameworkElement"/> that paints the most recently submitted frame's
/// <see cref="DrawCommand"/> list via <see cref="DrawCommandRenderer"/>.
///
/// <para>Usage from M02's frame loop (on the UI/dispatcher thread):
/// <c>queue.BeginFrame()</c> → <c>queue.Enqueue(...)</c> … →
/// <c>surface.Submit(queue.EndFrame())</c>. <see cref="Submit"/> stores the ordered
/// list and invalidates the visual; WPF then calls <see cref="OnRender"/> to paint it
/// during the next compositor pass (Vsync-aligned by the WPF compositor — the module
/// does not, and must not, drive Present itself).</para>
///
/// <para><b>Policy.</b> This element lives inside the overlay's own independent window
/// (see <see cref="MainWindow"/>). It composites through WPF/the OS compositor only —
/// no DirectX swapchain hooking, no injection. See <see cref="DrawCommandRenderer"/>
/// for the full software-fallback / no-DX-hook rationale.</para>
/// </summary>
public sealed class RenderSurface : FrameworkElement
{
    private IReadOnlyList<DrawCommand> _frame = Array.Empty<DrawCommand>();

    public RenderSurface()
    {
        // Crisp HUD text: pixel-snapped glyphs (Display mode) with grayscale AA, which
        // renders cleanly on the overlay's (possibly transparent) top-level window where
        // ClearType sub-pixel AA would fringe.
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
    }

    /// <summary>
    /// Submit the ordered command list for the next paint (the return value of
    /// <see cref="RenderQueue.EndFrame"/>). Must be called on the UI thread. Triggers a
    /// repaint via <see cref="UIElement.InvalidateVisual"/>.
    /// </summary>
    public void Submit(IReadOnlyList<DrawCommand> frame)
    {
        _frame = frame ?? Array.Empty<DrawCommand>();
        InvalidateVisual();
    }

    /// <summary>
    /// Paint the current frame. WPF drives this on the render pass; the commands are
    /// already zOrder-sorted by <see cref="RenderQueue"/>, so this just replays them.
    /// </summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        // (loop 520) Pass the real per-DIP pixel density so FormattedText is laid out for the
        // active display scaling. The renderer hardcoded 1.0, which formats glyphs for 96 dpi and
        // then lets WPF scale them — slightly soft/mispositioned HUD text at 125%/150%.
        DrawCommandRenderer.Render(drawingContext, _frame, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
