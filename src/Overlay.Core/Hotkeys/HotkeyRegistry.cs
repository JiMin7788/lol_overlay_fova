namespace Overlay.Core.Hotkeys;

/// <summary>
/// M13 Hotkey Manager (docs/modules/M13_HOTKEY_MANAGER.md): the testable core that tracks the
/// set of registered global hotkeys, rejects conflicting/OS-reserved combos, and — when a
/// registered combo fires — publishes <c>UI.HOTKEY_TRIGGERED { hotkeyId }</c> on the M15 bus
/// and invokes the direct callback.
///
/// ─── READ-ONLY LISTENER (P3/P4) ──────────────────────────────────────────────────────────
/// This module ONLY detects the specific combos a caller registered. It generates and sends
/// NO input (no SendInput/keybd_event/SendKeys/etc.) and captures NO arbitrary/all keystrokes
/// (no low-level keyboard hook, no keylog buffer). The OS-facing detection is delegated to the
/// <see cref="IHotkeyHook"/> seam, whose production impl uses <c>RegisterHotKey</c> — the OS
/// filters and only ever reports the registered combo. Nothing here reads other keys.
///
/// ─── OWNERSHIP BOUNDARY ──────────────────────────────────────────────────────────────────
/// M13 does not own the hotkey↔combo mapping (M04 persists that) and does not resolve or
/// execute combos. It emits <c>hotkeyId</c>; a consumer maps it and runs the combo.
///
/// Thread-safe: the Win32 message pump (or any caller) may <see cref="Fire"/> concurrently with
/// Register/Unregister; internal maps are lock-guarded and callbacks run outside the lock.
/// </summary>
public sealed class HotkeyRegistry
{
    /// <summary>The event type published when a registered hotkey fires (UI.* ⇒ dispatched
    /// async by M15, off the caller's thread).</summary>
    public const string EventType = "UI.HOTKEY_TRIGGERED";

    /// <summary>OS-reserved / shell combos that must never be registered as app hotkeys. Stored
    /// as canonical strings (<see cref="HotkeyCombo.Canonical"/>) so matching is case/order
    /// insensitive. A small, explicit set — not exhaustive, but covers the combos the spec
    /// names plus the common shell shortcuts.</summary>
    public static readonly IReadOnlySet<string> ReservedCombos = new HashSet<string>(StringComparer.Ordinal)
    {
        "ALT+F4",            // close window
        "ALT+TAB",           // task switch
        "ALT+ESCAPE",        // cycle windows
        "CTRL+ESCAPE",       // Start menu
        "CTRL+ALT+DELETE",   // secure attention sequence
        "CTRL+SHIFT+ESCAPE", // Task Manager
        "WIN+L",             // lock workstation
        "WIN+D",             // show desktop
        "WIN+R",             // Run dialog
    };

    private readonly IHotkeyHook _hook;
    private readonly string _source;

    private readonly object _lock = new();
    private readonly Dictionary<string, Registration> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonicalToId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _osIdToId = new();
    private int _nextOsId = 1;

    public HotkeyRegistry(IHotkeyHook hook, string source = "M13.HotkeyRegistry")
    {
        _hook = hook ?? throw new ArgumentNullException(nameof(hook));
        _source = source;
    }

    // ── Spec Interfaces ──────────────────────────────────────────────────────────────────

    /// <summary>Registers <paramref name="hotkey"/> and returns an opaque registration id.
    /// Rejects an OS-reserved combo with <see cref="ReservedHotkeyException"/> and a
    /// duplicate (already-registered) combo with <see cref="HotkeyConflictException"/>. A
    /// malformed hotkey string throws <see cref="FormatException"/> (via
    /// <see cref="HotkeyCombo.Parse"/>).</summary>
    public string Register(string hotkey, Action callback, string registeredBy)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var combo = HotkeyCombo.Parse(hotkey);
        var canonical = combo.Canonical;

        if (ReservedCombos.Contains(canonical))
            throw new ReservedHotkeyException(
                $"Hotkey '{canonical}' is reserved by the OS and cannot be registered.");

