using Overlay.Core.Hotkeys;

namespace Overlay.Core.Tests;

/// <summary>(lolf01-t7) Hotkey capture: any modifiers plus up to <see cref="HotkeyCombo.MaxKeys"/> keys.</summary>
public class ChordCaptureTests
{
    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    [Fact]
    public void ModifiersDoNotCountTowardTheKeyLimit_CtrlAltQW_FinishesOnKeyUp()
    {
        var capture = new ChordCapture();

        Assert.Null(capture.OnKeyDown(CtrlAlt, "Q"));
        Assert.Null(capture.OnKeyDown(CtrlAlt, "W"));
        var chord = capture.OnKeyUp("W");

        Assert.Equal("CTRL+ALT+Q+W", chord);
        Assert.Equal("CTRL+ALT+Q+W", HotkeyCombo.Parse(chord!).Canonical);
    }

    [Fact]
    public void ThirdKeyDown_FinishesThePlainChord()
    {
        var capture = new ChordCapture();

        Assert.Null(capture.OnKeyDown(HotkeyModifiers.None, "Q"));
        Assert.Null(capture.OnKeyDown(HotkeyModifiers.None, "W"));
        Assert.Equal("Q+W+E", capture.OnKeyDown(HotkeyModifiers.None, "E"));
    }

    [Fact]
    public void AltQW_FinishesOnKeyUp()
    {
        var capture = new ChordCapture();

        Assert.Null(capture.OnKeyDown(HotkeyModifiers.Alt, "Q"));
        Assert.Null(capture.OnKeyDown(HotkeyModifiers.Alt, "W"));
        Assert.Equal("ALT+Q+W", capture.OnKeyUp("Q"));
    }

    [Fact]
    public void KeyUpOfAKeyNotPending_ReturnsNull()
    {
        var capture = new ChordCapture();

        Assert.Null(capture.OnKeyUp("Q"));
        Assert.Null(capture.OnKeyDown(HotkeyModifiers.None, "Q"));
        Assert.Null(capture.OnKeyUp("W"));
        Assert.True(capture.HasPendingKeys);
    }

    [Fact]
    public void RepeatedKeyDown_AddsTheKeyOnce()
    {
        var capture = new ChordCapture();

        Assert.Null(capture.OnKeyDown(HotkeyModifiers.None, "Q"));
        Assert.Null(capture.OnKeyDown(HotkeyModifiers.None, "Q"));
        Assert.Null(capture.OnKeyDown(HotkeyModifiers.None, "W"));
        Assert.Null(capture.OnKeyDown(HotkeyModifiers.None, "Q"));
        Assert.Equal("Q+W", capture.OnKeyUp("Q"));
    }

    [Fact]
    public void AfterAChordIsReturned_NothingIsPending()
    {
        var capture = new ChordCapture();

        capture.OnKeyDown(HotkeyModifiers.Shift, "Q");
        Assert.True(capture.HasPendingKeys);
        Assert.Equal("SHIFT+Q", capture.OnKeyUp("Q"));
        Assert.False(capture.HasPendingKeys);

        capture.OnKeyDown(HotkeyModifiers.None, "Q");
        capture.OnKeyDown(HotkeyModifiers.None, "W");
        Assert.Equal("Q+W+E", capture.OnKeyDown(HotkeyModifiers.None, "E"));
        Assert.False(capture.HasPendingKeys);
        Assert.Null(capture.OnKeyUp("E"));
    }

    [Fact]
    public void Reset_DropsPendingKeysAndModifiers()
    {
        var capture = new ChordCapture();

        capture.OnKeyDown(CtrlAlt, "Q");
        capture.Reset();

        Assert.False(capture.HasPendingKeys);
        Assert.Null(capture.OnKeyUp("Q"));
    }
}
