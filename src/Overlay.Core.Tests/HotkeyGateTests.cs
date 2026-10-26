using Overlay.Core.Hotkeys;

namespace Overlay.Core.Tests;

/// <summary>(lolf01-t5) In a game, keys fire only over the League window with its chat closed; no game, anywhere.</summary>
public class HotkeyGateTests
{
    private const HotkeyModifiers None = HotkeyModifiers.None;

    private static HotkeyGate InGame() => new(() => true);

    /// <summary>A key press: down then up.</summary>
    private static bool Press(HotkeyGate gate, string token, HotkeyModifiers mods = None, bool leagueInFront = true)
    {
        bool allow = gate.OnKeyDown(token, mods, leagueInFront);
        gate.OnKeyUp(token);
        return allow;
    }

    [Fact]
    public void NoGame_EveryKeyFires_AnyWindow_ChatIgnored_TabStillFlips()
    {
        var gate = new HotkeyGate(() => false);

        Assert.True(Press(gate, "P", leagueInFront: false));
        Assert.True(Press(gate, "P", leagueInFront: true));
        Assert.True(Press(gate, "ENTER"));
        Assert.False(gate.ChatOpen);
        Assert.True(Press(gate, "P"));
        Assert.True(Press(gate, "ESCAPE"));
        Assert.False(gate.ChatOpen);

        Assert.True(Press(gate, "TAB", leagueInFront: false));
        Assert.True(gate.ScoreboardOpen);
        Assert.True(Press(gate, "TAB"));
        Assert.False(gate.ScoreboardOpen);
    }

    [Fact]
    public void InGame_KeyFiresOnlyOverLeague()
    {
        var gate = InGame();

        Assert.False(Press(gate, "P", leagueInFront: false));
        Assert.True(Press(gate, "P", leagueInFront: true));
        Assert.False(gate.ChatOpen);
    }

    [Theory]
    [InlineData("ENTER", HotkeyModifiers.Shift)]
    [InlineData("ENTER", HotkeyModifiers.None)]
    [InlineData("ESCAPE", HotkeyModifiers.None)]
    public void InGame_EnterOpensChat_KeysBlocked_UntilClosed(string closeKey, HotkeyModifiers closeMods)
    {
        var gate = InGame();

        Assert.True(Press(gate, "ENTER")); // judged before it opens the chat
        Assert.True(gate.ChatOpen);
        Assert.False(Press(gate, "P"));

        Assert.False(Press(gate, closeKey, closeMods)); // typed into the chat box
        Assert.False(gate.ChatOpen);
        Assert.True(Press(gate, "P"));
    }

    [Fact]
    public void InGame_EnterAutoRepeat_FlipsOnce()
    {
        var gate = InGame();

        gate.OnKeyDown("ENTER", None, true);
        gate.OnKeyDown("ENTER", None, true);
        Assert.True(gate.ChatOpen);
        gate.OnKeyUp("ENTER");

        Press(gate, "ENTER");
        Assert.False(gate.ChatOpen);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Alt)]
    [InlineData(HotkeyModifiers.Control)]
    public void InGame_AltOrCtrlEnter_DoesNotFlipChat(HotkeyModifiers mods)
    {
        var gate = InGame();

        Press(gate, "ENTER", mods);
        Assert.False(gate.ChatOpen);
    }

    [Fact]
    public void ChatMirror_ClosesWhenLeagueLosesForeground()
    {
        var gate = InGame();
        Press(gate, "ENTER");
        Assert.True(gate.ChatOpen);

        gate.OnForegroundChanged(false);
        Assert.False(gate.ChatOpen);
    }

    [Fact]
    public void ChatMirror_ClosesOnAKeyPressedOutsideLeague()
    {
        var gate = InGame();
        Press(gate, "ENTER");
        Assert.True(gate.ChatOpen);

        Assert.False(Press(gate, "P", leagueInFront: false));
        Assert.False(gate.ChatOpen);
    }

    [Fact]
    public void InGame_TabFlipsOnlyOverLeague_WithChatClosed()
    {
        var gate = InGame();

        Press(gate, "TAB", leagueInFront: false);
        Assert.False(gate.ScoreboardOpen);

        Press(gate, "ENTER");
        Press(gate, "TAB");
        Assert.False(gate.ScoreboardOpen);
        Press(gate, "ENTER");

        Press(gate, "TAB");
        Assert.True(gate.ScoreboardOpen);

        Press(gate, "TAB", HotkeyModifiers.Alt);
        Assert.True(gate.ScoreboardOpen);

        Press(gate, "TAB", HotkeyModifiers.Shift);
        Assert.False(gate.ScoreboardOpen);
    }

    [Fact]
    public void ResetForNewGame_ClosesEverything()
    {
        var gate = InGame();
        Press(gate, "TAB");
        Press(gate, "ENTER");
        gate.ToggleGlobalGold();
        Assert.True(gate.ScoreboardOpen && gate.ChatOpen && gate.GlobalGoldToggled);

        gate.ResetForNewGame();

        Assert.False(gate.ChatOpen);
        Assert.False(gate.ScoreboardOpen);
        Assert.False(gate.GlobalGoldToggled);
    }

    [Theory]
    [InlineData("TAB", true)]
    [InlineData(" tab ", true)]
    [InlineData("G", false)]
    public void GoldFollowsScoreboard_OnlyForTab(string chord, bool expected)
        => Assert.Equal(expected, HotkeyGate.GoldFollowsScoreboard(chord));
}
