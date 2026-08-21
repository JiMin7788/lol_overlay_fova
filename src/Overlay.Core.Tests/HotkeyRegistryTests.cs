using Overlay.Core.Hotkeys;
using Xunit;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M13 Hotkey Manager (docs/modules/M13_HOTKEY_MANAGER.md):
///  - hotkey-string normalization (case/order/whitespace variants → one canonical form).
///  - Register returns an id and tracks the binding via a fake IHotkeyHook (no real OS reg).
///  - duplicate registration → conflict (IsConflicting true + HotkeyConflictException).
///  - OS-reserved combo (Alt+F4, Ctrl+Alt+Del, …) → ReservedHotkeyException.
///  - Unregister removes the binding and frees the combo for re-registration.
///  - Fire(id) publishes UI.HOTKEY_TRIGGERED { hotkeyId } on the real M15 bus AND invokes the
///    direct callback; FireByOsId routes the OS press back through the same path.
///
/// EventBus (M15) is static/process-wide, so each test resets it first (same isolation pattern
/// as the other bus-using suites; AssemblyInfo disables cross-class parallelization).
/// </summary>
public class HotkeyRegistryTests
{
    public HotkeyRegistryTests() => EventBus.EventBus.ResetForTests();

    /// <summary>Records what the Core asked the OS seam to do — no real RegisterHotKey.</summary>
    private sealed class FakeHotkeyHook : IHotkeyHook
    {
        public readonly List<(int OsId, HotkeyCombo Combo)> Registered = new();
        public readonly List<int> Unregistered = new();

        public void Register(int osId, HotkeyCombo combo) => Registered.Add((osId, combo));
        public void Unregister(int osId) => Unregistered.Add(osId);
    }

    // ── Normalization ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("alt+1")]
    [InlineData("ALT+1")]
    [InlineData("Alt + 1")]
    [InlineData("1+ALT")]
    [InlineData("  1  +  alt  ")]
    public void Parse_NormalizesCaseOrderWhitespace_ToOneCanonicalForm(string input)
    {
        Assert.Equal("ALT+1", HotkeyCombo.Parse(input).Canonical);
    }

    [Fact]
    public void Parse_OrdersMultipleModifiersCanonically()
    {
        Assert.Equal("CTRL+ALT+SHIFT+A", HotkeyCombo.Parse("shift+a+ctrl+alt").Canonical);
    }

