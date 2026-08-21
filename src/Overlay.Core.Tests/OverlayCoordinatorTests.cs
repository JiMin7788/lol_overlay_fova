using Overlay.Core.Config;
using Overlay.Core.EventBus;
using Overlay.Core.Overlay;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for the testable heart of M02 Overlay Engine
/// (docs/modules/M02_OVERLAY_ENGINE.md) — <see cref="OverlayCoordinator"/>. Covers the
/// two display-independent Acceptance Criteria plus the spec Internal Logic:
///  - UI.* event → HUDPayload conversion (Internal Logic step 2).
///  - Auto-hide at the configured duration, asserted deterministically via a virtual
///    clock so the "within 100ms of the configured duration" criterion is provable
///    without any real sleep (Internal Logic step 3).
///  - HideHUD removes a HUD before its expiry.
///  - Z-Order priority: combo result &gt; item alert &gt; notification with 4+ active
///    HUDs (Internal Logic step 4).
///  - Default duration sourced from M14 overlay.hudDisplayDuration.
///  - Dashboard open/close state transitions.
///
/// The coordinator is exercised entirely against a <see cref="FakeView"/> — no WPF
/// window — exactly as M16's render logic is tested without a display. The 3-screen-mode
/// display + mouse-passthrough criteria need a real display run and are deferred to a
/// human (see M02 Agent Report).
/// </summary>
public class OverlayCoordinatorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public OverlayCoordinatorTests()
    {
        EventBus.EventBus.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M02_OverlayTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        EventBus.EventBus.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    private sealed class FakeClock : IClock
    {
        public long NowMs { get; set; }
    }

    private sealed class FakeView : IOverlayView
    {
        public List<HUDPayload> Shown { get; } = new();
        public List<string> Hidden { get; } = new();
        public int DashboardOpens { get; private set; }
        public int DashboardCloses { get; private set; }

        public void ShowHud(HUDPayload payload) => Shown.Add(payload);
        public void HideHud(string hudId) => Hidden.Add(hudId);
        public void OpenDashboard() => DashboardOpens++;
        public void CloseDashboard() => DashboardCloses++;
    }

    private ConfigManager NewConfig() => new(_configPath);

    private static HUDPayload Payload(string id, HudType type, object? content = null)
        => new(id, type, content, Position: null);

    // ── UI.* event → HUDPayload conversion (Internal Logic step 2) ───────────────

    [Fact]
    public void TryConvert_MapsUiEventTypeToHudType()
    {
        var evt = new Event
        {
            Id = "e1", Type = "UI.COMBO_RESULT", Source = "M03",
            Timestamp = 0, Payload = "combo-content",
        };

        Assert.True(OverlayCoordinator.TryConvert(evt, out var payload));
        Assert.Equal(HudType.ComboResult, payload.Type);
        Assert.Equal("combo-content", payload.Content);
        Assert.False(string.IsNullOrEmpty(payload.Id));
    }

    [Fact]
    public void TryConvert_UsesEmbeddedHudPayloadVerbatim()
    {
        var embedded = new HUDPayload("fixed-id", HudType.ItemAlert, "raptors", new HudPosition(10, 20));
        var evt = new Event
        {
            Id = "e2", Type = "UI.NOTIFICATION", Source = "X", Timestamp = 0, Payload = embedded,
        };

        Assert.True(OverlayCoordinator.TryConvert(evt, out var payload));
        Assert.Same(embedded, payload);
        Assert.Equal("fixed-id", payload.Id);
    }

    [Fact]
    public void TryConvert_UnmappedTypeWithNonPayload_ReturnsFalse()
    {
        var evt = new Event
        {
            Id = "e3", Type = "UI.SOMETHING_ELSE", Source = "X", Timestamp = 0, Payload = 42,
        };

        Assert.False(OverlayCoordinator.TryConvert(evt, out _));
    }

    [Fact]
    public async Task Subscribe_UiEventPublished_ShowsHud()
    {
        var view = new FakeView();
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, new FakeClock());
        coordinator.Start();

        EventBus.EventBus.Publish("UI.ITEM_ALERT", "Zhonya ready", source: "M07");

        // UI.* is dispatched async on the bus worker thread — poll briefly for delivery.
        await WaitUntil(() => coordinator.ActiveCount == 1);

        Assert.Equal(1, coordinator.ActiveCount);
        Assert.Single(view.Shown);
        Assert.Equal(HudType.ItemAlert, view.Shown[0].Type);
        Assert.Equal("Zhonya ready", view.Shown[0].Content);
    }

    [Fact]
    public async Task EnemyJunglerSpottedAlert_UsesFixed3sDuration_NotConfiguredDefault()
    {
        // M30: this alert's 3s lifetime is part of its own spec (user-specified "3초후
        // 페이드아웃"), not the user-configurable overlay.hudDisplayDuration every other toast
        // honors — so it must ignore a differently-configured default.
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 1000 };
        using var config = NewConfig();
        config.Set("overlay.hudDisplayDuration", 10.0); // would be 10s if this alert used the default
        using var coordinator = new OverlayCoordinator(view, config, clock);
        coordinator.Start();

        EventBus.EventBus.Publish("UI.ENEMY_JUNGLER_SPOTTED_ALERT", "적 정글이 감지되었습니다 (Kayn)", source: "M30");

        await WaitUntil(() => coordinator.ActiveCount == 1);

        var item = Assert.Single(coordinator.GetRenderList());
        Assert.Equal(HudType.EnemyJunglerSpottedAlert, item.Payload.Type);
        Assert.Equal(1000 + 3000, item.ExpiryMs); // fixed 3s, ignoring the configured 10s default

        clock.NowMs = 3999;
        Assert.Equal(1, coordinator.ActiveCount); // still visible just before 3s

        clock.NowMs = 4000;
        coordinator.PurgeExpired(clock.NowMs);
        Assert.Equal(0, coordinator.ActiveCount); // gone at exactly 3s (not 10s)
    }

    // ── Auto-hide at configured duration via virtual clock (Internal Logic step 3) ─

    [Fact]
    public void ComputeExpiry_IsShownAtPlusDuration()
        => Assert.Equal(5000, OverlayCoordinator.ComputeExpiry(1000, 4000));

    [Fact]
    public void ShowHud_AutoExpires_AtConfiguredDurationBoundary()
    {
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 1000 };
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, clock);

        coordinator.ShowHUD(Payload("h1", HudType.Notification), durationMs: 4000);

        // One millisecond before expiry: still active, nothing purged.
        clock.NowMs = 4999;
        Assert.Empty(coordinator.GetExpired(clock.NowMs));
        Assert.Empty(coordinator.PurgeExpired(clock.NowMs));
        Assert.Equal(1, coordinator.ActiveCount);

        // Exactly at expiry (shownAt 1000 + 4000): removed and the view is notified.
        clock.NowMs = 5000;
        Assert.Equal(new[] { "h1" }, coordinator.GetExpired(clock.NowMs));
        Assert.Equal(new[] { "h1" }, coordinator.PurgeExpired(clock.NowMs));
        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Equal(new[] { "h1" }, view.Hidden);
    }

    [Fact]
    public void AutoHide_Within100msTolerance_IsProvable()
    {
        // The spec allows 100ms error. The frame loop purges every ~16ms, so a HUD is
        // removed no later than one frame past its expiry. Model that worst case here:
        // expiry at 5000, next frame tick at 5016 (16ms later) — well inside 100ms.
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 1000 };
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, clock);

        coordinator.ShowHUD(Payload("h1", HudType.Notification), durationMs: 4000);

        clock.NowMs = 5016; // one 16ms frame after the 5000 expiry
        var purged = coordinator.PurgeExpired(clock.NowMs);

        Assert.Equal(new[] { "h1" }, purged);
        Assert.True(clock.NowMs - OverlayCoordinator.ComputeExpiry(1000, 4000) <= 100);
    }

    // ── Combo-result persistence (toggle-style, not auto-hide) ───────────────────

    [Fact]
    public void ComboResultHud_IsPersistent_NotPurgedAfterDefaultDuration()
    {
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 0 };
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, clock);

        // Persistent duration mirrors the UI.COMBO_RESULT path (see OnUiEvent).
        coordinator.ShowHUD(Payload("combo", HudType.ComboResult),
            OverlayCoordinator.PersistentDurationMs);

        // Far past the 4000ms default auto-hide: still active, nothing expired or purged.
        clock.NowMs = 60_000;
        Assert.Empty(coordinator.GetExpired(clock.NowMs));
        Assert.Empty(coordinator.PurgeExpired(clock.NowMs));
        Assert.Equal(1, coordinator.ActiveCount);
        Assert.Contains("combo", coordinator.GetRenderList().Select(i => i.Payload.Id));
        Assert.Empty(view.Hidden);
    }

    [Fact]
    public void NonComboHud_StillAutoHides()
    {
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 0 };
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, clock);

        // Default (config) duration = 4000ms — persistence must not leak to other HUD types.
        coordinator.ShowHUD(Payload("item", HudType.ItemAlert));
        coordinator.ShowHUD(Payload("note", HudType.Notification));

        clock.NowMs = 4000;
        var purged = coordinator.PurgeExpired(clock.NowMs);

        Assert.Equal(new[] { "item", "note" }, purged.OrderBy(x => x));
        Assert.Equal(0, coordinator.ActiveCount);
    }

    [Fact]
    public void ClearHud_RemovesActiveComboCard_AndNotifiesView()
    {
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 0 };
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, clock);

        coordinator.ShowHUD(Payload("combo", HudType.ComboResult),
            OverlayCoordinator.PersistentDurationMs);
        Assert.Equal(1, coordinator.ActiveCount);

        coordinator.ClearHud("combo");

        Assert.Equal(0, coordinator.ActiveCount);
        Assert.DoesNotContain("combo", coordinator.GetRenderList().Select(i => i.Payload.Id));
        Assert.Equal(new[] { "combo" }, view.Hidden);
    }

    [Fact]
    public void IsActive_ReflectsShowAndClear_ByStableId()
    {
        // M02/M04/M13 toggle-off wiring point: ComboRunner checks this before deciding whether
        // to re-trigger a combo or clear its already-shown card.
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 0 };
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, clock);

        Assert.False(coordinator.IsActive("UI.COMBO_RESULT"));

        coordinator.ShowHUD(Payload("UI.COMBO_RESULT", HudType.ComboResult),
            OverlayCoordinator.PersistentDurationMs);
        Assert.True(coordinator.IsActive("UI.COMBO_RESULT"));

        coordinator.ClearHud("UI.COMBO_RESULT");
        Assert.False(coordinator.IsActive("UI.COMBO_RESULT"));
    }

    // ── HideHUD removes before expiry ────────────────────────────────────────────

    [Fact]
    public void HideHud_RemovesBeforeExpiry_AndNotifiesView()
    {
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 0 };
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, clock);

        coordinator.ShowHUD(Payload("h1", HudType.RecallTimer), durationMs: 10000);
        Assert.Equal(1, coordinator.ActiveCount);

        coordinator.HideHUD("h1");

        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Equal(new[] { "h1" }, view.Hidden);
    }

    [Fact]
    public void HideHud_UnknownId_IsNoOp()
    {
        var view = new FakeView();
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, new FakeClock());

        coordinator.HideHUD("nope");

        Assert.Empty(view.Hidden);
    }

    // ── Z-Order priority with 5+ active HUDs (Internal Logic step 4) ──────────────

    [Fact]
    public void GetRenderList_OrdersByZOrder_ComboOverItemOverNotification()
    {
        var view = new FakeView();
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, new FakeClock());

        // Show 4 HUDs, deliberately out of priority order.
        coordinator.ShowHUD(Payload("combo", HudType.ComboResult), 10000);
        coordinator.ShowHUD(Payload("note", HudType.Notification), 10000);
        coordinator.ShowHUD(Payload("recall", HudType.RecallTimer), 10000);
        coordinator.ShowHUD(Payload("item", HudType.ItemAlert), 10000);

        var order = coordinator.GetRenderList();

        // Ascending zOrder (drawn bottom→top): note(40) < recall(60) < item(80) < combo(100).
        // Combo on top, notification at the bottom.
        Assert.Equal(
            new[] { "note", "recall", "item", "combo" },
            order.Select(i => i.Payload.Id));

        // Spec's explicit ranking holds: combo result > item alert > notification.
        int combo = IndexOf(order, "combo");
        int item = IndexOf(order, "item");
        int note = IndexOf(order, "note");
        Assert.True(combo > item && item > note);

        // zOrder integers are the M16 values.
        Assert.Equal(100, order.First(i => i.Payload.Id == "combo").ZOrder);
        Assert.Equal(40, order.First(i => i.Payload.Id == "note").ZOrder);
    }

    [Fact]
    public void GetRenderList_EqualPriority_PreservesShowOrder()
    {
        var view = new FakeView();
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, new FakeClock());

        coordinator.ShowHUD(Payload("a", HudType.Notification), 10000);
        coordinator.ShowHUD(Payload("b", HudType.Notification), 10000);
        coordinator.ShowHUD(Payload("c", HudType.Notification), 10000);

        Assert.Equal(new[] { "a", "b", "c" },
            coordinator.GetRenderList().Select(i => i.Payload.Id));
    }

    [Fact]
    public void ZOrderOverride_ChangesPriority()
    {
        var view = new FakeView();
        using var config = NewConfig();
        var overrides = new Dictionary<HudType, int> { [HudType.Notification] = 999 };
        using var coordinator = new OverlayCoordinator(view, config, new FakeClock(), overrides);

        coordinator.ShowHUD(Payload("combo", HudType.ComboResult), 10000);
        coordinator.ShowHUD(Payload("note", HudType.Notification), 10000);

        // Notification (normally lowest) now overrides to the top.
        Assert.Equal(new[] { "combo", "note" },
            coordinator.GetRenderList().Select(i => i.Payload.Id));
    }

    // ── Default duration + position sourced from M14 config ──────────────────────

    [Fact]
    public void DefaultDuration_ComesFromConfig_FourSecondsByDefault()
    {
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 0 };
        using var config = NewConfig(); // fresh file → overlay.hudDisplayDuration default = 4s
        using var coordinator = new OverlayCoordinator(view, config, clock);

        coordinator.ShowHUD(Payload("h1", HudType.Notification)); // no explicit duration

        clock.NowMs = 3999;
        Assert.Equal(1, coordinator.ActiveCount); // still visible just before 4s

        clock.NowMs = 4000;
        coordinator.PurgeExpired(clock.NowMs);
        Assert.Equal(0, coordinator.ActiveCount); // gone at exactly 4s
    }

    [Fact]
    public void DefaultDuration_HonoursConfigOverride()
    {
        var view = new FakeView();
        var clock = new FakeClock { NowMs = 0 };
        using var config = NewConfig();
        config.Set("overlay.hudDisplayDuration", 2.0); // 2 seconds

        using var coordinator = new OverlayCoordinator(view, config, clock);
        coordinator.ShowHUD(Payload("h1", HudType.Notification));

        clock.NowMs = 2000;
        coordinator.PurgeExpired(clock.NowMs);
        Assert.Equal(0, coordinator.ActiveCount);
    }

    [Fact]
    public void NullPosition_ResolvedToConfigDefault()
    {
        var view = new FakeView();
        using var config = NewConfig();
        config.Set("overlay.position.x", 100.0);
        config.Set("overlay.position.y", 200.0);

        using var coordinator = new OverlayCoordinator(view, config, new FakeClock());
        coordinator.ShowHUD(Payload("h1", HudType.Notification)); // Position null

        Assert.Equal(new HudPosition(100, 200), view.Shown[0].Position);
    }

    [Fact]
    public void ExplicitPosition_IsPreserved()
    {
        var view = new FakeView();
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, new FakeClock());

        coordinator.ShowHUD(new HUDPayload("h1", HudType.Notification, null, new HudPosition(7, 8)));

        Assert.Equal(new HudPosition(7, 8), view.Shown[0].Position);
    }

    // ── Dashboard open/close state transitions ───────────────────────────────────

    [Fact]
    public void Dashboard_OpenClose_IsIdempotentAndDelegatesToView()
    {
        var view = new FakeView();
        using var config = NewConfig();
        using var coordinator = new OverlayCoordinator(view, config, new FakeClock());

        Assert.False(coordinator.IsDashboardOpen);

        coordinator.OpenDashboard();
        coordinator.OpenDashboard(); // idempotent
        Assert.True(coordinator.IsDashboardOpen);
        Assert.Equal(1, view.DashboardOpens);

        coordinator.CloseDashboard();
        coordinator.CloseDashboard(); // idempotent
        Assert.False(coordinator.IsDashboardOpen);
        Assert.Equal(1, view.DashboardCloses);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static int IndexOf(IReadOnlyList<HudRenderItem> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Payload.Id == id) return i;
        return -1;
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
    }
}
