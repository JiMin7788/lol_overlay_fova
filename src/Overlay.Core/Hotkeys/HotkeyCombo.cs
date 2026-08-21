namespace Overlay.Core.Hotkeys;

/// <summary>
/// The modifier keys a hotkey can carry. <see cref="Flags"/> so a combo's modifiers are a
/// single bitset (Alt+Ctrl+Shift+Win). Values are M13-internal — they are NOT the Win32
/// <c>MOD_*</c> constants (that mapping lives in the Win32 layer, keeping this Core model
/// free of any OS dependency).
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A parsed, normalized hotkey combination: a set of <see cref="HotkeyModifiers"/> plus a
/// single non-modifier <see cref="Key"/> (e.g. "1", "F4", "A"). This is the pure, Win32-free
/// value the M13 Core reasons about; the Win32 layer maps it to <c>RegisterHotKey</c> args.
///
/// <para><b>Normalization is the point.</b> "alt+1", "Alt + 1" and "1+ALT" all
/// <see cref="Parse"/> to the same combo, and <see cref="Canonical"/> renders one stable
/// string ("ALT+1"). Because equality is by value over the (already-uppercased,
/// order-independent) modifiers+key, conflict detection is inherently case- and
/// order-insensitive.</para>
///
/// <para><b>Read-only.</b> This type only describes which specific combo was registered. It
/// generates no keystrokes and records no key input — it is a plain immutable descriptor.</para>
/// </summary>
public sealed record HotkeyCombo(HotkeyModifiers Modifiers, string Key)
{
    // Fixed render order so the Canonical string is deterministic regardless of input order.
    private static readonly (HotkeyModifiers Flag, string Token)[] CanonicalOrder =
    {
        (HotkeyModifiers.Control, "CTRL"),
        (HotkeyModifiers.Alt, "ALT"),
        (HotkeyModifiers.Shift, "SHIFT"),
        (HotkeyModifiers.Win, "WIN"),
    };

    private static readonly Dictionary<string, HotkeyModifiers> ModifierTokens = new(StringComparer.Ordinal)
    {
        ["ALT"] = HotkeyModifiers.Alt,
        ["CTRL"] = HotkeyModifiers.Control,
        ["CONTROL"] = HotkeyModifiers.Control,
        ["SHIFT"] = HotkeyModifiers.Shift,
        ["WIN"] = HotkeyModifiers.Win,
        ["WINDOWS"] = HotkeyModifiers.Win,
        ["META"] = HotkeyModifiers.Win,
        ["CMD"] = HotkeyModifiers.Win,
    };

    // A few common key spellings normalized to one form so reserved-combo matching is robust
    // (e.g. "ctrl+alt+del" == "CTRL+ALT+DELETE").
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.Ordinal)
    {
        ["ESC"] = "ESCAPE",
        ["DEL"] = "DELETE",
        ["INS"] = "INSERT",
        ["PGUP"] = "PAGEUP",
        ["PGDN"] = "PAGEDOWN",
    };

    /// <summary>The one stable, comparable string form (e.g. "CTRL+ALT+1"). Modifiers always
    /// render in <see cref="CanonicalOrder"/>, then the key.</summary>
    public string Canonical => Build();

    /// <summary>Parses a hotkey string. Throws <see cref="FormatException"/> if it is empty,
    /// has an empty component, names more than one non-modifier key, or names no key.</summary>
    public static HotkeyCombo Parse(string hotkey)
    {
        if (!TryParseInternal(hotkey, out var combo, out var error))
            throw new FormatException(error);
        return combo!;
    }

    /// <summary>Non-throwing <see cref="Parse"/>. Returns false (and a null combo) on any
    /// malformed input.</summary>
    public static bool TryParse(string? hotkey, out HotkeyCombo? combo)
        => TryParseInternal(hotkey, out combo, out _);

    private static bool TryParseInternal(string? hotkey, out HotkeyCombo? combo, out string? error)
    {
        combo = null;
        error = null;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            error = "Hotkey string must not be empty.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;

        foreach (var raw in hotkey.Split('+'))
        {
            var token = raw.Trim().ToUpperInvariant();
            if (token.Length == 0)
            {
                error = $"Hotkey '{hotkey}' has an empty component.";
                return false;
            }

            if (ModifierTokens.TryGetValue(token, out var mod))
            {
                modifiers |= mod;
                continue;
            }

            if (KeyAliases.TryGetValue(token, out var alias))
                token = alias;

            if (key is not null)
            {
                error = $"Hotkey '{hotkey}' names more than one non-modifier key ('{key}' and '{token}').";
                return false;
            }
            key = token;
        }

        if (key is null)
        {
            error = $"Hotkey '{hotkey}' has no non-modifier key.";
            return false;
        }

        combo = new HotkeyCombo(modifiers, key);
        return true;
    }

    private string Build()
    {
        var parts = new List<string>(5);
        foreach (var (flag, token) in CanonicalOrder)
        {
            if (Modifiers.HasFlag(flag)) parts.Add(token);
        }
        parts.Add(Key);
        return string.Join("+", parts);
    }

    public override string ToString() => Canonical;
}
