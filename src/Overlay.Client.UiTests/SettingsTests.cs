using static Overlay.Client.UiTests.Dashboard;

namespace Overlay.Client.UiTests;

/// <summary>
/// Settings hand checks (HUMAN_CHECKLIST.md 69-70; spec R11-R12): the overlay hotkey warnings and
/// refusals, and the one-time tray notice when closing with autostart on.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsTests
{
    private readonly AppCopyFixture _app;

    public SettingsTests(AppCopyFixture app) => _app = app;

    [UiFact]
    public void HotkeyWarnings() => UiTest.Run(_app, nameof(HotkeyWarnings), new[] { "HC:69", "R11" }, ctx =>
    {
        // The toggle key is a saved combo's chord (shadowed); the combo-card key is the one the skill
        // row will try to take.
        var seed = SeedCombos((NewId(), "겹치는 콤보", new[] { K3 }));
        seed["overlay.toggleHotkey"] = K3;
        seed["overlay.comboOverlayHotkey"] = K4;
        var session = ctx.Start(seed);
        var home = session.Home;
        // null by default: the view then shows its built-in key.
        string? skillBefore = ConfigSeed.Get(session.Config(), "overlay.skillOverlayHotkey")?.GetValue<string>();

        OpenNav(session, "NavSettings");
        var pill = WaitShown(home, "PillOverlay");
        session.Input.Click(pill);
        WaitUntil(() => pill.Patterns.SelectionItem.Pattern.IsSelected.Value, "PillOverlay selected");

        var warn = WaitShown(home, "HotkeyWarn");
        Assert.Equal($"지금은 콤보가 {K3}을(를) 쓰고 있어서 이 단축키는 동작하지 않아요. 다른 키로 바꾸세요.", NameOf(warn));
        foreach (var other in new[] { "ComboHotkeyWarn", "SkillHotkeyWarn", "GoldHotkeyWarn", "NextItemHotkeyWarn" })
            Assert.True(Shown(home, other) is null, $"{other} is shown although only the toggle key is shadowed");
        ScrollIntoView(session, warn);
        var colours = CountColours(ctx, session, warn, "shadowed-warning", ThemePalette.Warning, ThemePalette.Danger);
        Assert.True(colours[0] >= 10, $"the shadowed-key warning is not drawn in Warning (#{ThemePalette.Warning:X6}): {colours[0]} px");
        Assert.Equal(0, colours[1]);

        // The skill row refuses the combo-card key.
        var skill = WaitShown(home, "SkillHotkeyButton");
        string? labelBefore = NameOf(skill);
        ClickInView(session, skill);
        WaitName(skill, "단축키를 입력해주세요…", "SkillHotkeyButton capturing");
        session.Input.Keys(K4);
        var refusal = WaitShown(home, "SkillHotkeyWarn");
        WaitName(refusal, $"{K4}은(는) 다른 오버레이 단축키가 쓰고 있어요. 다른 키를 눌러 주세요.", "SkillHotkeyWarn");
        Assert.Equal("단축키를 입력해주세요…", NameOf(skill));
        Thread.Sleep(1000); // a late config write would land by now
        Assert.Equal(skillBefore, ConfigSeed.Get(session.Config(), "overlay.skillOverlayHotkey")?.GetValue<string>());
        session.Input.Keys("ESC");
        WaitName(skill, labelBefore!, "SkillHotkeyButton after Esc");
    });

    [UiFact]
    public void TrayNoticeOnce() => UiTest.Run(_app, nameof(TrayNoticeOnce), new[] { "HC:70", "R12" }, ctx =>
    {
        string runBefore = SmokeTests.RunValue();
        var session = ctx.Start(new Dictionary<string, object?>
        {
            ["general.autoStartWithGame"] = true,
            ["general.trayNoticeShown"] = false,
        });
        int Notices() => session.AppLogLines().Count(l => l.EndsWith("tray notice shown", StringComparison.Ordinal));

        // First close: hidden to the tray, notice once.
        CloseToTray(session, "first");
        WaitUntil(() => Notices() == 1, "one 'tray notice shown' line after the first close");
        WaitUntil(() => ConfigSeed.Get(session.Config(), "general.trayNoticeShown")?.GetValue<bool>() == true,
            "general.trayNoticeShown true");

        // A second launch brings the dashboard back; the second close hides it without a notice.
        using (var second = session.Relaunch())
        {
            WaitUntil(() => HomeVisible(session), "HOME shown again after the relaunch", TimeSpan.FromSeconds(20));
            Assert.True(second.WaitForExit(10_000), "the second launch did not exit");
        }
        CloseToTray(session, "second");
        Thread.Sleep(1500);
        Assert.Equal(1, Notices());
        Assert.True(ConfigSeed.Get(session.Config(), "general.trayNoticeShown")?.GetValue<bool>());
        Assert.Equal(runBefore, SmokeTests.RunValue());
    });

    private static void CloseToTray(FovaSession session, string which)
    {
        session.Home.Patterns.Window.Pattern.Close();
        WaitUntil(() => !HomeVisible(session), $"HOME hidden after the {which} close");
        Thread.Sleep(1500);
        Assert.True(session.IsAlive, $"Fova exited on the {which} close although autostart is on");
    }
}
