namespace Overlay.Core.Overlay;

/// <summary>
/// The seam between the testable, WPF-free <see cref="OverlayCoordinator"/> (Core) and
/// the actual overlay window / dashboard (WPF host layer, <c>Overlay.Client</c>). The
/// coordinator owns all HUD state, timing, and Z-Order decisions and merely tells the
/// view <em>what</em> changed; the view (the existing click-through <c>MainWindow</c>
/// plus M16's <c>RenderSurface</c>) turns that into pixels.
///
/// <para>Keeping this interface tiny lets the coordinator be unit-tested against a fake
/// view with no window at all, exactly as M16's render logic is tested without a
/// display.</para>
/// </summary>
public interface IOverlayView
{
    /// <summary>A HUD element was shown/updated (its position already resolved to a
    /// concrete value by the coordinator).</summary>
    void ShowHud(HUDPayload payload);

    /// <summary>A HUD element was removed (via <see cref="OverlayCoordinator.HideHUD"/>
    /// or auto-hide expiry).</summary>
    void HideHud(string hudId);

    /// <summary>Open the settings/combo-manager dashboard (spec Scope: dashboard vs.
    /// in-game HUD separation). The WPF host disables click-through so the user can
    /// interact.</summary>
    void OpenDashboard();

    /// <summary>Close the dashboard and return to click-through HUD mode.</summary>
    void CloseDashboard();
}
