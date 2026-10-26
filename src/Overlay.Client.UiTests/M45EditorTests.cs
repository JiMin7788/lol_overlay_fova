using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Overlay.Client.UiTests.M45;
using Overlay.Core.Combo;
using static Overlay.Client.UiTests.Dashboard;

namespace Overlay.Client.UiTests;

/// <summary>
/// The M45 per-champion editor checks (HUMAN_CHECKLIST.md 104-122 and on; spec R19-R21), driven from
/// <c>M45/m45_cases.json</c>: one theory row per checklist item, one Fova session per row. Each claim is
/// built in the combo editor with the real mouse (tile, palette drags, badge pickers) and every preview
/// number is compared with <see cref="EngineOracle"/> on the graph the editor saved. The oracle runs the
/// app's own engine, so this proves the editor's wiring and display, not the game numbers
/// (RECURRING_TRAPS 2.11); those rest on the claim's engine evidence.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class M45EditorTests
{
    private readonly AppCopyFixture _app;

    public M45EditorTests(AppCopyFixture app) => _app = app;

    public static IEnumerable<object[]> Items() => M45CaseFile.Load().Cases.Select(c => new object[] { c.Hc });

    [UiTheory]
    [MemberData(nameof(Items))]
    public void Item(int hc)
    {
        var item = M45CaseFile.Load().Case(hc);
        UiTest.Run(_app, $"M45EditorTests.Item_hc{hc}", new[] { $"HC:{hc}" }, ctx => new M45Driver(ctx, item).Run());
    }
}

/// <summary>One run of one checklist item: a session, the reference-combo check, then every claim.</summary>
internal sealed class M45Driver
{
    public const string ReferenceMismatch = "oracle/editor mismatch on the reference combo";

    // AI-NOTE: the reference combo (Annie [Q, W] against the fixed graph) is checked once per test
    // process; a mismatch means the oracle reads other data than the app, so every case fails with it.
    private static readonly object ReferenceGate = new();
    private static bool _referencePassed;
    private static string? _referenceFailure;

    private static readonly Regex AttackCountBadge = new(@"^\d+타$");
    private static readonly Regex HitDurationBadge = new(@"^\d+(\.\d+)?s$");
    private static readonly Regex StackCountBadge = new(@"^(\d+스택|최대)$");

    private readonly UiContext _ctx;
    private readonly M45Case _item;
    private FovaSession _session = null!;
    private EngineOracle _oracle = null!;
    private string? _champion;
    private double _dpi = 1;

    public M45Driver(UiContext ctx, M45Case item)
    {
        _ctx = ctx;
        _item = item;
    }

    private AutomationElement Home => _session.Home;

    /// <summary>A report note, also written with a timestamp to <c>runs/&lt;runId&gt;/m45.log</c> as it
    /// happens, so a long item can be followed while it runs.</summary>
    private void Note(string text)
    {
        _ctx.Note(text);
        Step(text);
    }

    private void Step(string text)
    {
        try
        {
            Directory.CreateDirectory(_ctx.App.RunDir);
            File.AppendAllText(Path.Combine(_ctx.App.RunDir, "m45.log"),
                $"{DateTime.Now:HH:mm:ss.fff} hc{_item.Hc} run{_ctx.Run} {text}\n");
        }
        catch (IOException)
        {
            // The log is a progress aid, not a result.
        }
    }

