using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using static Overlay.Client.UiTests.Dashboard;

namespace Overlay.Client.UiTests;

/// <summary>
/// Telemetry runtime checks (CLAUDE_CODE_TODO.md 1787-1788; spec R16-R18): the one-time HOME notice on a
/// first run and the Settings > 기본 toggle. Every request goes to the session's loopback listener.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class TelemetryTests
{
    private const string NoticeText =
        "품질 개선을 위해 익명 사용 통계와 오류 리포트를 보냅니다 (설치 ID·버전·OS·오류 내용만 — 게임/계정 정보 없음). 설정에서 끌 수 있습니다.";

    private static readonly string[] TelemetryPaths = { "/ping", "/crash" };

    private static readonly TimeSpan PingWindow = TimeSpan.FromSeconds(15);

    private readonly AppCopyFixture _app;

    public TelemetryTests(AppCopyFixture app) => _app = app;

    [UiTheory]
    [InlineData("Ok")]
    [InlineData("OptOut")]
    public void FirstRunNoticeOnce(string variant) => UiTest.Run(_app, $"{nameof(FirstRunNoticeOnce)}_{variant}", new[] { "TODO:1787", "R16", "R18" }, ctx =>
    {
        var session = ctx.Start(new Dictionary<string, object?>
        {
            ["telemetry.noticeShown"] = false,
            ["telemetry.enabled"] = true,
            ["general.firstRunGuideShown"] = false,
        });
        CheckIsolation(session);
        var home = session.Home;

        Assert.Equal(NoticeText, NameOf(WaitShown(home, "TelemetryNoticeText")));
        var ok = WaitShown(home, "TelemetryOk");
        var optOut = WaitShown(home, "TelemetryOptOut");
        Assert.Equal("확인", NameOf(ok));
        Assert.Equal("보내지 않기", NameOf(optOut));

        BringToFront(session);
        session.Input.Click(variant == "Ok" ? ok : optOut);
        WaitUntil(() => !NoticeShown(session), "the telemetry notice hidden after the click");
        WaitUntil(() => ConfigSeed.Get(session.Config(), "telemetry.noticeShown")?.GetValue<bool>() == true,
            "telemetry.noticeShown true");
        bool? enabled = ConfigSeed.Get(session.Config(), "telemetry.enabled")?.GetValue<bool>();
        Assert.True(enabled == (variant == "Ok"), $"telemetry.enabled is {enabled?.ToString() ?? "missing"} after {variant}");

        session.Restart();
        CheckIsolation(session);
        // AI-NOTE: HomeWindow decides the notice in its constructor, before HOME is found; the wait
        // only covers a late layout pass.
        Thread.Sleep(1000);
        Assert.False(NoticeShown(session), "the telemetry notice is shown again after the relaunch");
        CheckIsolation(session);
    });

    [UiFact]
    public void TelemetryToggle() => UiTest.Run(_app, nameof(TelemetryToggle), new[] { "TODO:1788", "R17", "R18" }, ctx =>
    {
        var session = ctx.Start();
        CheckIsolation(session);
        // The launch ping shows the listener receives what the app sends, so the zero below means something.
        WaitUntil(() => Pings(session, DateTime.MinValue) == 1, "the first launch's /ping", PingWindow);

        var toggle = OpenTelemetryToggle(session);
        Assert.Equal(ToggleState.On, State(toggle));
        ClickInView(session, toggle);
        WaitUntil(() => State(toggle) == ToggleState.Off, "TelemetryToggle off");
        WaitUntil(() => ConfigSeed.Get(session.Config(), "telemetry.enabled")?.GetValue<bool>() == false,
            "telemetry.enabled false at once", TimeSpan.FromSeconds(2));

        // Restart with telemetry off: the toggle reads off and no launch ping arrives.
        var offStart = DateTime.Now;
        session.Restart();
        CheckIsolation(session);
        toggle = OpenTelemetryToggle(session);
        Assert.Equal(ToggleState.Off, State(toggle));
        SleepUntil(offStart + PingWindow);
        Assert.Equal(0, Pings(session, offStart));

        ClickInView(session, toggle);
        WaitUntil(() => State(toggle) == ToggleState.On, "TelemetryToggle on");
        WaitUntil(() => ConfigSeed.Get(session.Config(), "telemetry.enabled")?.GetValue<bool>() == true,
            "telemetry.enabled true at once", TimeSpan.FromSeconds(2));

        // Restart with telemetry on: exactly one launch ping.
        var onStart = DateTime.Now;
        session.Restart();
        CheckIsolation(session);
        SleepUntil(onStart + PingWindow);
        Assert.Equal(1, Pings(session, onStart));
        Assert.Equal(ToggleState.On, State(OpenTelemetryToggle(session)));
        CheckIsolation(session);
    });

    /// <summary>Spec R18, read back by the test: the config still points telemetry at the listener with
    /// ads and the LCU off, and the listener has seen telemetry paths only.</summary>
    private static void CheckIsolation(FovaSession session)
    {
        var config = session.Config();
        Assert.Equal(session.Listener.Url, ConfigSeed.Get(config, "telemetry.endpoint")?.GetValue<string>());
        Assert.True(ConfigSeed.Get(config, "ads.enabled")?.GetValue<bool>() == false, "ads.enabled is not false");
        Assert.True(ConfigSeed.Get(config, "champSelect.enabled")?.GetValue<bool>() == false, "champSelect.enabled is not false");
        var stray = session.Listener.Requests.Where(r => !TelemetryPaths.Contains(r.Path)).ToList();
        Assert.True(stray.Count == 0,
            "the listener recorded non-telemetry requests: " + string.Join(", ", stray.Select(r => $"{r.Method} {r.Path}")));
    }

    private static bool NoticeShown(FovaSession session) =>
        Shown(session.Home, "TelemetryNoticeText") is not null
        || Shown(session.Home, "TelemetryOk") is not null
        || Shown(session.Home, "TelemetryOptOut") is not null;

    /// <summary>Opens Settings > 기본, checks the caption and returns the toggle.</summary>
    private static AutomationElement OpenTelemetryToggle(FovaSession session)
    {
        BringToFront(session);
        OpenNav(session, "NavSettings");
        var pill = WaitShown(session.Home, "PillGeneral");
        if (!pill.Patterns.SelectionItem.Pattern.IsSelected.Value) session.Input.Click(pill);
        WaitUntil(() => pill.Patterns.SelectionItem.Pattern.IsSelected.Value, "PillGeneral selected");
        Assert.Equal("익명 사용 통계·오류 리포트", NameOf(WaitShown(session.Home, "TelemetryToggleCaption")));
        return WaitShown(session.Home, "TelemetryToggle");
    }

    /// <summary>Raises HOME with a click on the first uncovered point of its title bar.</summary>
    /// <remarks>AI-NOTE: a freshly launched copy does not take the foreground from the window the user
    /// is in, which can cover part of HOME (seen: Chrome over the notice buttons), and SafeInput then
    /// refuses the click. SetForeground is refused by the Windows foreground lock, so a real click is
    /// the way in. The title bar is harmless to click; its left end (the system-menu icon) is skipped.</remarks>
    private static void BringToFront(FovaSession session)
    {
        if (session.Input.IsSessionForeground()) return;
        NativeMethods.GetWindowRect(session.Home.Properties.NativeWindowHandle.Value, out var r);
        var spot = Enumerable.Range(1, 9)
            .Select(i => new System.Drawing.Point(r.Left + r.Width * i / 10, r.Top + 15))
            .Where(p => p.X - r.Left > 100)
            .FirstOrDefault(session.Input.IsInsideSessionWindow);
        if (spot.IsEmpty) throw new InvalidOperationException("HOME's title bar is covered by another window");
        session.Input.Click(spot);
        WaitUntil(session.Input.IsSessionForeground, "HOME in the foreground");
    }

    private static ToggleState State(AutomationElement toggle) => toggle.Patterns.Toggle.Pattern.ToggleState.Value;

    private static int Pings(FovaSession session, DateTime since) =>
        session.Listener.Requests.Count(r => r.Path == "/ping" && r.Time >= since);

    private static void SleepUntil(DateTime time)
    {
        var left = time - DateTime.Now;
        if (left > TimeSpan.Zero) Thread.Sleep(left);
    }
}
