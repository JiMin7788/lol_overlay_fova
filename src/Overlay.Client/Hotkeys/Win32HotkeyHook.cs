using System.Runtime.InteropServices;
using System.Windows.Interop;
using Overlay.Core.Hotkeys;

namespace Overlay.Client.Hotkeys;

/// <summary>
/// Win32 implementation of the M13 <see cref="IHotkeyHook"/> seam. Registers each combo with
/// the OS via <c>RegisterHotKey</c> and receives presses via <c>WM_HOTKEY</c> on the overlay
/// window's own HWND (through <see cref="HwndSource.AddHook"/>). Follows the existing P/Invoke
/// style in <see cref="MainWindow"/> (which already <c>DllImport</c>s user32.dll).
///
/// ─── WHY RegisterHotKey, NOT a low-level keyboard hook ───────────────────────────────────
/// <c>RegisterHotKey</c> asks the OS to notify us ONLY when a specific registered combo is
/// pressed — the OS does the filtering; this code never sees any other keystroke. That
/// structurally satisfies M13's "hooking scope limited to the registered combos" /
/// "keylogger suspicion excluded" requirements. A <c>WH_KEYBOARD_LL</c> hook would see EVERY
/// keystroke and is deliberately NOT used.
///
/// ─── READ-ONLY ───────────────────────────────────────────────────────────────────────────
/// This class only detects the registered combos. It sends NO input (no SendInput/keybd_event)
/// and stores no keystrokes. On a press it just raises <see cref="HotkeyPressed"/> with the
/// combo's OS id; the wiring (see MainWindow note below) routes that to
/// <see cref="HotkeyRegistry.FireByOsId"/>.
///
/// ─── WIRING (MainWindow) ─────────────────────────────────────────────────────────────────
/// This layer is intentionally not force-wired into <see cref="MainWindow"/> to keep the
/// integration reviewable and headless-safe. To activate it, add in
/// <see cref="MainWindow.OnSourceInitialized"/> (after <c>_hwnd</c> is known):
/// <code>
///   var hook = new Win32HotkeyHook(_hwnd);
///   var hotkeys = new HotkeyRegistry(hook);
///   hook.HotkeyPressed += id => hotkeys.FireByOsId(id);
///   // hand `hotkeys` to whoever registers combos (M04/M02); dispose `hook` on window close.
/// </code>
/// </summary>
public sealed class Win32HotkeyHook : IHotkeyHook, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000; // ignore auto-repeat while held

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;

    /// <summary>Raised (on the UI thread, from the window's message pump) with the OS id of the
    /// combo that was pressed. Subscribe and forward to <see cref="HotkeyRegistry.FireByOsId"/>.</summary>
    public event Action<int>? HotkeyPressed;

    public Win32HotkeyHook(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd)
            ?? throw new ArgumentException("No HwndSource for the given handle.", nameof(hwnd));
        _source.AddHook(WndProc);
    }

    public void Register(int osId, HotkeyCombo combo)
    {
        if (!RegisterHotKey(_hwnd, osId, ToModifierFlags(combo.Modifiers), ToVirtualKey(combo.Key)))
        {
            throw new InvalidOperationException(
                $"RegisterHotKey failed for '{combo.Canonical}' (Win32 error {Marshal.GetLastWin32Error()}); " +
                "the combo may already be claimed by another application.");
        }
    }

    public void Unregister(int osId) => UnregisterHotKey(_hwnd, osId);

    public void Dispose() => _source.RemoveHook(WndProc);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            HotkeyPressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static uint ToModifierFlags(HotkeyModifiers modifiers)
    {
        uint flags = MOD_NOREPEAT;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) flags |= MOD_ALT;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) flags |= MOD_CONTROL;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) flags |= MOD_SHIFT;
        if (modifiers.HasFlag(HotkeyModifiers.Win)) flags |= MOD_WIN;
        return flags;
    }

    /// <summary>Maps a normalized key token to a Win32 virtual-key code. Covers digits, letters
    /// and function keys (the combos an overlay hotkey realistically uses); throws for anything
    /// else so an unsupported key fails loudly rather than registering the wrong key.</summary>
    /// <summary>Named (non-alphanumeric) keys the Win32 hook supports, keyed by the
    /// upper-cased token <see cref="HotkeyCombo"/> produces. Covers the common overlay/combo
    /// hotkey keys — notably TAB (default overlay toggle SHIFT+TAB), space, arrows, etc.</summary>
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.Ordinal)
    {
        ["TAB"] = 0x09, ["SPACE"] = 0x20, ["RETURN"] = 0x0D, ["ENTER"] = 0x0D,
        ["ESCAPE"] = 0x1B, ["ESC"] = 0x1B, ["BACK"] = 0x08, ["BACKSPACE"] = 0x08,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["INSERT"] = 0x2D, ["DELETE"] = 0x2E, ["HOME"] = 0x24, ["END"] = 0x23,
        ["PRIOR"] = 0x21, ["PAGEUP"] = 0x21, ["NEXT"] = 0x22, ["PAGEDOWN"] = 0x22,
    };

    private static uint ToVirtualKey(string key)
    {
        if (key.Length == 1)
        {
            char c = key[0];
            if (c >= '0' && c <= '9') return c;          // VK_0..VK_9 == '0'..'9'
            if (c >= 'A' && c <= 'Z') return c;          // VK_A..VK_Z == 'A'..'Z'
        }
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int n) && n >= 1 && n <= 24)
            return (uint)(0x70 + (n - 1));               // VK_F1 (0x70) .. VK_F24 (0x87)

        if (NamedKeys.TryGetValue(key, out var vk)) return vk;

        throw new NotSupportedException($"Hotkey key '{key}' is not supported by the Win32 hook.");
    }
}
