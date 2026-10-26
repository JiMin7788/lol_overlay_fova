namespace Overlay.Client;

/// <summary>
/// Which build this is. <see cref="IsBeta"/> is true only in a beta build — the full build compiled
/// with BETA, set by <c>-p:FovaBeta=true</c> (<c>packaging/build-release.ps1 -Beta</c>), lolF05.
/// </summary>
internal static class BuildFlavor
{
    // AI-NOTE: a get-only property, not a const — a const is copied into every assembly that
    // reads it, so a consumer whose rebuild is skipped would keep the other flavour's value.
#if BETA
    public static bool IsBeta { get; } = true;
#else
    public static bool IsBeta { get; } = false;
#endif
}
