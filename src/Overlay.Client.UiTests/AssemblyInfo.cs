using System.Runtime.CompilerServices;

// AI-NOTE: UI tests share one real mouse, keyboard and screen; two at once would steal each
// other's input and foreground window.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Overlay.Client.UiTests;

internal static class ProcessDpi
{
    /// <summary>Makes the test process per-monitor DPI aware before any window or UIA call.</summary>
    /// <remarks>AI-NOTE: (lolF02 t5) UIA reports physical pixels, but in a DPI-unaware process the cursor
    /// and GetWindowRect are scaled. At a 200% display every SafeInput point then missed the window it was
    /// read from; at 100% this changes nothing. Fails harmlessly when the host already set an awareness.</remarks>
    [ModuleInitializer]
    internal static void PerMonitorAware() => NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.PerMonitorAwareV2);
}
