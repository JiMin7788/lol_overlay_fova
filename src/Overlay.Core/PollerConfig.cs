namespace Overlay.Core;

/// <summary>
/// Configuration for the local data core. Values here are intended to be
/// hydrated from a user JSON config (Hard Rule #4: config-driven, never hardcoded).
/// </summary>
public sealed class PollerConfig
{
    /// <summary>M01 Internal Logic #5 hard floor: Riot's recommended polling cadence
    /// must never be exceeded, so no configured value may bring the effective interval
    /// below 0.1s. This is a hard-coded minimum, not a mere default — see
    /// <see cref="PollInterval"/>'s init accessor, which clamps up to this floor rather
    /// than accepting a smaller value.</summary>
    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Endpoint to poll. Locked to the Riot Live Client Data API (Hard Rule #1).</summary>
    public string AllGameDataUrl { get; init; } = "https://127.0.0.1:2999/liveclientdata/allgamedata";

    private readonly TimeSpan _pollInterval = MinPollInterval;

    /// <summary>Poll interval. Default 0.1s per M01 Internal Logic #1. Any assigned
    /// value below <see cref="MinPollInterval"/> is clamped up to it — the 0.1s
    /// cadence is a hard rate-limit floor (Internal Logic #5), not just a suggestion.</summary>
    public TimeSpan PollInterval
    {
        get => _pollInterval;
        init => _pollInterval = value < MinPollInterval ? MinPollInterval : value;
    }

    /// <summary>Per-request HTTP timeout. Kept below the poll interval so a stalled
    /// request cannot starve the loop.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMilliseconds(90);

    /// <summary>Initial size of the pooled receive buffer (bytes). Grows as needed.</summary>
    public int InitialReceiveBufferSize { get; init; } = 64 * 1024;
}
