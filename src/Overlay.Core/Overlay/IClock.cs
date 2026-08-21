namespace Overlay.Core.Overlay;

/// <summary>
/// Minimal monotonic-ish time source for the Overlay Engine's auto-hide scheduling.
/// Injected into <see cref="OverlayCoordinator"/> so the "HUD auto-hides within 100ms
/// of the configured duration" Acceptance Criterion is unit-testable deterministically
/// with a fake clock — no real wall-clock <c>Thread.Sleep</c> in tests.
/// </summary>
public interface IClock
{
    /// <summary>Current time in Unix-epoch milliseconds.</summary>
    long NowMs { get; }
}

/// <summary>Production <see cref="IClock"/> backed by the system UTC clock.</summary>
public sealed class SystemClock : IClock
{
    public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
