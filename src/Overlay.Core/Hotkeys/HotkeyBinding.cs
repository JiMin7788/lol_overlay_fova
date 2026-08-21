namespace Overlay.Core.Hotkeys;

/// <summary>
/// M13 Data Model: a single registered hotkey. <see cref="Id"/> is the opaque registration
/// handle returned by <see cref="HotkeyRegistry.Register"/> (also the <c>hotkeyId</c> carried
/// on the fired event); <see cref="KeyCombo"/> is the normalized combo string
/// (<see cref="HotkeyCombo.Canonical"/>); <see cref="RegisteredBy"/> names the calling module
/// (e.g. "M04") for traceability.
/// </summary>
public sealed record HotkeyBinding(string Id, string KeyCombo, string RegisteredBy);

/// <summary>Payload published on <c>UI.HOTKEY_TRIGGERED</c> when a registered hotkey fires.
/// <see cref="HotkeyId"/> is the binding's registration id; a consumer (M04/M02) maps it to a
/// combo and runs it — M13 itself never resolves or executes combos.</summary>
public sealed record HotkeyTriggeredPayload(string HotkeyId);
