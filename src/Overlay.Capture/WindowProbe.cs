using System.Runtime.InteropServices;
using System.Text;

namespace Overlay.Capture;

/// <summary>The display mode the tracked game window is (best-effort) running in — drives the
/// M31 §1 backend choice.</summary>
public enum GameDisplayMode
{
    /// <summary>Bordered/resizable window → Windows Graphics Capture.</summary>
    Windowed,

    /// <summary>Borderless window filling its monitor → Windows Graphics Capture.</summary>
    Borderless,

    /// <summary>Likely exclusive fullscreen → DXGI Desktop Duplication. NOTE: exclusive
    /// fullscreen is not reliably distinguishable from borderless by window styles alone; the
    /// authoritative signal is "WGC delivers no frames", which the orchestrator uses as a
    /// fallback trigger. This value is only the style-probe's best guess.</summary>
    ExclusiveFullscreenLikely,
}

/// <summary>
/// M31 P1 native window probing (user32/kernel32 P/Invoke): the tiny bits of Win32 the capture
/// orchestrator needs about the tracked game HWND — client size, containing monitor, foreground/
/// minimized state, a style-based display-mode guess, and the League install path for
/// <c>game.cfg</c> discovery (§2 layer 0). Path discovery uses
/// <c>QueryFullProcessImageName</c> with <c>PROCESS_QUERY_LIMITED_INFORMATION</c> — a PATH query,
/// explicitly NOT memory access (P3-safe; note this wording in the Riot submission).
///
/// <para>All methods are pure Win32 reads. UNVERIFIED in Cowork (no live window / no build) —
/// see CLAUDE_CODE_TODO.md §38-B.</para>
/// </summary>
public static class WindowProbe
{
    public readonly record struct PixelSize(int Width, int Height);
    public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>Client-area size (excludes title bar/borders) — the pixel space the minimap rect
    /// is calibrated in.</summary>
    public static PixelSize GetClientSize(IntPtr hwnd)
    {
        if (GetClientRect(hwnd, out var r))
            return new PixelSize(r.Right - r.Left, r.Bottom - r.Top);
        return new PixelSize(0, 0);
    }

    /// <summary>Handle of the monitor the window is (mostly) on — DXGI duplication captures this
    /// output.</summary>
    public static IntPtr GetContainingMonitor(IntPtr hwnd)
        => MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

    /// <summary>Full (virtual-desktop) bounds of a monitor.</summary>
    public static PixelRect GetMonitorBounds(IntPtr hMonitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hMonitor, ref mi))
            return new PixelRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
        return default;
    }

    public static bool IsForeground(IntPtr hwnd) => GetForegroundWindow() == hwnd;
    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    /// <summary>Best-effort display-mode guess from window styles + monitor coverage. See the
    /// caveat on <see cref="GameDisplayMode.ExclusiveFullscreenLikely"/>: prefer WGC and let a
    /// no-frames timeout fall back to DXGI rather than trusting this alone.</summary>
    public static GameDisplayMode ProbeDisplayMode(IntPtr hwnd)
    {
        long style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION; // caption bit set = bordered window
        bool hasThickFrame = (style & WS_THICKFRAME) != 0;

        if (hasCaption || hasThickFrame)
            return GameDisplayMode.Windowed;

        // No caption/frame → borderless OR exclusive fullscreen. If it fills its monitor exactly,
        // it *could* be exclusive; we return "likely", but the orchestrator prefers WGC first.
        if (GetWindowRect(hwnd, out var wr))
        {
            var mon = GetMonitorBounds(GetContainingMonitor(hwnd));
            if (mon.Width > 0 &&
                wr.Left <= mon.Left && wr.Top <= mon.Top &&
                wr.Right >= mon.Right && wr.Bottom >= mon.Bottom)
            {
                return GameDisplayMode.ExclusiveFullscreenLikely;
            }
        }
        return GameDisplayMode.Borderless;
    }

    /// <summary>Full path of the process image behind <paramref name="hwnd"/> (e.g.
    /// <c>…\League of Legends\Game\League of Legends.exe</c>), or null. PATH query only — not
    /// memory access.</summary>
    public static string? TryGetProcessImagePath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Resolve <c>Config\game.cfg</c> from the tracked game window's process image path.
    /// League's exe lives at <c>(install)\Game\League of Legends.exe</c>; game.cfg is at
    /// <c>(install)\Config\game.cfg</c>. Returns null if the path can't be derived — the caller
    /// falls back to well-known paths / a user setting (M31 §2 layer 0).</summary>
    public static string? TryGetGameCfgPath(IntPtr hwnd)
    {
        string? exe = TryGetProcessImagePath(hwnd);
        if (string.IsNullOrEmpty(exe)) return null;

        // …\<install>\Game\League of Legends.exe → …\<install>\Config\game.cfg
        string? gameDir = Path.GetDirectoryName(exe);
        string? installDir = gameDir is null ? null : Path.GetDirectoryName(gameDir);
        if (string.IsNullOrEmpty(installDir)) return null;

        return Path.Combine(installDir, "Config", "game.cfg");
    }

    // --- Win32 ---------------------------------------------------------------------------------

    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    // Bare "GetWindowLongPtr" is a C macro; the real x64 export is GetWindowLongPtrW. (Assumes an
    // x64 host — the WPF app is x64. On x86 this entry point does not exist; noted in the TODO.)
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
}
