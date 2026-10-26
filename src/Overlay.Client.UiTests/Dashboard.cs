using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Overlay.Core.Combo;

namespace Overlay.Client.UiTests;

/// <summary>
/// Dashboard (HOME window) helpers shared by the combo-editor and settings tests: waiting, element
/// lookup, scrolling an element into view, saved-combo seeds and the pixel colour check.
/// </summary>
public static class Dashboard
{
    public const string Champion = "Annie";
    public const string ChampionKo = "애니";

    /// <summary>Chords from CTRL+ALT+SHIFT+F9..F12: no game, Windows or League binding uses them.</summary>
    public const string K1 = "CTRL+ALT+SHIFT+F9";
    public const string K2 = "CTRL+ALT+SHIFT+F10";
    public const string K3 = "CTRL+ALT+SHIFT+F11";
    public const string K4 = "CTRL+ALT+SHIFT+F12";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Colour distance (|ΔR|+|ΔG|+|ΔB|) within which a pixel counts as a theme colour.</summary>
    public const int ColourTolerance = 30;

    // ── waiting and lookup ──────────────────────────────────────────────────────────

    public static void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (true)
        {
            bool ok;
            try { ok = condition(); } catch { ok = false; }
            if (ok) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            Thread.Sleep(100);
        }
    }

    public static T WaitFor<T>(Func<T?> probe, string what, TimeSpan? timeout = null) where T : class
    {
        T? found = null;
        WaitUntil(() => (found = probe()) is not null, what, timeout);
        return found!;
    }

    /// <summary>The element with <paramref name="automationId"/> when it is shown (not collapsed;
    /// scrolled out of the page still counts).</summary>
    public static AutomationElement? Shown(AutomationElement root, string automationId)
    {
        var element = root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        return element is not null && IsShown(element) ? element : null;
    }

    public static bool IsShown(AutomationElement? element)
    {
        if (element is null) return false;
        try { return !element.Properties.IsOffscreen.ValueOrDefault || !element.BoundingRectangle.IsEmpty; }
        catch { return false; }
    }

    public static AutomationElement WaitShown(AutomationElement root, string automationId) =>
        WaitFor(() => Shown(root, automationId), $"'{automationId}' shown");

    /// <summary>The Name of the element (button content, text) or null when it cannot be read.</summary>
    public static string? NameOf(AutomationElement element)
    {
        try { return element.Name; } catch { return null; }
    }

    public static void WaitName(AutomationElement element, string expected, string what) =>
        WaitUntil(() => NameOf(element) == expected, $"{what} = '{expected}' (is '{NameOf(element)}')");

    // ── navigation ──────────────────────────────────────────────────────────────────

    public static void OpenNav(FovaSession session, string navId)
    {
        var nav = WaitShown(session.Home, navId);
        session.Input.Click(nav);
        WaitUntil(() => nav.Patterns.SelectionItem.Pattern.IsSelected.Value, $"{navId} selected");
    }

    /// <summary>Opens 콤보설정, filters the picker to <see cref="Champion"/> and clicks its tile, then
    /// waits until each name in <paramref name="savedNames"/> is listed.</summary>
    public static void OpenComboEditor(FovaSession session, params string[] savedNames) =>
        OpenComboEditorFor(session, Champion, ChampionKo, savedNames);

    /// <summary><see cref="OpenComboEditor"/> for another champion (<paramref name="championKo"/> is
    /// its tile label).</summary>
    public static void OpenComboEditorFor(FovaSession session, string champion, string championKo, params string[] savedNames)
    {
        OpenNav(session, "NavCombo");
        var search = WaitShown(session.Home, "ChampionSearchBox");
        search.Patterns.Value.Pattern.SetValue(champion);
        // AI-NOTE: the tile Border is not in the UIA tree; its name label sits right under it, both
        // ChipSize wide, so the tile's centre is half a label width above the label's top.
        var label = WaitFor(() => session.Home.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName(championKo))),
            $"{championKo} tile label");
        var r = label.BoundingRectangle;
        session.Input.Click(new Point(r.Left + r.Width / 2, r.Top - r.Width / 2 - 2));
        foreach (var name in savedNames)
            WaitFor(() => SavedCard(session, name), $"saved card '{name}'");
    }

    // ── saved combo cards ───────────────────────────────────────────────────────────

    /// <summary>The saved-list item whose title is <paramref name="name"/>, or null.</summary>
    public static AutomationElement? SavedCard(FovaSession session, string name)
    {
        var list = Shown(session.Home, "SavedList");
        if (list is null) return null;
        foreach (var item in list.FindAllChildren())
            if (item.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName(name))) is not null)
                return item;
        return null;
    }

    public static AutomationElement CardButton(FovaSession session, string comboName, params string[] names) =>
        WaitFor(() =>
        {
            var card = SavedCard(session, comboName);
            return card?.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => names.Contains(NameOf(b)));
        }, $"button {string.Join("/", names)} on '{comboName}'");

    // ── scrolling ───────────────────────────────────────────────────────────────────

    /// <summary>The page's own scroller: <c>PageScroll</c> on the combo editor, else the largest
    /// scrollable pane in HOME (Settings' page ScrollViewer has no name).</summary>
    public static AutomationElement PageScroller(FovaSession session) =>
        Shown(session.Home, "PageScroll")
        ?? session.Home.FindAllDescendants(cf => cf.ByControlType(ControlType.Pane))
            .Where(e => e.Patterns.Scroll.IsSupported && IsShown(e))
            .OrderByDescending(e => e.BoundingRectangle.Width * e.BoundingRectangle.Height)
            .First();

    /// <summary>Scrolls the page (UIA ScrollPattern on the test side) until <paramref name="element"/>
    /// lies fully inside the page viewport.</summary>
    public static void ScrollIntoView(FovaSession session, AutomationElement element)
    {
        var scroller = PageScroller(session);
        var scroll = scroller.Patterns.Scroll.Pattern;
        for (int i = 0; i < 40; i++)
        {
            var view = scroller.BoundingRectangle;
            var r = element.BoundingRectangle;
            if (!r.IsEmpty && r.Top >= view.Top && r.Bottom <= view.Bottom) return;
            bool down = r.IsEmpty ? scroll.VerticalScrollPercent.Value < 100 : r.Bottom > view.Bottom;
            scroll.Scroll(ScrollAmount.NoAmount, down ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement);
            Thread.Sleep(60);
        }
        throw new InvalidOperationException($"Could not scroll '{NameOf(element)}' into view");
    }

    public static void ClickInView(FovaSession session, AutomationElement element)
    {
        ScrollIntoView(session, element);
        session.Input.Click(element);
    }

    // ── config ──────────────────────────────────────────────────────────────────────

    /// <summary>Seed overrides for saved combos of <see cref="Champion"/>: <c>combos.saved.&lt;id&gt;</c>
    /// (a serialized <see cref="SavedCombo"/>) and one <c>hotkeys.comboSlots</c> key per chord.</summary>
    public static Dictionary<string, object?> SeedCombos(params (string Id, string Name, string[] Hotkeys)[] combos)
    {
        var graphOptions = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        string graphJson = JsonSerializer.Serialize(EngineOracle.SkillSequence("Q", "W", "R"), graphOptions);
        var seed = new Dictionary<string, object?>();
        foreach (var (id, name, hotkeys) in combos)
        {
            seed["combos.saved." + id] = JsonSerializer.Serialize(new SavedCombo(id, Champion, name, graphJson));
            foreach (var hotkey in hotkeys)
                seed["hotkeys.comboSlots." + ComboEditor.ComposeSlotKey(hotkey, Champion)] = id;
        }
        return seed;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static JsonObject Section(FovaSession session, string key) =>
        ConfigSeed.Get(session.Config(), key) as JsonObject ?? new JsonObject();

    /// <summary>True when <paramref name="key"/> holds a non-null value in <paramref name="section"/>.</summary>
    /// <remarks>AI-NOTE: the app removes a dictionary entry with <c>ConfigManager.Set(key, null)</c>,
    /// which leaves <c>"key": null</c> in the file; <c>ConfigManager.Get</c> and hotkey dispatch
    /// (<c>AppComposition.RefreshComboHotkeys</c> skips null values) both read that as absent.</remarks>
    public static bool Has(JsonObject section, string key) =>
        section.TryGetPropertyValue(key, out var value) && value is not null;

    /// <summary>Saved combos of <see cref="Champion"/> named <paramref name="name"/>: id → combo.</summary>
    public static List<SavedCombo> SavedNamed(FovaSession session, string name) =>
        Section(session, "combos.saved")
            .Select(p => p.Value?.GetValue<string>() is { } json ? JsonSerializer.Deserialize<SavedCombo>(json) : null)
            .Where(c => c is not null && c.ChampionId == Champion && c.Name == name)
            .Select(c => c!)
            .ToList();

    public static string SlotKey(string hotkey) => ComboEditor.ComposeSlotKey(hotkey, Champion);

    // ── pixels ──────────────────────────────────────────────────────────────────────

    /// <summary>Captures HOME (saved as evidence) and counts the pixels inside
    /// <paramref name="element"/>'s rectangle that are within <see cref="ColourTolerance"/> of each colour.</summary>
    public static int[] CountColours(UiContext ctx, FovaSession session, AutomationElement element, string label, params uint[] colours)
    {
        var hwnd = session.Home.Properties.NativeWindowHandle.Value;
        using var bitmap = Screen.Capture(hwnd);
        NativeMethods.GetWindowRect(hwnd, out var win);
        var r = element.BoundingRectangle;
        var crop = Rectangle.Intersect(new Rectangle(r.Left - win.Left, r.Top - win.Top, r.Width, r.Height),
            new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        string path = Path.Combine(ctx.App.RunDir, $"{ctx.TestName}-run{ctx.Run}-{label}.png");
        using (var cropped = bitmap.Clone(crop, PixelFormat.Format32bppRgb)) cropped.Save(path, ImageFormat.Png);
        ctx.AddScreenshot(path);

        var counts = new int[colours.Length];
        var targets = colours.Select(ThemePalette.ToColor).ToArray();
        for (int y = crop.Top; y < crop.Bottom; y++)
            for (int x = crop.Left; x < crop.Right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                for (int i = 0; i < targets.Length; i++)
                    if (Screen.Distance(pixel, targets[i]) <= ColourTolerance) counts[i]++;
            }
        ctx.Note($"{label}: {string.Join(", ", colours.Select((c, i) => $"#{c:X6}={counts[i]}"))} px in {crop.Width}x{crop.Height}");
        return counts;
    }

    public static bool HomeVisible(FovaSession session)
    {
        var hwnd = session.Home.Properties.NativeWindowHandle.Value;
        return session.IsAlive && NativeMethods.IsWindowVisible(hwnd);
    }
}