    [Fact]
    public void Parse_VariantsAreValueEqual()
    {
        Assert.Equal(HotkeyCombo.Parse("ctrl+alt+1"), HotkeyCombo.Parse("1 + Alt + Control"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ALT")]          // no non-modifier key
    [InlineData("ALT+")]         // empty component
    [InlineData("ALT+1+2")]      // two non-modifier keys
    public void Parse_MalformedInput_Throws(string input)
    {
        Assert.Throws<FormatException>(() => HotkeyCombo.Parse(input));
    }

    // ── Register / tracking ──────────────────────────────────────────────────────────────

    [Fact]
    public void Register_ReturnsId_AndTracksBinding_ViaHook()
    {
        var hook = new FakeHotkeyHook();
        var registry = new HotkeyRegistry(hook);

        var id = registry.Register("alt+1", () => { }, "M04");

        Assert.False(string.IsNullOrEmpty(id));
        var binding = Assert.Single(registry.Bindings);
        Assert.Equal(id, binding.Id);
        Assert.Equal("ALT+1", binding.KeyCombo);
        Assert.Equal("M04", binding.RegisteredBy);
        // The OS seam was asked to register the normalized combo exactly once.
        var reg = Assert.Single(hook.Registered);
        Assert.Equal("ALT+1", reg.Combo.Canonical);
    }

    [Fact]
    public void Register_NullCallback_Throws()
    {
        var registry = new HotkeyRegistry(new FakeHotkeyHook());
        Assert.Throws<ArgumentNullException>(() => registry.Register("alt+1", null!, "M04"));
    }

    // ── Duplicate → conflict ─────────────────────────────────────────────────────────────

    [Fact]
    public void Register_Duplicate_IsConflicting_AndThrows()
    {
        var hook = new FakeHotkeyHook();
        var registry = new HotkeyRegistry(hook);

        Assert.False(registry.IsConflicting("alt+1"));
        registry.Register("alt+1", () => { }, "M04");

        // Conflict detection is order/case-insensitive (normalized form).
        Assert.True(registry.IsConflicting("1 + ALT"));
        Assert.Throws<HotkeyConflictException>(() => registry.Register("1+alt", () => { }, "M02"));

        // The rejected duplicate did not reach the OS seam.
        Assert.Single(hook.Registered);
    }

    // ── Reserved → rejected ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("alt+f4")]
    [InlineData("ctrl+alt+del")]   // DEL alias → DELETE, matches CTRL+ALT+DELETE
    [InlineData("ctrl+esc")]       // ESC alias → ESCAPE
    [InlineData("win+l")]
    [InlineData("alt+tab")]
    public void Register_Reserved_ThrowsReserved(string reserved)
    {
        var hook = new FakeHotkeyHook();
        var registry = new HotkeyRegistry(hook);

        Assert.True(registry.IsConflicting(reserved));
        Assert.Throws<ReservedHotkeyException>(() => registry.Register(reserved, () => { }, "M04"));
        Assert.Empty(hook.Registered); // never reached the OS
    }

    // ── Unregister frees the combo ───────────────────────────────────────────────────────

    [Fact]
    public void Unregister_RemovesBinding_AndFreesComboForReRegistration()
    {
        var hook = new FakeHotkeyHook();
        var registry = new HotkeyRegistry(hook);

        var id = registry.Register("alt+1", () => { }, "M04");
        registry.Unregister(id);

        Assert.Empty(registry.Bindings);
        Assert.False(registry.IsConflicting("alt+1"));
        Assert.Single(hook.Unregistered);

        // Combo is free again.
        var id2 = registry.Register("alt+1", () => { }, "M04");
        Assert.NotEqual(id, id2);
    }

    [Fact]
    public void Unregister_UnknownId_IsNoOp()
    {
        var registry = new HotkeyRegistry(new FakeHotkeyHook());
        registry.Unregister("nope");           // does not throw
        registry.Unregister(string.Empty);     // does not throw
        Assert.Empty(registry.Bindings);
    }

    // ── Trigger dispatch ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Fire_PublishesUiEventWithHotkeyId_AndInvokesCallback()
    {
        var hook = new FakeHotkeyHook();
        var registry = new HotkeyRegistry(hook);

        bool callbackInvoked = false;
        var id = registry.Register("alt+1", () => callbackInvoked = true, "M04");

        HotkeyTriggeredPayload? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe(HotkeyRegistry.EventType, evt =>
        {
            received = evt.Payload as HotkeyTriggeredPayload;
            gate.Set();
        });

        registry.Fire(id);

        Assert.True(callbackInvoked, "direct callback was not invoked");
        // UI.* is dispatched async on the bus worker thread — wait for delivery.
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.HOTKEY_TRIGGERED was not delivered");
        Assert.NotNull(received);
        Assert.Equal(id, received!.HotkeyId);
    }

    [Fact]
    public void FireByOsId_RoutesOsPress_ToTheBinding()
    {
        var hook = new FakeHotkeyHook();
        var registry = new HotkeyRegistry(hook);

        bool invoked = false;
        registry.Register("alt+2", () => invoked = true, "M04");
        int osId = hook.Registered[0].OsId; // the id the Core handed the OS seam

        Assert.True(registry.FireByOsId(osId));
        Assert.True(invoked);
        Assert.False(registry.FireByOsId(9999)); // unknown OS id
    }

    [Fact]
    public void Fire_UnknownId_IsNoOp()
    {
        var registry = new HotkeyRegistry(new FakeHotkeyHook());
        registry.Fire("nope"); // no throw, no callback, nothing to assert beyond not throwing
    }
}
