using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Overlay.Core.Hotkeys;

namespace Overlay.Client.Hotkeys;

/// <summary>
/// Low-level keyboard-hook implementation of the M13 <see cref="IHotkeyHook"/> seam. Drop-in
/// replacement for <see cref="Win32HotkeyHook"/>: same <c>Register(osId, combo)</c> /
/// <c>Unregister(osId)</c> surface and the same <see cref="HotkeyPressed"/> event, so
/// <c>AppComposition.WireHotkeys</c> swaps one for the other with no other change.
///
/// ─── WHY (RegisterHotKey failed over the game) ─────────────────────────────────────────────
/// <c>RegisterHotKey</c> relies on the OS delivering <c>WM_HOTKEY</c> to our window. Over a
/// focused, full-screen League client the OS frequently does NOT deliver those messages (the
/// game's input handling / focus model swallows them), so the overlay toggle and combo hotkeys
/// silently did nothing while in a game — even running as administrator. A <c>WH_KEYBOARD_LL</c>
/// hook is global and fires for key events regardless of which window has focus, so it works
/// over the game.
///
/// ─── POLICY RECONCILIATION (this is a hotkey MATCHER, not a keylogger) ─────────────────────
/// The original M13 design banned <c>WH_KEYBOARD_LL</c> out of keylogger concern. This class is
/// deliberately constrained to be functionally equivalent to <c>RegisterHotKey</c> — it only
/// reacts to the exact set of REGISTERED combos, it just does so over the game:
///  • It checks each key event ONLY against the registered combos and fires on a match.
///  • It NEVER logs, stores, buffers, or transmits any keystroke. There is no key history, no
///    file/console output of keys, no "all keys" collection. The ONLY state kept is: the registered
///    combos; the currently-held modifier keys; the currently-held non-modifier key tokens (each only
///    while held - removed on its key-up, and all held keys cleared on every foreground-window change
///    and on reinstall); the Tab-scoreboard and chat open/closed flags in <see cref="HotkeyGate"/>
///    (with their Enter/Tab first-down latches); and the foreground-window class/title check (is the
///    League game window in front - a yes/no, the name itself is not kept). That state is tracked purely
///    from this hook's own key events and the foreground window — never via <c>GetAsyncKeyState</c> /
///    <c>GetKeyboardState</c> (those are intentionally not used).
///  • It does NOT consume/swallow the key: every event is passed through with
///    <c>CallNextHookEx</c>, so normal gameplay typing is completely unaffected.
///  • (lolF01) A <see cref="HotkeyGate"/> decides whether a key may fire: in a game, only over the League window with its chat closed.
///
/// ─── READ-ONLY (P3/P4) ─────────────────────────────────────────────────────────────────────
/// Sends NO input (no SendInput/keybd_event). On a match it only raises
/// <see cref="HotkeyPressed"/> with the combo's OS id; the wiring routes that to
/// <see cref="HotkeyRegistry.FireByOsId"/>.
///
/// ─── THREADING ─────────────────────────────────────────────────────────────────────────────
/// A low-level hook's callback is dispatched on the thread that installed it, and that thread
/// must run a message pump. <c>AppComposition.WireHotkeys</c> runs on the WPF UI thread (which
/// has a Dispatcher message pump), so the hook is installed there and the callback also runs on
/// the UI thread.
///
/// <para>(loop 518) The callback does NOT run <see cref="HotkeyPressed"/> synchronously. Windows
/// silently UNHOOKS a WH_KEYBOARD_LL whose callback exceeds <c>LowLevelHooksTimeout</c> (~300 ms
/// default) — no error, no event — and the handler here drives the full combo damage engine
/// (under a lock, and it can block on a live-refresh compute), plus WPF show/hide and config
/// fan-out. Any of those can exceed the budget on a busy UI thread and kill every hotkey until
/// restart. So the callback only tracks modifiers and posts the fire via
/// <see cref="Dispatcher.BeginInvoke(DispatcherPriority, Delegate)"/>, returning to
/// <c>CallNextHookEx</c> in microseconds. The handler still runs on the UI thread, exactly as
/// before — only the timing changes from sync to queued. A watchdog reinstalls the hook if
/// Windows removes it anyway, and modifier state is resynced across session switches.</para>
/// </summary>
public sealed class LowLevelHotkeyHook : IHotkeyHook, IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // Modifier virtual-key codes (generic + left/right variants). Held modifiers are tracked by these
    // codes; held non-modifier keys only as combo tokens (see _heldKeys).
    private const uint VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const uint VK_CONTROL = 0x11, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const uint VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5; // Alt
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Same identifiers MainWindow uses to find the League in-game window.
    private const string LeagueWindowClass = "RiotWindowClass";
    private const string LeagueWindowTitle = "League of Legends (TM) Client";

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    // Kept in a field so the GC never collects the delegate while the hook is installed.
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookHandle;

    // (lolF01) Decides whether a key may fire and owns the chat/scoreboard mirrors.
    private readonly HotkeyGate _gate;
    // Foreground-change hook (out-of-context: delivered on this install thread's pump). Delegate kept for the GC.
    private readonly WinEventProc _foregroundProc;
    private IntPtr _foregroundHook;
    // Reused for the foreground window's class/title (both callbacks run on the install thread).
    private readonly char[] _nameBuffer = new char[64];

    // The install thread's dispatcher — the fire is queued back onto it so the hook callback
    // returns immediately (see class doc "THREADING"). Reinstalls must also run on this thread.
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _watchdog;
    private long _lastCallbackTick = Environment.TickCount64;
    private bool _disposed;

    // Watchdog: reinstall if the callback has been silent longer than this. In game the QWER keys
    // fire constantly so this is never stale during play; it only elapses when truly idle, where a
    // reinstall is harmless (and clears any modifier stuck by a missed key-up). Comfortably above
    // any real idle-between-abilities gap so it never churns mid-fight.
    private const long SilenceReinstallMs = 120_000;

    private readonly object _lock = new();
    // The only state this class keeps (the gate's flags aside - see class doc "POLICY RECONCILIATION"):
    // which combos are registered (both directions for O(1) lookup) and which keys are held right now.
    private readonly Dictionary<HotkeyCombo, int> _comboToOsId = new();
    private readonly Dictionary<int, HotkeyCombo> _osIdToCombo = new();
    // Currently-held modifier keys (by vkCode). Not a keystroke log — removed on key-up, cleared on a
    // foreground change and on reinstall, and used solely to build the chord for a registered-combo match.
    private readonly HashSet<uint> _heldModifiers = new();

    // (M46) Non-modifier keys currently down, as combo tokens - a chord may hold up to three keys (Q+W).
    // Same lifetime as _heldModifiers: only while held.
    private readonly List<string> _heldKeys = new();

    /// <summary>Raised (on the install/UI thread, from the hook callback) with the OS id of the
    /// registered combo that was pressed. Subscribe and forward to
    /// <see cref="HotkeyRegistry.FireByOsId"/>.</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>Optional sink for the rare watchdog/reinstall lines (wire to M18 logging).</summary>
    public Action<string>? Log { get; init; }

    public LowLevelHotkeyHook(HotkeyGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _proc = HookCallback;
        _dispatcher = Dispatcher.CurrentDispatcher; // the install thread's pump
        _hookHandle = Install();
        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"SetWindowsHookEx(WH_KEYBOARD_LL) failed (Win32 error {Marshal.GetLastWin32Error()}).");

        // AI-NOTE: a failed foreground hook (zero handle) is not fatal - each key-down re-checks the foreground
        // window, so the gate still blocks keys and closes the chat mirror, just at the next key.
        _foregroundProc = (_, _, _, _, _, _, _) =>
        {
            // AI-NOTE: a key-up can be lost (elevated foreground window, secure desktop, hook timeout) and the
            // keyboard state APIs are banned here, so a window switch is where a stale held key gets dropped.
            lock (_lock) { _heldModifiers.Clear(); _heldKeys.Clear(); }
            _gate.OnForegroundChanged(IsLeagueGameForeground());
        };
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        // A user lock / UAC secure-desktop transition can eat a modifier's key-up (leaving it stuck
        // "held") and can drop the hook; resync + reinstall on the way back is the concrete fix for
        // both, cleaner than any keyboard-idle heuristic.
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _watchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _watchdog.Tick += (_, _) => WatchdogTick();
        _watchdog.Start();
    }

    private IntPtr Install()
        // LL hooks are global and do not live in a DLL; the module handle of the current process
        // is the accepted value for hMod (dwThreadId 0 = all threads on this desktop).
        => SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

    public void Register(int osId, HotkeyCombo combo)
    {
        lock (_lock)
        {
            _comboToOsId[combo] = osId;
            _osIdToCombo[osId] = combo;
        }
    }

    public void Unregister(int osId)
    {
        lock (_lock)
        {
            if (_osIdToCombo.Remove(osId, out var combo))
                _comboToOsId.Remove(combo);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _watchdog?.Stop();
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
    }

    // ── watchdog + reinstall (defends the hook against a silent timeout removal) ──────────────

    private void WatchdogTick()
    {
        if (_disposed) return;
        // The callback timestamps itself on every key event; in game the ability keys keep it fresh,
        // so a long silence means either genuine idle or a removed hook. Reinstalling covers the
        // second and is harmless in the first (it also flushes any stuck modifier).
        if (Environment.TickCount64 - _lastCallbackTick < SilenceReinstallMs) return;
        Log?.Invoke("hotkeys: keyboard hook silent — reinstalling as a precaution");
        Reinstall();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // Unlock / remote-connect: a modifier's key-up may have happened on the secure desktop and
        // never reached us, and the hook itself can be lost. Marshal to the install thread.
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon
                     or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _dispatcher.BeginInvoke(new Action(Reinstall));
    }

    /// <summary>Clears stuck modifier state and reinstalls the hook. Idempotent; must run on the
    /// install thread (the DispatcherTimer and the marshalled SessionSwitch handler both do).</summary>
    public void Reinstall()
    {
        if (_disposed) return;
        lock (_lock) { _heldModifiers.Clear(); _heldKeys.Clear(); }
        // AI-NOTE: their key-ups may have been lost with the hook; a stuck latch would ignore the next press.
        _gate.OnKeyUp("TAB");
        _gate.OnKeyUp("ENTER");

        IntPtr old = _hookHandle;
        IntPtr fresh = Install();
        if (fresh == IntPtr.Zero)
        {
            // Keep the old handle if it happens to still be live; don't tear down what we have.
            Log?.Invoke($"hotkeys: hook reinstall failed (Win32 {Marshal.GetLastWin32Error()}); keeping the existing hook");
            return;
        }
        _hookHandle = fresh;
        _lastCallbackTick = Environment.TickCount64;
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
    }

    /// <summary>True if <paramref name="modifier"/> is currently held, read from the SAME
    /// <see cref="_heldModifiers"/> state <see cref="OnKeyDown"/>/<see cref="OnKeyUp"/> already
    /// maintain (no new tracking, no <c>GetAsyncKeyState</c>/<c>GetKeyboardState</c> — see class doc
    /// "POLICY RECONCILIATION"). M02 loop 38 continuation 12 (combo-overlay click-to-target) polls
    /// this to decide when to temporarily clear the overlay window's WS_EX_TRANSPARENT style.
    /// <paramref name="modifier"/> is expected to be a single flag (e.g. <see cref="HotkeyModifiers.Control"/>);
    /// <see cref="HotkeyModifiers.None"/> always returns false.</summary>
    public bool IsModifierHeld(HotkeyModifiers modifier)
    {
        if (modifier == HotkeyModifiers.None) return false;
        lock (_lock)
            return (CurrentModifiers() & modifier) == modifier;
    }

    /// <summary>True when the foreground window is the League in-game client (class or title as
    /// MainWindow finds it); false on any failure. Reads into <see cref="_nameBuffer"/>, no allocation.</summary>
    private bool IsLeagueGameForeground()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            int n = GetClassName(hwnd, _nameBuffer, _nameBuffer.Length);
            if (n > 0 && _nameBuffer.AsSpan(0, n).SequenceEqual(LeagueWindowClass)) return true;
            n = GetWindowText(hwnd, _nameBuffer, _nameBuffer.Length);
            return n > 0 && _nameBuffer.AsSpan(0, n).SequenceEqual(LeagueWindowTitle);
        }
        catch
        {
            return false;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        _lastCallbackTick = Environment.TickCount64; // watchdog heartbeat (see WatchdogTick)
        // nCode < 0 means "just pass it on, don't process" per the hook contract.
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            uint vkCode = (uint)Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field.

            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                OnKeyDown(vkCode);
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                OnKeyUp(vkCode);
        }

        // NEVER swallow the key — always let it reach the game so gameplay is unaffected.
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void OnKeyDown(uint vkCode)
    {
        if (IsModifier(vkCode))
        {
            lock (_lock) _heldModifiers.Add(vkCode);
            return; // a modifier alone never completes a chord.
        }

        var key = ToKeyToken(vkCode);
        if (key is null) return; // key we can't express as a combo token → cannot match anything.

        bool leagueInFront = IsLeagueGameForeground();
        int osId = 0;
        lock (_lock)
        {
            var mods = CurrentModifiers();
            bool allow = _gate.OnKeyDown(key, mods, leagueInFront);
            // (M46) the held keys together first (Q+W), then smaller chords with this key, then the key alone - a key
            // held for something else never blocks a chord or a plain binding
            bool found = false;
            if (allow)
                foreach (var pressed in HotkeyCombo.Candidates(mods, _heldKeys, key))
                    if (_comboToOsId.TryGetValue(pressed, out osId)) { found = true; break; }
            if (!_heldKeys.Contains(key)) _heldKeys.Add(key);
            if (!found) return;
        }

        // (loop 518) Queue the fire instead of running the (potentially slow) handler on the hook
        // stack — see class doc "THREADING". BeginInvoke returns immediately; the handler runs on
        // this same UI thread once the callback has returned to CallNextHookEx.
        int id = osId;
        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => HotkeyPressed?.Invoke(id)));
    }

    private void OnKeyUp(uint vkCode)
    {
        if (IsModifier(vkCode))
            lock (_lock) _heldModifiers.Remove(vkCode);
        else if (ToKeyToken(vkCode) is { } token)
        {
            lock (_lock) _heldKeys.Remove(token);
            _gate.OnKeyUp(token);
        }
    }

    private static bool IsModifier(uint vk) => vk switch
    {
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT
            or VK_CONTROL or VK_LCONTROL or VK_RCONTROL
            or VK_MENU or VK_LMENU or VK_RMENU
            or VK_LWIN or VK_RWIN => true,
        _ => false,
    };

    /// <summary>Builds the current modifier bitset from the held-modifier set (modifiers only).</summary>
    private HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        foreach (var vk in _heldModifiers)
        {
            switch (vk)
            {
                case VK_SHIFT or VK_LSHIFT or VK_RSHIFT: m |= HotkeyModifiers.Shift; break;
                case VK_CONTROL or VK_LCONTROL or VK_RCONTROL: m |= HotkeyModifiers.Control; break;
                case VK_MENU or VK_LMENU or VK_RMENU: m |= HotkeyModifiers.Alt; break;
                case VK_LWIN or VK_RWIN: m |= HotkeyModifiers.Win; break;
            }
        }
        return m;
    }

    /// <summary>Named (non-alphanumeric) virtual keys mapped to the same upper-cased token
    /// <see cref="HotkeyCombo"/> stores after parsing (aliases resolved: ESC→ESCAPE, DEL→DELETE,
    /// INS→INSERT, PGUP→PAGEUP, PGDN→PAGEDOWN), so a built chord compares equal to what was
    /// registered. This is the reverse of <see cref="Win32HotkeyHook.ToVirtualKey"/>.</summary>
    private static readonly Dictionary<uint, string> NamedKeys = new()
    {
        [0x09] = "TAB", [0x20] = "SPACE", [0x0D] = "ENTER", [0x1B] = "ESCAPE", [0x08] = "BACKSPACE",
        [0x25] = "LEFT", [0x26] = "UP", [0x27] = "RIGHT", [0x28] = "DOWN",
        [0x2D] = "INSERT", [0x2E] = "DELETE", [0x24] = "HOME", [0x23] = "END",
        [0x21] = "PAGEUP", [0x22] = "PAGEDOWN",
        [0xC0] = "`", // (M46) VK_OEM_3, the backtick / tilde key (ALT+` = the skill overlay)
    };

    /// <summary>Maps a virtual-key code to the normalized key token a registered
    /// <see cref="HotkeyCombo"/> uses (digits, letters, F-keys, and the named keys above).
    /// Returns null for any key that cannot be a combo key, so it simply never matches.</summary>
    private static string? ToKeyToken(uint vk)
    {
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();          // VK_0..VK_9 == '0'..'9'
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();          // VK_A..VK_Z == 'A'..'Z'
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);        // VK_F1..VK_F24
        return NamedKeys.TryGetValue(vk, out var token) ? token : null;
    }
}
