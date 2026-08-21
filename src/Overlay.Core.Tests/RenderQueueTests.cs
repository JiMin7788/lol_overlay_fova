using Overlay.Core.Render;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for the pure, testable heart of M16 Render Pipeline
/// (docs/modules/M16_RENDER_PIPELINE.md) — <see cref="RenderQueue"/> and the
/// <see cref="DrawCommand"/> model. Covers the two testable Acceptance Criteria that do
/// not require a display:
///  - Z-Order correctness: EndFrame returns commands sorted by zOrder ascending, and
///    STABLE within equal zOrder ("5+ HUD 요소가 표시되어도 Z-Order가 명세대로 정확히
///    렌더링된다" — the ordering half of that criterion, verified without any rendering).
///  - Frame-clear semantics: BeginFrame clears the prior frame (spec Internal Logic
///    step 1).
///
/// The 60fps frame-drop criterion needs a live display run and is deferred to a manual
/// measurement via Overlay.Client.Render.FrameDropMeter (see M16 Agent Report). No
/// WPF/rendering code is exercised here — that layer is not unit-testable headlessly.
/// </summary>
public class RenderQueueTests
{
    private static DrawCommand Cmd(string? content = null, DrawCommandType type = DrawCommandType.Rect)
        => new(type, new RenderBounds(0, 0, 10, 10), new RenderStyle(0xFFFFFFFF), content);

    // ── Basic zOrder sort ──────────────────────────────────────────────────────

    [Fact]
    public void EndFrame_ReturnsCommandsSortedByZOrderAscending()
    {
        var q = new RenderQueue();
        q.BeginFrame();
        q.Enqueue(Cmd("top"), zOrder: 10);
        q.Enqueue(Cmd("bottom"), zOrder: 1);
        q.Enqueue(Cmd("middle"), zOrder: 5);

        var frame = q.EndFrame();

        Assert.Equal(new[] { "bottom", "middle", "top" }, frame.Select(c => c.Content));
    }

    [Fact]
    public void EndFrame_SupportsNegativeAndZeroZOrder()
    {
        var q = new RenderQueue();
        q.BeginFrame();
        q.Enqueue(Cmd("zero"), 0);
        q.Enqueue(Cmd("neg"), -5);
        q.Enqueue(Cmd("pos"), 3);

        var frame = q.EndFrame();

        Assert.Equal(new[] { "neg", "zero", "pos" }, frame.Select(c => c.Content));
    }

    // ── Stable ordering within equal zOrder ─────────────────────────────────────

    [Fact]
    public void EndFrame_EqualZOrder_PreservesEnqueueOrder_Stable()
    {
        var q = new RenderQueue();
        q.BeginFrame();
        // All same zOrder: output order must equal enqueue order.
        q.Enqueue(Cmd("a"), 5);
        q.Enqueue(Cmd("b"), 5);
        q.Enqueue(Cmd("c"), 5);
        q.Enqueue(Cmd("d"), 5);

        var frame = q.EndFrame();

        Assert.Equal(new[] { "a", "b", "c", "d" }, frame.Select(c => c.Content));
    }

    [Fact]
    public void EndFrame_InterleavedZOrders_SortStableWithinEachLayer()
    {
        var q = new RenderQueue();
        q.BeginFrame();
        // 6 commands, interleaved zOrders with duplicates. Expected:
        //   z=1: x1 ; z=2: y1, y2 (enqueue order) ; z=3: z1, z2 (enqueue order)
        q.Enqueue(Cmd("z1"), 3);
        q.Enqueue(Cmd("y1"), 2);
        q.Enqueue(Cmd("x1"), 1);
        q.Enqueue(Cmd("z2"), 3);
        q.Enqueue(Cmd("y2"), 2);
        q.Enqueue(Cmd("z3"), 3);

        var frame = q.EndFrame();

        Assert.Equal(new[] { "x1", "y1", "y2", "z1", "z2", "z3" }, frame.Select(c => c.Content));
    }

