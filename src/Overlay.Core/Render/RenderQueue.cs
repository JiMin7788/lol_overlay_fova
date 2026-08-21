namespace Overlay.Core.Render;

/// <summary>
/// Pure, deterministic frame-lifecycle engine for the M16 Render Pipeline — the
/// testable heart of the module. It owns the spec's Internal Logic steps 1–3
/// (frame-clear, enqueue, zOrder sort) and contains <b>no rendering code at all</b>:
/// the actual drawing lives in the platform layer (WPF: <c>Overlay.Client.Render</c>),
/// which consumes the ordered list this class produces.
///
/// <para><b>Frame lifecycle</b> (spec Interfaces + Internal Logic):
/// <list type="number">
///   <item><see cref="BeginFrame"/> — clears the previous frame's queue (step 1).</item>
///   <item><see cref="Enqueue(DrawCommand, int)"/> — M02 registers each item with its
///   zOrder (step 2). Called any number of times per frame.</item>
///   <item><see cref="EndFrame"/> — returns this frame's commands sorted by zOrder
///   ascending (step 3), ready for a single batched draw pass on the display thread.</item>
/// </list></para>
///
/// <para><b>Stable ordering.</b> The sort is stable: commands with equal zOrder are
/// returned in the order they were enqueued. This makes "5+ HUD elements render in the
/// specified order" fully deterministic (spec Acceptance Criteria), including ties.
/// Stability is achieved by carrying a monotonic per-frame enqueue sequence number and
/// using it as the tiebreaker, rather than relying on <see cref="List{T}.Sort"/> (which
/// is not stable on its own).</para>
///
/// <para><b>Not thread-safe by design.</b> M02 fills the queue and the render layer
/// drains it on a single logical frame timeline; there is no cross-thread contract in
/// the spec, so no locking is added (Hard Rule: simplicity first — no speculative
/// synchronisation).</para>
/// </summary>
public sealed class RenderQueue
{
    /// <summary>One enqueued command plus its stable-sort key (enqueue sequence).</summary>
    private readonly struct Entry
    {
        public readonly DrawCommand Command;
        public readonly int ZOrder;
        public readonly long Seq;

        public Entry(DrawCommand command, int zOrder, long seq)
        {
            Command = command;
            ZOrder = zOrder;
            Seq = seq;
        }
    }

    private readonly List<Entry> _entries = new();
    private long _seq;

    /// <summary>
    /// Begin a new frame: clear the previous frame's queued commands (spec Internal
    /// Logic step 1). Safe to call before the first frame and after any
    /// <see cref="EndFrame"/>.
    /// </summary>
    public void BeginFrame()
    {
        _entries.Clear();
        _seq = 0;
    }

    /// <summary>
    /// Register a command to be drawn this frame at the given <paramref name="zOrder"/>
    /// (spec Internal Logic step 2). Higher zOrder draws on top (returned later by
    /// <see cref="EndFrame"/>). Equal zOrder preserves enqueue order.
    /// </summary>
    public void Enqueue(DrawCommand command, int zOrder)
        => _entries.Add(new Entry(command, zOrder, _seq++));

    /// <summary>
    /// Number of commands currently queued for the in-progress frame.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// End the frame and return this frame's commands sorted by zOrder ascending,
    /// stable within equal zOrder (spec Internal Logic step 3). The result is a fresh
    /// snapshot array; the internal queue is left intact (idempotent — calling twice
    /// yields the same ordering) until the next <see cref="BeginFrame"/> clears it.
    /// </summary>
    public IReadOnlyList<DrawCommand> EndFrame()
    {
        var ordered = new List<Entry>(_entries);

        // Ascending zOrder; ties broken by enqueue sequence => stable ordering.
        ordered.Sort(static (a, b) =>
        {
            int byZ = a.ZOrder.CompareTo(b.ZOrder);
            return byZ != 0 ? byZ : a.Seq.CompareTo(b.Seq);
        });

        var result = new DrawCommand[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
            result[i] = ordered[i].Command;
        return result;
    }
}
