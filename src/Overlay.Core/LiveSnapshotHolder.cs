namespace Overlay.Core;

/// <summary>
/// Holds a stable copy of the latest game snapshot from a <see cref="LiveClientPoller"/>, and
/// clears it when the poller reports the game gone (null = no game running).
/// </summary>
public sealed class LiveSnapshotHolder
{
    private volatile GameSnapshot? _latest;

    /// <summary>Latest per-tick capture, or null before the first tick / after a disconnect.
    /// Written on the poller's background thread; the volatile field publishes the new
    /// reference to readers on any thread ("latest wins").</summary>
    public GameSnapshot? Latest => _latest;

    /// <summary>AI-NOTE: construct this BEFORE any <see cref="LiveClientEventPublisher"/> on the same
    /// poller. Multicast handlers run in subscription order, so the disconnect clear then happens
    /// before GAME.DISCONNECTED subscribers run and read <see cref="Latest"/>.</summary>
    public LiveSnapshotHolder(LiveClientPoller poller)
    {
        ArgumentNullException.ThrowIfNull(poller);

        // 'cur' is the poller's REUSABLE buffer (_current), which it swaps and re-parses in
        // place on the very next tick — so we must COPY it, not store the reference, or a
        // reader off the poll thread (a combo hotkey press) could see a torn/half-updated
        // snapshot. GameSnapshot.CopyTo gives a stable per-tick capture the poller never mutates.
        poller.SnapshotAvailable += (prev, cur, initial) =>
        {
            var capture = new GameSnapshot();
            cur.CopyTo(capture);
            _latest = capture;
        };

        // The poller raises no further SnapshotAvailable after a disconnect, so without this
        // the last game's snapshot would stay "live" forever.
        poller.DiffAvailable += diff =>
        {
            if (diff.GameAvailabilityChanged && !diff.GameIsActive)
                _latest = null;
        };
    }
}