    public void Run()
    {
        Step("start");
        _session = _item.Claims.Any(c => c.Checks.Any(k => k.Type == "card")) ? _ctx.Start(CardSeed()) : _ctx.Start();
        Step("session started");
        WaitForDataDragon();
        // AI-NOTE: maximized, the palette and the sequence fit in the page together on any display scale
        // (at 200% the restored HOME is taller than a 4K screen).
        Home.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        Thread.Sleep(500);
        _dpi = Screen.Dpi(Home.Properties.NativeWindowHandle.Value);
        OpenNav(_session, "NavCombo");
        using var oracle = new EngineOracle(_ctx.App.AppDir, _session.ConfigPath, _ctx.ScratchDir("oracle"));
        _oracle = oracle;
        Note($"oracle Data Dragon {EngineOracle.DDragonVersion}, DPI scale {_dpi:0.##}");
        ReferenceCheck();

        var failures = new List<string>();
        foreach (var claim in _item.Claims)
        {
            try
            {
                failures.AddRange(RunClaim(claim));
            }
            catch (Exception ex)
            {
                failures.Add($"HC:{_item.Hc} claim {claim.Id}: {ex.GetType().Name}: {ex.Message}");
                _ctx.CaptureAllWindows();
                Recover();
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join("\n", failures));
    }

    /// <summary>The copy downloads the live Data Dragon on its first launch and uses it from the next
    /// one (DDragonUpdater). The oracle loads the newest folder, so a session still on the old one is
    /// restarted onto it.</summary>
    private void WaitForDataDragon()
    {
        string log = Path.Combine(_session.DataDir, "logs", "assets.log");
        string? Line() => File.Exists(log)
            ? File.ReadLines(log).LastOrDefault(l => l.Contains("Data Dragon") || l.Contains("retry next launch"))
            : null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? line;
        while ((line = Line()) is null && DateTime.UtcNow < deadline) Thread.Sleep(250);
        Note($"assets.log: {line ?? "no Data Dragon line in 30 s"}");
        if (line?.Contains("used from next launch") == true)
        {
            _session.Restart();
            Note("restarted onto the downloaded Data Dragon");
        }
    }

    private void ReferenceCheck()
    {
        lock (ReferenceGate)
        {
            if (_referenceFailure is not null) throw new InvalidOperationException(_referenceFailure);
            if (_referencePassed) return;
            SelectChampion("Annie", ChampionKo);
            Build(new[] { "Q", "W" });
            var shown = ReadPreview();
            var expected = _oracle.Preview("Annie", EngineOracle.SkillSequence("Q", "W"))
                           ?? throw new InvalidOperationException("the oracle computed nothing for Annie [Q, W]");
            ClearSequence();
            Note($"reference Annie [Q, W]: editor '{shown.Text}', oracle {expected.Resolved:0.##} ({expected.Min:0.##}~{expected.Max:0.##})");
            if (OracleMismatch(shown, expected) is { } why)
            {
                _referenceFailure = $"{ReferenceMismatch}: Annie [Q, W] {why}";
                throw new InvalidOperationException(_referenceFailure);
            }
            _referencePassed = true;
        }
    }

    // ── one claim ──────────────────────────────────────────────────────────────────

    private List<string> RunClaim(M45Claim claim)
    {
        var failures = new List<string>();
        void Fail(object check, string found) => failures.Add($"HC:{_item.Hc} claim {claim.Id}: {check}: {found}");

        SelectChampion(claim.Champion, claim.ChampionKo);
        foreach (var check in claim.Checks.Where(c => c.Type == "paletteHas"))
            if (PaletteLabel(check.Slot!) is null) Fail(check, $"no '{check.Slot}' chip in the palette");

        var oracleCheck = claim.Checks.FirstOrDefault(c => c.Type == "oracle");
        var readings = new Dictionary<string, M45Reading>();
        for (int s = 0; s < claim.States.Count; s++)
        {
            var state = claim.States[s];
            var sequence = claim.SequenceOf(state);
            Build(sequence);
            if (s == 0)
                foreach (var check in claim.Checks)
                    if (StructureCheck(check) is { } found) Fail(check, found);

            foreach (var knob in state.Knobs) SetKnob(knob);
            var reading = ReadPreview();
            readings[state.Name] = reading;
            Note($"{claim.Id} {state.Name} [{string.Join(",", sequence)}]: '{reading.Text}'");

            if (oracleCheck is not null && (oracleCheck.States is null || oracleCheck.States.Contains(state.Name)))
            {
                if (SaveAndCompare(claim, state, sequence, reading) is { } found) Fail(oracleCheck, $"state {state.Name}: {found}");
            }
            else
                ClearSequence();
        }

        foreach (var check in claim.Checks)
        {
            if (check.Type == "compare" && M45Compare.Check(check, readings) is { } found) Fail(check, found);
            if (check.Type == "card" && CardCheck(claim, check) is { } card) Fail(check, card);
        }
        return failures;
    }

    // ── the overlay combo card (lolF02 t6b) ────────────────────────────────────────

    private const string PressKey = "단축키를 입력해주세요…";

    /// <summary>The seed of an item with a card check: the HUD drawn at the scale the overlay OCR reads
    /// (OverlayPreviewTests), every preview element but the combo card switched off.</summary>
    private static Dictionary<string, object?> CardSeed()
    {
        var seed = new Dictionary<string, object?>
        {
            ["overlay.scale"] = OverlayPreviewTests.UserScaleFor(OverlayPreviewTests.PrimaryDipHeight()),
        };
        OverlayPreviewTests.HideAllBut(seed, "comboResult");
        return seed;
    }

    /// <summary>Saves state <c>b</c> on K1 and state <c>a</c> on K2 from the editor, opens the overlay
    /// preview, presses each chord and OCRs the number of the card line holding the check's label; null
    /// when <c>a op b</c> holds for those numbers.</summary>
    private string? CardCheck(M45Claim claim, M45Check check)
    {
        string label = check.Label ?? throw new ArgumentException("card needs label");
        var saved = new List<(string State, string Name, string Id, int Chips, string Chord)>();
        try
        {
            foreach (var (stateName, chord) in new[] { (check.B!, K1), (check.A!, K2) })
            {
                var state = claim.States.Single(s => s.Name == stateName);
                var sequence = claim.SequenceOf(state);
                Build(sequence);
                foreach (var knob in state.Knobs) SetKnob(knob);
                string name = $"m45 {claim.Id} card {stateName}";
                Step($"save '{name}' on {chord}");
                WaitShown(Home, "NameBox").Patterns.Value.Pattern.SetValue(name);
                var hotkey = WaitShown(Home, "HotkeyButton");
                ClickInView(_session, hotkey);
                WaitName(hotkey, PressKey, "HotkeyButton capturing");
                _session.Input.Keys(chord);
                WaitName(hotkey, chord, "HotkeyButton");
                ClickInView(_session, WaitShown(Home, "SaveButton"));
                WaitName(WaitShown(Home, "SaveMessage"), $"'{name}' 콤보를 저장했습니다.", "SaveMessage");
                saved.Add((stateName, name, WaitFor(() => SavedId(claim.Champion, name), $"combos.saved entry '{name}'"), sequence.Count, chord));
            }

            var values = new Dictionary<string, double>();
            var overlay = OverlayPreviewTests.ShowPreview(_ctx, _session);
            try
            {
                // AI-NOTE: the preview opens on the champion's first saved combo (b's), and pressing the chord
                // of the card already shown hides it, so a's chord goes first. The two cards are told apart by
                // their chip count, so the case gives a and b sequences of different lengths.
                foreach (var (stateName, name, _, chips, chord) in new[] { saved[1], saved[0] })
                {
                    Step($"press {chord} for '{name}'");
                    _session.Input.Keys(chord);
                    double? value = null;
                    string text = "";
                    var (bitmap, card) = OverlayPreviewTests.WaitForCard(_ctx, overlay,
                        (b, c) => !c.ChipEmpty(b, chips - 1) && c.ChipEmpty(b, chips)
                                  && c.LineBand(b) is { } band && (value = M45CardLine.Value(text = Screen.Ocr(b, band), label)) is not null,
                        $"the '{name}' card with a '{label}' line", TimeSpan.FromSeconds(20));
                    using (bitmap)
                    {
                        string path = Path.Combine(_ctx.App.RunDir, $"M45-hc{_item.Hc}-{claim.Id}-card-{stateName}-run{_ctx.Run}.png");
                        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        _ctx.AddScreenshot(path);
                        Note($"{claim.Id} card {stateName}: OCR '{text.Replace('\n', ' ')}', value {value}");
                    }
                    values[stateName] = value!.Value;
                }
            }
            finally
            {
                // AI-NOTE: a plain click: the header's PreviewToggle is outside the page scroller, and
                // ClickInView's scroll-into-view threw InvalidOperationException on it.
                _session.Input.Click(WaitShown(Home, "PreviewToggle")); // the overlay preview off again
            }

            double a = values[check.A!], b = values[check.B!];
            bool ok = check.Op switch
            {
                ">" => a > b,
                "<" => a < b,
                _ => throw new ArgumentException($"unknown card op '{check.Op}' (> <)"),
            };
            return ok ? null : $"'{label}' {a:0} ({check.A}) is not {check.Op} {b:0} ({check.B})";
        }
        finally
        {
            foreach (var (_, name, id, _, _) in saved) DeleteSaved(name, id);
        }
    }

    /// <summary>badge / noBadge / sliderMax on the freshly built sequence; null when it holds.</summary>
    private string? StructureCheck(M45Check check)
    {
        switch (check.Type)
        {
            case "badge":
                return HasBadge(check.Node!.Value, check.Kind!) ? null : "no such badge";
            case "noBadge":
                return HasBadge(check.Node!.Value, check.Kind!) ? "the badge is there" : null;
            case "sliderMax":
                var badge = Badge(check.Node!.Value, check.Kind!) ?? throw new InvalidOperationException("no such badge");
                ClickInView(_session, badge);
                var popup = WaitPopup(p => SliderOf(p) is not null, "the picker popup");
                double max = SliderOf(popup)!.Patterns.RangeValue.Pattern.Maximum.Value;
                ClosePopup(popup);
                return Math.Abs(max - check.Max!.Value) <= 1e-6 ? null : $"the slider maximum is {max:0.##}";
            default:
                return null;
        }
    }

    // ── champion and sequence ──────────────────────────────────────────────────────

    private void SelectChampion(string champion, string championKo)
    {
        if (_champion == champion) return;
        Step($"select {champion}");
        var search = WaitShown(Home, "ChampionSearchBox");
        ScrollIntoView(_session, search);
        search.Patterns.Value.Pattern.SetValue(champion);
        var label = WaitFor(() => TileLabel(champion, championKo), $"the {champion} tile");
        ScrollIntoView(_session, label);
        // AI-NOTE: the tile Border has no UIA peer; its name label sits under it, both ChipSize wide,
        // so the tile's centre is half a label width above the label's top (as Dashboard.OpenComboEditorFor).
        var r = label.BoundingRectangle;
        _session.Input.Click(new Point(r.Left + r.Width / 2, r.Top - r.Width / 2 - 2));
        WaitFor(() => PaletteLabel("AA"), $"the {champion} palette");
        // The ability icons load right after and rebuild the palette; let that land before a drag.
        Thread.Sleep(1500);
        _champion = champion;
    }

    /// <summary>The champion tile's name label: the only tile left by the search, or the one named
    /// <paramref name="championKo"/> (or the id, before the Korean names have loaded).</summary>
    private AutomationElement? TileLabel(string champion, string championKo)
    {
        var builder = Builder();
        double top = builder.Bottom;
        double bottom = WaitShown(Home, "PaletteCaption").BoundingRectangle.Top;
        var labels = Texts().Where(t => Within(t, top, bottom, builder)).ToList();
        return labels.FirstOrDefault(t => NameOf(t) == championKo || NameOf(t) == champion)
               ?? (labels.Count == 1 ? labels[0] : null);
    }

    private IEnumerable<AutomationElement> Texts() =>
        Home.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));

    /// <summary>The champion search box: as wide as the builder card's content.</summary>
    private Rectangle Builder() => WaitShown(Home, "ChampionSearchBox").BoundingRectangle;

    /// <summary>True when <paramref name="e"/>'s centre lies between <paramref name="top"/> and
    /// <paramref name="bottom"/> inside the builder card (the left column, as wide as the champion search box).</summary>
    /// <remarks>AI-NOTE: the right column's preset rows show the same slot letters on their icons at the
    /// palette's height, so a lookup by text alone picked a preset icon.</remarks>
    private static bool Within(AutomationElement e, double top, double bottom, Rectangle builder)
    {
        var r = e.BoundingRectangle;
        double x = r.Left + r.Width / 2.0, y = r.Top + r.Height / 2.0;
        return !r.IsEmpty && y > top && y < bottom && x >= builder.Left && x <= builder.Right;
    }

    /// <summary>The key label of the palette chip for <paramref name="slot"/> (AA reads "A"). A chip
    /// without its icon shows the key twice; the bottom one is the key badge.</summary>
    private AutomationElement? PaletteLabel(string slot)
    {
        string key = slot == "AA" ? "A" : slot;
        double top = WaitShown(Home, "DragHint").BoundingRectangle.Bottom;
        double bottom = WaitShown(Home, "ItemCaption").BoundingRectangle.Top;
        var builder = Builder();
        return Texts().Where(t => NameOf(t) == key && Within(t, top, bottom, builder))
            .OrderByDescending(t => t.BoundingRectangle.Bottom).FirstOrDefault();
    }

    /// <summary>The '＋fx' button of each sequence entry, in sequence order: every entry has one.</summary>
    /// <remarks>AI-NOTE: ordered by X alone. The cases keep a sequence to one row (at most 5 entries, 50 DIP
    /// apart), and within its 44-DIP column the button wraps below the badges, so its Y varies per entry.</remarks>
    private List<AutomationElement> Entries() =>
        Home.FindAllDescendants(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("＋fx")))
            .OrderBy(b => b.BoundingRectangle.Left)
            .ToList();

    private void Build(IReadOnlyList<string> sequence)
    {
        ClearSequence();
        foreach (var slot in sequence)
        {
            int count = Entries().Count;
            Step($"drag {slot}");
            var label = WaitFor(() => PaletteLabel(slot), $"the '{slot}' palette chip");
            AutomationElement anchor = count == 0 ? WaitShown(Home, "SequenceHint") : Entries()[^1];
            ScrollIntoView(_session, anchor);
            if (!InPage(label))
            {
                ScrollIntoView(_session, label);
                if (!InPage(anchor)) throw new InvalidOperationException("the palette and the sequence do not fit in the page together");
            }
            label = PaletteLabel(slot)!;
            anchor = count == 0 ? WaitShown(Home, "SequenceHint") : Entries()[^1];
            var a = anchor.BoundingRectangle;
            // An empty zone: onto its hint. Otherwise right of every placed chip on the last row: the end.
            var target = count == 0
                ? SafeInput.Center(anchor)
                : new Point(WaitShown(Home, "ChampionSearchBox").BoundingRectangle.Right - (int)(10 * _dpi), a.Top + a.Height / 2);
            Step($"drag {slot} from {SafeInput.Center(label)} to {target}");
            _session.Input.Drag(SafeInput.Center(label), target);
            WaitUntil(() => Entries().Count == count + 1, $"'{slot}' dropped into the sequence ({count + 1} entries)");
        }
    }

    private bool InPage(AutomationElement element)
    {
        var view = PageScroller(_session).BoundingRectangle;
        var r = element.BoundingRectangle;
        return !r.IsEmpty && r.Top >= view.Top && r.Bottom <= view.Bottom;
    }

    private void ClearSequence()
    {
        if (Entries().Count == 0) return;
        ClickInView(_session, WaitShown(Home, "ClearButton"));
        WaitUntil(() => Entries().Count == 0, "the sequence cleared ('순서 비우기')");
    }

    /// <summary>After a failed claim: close a stray popup and empty the sequence and the name.</summary>
    private void Recover()
    {
        try
        {
            if (FindPopup(_ => true) is { } popup) ClosePopup(popup);
            ClearSequence();
            WaitShown(Home, "NameBox").Patterns.Value.Pattern.SetValue("");
        }
        catch
        {
            _champion = null; // the next claim selects its champion again
        }
    }

    // ── badges and knobs ───────────────────────────────────────────────────────────

    private static bool IsBadge(string kind, string text) => kind switch
    {
        "attackCount" => AttackCountBadge.IsMatch(text),
        "hitDuration" => HitDurationBadge.IsMatch(text),
        "distance" => text.StartsWith('↔'),
        "stackCount" => StackCountBadge.IsMatch(text),
        "condition" => text.StartsWith("☐ ") || text.StartsWith("☑ "),
        _ => throw new ArgumentException($"unknown badge kind '{kind}'"),
    };

    private bool HasBadge(int node, string kind) =>
        kind == "fx" ? Entries()[node].IsEnabled : Badge(node, kind) is not null;

    /// <summary>The <paramref name="kind"/> badge under sequence entry <paramref name="node"/>: badges sit in
    /// their entry's ChipSize-wide column, so each belongs to the '＋fx' button nearest it horizontally.</summary>
    private AutomationElement? Badge(int node, string kind)
    {
        var entries = Entries();
        if (node >= entries.Count) throw new InvalidOperationException($"node {node}: the sequence has {entries.Count} entries");
        double top = WaitShown(Home, "SequenceCaption").BoundingRectangle.Bottom;
        double bottom = WaitShown(Home, "ClearButton").BoundingRectangle.Top;
        var builder = Builder();
        double CenterX(AutomationElement e) => e.BoundingRectangle.Left + e.BoundingRectangle.Width / 2.0;
        foreach (var text in Texts())
        {
            if (NameOf(text) is not { } name || !IsBadge(kind, name) || !Within(text, top, bottom, builder)) continue;
            int nearest = Enumerable.Range(0, entries.Count).MinBy(i => Math.Abs(CenterX(entries[i]) - CenterX(text)));
            if (nearest == node) return text;
        }
        return null;
    }

    private void SetKnob(M45Knob knob)
    {
        string what = $"{knob.Kind} = {knob.Text} on node {knob.Node}";
        Step($"knob {what}");
        switch (knob.Kind)
        {
            case "condition":
            {
                var badge = Badge(knob.Node, "condition") ?? throw new InvalidOperationException($"{what}: no condition badge");
                if (NameOf(badge)!.StartsWith("☑")) break;
                ClickInView(_session, badge);
                WaitUntil(() => NameOf(Badge(knob.Node, "condition")!)?.StartsWith("☑") == true, $"{what}: the badge ticked");
                break;
            }
            case "fx":
            {
                var button = Entries()[knob.Node];
                ClickInView(_session, button);
                string label = knob.Text;
                // AI-NOTE: the picker's rows are found among HOME's descendants (where UIA lists the popup's
                // content); only the open picker shows these labels.
                AutomationElement row;
                try
                {
                    row = WaitFor(() => FxRow(Home, label), $"{what}: the '＋fx' picker row '{label}'");
                }
                catch (TimeoutException ex)
                {
                    var seen = Texts().Select(NameOf).Where(n => n?.Contains('·') == true).Select(n => $"'{n}'");
                    throw new TimeoutException($"{ex.Message}; rows seen: {string.Join(", ", seen)}", ex);
                }
                _session.Input.Click(row);
                WaitUntil(() => FxRow(Home, label) is null, $"{what}: the picker closed");
                break;
            }
            default:
            {
                var badge = Badge(knob.Node, knob.Kind) ?? throw new InvalidOperationException($"{what}: no {knob.Kind} badge");
                ClickInView(_session, badge);
                var popup = WaitPopup(p => SliderOf(p) is not null, $"{what}: the picker popup");
                var range = SliderOf(popup)!.Patterns.RangeValue.Pattern;
                // AI-NOTE: (lolF02 t6a) an unset distance slider already shows 100%; setting the value it
                // shows raises no change, so move it off and back as a user would.
                if (Math.Abs(range.Value.Value - knob.Number) < 1e-9)
                    range.SetValue(knob.Number > range.Minimum.Value ? range.Minimum.Value : range.Maximum.Value);
                range.SetValue(knob.Number);
                ClosePopup(popup);
                string expected = knob.Kind switch
                {
                    "attackCount" => $"{(int)knob.Number}타",
                    "hitDuration" => knob.Number.ToString("0.#", CultureInfo.InvariantCulture) + "s",
                    "distance" => $"↔{(int)Math.Round(knob.Number)}%",
                    "stackCount" => $"{(int)knob.Number}스택",
                    _ => throw new ArgumentException($"unknown knob kind '{knob.Kind}'"),
                };
                WaitUntil(() => Badge(knob.Node, knob.Kind) is { } b && NameOf(b) == expected, $"{what}: the badge reads '{expected}'");
                break;
            }
        }
    }

    private static AutomationElement? SliderOf(AutomationElement popup) =>
        popup.FindFirstDescendant(cf => cf.ByControlType(ControlType.Slider));

    private static AutomationElement? FxRow(AutomationElement root, string label) =>
        root.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            // AI-NOTE: a row reads "✓ " or figure spaces (U+2007), then the label; TrimStart() drops any Unicode space.
            .FirstOrDefault(t => NameOf(t) is { } name && name.TrimStart().TrimStart('✓').TrimStart() == label && name != label);

    /// <summary>A popup of the session matching <paramref name="match"/>.</summary>
    /// <remarks>AI-NOTE: a WPF Popup is its own native top-level window, but UIA lists its root as an
    /// unnamed Window child of HOME, not of the desktop; both places are searched.</remarks>
    private AutomationElement? FindPopup(Func<AutomationElement, bool> match)
    {
        var home = Home.Properties.NativeWindowHandle.Value;
        var candidates = Home.FindAllChildren(cf => cf.ByControlType(ControlType.Window))
            .Concat(_session.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(_session.Process!.Id)));
        foreach (var window in candidates)
        {
            try
            {
                if (window.Properties.NativeWindowHandle.ValueOrDefault == home) continue;
                if (match(window)) return window;
            }
            catch
            {
                // A window closing while it is read.
            }
        }
        return null;
    }

    private AutomationElement WaitPopup(Func<AutomationElement, bool> match, string what)
    {
        try
        {
            return WaitFor(() => FindPopup(match), what);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message}; session windows: {DescribeWindows()}", ex);
        }
    }

    /// <summary>The session's visible native windows and UIA top-level elements, for a popup that was not found.</summary>
    private string DescribeWindows()
    {
        var native = _session.Windows().Select(h =>
        {
            NativeMethods.GetWindowRect(h, out var r);
            return $"0x{h.ToInt64():X} ({r.Left},{r.Top} {r.Width}x{r.Height})";
        });
        var uia = _session.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(_session.Process!.Id)).Select(w =>
        {
            try
            {
                var kinds = w.FindAllDescendants().Take(12).Select(d => $"{d.ControlType}:{NameOf(d)}");
                return $"{w.ControlType} '{NameOf(w)}' 0x{w.Properties.NativeWindowHandle.ValueOrDefault.ToInt64():X} [{string.Join(", ", kinds)}]";
            }
            catch (Exception e)
            {
                return e.GetType().Name;
            }
        });
        return $"native [{string.Join("; ", native)}] uia [{string.Join("; ", uia)}]";
    }

    /// <summary>Commits a picker the way a user does: a click outside it, on a caption of the page.</summary>
    private void ClosePopup(AutomationElement popup)
    {
        var p = popup.BoundingRectangle;
        var view = PageScroller(_session).BoundingRectangle;
        var spot = new[] { "SequenceCaption", "PaletteCaption", "PreviewBasis", "NameCaption", "BuildCaption" }
            .Select(id => Shown(Home, id))
            .Where(e => e is not null && InPage(e) && !e.BoundingRectangle.IntersectsWith(p))
            .Select(e => SafeInput.Center(e!))
            .FirstOrDefault(c => view.Contains(c) && !p.Contains(c));
        if (spot.IsEmpty) throw new InvalidOperationException("no caption outside the picker to click");
        _session.Input.Click(spot);
        WaitUntil(() => FindPopup(w => SliderOf(w) is not null || w.BoundingRectangle == p) is null, "the picker closed");
    }

    // ── preview, save, oracle ──────────────────────────────────────────────────────

    /// <summary>The preview once it stops changing (three equal reads 150 ms apart).</summary>
    private M45Reading ReadPreview()
    {
        var preview = WaitShown(Home, "PreviewRange");
        string? last = null;
        int same = 0;
        var deadline = DateTime.UtcNow + Dashboard.Timeout;
        while (same < 3)
        {
            string? text = NameOf(preview);
            same = text is { Length: > 0 } && text != "—" && text == last ? same + 1 : 0;
            last = text;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"PreviewRange did not settle (is '{text}')");
            Thread.Sleep(150);
        }
        // AI-NOTE: the app formats with N0 in its own current culture; the test runs as the same user on the
        // same machine, so CurrentCulture is that culture.
        return M45Reading.Parse(last!, CultureInfo.CurrentCulture);
    }

    /// <summary>Saves the state under a scratch name, compares the shown numbers with the oracle on the
    /// graph the editor saved, then deletes the scratch combo (✕ twice). Null when they agree.</summary>
    private string? SaveAndCompare(M45Claim claim, M45State state, IReadOnlyList<string> sequence, M45Reading shown)
    {
        string name = $"m45 {claim.Id} {state.Name}";
        Step($"save '{name}'");
        WaitShown(Home, "NameBox").Patterns.Value.Pattern.SetValue(name);
        ClickInView(_session, WaitShown(Home, "SaveButton"));
        WaitName(WaitShown(Home, "SaveMessage"), $"'{name}' 콤보를 저장했습니다.", "SaveMessage");
        string id = WaitFor(() => SavedId(claim.Champion, name), $"combos.saved entry '{name}'");
        try
        {
            var (champion, graph) = _oracle.FromSavedCombo(_session.Config(), id);
            var slots = graph.Nodes.Select(n => n.Id.Split('_')[0]).ToList();
            if (champion != claim.Champion || !slots.SequenceEqual(sequence))
                return $"the saved combo is {champion} [{string.Join(",", slots)}]";
            var expected = _oracle.Preview(champion, graph);
            if (expected is null) return "the oracle computed nothing";
            Note($"{claim.Id} {state.Name}: oracle {expected.Resolved:0.##} ({expected.Min:0.##}~{expected.Max:0.##})");
            return OracleMismatch(shown, expected);
        }
        finally
        {
            DeleteSaved(name, id);
        }
    }

    /// <summary>Deletes the scratch combo <paramref name="name"/> from its saved card (✕ twice).</summary>
    private void DeleteSaved(string name, string id)
    {
        Step($"delete '{name}'");
        var delete = CardButton(_session, name, "✕");
        ClickInView(_session, delete);
        WaitName(delete, "삭제할까요?", "✕ armed");
        _session.Input.Click(SafeInput.Center(delete));
        WaitUntil(() => !Has(Section(_session, "combos.saved"), id), $"scratch combo '{name}' deleted");
    }

    private string? SavedId(string champion, string name)
    {
        foreach (var (id, value) in Section(_session, "combos.saved"))
        {
            if (value?.GetValue<string>() is not { } json) continue;
            var combo = JsonSerializer.Deserialize<SavedCombo>(json);
            if (combo is not null && combo.ChampionId == champion && combo.Name == name) return id;
        }
        return null;
    }

    /// <summary>Null when every shown number is the oracle's within 0.5: a range shows Min and Max, a
    /// single number shows Resolved (and the oracle must have no span then).</summary>
    private static string? OracleMismatch(M45Reading shown, ComboPreview oracle)
    {
        const double tolerance = 0.5;
        bool oracleRange = oracle.Max - oracle.Min >= 1.0;
        string o = $"oracle {oracle.Resolved:0.##} ({oracle.Min:0.##}~{oracle.Max:0.##})";
        if (shown.IsRange != oracleRange) return $"shows '{shown.Text}', {o}";
        bool ok = shown.IsRange
            ? Math.Abs(shown.Min - oracle.Min) <= tolerance && Math.Abs(shown.Max - oracle.Max) <= tolerance
            : Math.Abs(shown.Min - oracle.Resolved) <= tolerance;
        return ok ? null : $"shows '{shown.Text}', {o}";
    }
}