    [Fact]
    public void EndFrame_FiveHudElements_RenderInSpecifiedOrder()
    {
        // Acceptance Criteria: 5+ concurrent HUD elements sort by zOrder exactly.
        var q = new RenderQueue();
        q.BeginFrame();
        q.Enqueue(Cmd("tooltip"), 100);
        q.Enqueue(Cmd("background"), 0);
        q.Enqueue(Cmd("progressbar"), 20);
        q.Enqueue(Cmd("icon"), 30);
        q.Enqueue(Cmd("label"), 20); // ties progressbar's layer; enqueued after it

        var order = q.EndFrame().Select(c => c.Content).ToArray();

        // background(0) < progressbar(20) < label(20, stable after progressbar) < icon(30) < tooltip(100)
        Assert.Equal(new[] { "background", "progressbar", "label", "icon", "tooltip" }, order);
    }

    // ── Frame-clear semantics ───────────────────────────────────────────────────

    [Fact]
    public void BeginFrame_ClearsPreviousFrame()
    {
        var q = new RenderQueue();
        q.BeginFrame();
        q.Enqueue(Cmd("frame1-a"), 1);
        q.Enqueue(Cmd("frame1-b"), 2);
        Assert.Equal(2, q.EndFrame().Count);

        // New frame must not carry over the previous frame's commands.
        q.BeginFrame();
        Assert.Equal(0, q.Count);
        Assert.Empty(q.EndFrame());

        // A fresh command in the new frame is the only one present.
        q.BeginFrame();
        q.Enqueue(Cmd("frame3"), 7);
        var frame3 = q.EndFrame();
        Assert.Single(frame3);
        Assert.Equal("frame3", frame3[0].Content);
    }

    [Fact]
    public void EndFrame_EmptyFrame_ReturnsEmpty()
    {
        var q = new RenderQueue();
        q.BeginFrame();
        Assert.Empty(q.EndFrame());
    }

    [Fact]
    public void EndFrame_IsIdempotent_DoesNotMutateQueue()
    {
        var q = new RenderQueue();
        q.BeginFrame();
        q.Enqueue(Cmd("a"), 2);
        q.Enqueue(Cmd("b"), 1);

        var first = q.EndFrame().Select(c => c.Content).ToArray();
        var second = q.EndFrame().Select(c => c.Content).ToArray();

        Assert.Equal(new[] { "b", "a" }, first);
        Assert.Equal(first, second); // calling twice yields identical ordering
    }

    // ── DrawCommand model: each type constructs and round-trips its fields ───────

    [Theory]
    [InlineData(DrawCommandType.Text)]
    [InlineData(DrawCommandType.Rect)]
    [InlineData(DrawCommandType.Icon)]
    [InlineData(DrawCommandType.ProgressBar)]
    [InlineData(DrawCommandType.Line)]
    public void DrawCommand_EachType_Constructs(DrawCommandType type)
    {
        var cmd = new DrawCommand(
            type,
            new RenderBounds(1, 2, 3, 4),
            new RenderStyle(0x80FF0000, Opacity: 0.5, FontSize: 14),
            Content: "payload",
            Value: 0.75);

        Assert.Equal(type, cmd.Type);
        Assert.Equal(new RenderBounds(1, 2, 3, 4), cmd.Bounds);
        Assert.Equal(0x80FF0000u, cmd.Style.Color);
        Assert.Equal(0.5, cmd.Style.Opacity);
        Assert.Equal(14, cmd.Style.FontSize);
        Assert.Equal("payload", cmd.Content);
        Assert.Equal(0.75, cmd.Value);
    }

    [Fact]
    public void RenderStyle_DefaultsOpacityAndFontSize()
    {
        var style = new RenderStyle(0xFF000000);
        Assert.Equal(1.0, style.Opacity);
        Assert.Equal(12.0, style.FontSize);
    }

    [Fact]
    public void DrawCommand_ContentAndValue_DefaultToNull()
    {
        var cmd = new DrawCommand(DrawCommandType.Rect, default, new RenderStyle(0));
        Assert.Null(cmd.Content);
        Assert.Null(cmd.Value);
    }
}
