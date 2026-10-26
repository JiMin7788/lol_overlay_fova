using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using static Overlay.Client.UiTests.Dashboard;

namespace Overlay.Client.UiTests;

/// <summary>
/// The combo editor's hand checks (HUMAN_CHECKLIST.md 66-68, 96; spec R8-R10, R13) on a real copy
/// with seeded saved combos of <see cref="Dashboard.Champion"/>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ComboEditorTests
{
    private const string PressKey = "단축키를 입력해주세요…";
    private const string SetHotkey = "단축키 설정";
    private const string EmptySlot2 = "＋";
    private const string DeleteConfirm = "삭제할까요?";

    private readonly AppCopyFixture _app;

    public ComboEditorTests(AppCopyFixture app) => _app = app;

    [UiFact]
    public void DeleteNeedsTwoClicks() => UiTest.Run(_app, nameof(DeleteNeedsTwoClicks), new[] { "HC:66", "R8" }, ctx =>
    {
        string id = NewId(), keep = NewId();
        const string name = "삭제 대상", other = "남는 콤보";
        var session = ctx.Start(SeedCombos((id, name, new[] { K1 }), (keep, other, new[] { K2 })));
        OpenComboEditor(session, name, other);

        // First click arms only.
        var delete = CardButton(session, name, "✕");
        ClickInView(session, delete);
        WaitName(delete, DeleteConfirm, "✕ after one click");
        Assert.True(Has(Section(session, "combos.saved"), id), "one click deleted the combo");

        // Leaving the button disarms it; the next click only arms again.
        session.Input.MoveOut(delete);
        WaitName(delete, "✕", "✕ after mouse leave");
        var sinceClick = Stopwatch.StartNew();
        session.Input.Click(delete);
        WaitName(delete, DeleteConfirm, "✕ clicked after the disarm");
        Assert.True(Has(Section(session, "combos.saved"), id), "the click after a disarm deleted the combo");

        // With no input it stays armed at 2.0 s and has disarmed by 4.0 s.
        SleepUntil(sinceClick, 2.0);
        Assert.Equal(DeleteConfirm, NameOf(delete));
        SleepUntil(sinceClick, 4.0);
        Assert.Equal("✕", NameOf(delete));

        // Two clicks within 3 s delete the combo and its hotkey.
        session.Input.Click(delete);
        WaitName(delete, DeleteConfirm, "✕ armed again");
        Assert.True(Has(Section(session, "combos.saved"), id));
        Assert.True(Has(Section(session, "hotkeys.comboSlots"), SlotKey(K1)));
        session.Input.Click(SafeInput.Center(delete));

        WaitUntil(() => SavedCard(session, name) is null, "the deleted card gone");
        Assert.NotNull(SavedCard(session, other));
        WaitUntil(() => !Has(Section(session, "combos.saved"), id), "combos.saved.<id> absent");
        Assert.False(Has(Section(session, "hotkeys.comboSlots"), SlotKey(K1)), "the deleted combo's hotkey is still mapped");
        Assert.True(Has(Section(session, "combos.saved"), keep), "the other combo was deleted too");
        Assert.Equal(keep, Section(session, "hotkeys.comboSlots")[SlotKey(K2)]?.GetValue<string>());
        var message = WaitShown(session.Home, "SaveMessage");
        WaitName(message, $"'{name}' 콤보를 삭제했어요.", "SaveMessage");
    });

    [UiFact]
    public void EditAndCancelEdit() => UiTest.Run(_app, nameof(EditAndCancelEdit), new[] { "HC:67", "R9" }, ctx =>
    {
        string id = NewId();
        const string name = "올인";
        var session = ctx.Start(SeedCombos((id, name, new[] { K1 })));
        OpenComboEditor(session, name);
        var home = session.Home;

        ClickInView(session, CardButton(session, name, "✎"));
        var nameBox = WaitShown(home, "NameBox");
        WaitUntil(() => nameBox.Patterns.Value.Pattern.Value.Value == name, "NameBox filled");
        Assert.Null(Shown(home, "SequenceHint")); // the hint shows only while the sequence is empty
        Assert.Equal(K1, NameOf(WaitShown(home, "HotkeyButton")));
        Assert.NotNull(Shown(home, "CancelEditButton"));
        WaitName(WaitShown(home, "SaveMessage"), $"수정 중: {name} — 저장하면 기존 콤보를 대체합니다.", "SaveMessage");

        string before = ComboConfig(session);
        ClickInView(session, WaitShown(home, "CancelEditButton"));
        WaitUntil(() => Shown(home, "CancelEditButton") is null, "CancelEditButton hidden");
        Assert.Equal("", nameBox.Patterns.Value.Pattern.Value.Value);
        Assert.NotNull(Shown(home, "SequenceHint"));
        Assert.Equal(SetHotkey, NameOf(WaitShown(home, "HotkeyButton")));
        Thread.Sleep(1000); // a late config write would land by now
        Assert.Equal(before, ComboConfig(session));

        // Edit again, move the hotkey from K1 to K2, save.
        ClickInView(session, CardButton(session, name, "✎"));
        WaitUntil(() => nameBox.Patterns.Value.Pattern.Value.Value == name, "NameBox filled again");
        var hotkey = WaitShown(home, "HotkeyButton");
        ClickInView(session, hotkey);
        WaitName(hotkey, PressKey, "HotkeyButton capturing");
        session.Input.Keys(K2);
        WaitName(hotkey, K2, "HotkeyButton");
        ClickInView(session, WaitShown(home, "SaveButton"));
        WaitName(WaitShown(home, "SaveMessage"), $"'{name}' 콤보를 저장했습니다.", "SaveMessage");

        WaitUntil(() => SavedNamed(session, name).Count == 1 && SavedNamed(session, name)[0].Id != id, "one re-saved combo");
        string newId = SavedNamed(session, name)[0].Id;
        var slots = Section(session, "hotkeys.comboSlots");
        Assert.Equal(newId, slots[SlotKey(K2)]?.GetValue<string>());
        Assert.False(Has(slots, SlotKey(K1)), $"{SlotKey(K1)} still mapped after the edit moved the key");
    });

    [UiFact]
    public void HotkeyCapture() => UiTest.Run(_app, nameof(HotkeyCapture), new[] { "HC:68", "R10" }, ctx =>
    {
        string id = NewId();
        const string name = "견제";
        var session = ctx.Start(SeedCombos((id, name, new[] { K1 })));
        OpenComboEditor(session, name);
        var home = session.Home;
        ClickInView(session, CardButton(session, name, "✎"));
        var slot1 = WaitShown(home, "HotkeyButton");
        var slot2 = WaitShown(home, "HotkeyButton2");
        WaitName(slot1, K1, "HotkeyButton loaded");

        // Esc keeps the previous chord, Backspace clears it.
        Press(session, slot1, "ESC");
        WaitName(slot1, K1, "HotkeyButton after Esc");
        Press(session, slot1, "BACKSPACE");
        WaitName(slot1, SetHotkey, "HotkeyButton after Backspace");

        Press(session, slot2, "ESC");
        WaitName(slot2, EmptySlot2, "HotkeyButton2 after Esc with no chord");
        Press(session, slot2, K3);
        WaitName(slot2, K3, "HotkeyButton2");
        Press(session, slot2, "ESC");
        WaitName(slot2, K3, "HotkeyButton2 after Esc");
        Press(session, slot2, "BACKSPACE");
        WaitName(slot2, EmptySlot2, "HotkeyButton2 after Backspace");

        // Both slots cleared: the save binds nothing.
        ClickInView(session, WaitShown(home, "SaveButton"));
        WaitName(WaitShown(home, "SaveMessage"), $"'{name}' 콤보를 저장했습니다.", "SaveMessage");
        WaitUntil(() => SavedNamed(session, name).Count == 1 && SavedNamed(session, name)[0].Id != id, "the re-saved combo");
        string savedId = SavedNamed(session, name)[0].Id;
        var slots = Section(session, "hotkeys.comboSlots");
        Assert.DoesNotContain(slots, p => p.Value?.GetValue<string>() == savedId);
        Assert.False(Has(slots, SlotKey(K1)));
        Assert.False(Has(slots, SlotKey(K3)));

        // ALT+F4 is refused in red on both slots; capture goes on and the next chord is taken.
        ClickInView(session, CardButton(session, name, "✎"));
        WaitName(slot1, SetHotkey, "HotkeyButton loaded with no chord");
        const string reserved = "ALT+F4은(는) Windows가 쓰는 키라 지정할 수 없어요. 다른 키를 눌러 주세요.";
        foreach (var (slot, next, label) in new[] { (slot1, K2, "slot1"), (slot2, K4, "slot2") })
        {
            // AI-NOTE: scroll before the capture starts. A UIA scroll of PageScroll moves keyboard
            // focus to it, and the button's LostFocus ends the capture (as a real click elsewhere would).
            ScrollIntoView(session, WaitShown(home, "SaveMessage"));
            Press(session, slot, "ALT+F4");
            var message = WaitShown(home, "SaveMessage");
            WaitName(message, reserved, $"SaveMessage after ALT+F4 on {label}");
            Thread.Sleep(300);
            Assert.True(HomeVisible(session), $"ALT+F4 on {label} closed the Fova window");
            Assert.Equal(PressKey, NameOf(slot));
            var view = PageScroller(session).BoundingRectangle;
            Assert.True(view.Contains(message.BoundingRectangle), $"SaveMessage {message.BoundingRectangle} is outside the page {view}");
            var red = CountColours(ctx, session, message, $"reserved-{label}", ThemePalette.Danger, ThemePalette.Warning);
            Assert.True(red[0] >= 10, $"the reserved-key message is not drawn in Danger (#{ThemePalette.Danger:X6}): {red[0]} px");
            Assert.Equal(0, red[1]);

            session.Input.Keys(next);
            WaitName(slot, next, $"{label} after the refused chord");
        }

        ClickInView(session, WaitShown(home, "SaveButton"));
        var saved = WaitShown(home, "SaveMessage");
        WaitName(saved, $"'{name}' 콤보를 저장했습니다.", "SaveMessage after the last save");
        ScrollIntoView(session, saved);
        var normal = CountColours(ctx, session, saved, "saved-message", ThemePalette.Danger, ThemePalette.Warning);
        Assert.Equal(0, normal[0]); // negative control: a normal message has no Danger pixels
        WaitUntil(() => SavedNamed(session, name).Count == 1 && SavedNamed(session, name)[0].Id != savedId, "the last save");
        string lastId = SavedNamed(session, name)[0].Id;
        slots = Section(session, "hotkeys.comboSlots");
        Assert.Equal(lastId, slots[SlotKey(K2)]?.GetValue<string>());
        Assert.Equal(lastId, slots[SlotKey(K4)]?.GetValue<string>());
    });

    [UiFact]
    public void EnemyRuneNoteStaysVisible() => UiTest.Run(_app, nameof(EnemyRuneNoteStaysVisible), new[] { "HC:96", "R13" }, ctx =>
    {
        var combos = Enumerable.Range(1, 7).Select(i => (NewId(), $"콤보 {i}", Array.Empty<string>())).ToArray();
        var session = ctx.Start(SeedCombos(combos));
        OpenComboEditor(session, combos.Select(c => c.Item2).ToArray());
        var home = session.Home;

        var note = WaitShown(home, "EnemyRunesNote");
        Assert.Equal("적의 룬 효과(체력 파편, 뼈 방패, 재생의 바람 등)는 계산에 반영되지 않습니다. 게임이 상대 룬 정보를 주지 않아, 실제로는 피해가 조금 덜 들어가거나 처치가 안 될 수 있습니다.",
            NameOf(note));

        var page = WaitShown(home, "PageScroll");
        var scroll = page.Patterns.Scroll.Pattern;
        scroll.SetScrollPercent(FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll, 0);
        WaitUntil(() => scroll.VerticalScrollPercent.Value <= 0.01, "PageScroll at the top");
        Assert.True(scroll.VerticallyScrollable.Value, "the page does not scroll with 7 saved combos");
        AssertNoteInView(ctx, session, note, page, "top");

        // Scroll to the bottom with the real wheel over the saved list's card.
        var lastTitle = SavedCard(session, combos[^1].Item2)!;
        int titleTopBefore = lastTitle.BoundingRectangle.Top;
        double before = scroll.VerticalScrollPercent.Value;
        for (int i = 0; i < 40 && scroll.VerticalScrollPercent.Value < 99.99; i++)
        {
            var view = page.BoundingRectangle;
            session.Input.Wheel(new System.Drawing.Point(view.Right - view.Width / 5, view.Top + view.Height / 2), -3);
            Thread.Sleep(80);
        }
        double after = scroll.VerticalScrollPercent.Value;
        ctx.Note($"PageScroll vertical percent {before:0.#} -> {after:0.#}; last card top {titleTopBefore} -> {lastTitle.BoundingRectangle.Top}");
        Assert.True(after >= 99.99, $"the wheel did not reach the bottom ({after:0.#}%)");
        Assert.True(lastTitle.BoundingRectangle.Top < titleTopBefore, "the saved list did not move up");
        AssertNoteInView(ctx, session, note, page, "bottom");
    });

    // ── helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Clicks a hotkey button, checks it is capturing, then presses <paramref name="chord"/>.</summary>
    private static void Press(FovaSession session, AutomationElement button, string chord)
    {
        if (NameOf(button) != PressKey)
        {
            ClickInView(session, button);
            WaitName(button, PressKey, $"{button.AutomationId} capturing");
        }
        session.Input.Keys(chord);
    }

    private static void SleepUntil(Stopwatch watch, double seconds)
    {
        var left = TimeSpan.FromSeconds(seconds) - watch.Elapsed;
        if (left > TimeSpan.Zero) Thread.Sleep(left);
    }

    /// <summary>combos.saved and hotkeys.comboSlots as JSON, for an unchanged-config comparison.</summary>
    private static string ComboConfig(FovaSession session) =>
        Section(session, "combos.saved").ToJsonString() + "\n" + Section(session, "hotkeys.comboSlots").ToJsonString();

    private static void AssertNoteInView(UiContext ctx, FovaSession session, AutomationElement note, AutomationElement page, string where)
    {
        var r = note.BoundingRectangle;
        var home = session.Home.BoundingRectangle;
        var view = page.BoundingRectangle;
        ctx.Note($"{where}: note {r}, page {view}, HOME {home}");
        Assert.False(r.IsEmpty, $"EnemyRunesNote has no bounds at the {where}");
        Assert.False(note.Properties.IsOffscreen.ValueOrDefault, $"EnemyRunesNote is offscreen at the {where}");
        Assert.True(home.Contains(r), $"EnemyRunesNote {r} is outside HOME {home} at the {where}");
        Assert.True(r.Top >= view.Bottom - 1, $"EnemyRunesNote {r} is not below the scrolling page {view} at the {where}");
    }
}
