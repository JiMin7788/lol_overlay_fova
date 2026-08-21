namespace Overlay.Core.Hotkeys;

/// <summary>
/// The seam between the testable, Win32-free <see cref="HotkeyRegistry"/> (Core) and the OS.
/// The production implementation (<c>Overlay.Client.Hotkeys.Win32HotkeyHook</c>) asks Windows
/// to notify the app ONLY when a specific registered combo is pressed, via
/// <c>RegisterHotKey</c>/<c>WM_HOTKEY</c> — NOT a low-level keyboard hook. The OS does the
/// filtering; the app never sees any other keystroke (this is what structurally excludes any
/// keylogger behaviour).
///
/// <para>Keeping this interface tiny lets the registry be unit-tested against a fake hook with
/// no real OS registration at all. The registry assigns each combo an integer
/// <paramref name="osId"/> (the id it also passes to <c>RegisterHotKey</c>); when the OS later
/// reports that id via <c>WM_HOTKEY</c>, the Win32 layer routes it back through
/// <see cref="HotkeyRegistry.FireByOsId"/>.</para>
/// </summary>
public interface IHotkeyHook
{
    /// <summary>Ask the OS to notify us when <paramref name="combo"/> is pressed, tagged with
    /// <paramref name="osId"/>. Implementations may throw if the OS refuses the registration
    /// (e.g. the combo is already claimed by another application).</summary>
    void Register(int osId, HotkeyCombo combo);

    /// <summary>Release the OS registration previously made under <paramref name="osId"/>.</summary>
    void Unregister(int osId);
}