        lock (_lock)
        {
            if (_canonicalToId.ContainsKey(canonical))
                throw new HotkeyConflictException(
                    $"Hotkey '{canonical}' is already registered (duplicate registration).");

            var registrationId = Guid.NewGuid().ToString();
            int osId = _nextOsId++;

            // Ask the OS (via the seam) first; if it refuses, nothing is tracked.
            _hook.Register(osId, combo);

            var binding = new HotkeyBinding(registrationId, canonical, registeredBy ?? string.Empty);
            _byId[registrationId] = new Registration(binding, callback, osId);
            _canonicalToId[canonical] = registrationId;
            _osIdToId[osId] = registrationId;
            return registrationId;
        }
    }

    /// <summary>Removes the registration, freeing its combo for re-registration. No-op if the
    /// id is unknown/already removed.</summary>
    public void Unregister(string registrationId)
    {
        if (string.IsNullOrEmpty(registrationId)) return;

        Registration reg;
        lock (_lock)
        {
            if (!_byId.TryGetValue(registrationId, out var existing)) return;
            reg = existing;
            _byId.Remove(registrationId);
            _canonicalToId.Remove(reg.Binding.KeyCombo);
            _osIdToId.Remove(reg.OsId);
        }
        _hook.Unregister(reg.OsId);
    }

    /// <summary>True if <paramref name="hotkey"/> cannot be registered because it is either
    /// OS-reserved or already registered. Throws <see cref="FormatException"/> on a malformed
    /// string.</summary>
    public bool IsConflicting(string hotkey)
    {
        var canonical = HotkeyCombo.Parse(hotkey).Canonical;
        if (ReservedCombos.Contains(canonical)) return true;
        lock (_lock)
        {
            return _canonicalToId.ContainsKey(canonical);
        }
    }

    // ── Trigger dispatch (spec Internal Logic #2) ────────────────────────────────────────

    /// <summary>Fires the binding with the given registration id: publishes
    /// <c>UI.HOTKEY_TRIGGERED { hotkeyId }</c> on the M15 bus AND invokes the direct callback.
    /// No-op if the id is unknown. This is the seam a real key press ultimately reaches, so the
    /// dispatch is fully testable without any OS key press.</summary>
    public void Fire(string registrationId)
    {
        Registration reg;
        lock (_lock)
        {
            if (!_byId.TryGetValue(registrationId, out var existing)) return;
            reg = existing;
        }

        // Publish (UI.* ⇒ async on the bus worker) and invoke the direct callback. Both paths
        // per spec; callback runs outside the lock so it may safely (Un)register.
        EventBus.EventBus.Publish(EventType, new HotkeyTriggeredPayload(reg.Binding.Id), _source);
        reg.Callback();
    }

    /// <summary>The entry point the Win32 layer calls from <c>WM_HOTKEY</c>: resolves the OS
    /// id back to a registration and <see cref="Fire"/>s it. Returns false if the OS id is not
    /// (or no longer) ours.</summary>
    public bool FireByOsId(int osId)
    {
        string id;
        lock (_lock)
        {
            if (!_osIdToId.TryGetValue(osId, out var found)) return false;
            id = found;
        }
        Fire(id);
        return true;
    }

    /// <summary>Snapshot of the currently registered bindings (for inspection/tests).</summary>
    public IReadOnlyCollection<HotkeyBinding> Bindings
    {
        get
        {
            lock (_lock)
            {
                return _byId.Values.Select(r => r.Binding).ToArray();
            }
        }
    }

    private sealed record Registration(HotkeyBinding Binding, Action Callback, int OsId);
}

/// <summary>Thrown by <see cref="HotkeyRegistry.Register"/> when the requested combo is already
/// registered (spec Acceptance: duplicate registration → conflict warning).</summary>
public sealed class HotkeyConflictException : Exception
{
    public HotkeyConflictException(string message) : base(message) { }
}

/// <summary>Thrown by <see cref="HotkeyRegistry.Register"/> when the requested combo is an
/// OS-reserved shortcut (spec Acceptance: reserved combo → rejected with a clear error).</summary>
public sealed class ReservedHotkeyException : Exception
{
    public ReservedHotkeyException(string message) : base(message) { }
}
