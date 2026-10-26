namespace Overlay.Core.Hotkeys;

/// <summary>(lolf01-t7) The capture rule every hotkey button shares (Settings and the combo editor): any
/// modifiers plus up to <see cref="HotkeyCombo.MaxKeys"/> non-modifier keys pressed together. The chord is taken
/// when one of its keys is released or the last allowed key goes down; modifiers never count toward the limit,
/// so CTRL+ALT+Q+W can be captured. The view maps its key events to tokens; this class only collects them.</summary>
public sealed class ChordCapture
{
    private readonly List<string> _keys = new(HotkeyCombo.MaxKeys);
    private HotkeyModifiers _mods;

    /// <summary>True while at least one non-modifier key of an unfinished chord is held.</summary>
    public bool HasPendingKeys => _keys.Count > 0;

    public void Reset()
    {
        _keys.Clear();
        _mods = HotkeyModifiers.None;
    }

    /// <summary>A non-modifier key went down with <paramref name="mods"/> held. Returns the chord once
    /// <see cref="HotkeyCombo.MaxKeys"/> keys are pending, otherwise null (the chord waits for a key-up).</summary>
    public string? OnKeyDown(HotkeyModifiers mods, string token)
    {
        // AI-NOTE: the latest modifiers win, so CTRL pressed after Q still lands in the chord; a key-down
        // auto-repeats while held, hence the add-once.
        _mods = mods;
        if (!_keys.Contains(token)) _keys.Add(token);
        return _keys.Count >= HotkeyCombo.MaxKeys ? Take() : null;
    }

    /// <summary>A non-modifier key went up. Releasing one of the pending keys finishes the chord ("Q+W",
    /// "CTRL+ALT+Q+W"); any other key-up returns null.</summary>
    public string? OnKeyUp(string token) => _keys.Contains(token) ? Take() : null;

    private string Take()
    {
        var parts = new List<string>(4 + _keys.Count);
        if (_mods.HasFlag(HotkeyModifiers.Control)) parts.Add("CTRL");
        if (_mods.HasFlag(HotkeyModifiers.Alt)) parts.Add("ALT");
        if (_mods.HasFlag(HotkeyModifiers.Shift)) parts.Add("SHIFT");
        if (_mods.HasFlag(HotkeyModifiers.Win)) parts.Add("WIN");
        parts.AddRange(_keys);
        Reset();
        return string.Join("+", parts);
    }
}
