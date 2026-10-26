namespace Overlay.Core.Hotkeys;

/// <summary>(lolF01) Decides whether a key press may fire a hotkey, and mirrors League's own toggles
/// from the hook's key events: the in-game chat (ENTER opens/closes, ESCAPE closes) and the
/// scoreboard (TAB). In a game a key fires only while the League game window is in front and the
/// chat mirror is closed; with no game every key fires, in any window. No Win32: the hook passes in
/// whether League is in front.</summary>
public sealed class HotkeyGate
{
    // Alt+Tab switches windows and Ctrl/Alt+Enter is not a chat send - neither is a League toggle.
    private const HotkeyModifiers NotPlain = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Win;

    private readonly Func<bool> _inGame;

    // Written on the hook's (UI) thread, read by the renderer and the 1s timer.
    private volatile bool _chatOpen;
    private volatile bool _scoreboardOpen;
    private volatile bool _globalGoldToggled;

    // First-down latches: holding a key auto-repeats its key-down.
    private bool _enterDown;
    private bool _tabDown;

    public HotkeyGate(Func<bool> inGame)
    {
        ArgumentNullException.ThrowIfNull(inGame);
        _inGame = inGame;
    }

    /// <summary>Mirror of League's in-game chat box (only tracked in a game, over the League window).</summary>
    public bool ChatOpen => _chatOpen;

    /// <summary>Mirror of League's scoreboard toggle (Tab).</summary>
    public bool ScoreboardOpen => _scoreboardOpen;

    /// <summary>The global gold panel's own key (when it is not TAB) toggles this.</summary>
    public bool GlobalGoldToggled => _globalGoldToggled;

    /// <summary>TAB is League's own scoreboard key: the gold panel mirrors it instead of binding it.</summary>
    public static bool GoldFollowsScoreboard(string chord) => string.Equals(chord.Trim(), "TAB", StringComparison.OrdinalIgnoreCase);

    public void ToggleGlobalGold() => _globalGoldToggled = !_globalGoldToggled;

    /// <summary>A new game starts with chat and scoreboard closed and the gold panel off - call on game start
    /// so a key pressed in some other window beforehand cannot leave a mirror inverted.</summary>
    public void ResetForNewGame()
    {
        _chatOpen = false;
        _scoreboardOpen = false;
        _globalGoldToggled = false;
    }

    /// <summary>League losing the foreground closes its chat box.</summary>
    public void OnForegroundChanged(bool leagueInFront)
    {
        if (!leagueInFront) _chatOpen = false;
    }

    /// <summary>Called for each non-modifier key-down. Returns whether this key may fire a hotkey, then
    /// updates the chat/scoreboard mirrors from it.</summary>
    public bool OnKeyDown(string token, HotkeyModifiers mods, bool leagueInFront)
    {
        bool inGame = _inGame();
        // AI-NOTE: judged before this key's own mirror update, so the ENTER that opens chat may still fire
        // and the ENTER/ESCAPE that closes it may not (it was typed into the chat box).
        bool allow = !inGame || (leagueInFront && !_chatOpen);
        bool plain = (mods & NotPlain) == 0;

        switch (token)
        {
            case "ENTER":
                bool firstEnter = !_enterDown;
                _enterDown = true;
                if (inGame && leagueInFront && firstEnter && plain) _chatOpen = !_chatOpen; // Shift+Enter (all chat) too
                break;
            case "ESCAPE":
                if (inGame && leagueInFront) _chatOpen = false;
                break;
            case "TAB":
                bool firstTab = !_tabDown;
                _tabDown = true;
                // allow: no game, or in game over League with chat closed (Tab in chat does not open the scoreboard)
                if (firstTab && plain && allow) _scoreboardOpen = !_scoreboardOpen;
                break;
        }

        if (inGame && !leagueInFront) _chatOpen = false;
        return allow;
    }

    /// <summary>Called for each non-modifier key-up.</summary>
    public void OnKeyUp(string token)
    {
        if (token == "ENTER") _enterDown = false;
        else if (token == "TAB") _tabDown = false;
    }
}
