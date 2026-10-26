using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Vision;
using ComboNode = Overlay.Core.Combo.ComboNode;
using static Overlay.Client.UiTests.Dashboard;

namespace Overlay.Client.UiTests;

/// <summary>
/// The overlay screen checks of CLAUDE_CODE_TODO.md 28, 47, 48, 251 and 540 (spec R14-R15) on the
/// preview's sample game: seeded saved combos, the HOME 오버레이 미리보기 button, the combo's chord,
/// then pixel and OCR checks on a capture of the overlay window. Every crop is placed from
/// <see cref="OverlayLayout"/> (OverlayHost's own numbers × the HUD scale × the DPI scale) and found
/// by <see cref="OverlayPalette"/> colours; no screen coordinate is written down here.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverlayPreviewTests
{
    /// <summary>Colour distance within which a pixel counts as a HUD colour.</summary>
    private const int ColourTolerance = 40;

    private readonly AppCopyFixture _app;

    public OverlayPreviewTests(AppCopyFixture app) => _app = app;

    // ── TODO:28 ─────────────────────────────────────────────────────────────────────

    [UiFact]
    public void AbilityChipsAreSharp() => UiTest.Run(_app, nameof(AbilityChipsAreSharp), new[] { "TODO:28", "R14" }, ctx =>
    {
        var seed = Seed("Annie",
            new SeedCombo(NewId(), "미끼", Graph(Skill("W")), null),
            new SeedCombo(NewId(), "칩 모양", Graph(Skill("Q"), Aa(), Skill("W"), Skill("E"), Skill("R")), K1));
        HideAllBut(seed, "comboResult");
        var session = ctx.Start(seed);
        var overlay = ShowPreview(ctx, session);
        WaitForCard(ctx, overlay, (b, c) => ChipIs(b, c, 0, OverlayPalette.SlotW), "the preview's own card (the first saved combo)").Bitmap.Dispose();

        session.Input.Keys(K1);
        var (bitmap, card) = WaitForCard(ctx, overlay,
            (b, c) => ChipIs(b, c, 0, OverlayPalette.SlotQ) && ChipIs(b, c, 1, OverlayPalette.SlotA), "the chord's card (Q A W E R)");
        using (bitmap)
        {
            var background = card.Background(bitmap);
            ctx.Note($"card background {Hex(background)} (OverlayPanelBg opaque {Hex(OverlayPalette.Opaque(OverlayPalette.OverlayPanelBg))})");
            Assert.True(Screen.Distance(background, OverlayPalette.Opaque(OverlayPalette.OverlayPanelBg)) <= ColourTolerance,
                $"the sampled card background {Hex(background)} is not OverlayPanelBg");

            foreach (var (index, slot, colour) in new[] { (0, "Q", OverlayPalette.SlotQ), (2, "W", OverlayPalette.SlotW), (3, "E", OverlayPalette.SlotE), (4, "R", OverlayPalette.SlotR) })
            {
                var chip = card.ChipBox(bitmap, index, colour);
                var corners = CornerDistances(bitmap, chip, background);
                ctx.Note($"{slot} chip {chip}: corner distances {string.Join(", ", corners)}");
                Assert.True(Screen.CornersDifferFrom(bitmap, chip, background),
                    $"the {slot} chip {chip} is not square: corner distances {string.Join(", ", corners)} (each must be > {Screen.CornerTolerance})");
            }

            // Negative control: the basic-attack chip keeps the rounded corners.
            var auto = card.ChipBox(bitmap, 1, OverlayPalette.SlotA);
            var autoCorners = CornerDistances(bitmap, auto, background);
            ctx.Note($"AA chip {auto}: corner distances {string.Join(", ", autoCorners)}");
            Assert.True(autoCorners.Min() <= Screen.CornerTolerance,
                $"the basic-attack chip {auto} has no rounded corner: corner distances {string.Join(", ", autoCorners)}");

            Evidence(ctx, "todo28_chips.png", bitmap, card.ChipRow(5));
        }
    });

    // ── TODO:251 ────────────────────────────────────────────────────────────────────

    [UiFact]
    public void ComboCardLayoutAndIcons() => UiTest.Run(_app, nameof(ComboCardLayoutAndIcons), new[] { "TODO:251", "R14" }, ctx =>
    {
        const string champion = "Annie";
        const string longName = "가나다라마바사아자차카타"; // 12 Hangul syllables
        Assert.Equal(12, longName.Length);
        var seed = Seed(champion,
            new SeedCombo(NewId(), "미끼", Graph(Skill("W")), null),
            new SeedCombo(NewId(), longName, Graph(Skill("Q"), Skill("W"), Skill("E"), Skill("R"), Summoner("Ignite"), Summoner("Flash")), K1));
        HideAllBut(seed, "comboResult");
        var session = ctx.Start(seed);
        var overlay = ShowPreview(ctx, session);
        WaitForCard(ctx, overlay, (b, c) => ChipIs(b, c, 0, OverlayPalette.SlotW), "the preview's own card (the first saved combo)").Bitmap.Dispose();
        session.Input.Keys(K1);
        WaitForCard(ctx, overlay, (b, c) => ChipIs(b, c, 0, OverlayPalette.SlotQ), "the chord's card (Q W E R Ignite Flash)").Bitmap.Dispose();

        // The icons the chips must show, from the app copy's Data Dragon cache (waited for: the
        // overlay draws a letter until its background download lands).
        var icons = SlotIcons(ctx.App.AppDir, champion);
        icons["Ignite"] = SpellIcon(ctx.App.AppDir, "SummonerDot");
        icons["Flash"] = SpellIcon(ctx.App.AppDir, "SummonerFlash");
        foreach (var path in icons.Values) WaitForFile(path);

        var chips = new[] { (0, "Q", OverlayPalette.SlotQ), (1, "W", OverlayPalette.SlotW), (2, "E", OverlayPalette.SlotE),
                            (3, "R", OverlayPalette.SlotR), (4, "Ignite", OverlayPalette.SlotA), (5, "Flash", OverlayPalette.SlotA) };
        Dictionary<string, Dictionary<string, double>> diffs = new();
        var (bitmap, card) = WaitForCard(ctx, overlay, (b, c) =>
        {
            diffs = chips.ToDictionary(t => t.Item2, t => IconDiffs(b, c.IconRect(b, t.Item1, t.Item3), icons));
            return diffs.All(d => d.Value[d.Key] <= 40);
        }, "every chip drawn with its icon", TimeSpan.FromSeconds(20));
        using (bitmap)
        {
            foreach (var (_, slot, _) in chips)
            {
                var d = diffs[slot];
                ctx.Note($"{slot} chip vs icons: {string.Join(", ", d.Select(p => $"{p.Key}={p.Value:0.0}"))}");
                Assert.True(d[slot] <= 40, $"the {slot} chip differs from {icons[slot]} by {d[slot]:0.0} (mean abs channel diff, max 40)");
                foreach (var (other, value) in d.Where(p => p.Key != slot))
                    Assert.True(d[slot] < value, $"the {slot} chip is closer to the {other} icon ({value:0.0}) than to its own ({d[slot]:0.0})");
            }

            // The HP gauge stops short of the target portrait.
            var hpBar = card.HpBarBox(bitmap) ?? throw new InvalidOperationException("no HP gauge frame (HpBarBorder) on the card");
            var portrait = card.PortraitBox(bitmap) ?? throw new InvalidOperationException("no target portrait ring (AccentGold) on the card");
            ctx.Note($"HP gauge {hpBar}, portrait {portrait}");
            // Control: the two share rows, so being apart is a horizontal fact, not a missed search.
            Assert.True(hpBar.Top < portrait.Bottom && portrait.Top < hpBar.Bottom, $"the HP gauge {hpBar} and the portrait {portrait} share no row");
            Assert.False(hpBar.IntersectsWith(portrait), $"the HP gauge {hpBar} overlaps the target portrait {portrait}");

            // The 12-syllable name ends at the card's inner right edge: within 12 DIP, never past it.
            int nameRight = card.NameRight(bitmap, 6) ?? throw new InvalidOperationException("no combo name (NameDim) right of the chips");
            double innerR = card.InnerR, slack = overlay.Px(12);
            ctx.Note($"name ends at x={nameRight}, inner right edge {innerR:0.#}, card right {card.Right:0.#}");
            Assert.True(nameRight <= innerR, $"the combo name ends at x={nameRight}, past the card's inner right edge {innerR:0.#}");
            Assert.True(nameRight >= innerR - slack, $"the combo name ends at x={nameRight}, more than 12 DIP left of the inner right edge {innerR:0.#}");

            Evidence(ctx, "todo251_card.png", bitmap, card.Bounds);
        }
    });

    // ── TODO:540 ────────────────────────────────────────────────────────────────────

    [UiFact]
    public void KnobChipsAssumptionAndTrueDamage() => UiTest.Run(_app, nameof(KnobChipsAssumptionAndTrueDamage), new[] { "TODO:540", "R14" }, ctx =>
    {
        const string champion = "Maokai";
        const string rangeName = "마오카이 E";
        var seed = Seed(champion,
            new SeedCombo(NewId(), "미끼", Graph(Skill("Q"), Skill("W"), Skill("E"), Skill("R"), Aa()), null),
            new SeedCombo(NewId(), "노브", Graph(
                Skill("Q") with { UserConditionMet = true },
                Skill("W") with { UserDistanceFraction = 0.5 },
                Skill("E") with { UserAttackCount = 3 },
                Skill("R")), K1),
            new SeedCombo(NewId(), "점화", Graph(Summoner("Ignite")), K2),
            new SeedCombo(NewId(), rangeName, Graph(Skill("E")), K3));
        HideAllBut(seed, "comboResult");
        var session = ctx.Start(seed);
        var overlay = ShowPreview(ctx, session);

        // The preview's own card (the first saved combo) is the plain twin of the knob combo: same
        // chips, no knob, so any gold the knob card adds is the knobs' doing.
        var (plainBitmap, plain) = WaitForCard(ctx, overlay,
            (b, c) => ChipIs(b, c, 0, OverlayPalette.SlotQ) && ChipIs(b, c, 4, OverlayPalette.SlotA), "the preview's own card (Q W E R A)");
        using (plainBitmap)
        {
            session.Input.Keys(K1);
            var (bitmap, card) = WaitForCard(ctx, overlay,
                (b, c) => ChipIs(b, c, 0, OverlayPalette.SlotQ) && !ChipIs(b, c, 4, OverlayPalette.SlotA), "the knob combo's card");
            using (bitmap)
            {
                // (a) Q, condition assumed met: the gold frame and ▲.
                var q = card.Chip(0);
                double frame = GoldFraction(bitmap, card.LeftFrame(q));
                double plainFrame = GoldFraction(plainBitmap, plain.LeftFrame(plain.Chip(0)));
                double rFrame = GoldFraction(bitmap, card.LeftFrame(card.Chip(3)));
                ctx.Note($"gold frame left of Q: {frame:P0} (plain card {plainFrame:P0}, R on the knob card {rFrame:P0})");
                Assert.True(frame >= 0.6, $"no AccentGold frame left of the knob Q chip ({frame:P0} gold)");
                Assert.True(plainFrame <= 0.05 && rFrame <= 0.05, $"a chip without a knob has a gold frame (plain Q {plainFrame:P0}, knob card R {rFrame:P0})");

                int tri = GoldCount(bitmap, card.TriangleArea(q)), plainTri = GoldCount(plainBitmap, plain.TriangleArea(plain.Chip(0)));
                int glyph = card.GlyphPixels;
                ctx.Note($"▲ area gold px: {tri} (plain {plainTri}, needed +{glyph})");
                Assert.True(tri >= plainTri + glyph, $"no ▲ on the knob Q chip: {tri} gold px vs {plainTri} on the plain chip");

                // W, distance slider at 50%: the fill bar under the chip, half its width.
                var (fillW, fillAt) = card.FillBar(bitmap, card.Chip(1));
                var (plainFillW, _) = plain.FillBar(plainBitmap, plain.Chip(1));
                double half = card.Chip(1).Width * 0.5;
                ctx.Note($"fill bar under W: {fillW} px gold from x={fillAt} (expected {half:0.#}; plain card {plainFillW})");
                Assert.True(Math.Abs(fillW - half) <= overlay.Px(2) + 1, $"the fill bar under the W chip is {fillW} px, expected {half:0.#} (50%)");
                Assert.Equal(0, plainFillW);

                // E, three summon hits: the ×n mark.
                int count = GoldCount(bitmap, card.CountArea(card.Chip(2))), plainCount = GoldCount(plainBitmap, plain.CountArea(plain.Chip(2)));
                ctx.Note($"×n area gold px: {count} (plain {plainCount}, needed +{glyph})");
                Assert.True(count >= plainCount + glyph, $"no ×n mark on the knob E chip: {count} gold px vs {plainCount} on the plain chip");

                // The 가정 N chip after the sequence: three knobs set.
                var chipN = card.AssumptionChip(4);
                double fill = GoldFraction(bitmap, chipN);
                string ocr = Squash(Screen.Ocr(bitmap, chipN));
                ctx.Note($"가정 chip {chipN}: {fill:P0} gold, OCR '{ocr}'");
                Assert.True(fill >= 0.5, $"no gold 가정 chip after the chips ({fill:P0} gold)");
                Assert.Contains("가정3", ocr);
                Evidence(ctx, "todo540_card.png", bitmap, card.Bounds);
            }
        }

        // (c) Ignite alone is true damage: one 확정 box, no 최대.
        session.Input.Keys(K2);
        var (igniteBitmap, ignite) = WaitForCard(ctx, overlay,
            (b, c) => ChipIs(b, c, 0, OverlayPalette.SlotA) && c.ChipEmpty(b, 1), "the Ignite combo's card");
        using (igniteBitmap)
        {
            var box = ignite.MaxBox(igniteBitmap) ?? throw new InvalidOperationException("no damage box (MaxBoxBorder) on the Ignite card");
            var minBox = ignite.MinBox(igniteBitmap);
            string ocr = Squash(Screen.Ocr(igniteBitmap, box));
            ctx.Note($"Ignite card: box {box}, min box {minBox?.ToString() ?? "none"}, OCR '{ocr}'");
            Assert.Null(minBox);
            Assert.True(box.Width >= ignite.BarWidth - overlay.Px(2) - 1, $"the single box {box} is not the full width {ignite.BarWidth:0.#}");
            Assert.Contains("확정", ocr);
            Assert.DoesNotContain("최대", ocr);
            Evidence(ctx, "todo540_true_damage.png", igniteBitmap, ignite.Bounds);
        }

        // Negative control: Maokai E with its brush condition left open is a range, 최대 and 최소.
        session.Input.Keys(K3);
        var (rangeBitmap, range) = WaitForCard(ctx, overlay,
            (b, c) => ChipIs(b, c, 0, OverlayPalette.SlotE) && c.ChipEmpty(b, 1), "the Maokai E combo's card");
        using (rangeBitmap)
        {
            var box = range.MaxBox(rangeBitmap) ?? throw new InvalidOperationException("no damage box (MaxBoxBorder) on the Maokai E card");
            var minBox = range.MinBox(rangeBitmap);
            string ocr = Squash(Screen.Ocr(rangeBitmap, box));
            ctx.Note($"Maokai E card: max box {box}, min box {minBox?.ToString() ?? "none"}, OCR '{ocr}'");
            Assert.NotNull(minBox);
            Assert.Contains("최대", ocr);
            Assert.DoesNotContain("확정", ocr);
            Evidence(ctx, "todo540_range.png", rangeBitmap, range.Bounds);
        }

        // (b) the editor: Maokai E offers '☐ 최대'; ticking it gives the engine's met-condition value.
        OpenComboEditorFor(session, champion, "마오카이", rangeName);
        ClickInView(session, CardButton(session, rangeName, "✎"));
        var home = session.Home;
        var previewRange = WaitShown(home, "PreviewRange");
        WaitUntil(() => NameOf(previewRange)?.Contains('~') == true, $"PreviewRange a range (is '{NameOf(previewRange)}')");
        string before = NameOf(previewRange)!;
        var badge = WaitFor(() => home.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("☐ 최대"))),
            "the '☐ 최대' badge on the E chip");
        ClickInView(session, badge);
        WaitFor(() => home.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("☑ 최대"))), "the badge ticked ('☑ 최대')");
        WaitUntil(() => NameOf(previewRange) is { } t && t != before && !t.Contains('~'), $"PreviewRange one number (is '{NameOf(previewRange)}')");
        string after = NameOf(previewRange)!;

        var unticked = Numbers(before);
        double ticked = Numbers(after).Single();
        using var oracle = new EngineOracle(ctx.App.AppDir, session.ConfigPath, ctx.ScratchDir("oracle"));
        var open = oracle.Preview(champion, Graph(Skill("E"))) ?? throw new InvalidOperationException("the oracle computed nothing for Maokai E");
        var met = oracle.Preview(champion, Graph(Skill("E") with { UserConditionMet = true }))
                  ?? throw new InvalidOperationException("the oracle computed nothing for Maokai E with the condition met");
        ctx.Note($"PreviewRange '{before}' -> '{after}'; oracle open {open.Min:0.##}~{open.Max:0.##}, met {met.Resolved:0.##}");
        Assert.Equal(2, unticked.Length);
        Assert.True(ticked > unticked[0], $"ticking 최대 did not raise the preview: '{before}' -> '{after}'");
        Assert.True(Math.Abs(unticked[0] - open.Min) <= 0.5, $"the unticked floor {unticked[0]} is not the engine's {open.Min:0.##}");
        Assert.True(Math.Abs(ticked - met.Resolved) <= 0.5, $"the ticked preview {ticked} is not the engine's {met.Resolved:0.##} with UserConditionMet=true");
        EvidenceWindow(ctx, "todo540_editor.png", home.Properties.NativeWindowHandle.Value);
    });

    // ── TODO:47 ─────────────────────────────────────────────────────────────────────

    [UiFact]
    public void DeadPortraitIsGrayscaleSilhouette() => UiTest.Run(_app, nameof(DeadPortraitIsGrayscaleSilhouette), new[] { "TODO:47", "R15" }, ctx =>
    {
        var seed = Seed("Annie", new SeedCombo(NewId(), "콤보", Graph(Skill("Q")), null));
        HideAllBut(seed, "enemyPortraitRow");
        var session = ctx.Start(seed);
        var overlay = ShowPreview(ctx, session);
        var row = new PortraitRow(overlay);

        // Waits for the portrait downloads: until then a cell is a flat initial-letter disc.
        double jinxSat = 0, jinxStd = 0, dariusSat = 0;
        var bitmap = WaitForCapture(ctx, overlay, b =>
        {
            (jinxSat, jinxStd) = row.Measure(b, PortraitRow.Jinx);
            (dariusSat, _) = row.Measure(b, PortraitRow.Darius);
            return dariusSat >= Screen.ColourMinSaturation && jinxStd >= Screen.GrayMinLumaStdDev;
        }, "the enemy portraits loaded", TimeSpan.FromSeconds(60));
        using (bitmap)
        {
            ctx.Note($"Jinx (dead): saturation {jinxSat:0.000}, luma std {jinxStd:0.000}; Darius (alive): saturation {dariusSat:0.000}");
            Assert.True(jinxSat <= Screen.GrayMaxSaturation, $"the dead Jinx portrait is not grayscale: mean saturation {jinxSat:0.000}");
            Assert.True(jinxStd >= Screen.GrayMinLumaStdDev, $"the dead Jinx portrait is a flat fill, not a silhouette: luma std {jinxStd:0.000}");
            Assert.True(dariusSat >= Screen.ColourMinSaturation, $"the living Darius portrait is not in colour: mean saturation {dariusSat:0.000}");
            Evidence(ctx, "todo47_portrait.png", bitmap, row.Bounds);
        }
    });

    // ── TODO:48 ─────────────────────────────────────────────────────────────────────

    [UiFact]
    public void TimerFormatToggle() => UiTest.Run(_app, nameof(TimerFormatToggle), new[] { "TODO:48", "R15" }, ctx =>
    {
        var seed = Seed("Annie", new SeedCombo(NewId(), "콤보", Graph(Skill("Q")), null));
        // Twice the other tests' size: the minimap labels are 11 DIP text, too thin for OCR at 1.5.
        seed["overlay.scale"] = UserScaleFor(PrimaryDipHeight(), 2 * TargetHudScale);
        HideAllBut(seed, "enemyPortraitRow", "inhibitorTimers");
        var session = ctx.Start(seed);
        var overlay = ShowPreview(ctx, session);
        var timers = new TimerLabels(overlay);

        ReadTimers(ctx, overlay, timers, new[] { "18", "252", "125" }, "todo48_timers_off.png");

        // Settings > 오버레이 > 타이머 분:초 표기, switched through its UIA Toggle; no restart.
        OpenNav(session, "NavSettings");
        var pill = WaitShown(session.Home, "PillOverlay");
        if (!pill.Patterns.SelectionItem.Pattern.IsSelected.Value) session.Input.Click(pill);
        WaitUntil(() => pill.Patterns.SelectionItem.Pattern.IsSelected.Value, "PillOverlay selected");
        var toggle = WaitShown(session.Home, "TimerMmSsToggle").Patterns.Toggle.Pattern;
        Assert.Equal(ToggleState.Off, toggle.ToggleState.Value);
        toggle.Toggle();
        WaitUntil(() => toggle.ToggleState.Value == ToggleState.On, "TimerMmSsToggle on");

        ReadTimers(ctx, overlay, timers, new[] { "0:18", "4:12", "2:05" }, "todo48_timers_on.png");
    });

    /// <summary>Waits until OCR reads <paramref name="expected"/> (Jinx's respawn, the two sample
    /// minimap timers), then asserts each and keeps the crops as <paramref name="evidence"/>.</summary>
    private static void ReadTimers(UiContext ctx, PreviewOverlay overlay, TimerLabels timers, string[] expected, string evidence)
    {
        string[] read = Array.Empty<string>();
        var bitmap = WaitForCapture(ctx, overlay, b =>
        {
            read = timers.Read(b);
            return read.SequenceEqual(expected);
        }, $"the timers to read {string.Join(" / ", expected)}", TimeSpan.FromSeconds(20),
        last =>
        {
            ctx.Note($"timers last read {string.Join(" / ", timers.Read(last))}");
            var masks = timers.Masks(last);
            for (int i = 0; i < masks.Length; i++)
                using (masks[i])
                {
                    string path = Path.Combine(ctx.App.RunDir, $"{ctx.TestName}-run{ctx.Run}-mask{i + 1}-{ctx.Screenshots.Count + 1}.png");
                    masks[i].Save(path, ImageFormat.Png);
                    ctx.AddScreenshot(path);
                }
        });
        using (bitmap)
        {
            ctx.Note($"timers read {string.Join(" / ", read)}");
            for (int i = 0; i < expected.Length; i++)
                Assert.True(expected[i] == read[i], $"{TimerLabels.Names[i]} reads '{read[i]}', expected '{expected[i]}'");
            Evidence(ctx, evidence, bitmap, timers.Crops);
        }
    }

    // ── seeding ─────────────────────────────────────────────────────────────────────

    /// <summary>The HUD scale the tests draw at: large enough for OCR of the small HUD text.</summary>
    internal const double TargetHudScale = 1.5;

    private sealed record SeedCombo(string Id, string Name, ComboGraph Graph, string? Hotkey);

    private static ComboNode Skill(string slot, int index = 0) => new(
        Id: $"{slot}_{index}", NodeType: ComboNodeType.Skill, Name: slot,
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    private static ComboNode Aa() => new(
        Id: "AA", NodeType: ComboNodeType.Aa, Name: "Auto Attack",
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    /// <summary>"Ignite" or "Flash", as the editor's palette builds them.</summary>
    private static ComboNode Summoner(string name) => new(
        Id: name, NodeType: ComboNodeType.Summoner, Name: name,
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.True,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    private static ComboGraph Graph(params ComboNode[] nodes) => new(nodes, Array.Empty<ComboEdge>());

    /// <summary>Seed overrides: <paramref name="combos"/> of <paramref name="champion"/> in this order,
    /// their chords, and <c>overlay.scale</c> chosen so the HUD draws at <see cref="TargetHudScale"/>
    /// on the primary monitor.</summary>
    /// <remarks>AI-NOTE: the preview publishes the first saved combo of its champion by itself
    /// (HomeWindow.PublishOverlaySamples), and pressing a shown combo's chord toggles its card off,
    /// so each test seeds a first combo without a chord and presses the chord of a later one.</remarks>
    private static Dictionary<string, object?> Seed(string champion, params SeedCombo[] combos)
    {
        var graphOptions = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var seed = new Dictionary<string, object?> { ["overlay.scale"] = UserScaleFor(PrimaryDipHeight()) };
        foreach (var combo in combos)
        {
            string graphJson = JsonSerializer.Serialize(combo.Graph, graphOptions);
            seed["combos.saved." + combo.Id] = JsonSerializer.Serialize(new SavedCombo(combo.Id, champion, combo.Name, graphJson));
            if (combo.Hotkey is { } hotkey)
                seed["hotkeys.comboSlots." + ComboEditor.ComposeSlotKey(hotkey, champion)] = combo.Id;
        }
        return seed;
    }

    /// <summary>The HUD elements a preview draws on its own; every one not in <paramref name="keep"/>
    /// is switched off so nothing else (or its size bar) lands on the element under test.</summary>
    /// <remarks>AI-NOTE: the enemy ward panel cannot be switched off from a seed (its key is not in
    /// ConfigSchema); it stays at the left edge, away from every checked area.</remarks>
    private static readonly string[] PreviewElements =
    {
        "comboResult", "enemyPortraitRow", "skillOverlay", "statusCard", "threatCard", "nextItemCard",
        "globalGold", "itemAlert", "enemyItemAlert", "notification", "enemyJunglerSpotted", "inhibitorTimers",
    };

    internal static void HideAllBut(Dictionary<string, object?> seed, params string[] keep)
    {
        foreach (var key in PreviewElements)
            seed[$"overlay.items.{key}.enabled"] = keep.Contains(key);
    }

    /// <summary>The primary monitor's height in DIPs (the overlay covers that monitor).</summary>
    internal static double PrimaryDipHeight() =>
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) * 96.0 / Math.Max(96u, NativeMethods.GetDpiForSystem());

    internal static double UserScaleFor(double surfaceDipHeight, double hudScale = TargetHudScale) =>
        Math.Round(Math.Clamp(hudScale / OverlayLayout.AutoFit(surfaceDipHeight), OverlayLayout.MinUserScale, OverlayLayout.MaxUserScale), 3);

    // ── the preview overlay ─────────────────────────────────────────────────────────

    /// <summary>The overlay window of a session in preview and the HUD scale it draws at.</summary>
    internal sealed record PreviewOverlay(IntPtr Hwnd, double Dpi, double Scale, double WidthDip, double HeightDip)
    {
        /// <summary>A DIP length in capture pixels.</summary>
        public int Px(double dip) => (int)Math.Round(dip * Dpi);

        /// <summary>Capture pixels per authored HUD DIP (HUD scale × DPI scale).</summary>
        public double U => Scale * Dpi;
    }

    /// <summary>Clicks PreviewToggle and waits for the overlay window (the session's other visible
    /// top-level window). Its HUD scale is <c>OverlayHost.ReadScale</c> on the window's DIP height.</summary>
    internal static PreviewOverlay ShowPreview(UiContext ctx, FovaSession session)
    {
        var toggle = WaitShown(session.Home, "PreviewToggle");
        Assert.Equal("오버레이 미리보기", NameOf(toggle));
        session.Input.Click(toggle);
        var homeHwnd = session.Home.Properties.NativeWindowHandle.Value;
        IntPtr hwnd = IntPtr.Zero;
        WaitUntil(() =>
        {
            hwnd = session.Windows().Where(w => w != homeHwnd)
                .Select(w => (w, ok: NativeMethods.GetWindowRect(w, out var r), area: (long)r.Width * r.Height))
                .Where(t => t.ok).OrderByDescending(t => t.area).Select(t => t.w).FirstOrDefault();
            return hwnd != IntPtr.Zero;
        }, "the overlay window", TimeSpan.FromSeconds(15));

        NativeMethods.GetWindowRect(hwnd, out var rect);
        double dpi = Screen.Dpi(hwnd);
        double userScale = ConfigSeed.Get(session.Config(), "overlay.scale")!.GetValue<double>();
        double heightDip = rect.Height / dpi;
        double scale = Math.Clamp(userScale, OverlayLayout.MinUserScale, OverlayLayout.MaxUserScale) * OverlayLayout.AutoFit(heightDip);
        ctx.Note($"overlay {rect.Width}x{rect.Height} px, DPI scale {dpi:0.##}, HUD scale {scale:0.###}");
        return new PreviewOverlay(hwnd, dpi, scale, rect.Width / dpi, heightDip);
    }

    /// <summary>Captures the overlay until <paramref name="ready"/> holds; returns that capture. On a
    /// timeout the last capture is kept next to REPORT.md.</summary>
    internal static Bitmap WaitForCapture(UiContext ctx, PreviewOverlay overlay, Func<Bitmap, bool> ready, string what,
        TimeSpan? timeout = null, Action<Bitmap>? onTimeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var bitmap = Screen.Capture(overlay.Hwnd);
            bool ok;
            try { ok = ready(bitmap); } catch (InvalidOperationException) { ok = false; }
            if (ok) return bitmap;
            if (DateTime.UtcNow > deadline)
            {
                string path = Path.Combine(ctx.App.RunDir, $"{ctx.TestName}-run{ctx.Run}-timeout-{ctx.Screenshots.Count + 1}.png");
                bitmap.Save(path, ImageFormat.Png);
                ctx.AddScreenshot(path);
                try { onTimeout?.Invoke(bitmap); } finally { bitmap.Dispose(); }
                throw new TimeoutException($"Timed out waiting for: {what}");
            }
            bitmap.Dispose();
            Thread.Sleep(250);
        }
    }

    internal static (Bitmap Bitmap, ComboCard Card) WaitForCard(UiContext ctx, PreviewOverlay overlay,
        Func<Bitmap, ComboCard, bool> ready, string what, TimeSpan? timeout = null)
    {
        ComboCard? card = null;
        var bitmap = WaitForCapture(ctx, overlay, b => (card = ComboCard.Find(b, overlay)) is not null && ready(b, card), what, timeout);
        return (bitmap, card!);
    }

    // ── pixels ──────────────────────────────────────────────────────────────────────

    private static Color C(uint argb) => OverlayPalette.Opaque(argb);

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static int Count(Bitmap bitmap, Rectangle rect, Color colour, int tolerance = ColourTolerance)
    {
        rect.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        int n = 0;
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
                if (Screen.Distance(bitmap.GetPixel(x, y), colour) <= tolerance) n++;
        return n;
    }

    private static double Fraction(Bitmap bitmap, Rectangle rect, Color colour, int tolerance = ColourTolerance) =>
        rect.Width * rect.Height == 0 ? 0 : Count(bitmap, rect, colour, tolerance) / (double)(rect.Width * rect.Height);

    private static int GoldCount(Bitmap bitmap, Rectangle rect) => Count(bitmap, rect, C(OverlayPalette.AccentGold));

    private static double GoldFraction(Bitmap bitmap, Rectangle rect) => Fraction(bitmap, rect, C(OverlayPalette.AccentGold));

    /// <summary>A slot-coloured chip in cell <paramref name="index"/>: its colour covers at least the
    /// frame the icon leaves around itself.</summary>
    private static bool ChipIs(Bitmap bitmap, ComboCard card, int index, uint slotColour) =>
        Fraction(bitmap, card.Chip(index), C(slotColour)) >= 0.08;

    private static int[] CornerDistances(Bitmap bitmap, Rectangle r, Color background) => new[]
    {
        new Point(r.Left, r.Top), new Point(r.Right - 1, r.Top), new Point(r.Left, r.Bottom - 1), new Point(r.Right - 1, r.Bottom - 1),
    }.Select(p => Screen.Distance(bitmap.GetPixel(p.X, p.Y), background)).ToArray();

    private static double[] Numbers(string text) =>
        Regex.Matches(text, @"\d[\d,]*(\.\d+)?").Select(m => double.Parse(m.Value.Replace(",", ""), CultureInfo.InvariantCulture)).ToArray();

    internal static string Squash(string ocr) => ocr.Replace(" ", "").Replace("\n", "").Replace("\r", "");

    // ── icons ───────────────────────────────────────────────────────────────────────

    /// <summary>The newest complete Data Dragon version folder of the app copy, the one the app
    /// draws from (<c>DDragonData.CurrentDir</c>).</summary>
    private static string DDragonDir(string appDir)
    {
        string root = Path.Combine(appDir, "data", "ddragon");
        return Directory.GetDirectories(root)
            .Where(d => DDragonData.ParseVersion(Path.GetFileName(d)) is not null && DDragonData.IsComplete(d))
            .OrderByDescending(d => DDragonData.ParseVersion(Path.GetFileName(d)))
            .FirstOrDefault() ?? throw new InvalidOperationException($"No complete Data Dragon version under {root}");
    }

    /// <summary>The champion's P/Q/W/E/R icon files, from its Data Dragon champion JSON (as
    /// <c>AbilityIconProvider.ResolveSlotFullsAsync</c> reads it).</summary>
    private static Dictionary<string, string> SlotIcons(string appDir, string champion)
    {
        string dir = DDragonDir(appDir);
        string json = Path.Combine(dir, "champion", champion + ".json");
        WaitForFile(json);
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var data = doc.RootElement.GetProperty("data").GetProperty(champion);
        var icons = new Dictionary<string, string>
        {
            ["P"] = Path.Combine(dir, "img", "passive", data.GetProperty("passive").GetProperty("image").GetProperty("full").GetString()!),
        };
        string[] slots = { "Q", "W", "E", "R" };
        int i = 0;
        foreach (var spell in data.GetProperty("spells").EnumerateArray().Take(4))
            icons[slots[i++]] = Path.Combine(dir, "img", "spell", spell.GetProperty("image").GetProperty("full").GetString()!);
        return icons;
    }

    private static string SpellIcon(string appDir, string spellId) => Path.Combine(DDragonDir(appDir), "img", "spell", spellId + ".png");

    private static void WaitForFile(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!(File.Exists(path) && new FileInfo(path).Length > 0))
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"icon not cached: {path}");
            Thread.Sleep(250);
        }
    }

    private static Dictionary<string, double> IconDiffs(Bitmap bitmap, Rectangle crop, Dictionary<string, string> icons) =>
        icons.ToDictionary(p => p.Key, p => Screen.MeanAbsDiff(bitmap, crop, p.Value));

    // ── evidence ────────────────────────────────────────────────────────────────────

    /// <summary>docs/reports/ui_tests/lolF02, where the passing tests leave their named crops.</summary>
    private static string EvidenceDir(UiContext ctx) => Path.Combine(ctx.App.RepoRoot, "docs", "reports", "ui_tests", "lolF02");

    /// <summary>Saves the crops (stacked top to bottom) next to REPORT.md and as
    /// <paramref name="fileName"/> in <see cref="EvidenceDir"/>. Called after a test's assertions.</summary>
    private static void Evidence(UiContext ctx, string fileName, Bitmap bitmap, params Rectangle[] crops)
    {
        var clipped = crops.Select(r => Rectangle.Intersect(r, new Rectangle(0, 0, bitmap.Width, bitmap.Height))).ToArray();
        using var sheet = new Bitmap(clipped.Max(r => r.Width), clipped.Sum(r => r.Height), PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(sheet))
        {
            int y = 0;
            foreach (var r in clipped)
            {
                g.DrawImage(bitmap, new Rectangle(0, y, r.Width, r.Height), r, GraphicsUnit.Pixel);
                y += r.Height;
            }
        }
        Keep(ctx, fileName, sheet);
    }

    private static void EvidenceWindow(UiContext ctx, string fileName, IntPtr hwnd)
    {
        using var bitmap = Screen.Capture(hwnd);
        Keep(ctx, fileName, bitmap);
    }

    private static void Keep(UiContext ctx, string fileName, Bitmap image)
    {
        string run = Path.Combine(ctx.App.RunDir, $"{ctx.TestName}-run{ctx.Run}-{fileName}");
        Directory.CreateDirectory(ctx.App.RunDir);
        image.Save(run, ImageFormat.Png);
        ctx.AddScreenshot(run);
        Directory.CreateDirectory(EvidenceDir(ctx));
        File.Copy(run, Path.Combine(EvidenceDir(ctx), fileName), overwrite: true);
    }

    // ── the combo card ──────────────────────────────────────────────────────────────

    /// <summary>The full combo card (OverlayHost.BuildComboCard) in a capture, top-centre with the
    /// portrait row switched off. Its left/right/top come from the layout; its bottom (the card's
    /// height depends on an optional kill-angle line) is the PanelBorder found below the gauge in
    /// the left padding column.</summary>
    internal sealed class ComboCard
    {
        private readonly PreviewOverlay _o;

        private ComboCard(PreviewOverlay o, double x0, double y0, double bottom)
        {
            _o = o;
            X0 = x0;
            Y0 = y0;
            Bottom = bottom;
        }

        public double X0 { get; }
        public double Y0 { get; }
        public double Bottom { get; }
        private double U => _o.U;

        public double Right => X0 + OverlayLayout.CardW * U;
        public double InnerX => X0 + OverlayLayout.CardPadX * U;
        public double InnerR => Right - OverlayLayout.CardPadX * U;
        public double BarWidth => (OverlayLayout.CardW - 2 * OverlayLayout.CardPadX) * U;
        private double ChipTop => Bottom - (OverlayLayout.CardPadY + OverlayLayout.ChipSize) * U;
        private double ChipSize => OverlayLayout.ChipSize * U;
        public Rectangle Bounds => Rectangle.FromLTRB(Floor(X0), Floor(Y0), Ceil(Right), Ceil(Bottom));

        /// <summary>A few pixels of slack around a computed rectangle.</summary>
        private int Slack => (int)Math.Ceiling(2 * U);

        /// <summary>Px per glyph a knob mark must add (a 10-DIP glyph has far more).</summary>
        public int GlyphPixels => (int)Math.Ceiling(4 * U * U);

        public static ComboCard? Find(Bitmap bitmap, PreviewOverlay o)
        {
            double u = o.U;
            double x0 = (o.WidthDip / 2) * o.Dpi - OverlayLayout.CardW / 2 * u;
            double y0 = OverlayLayout.StackTop * o.HeightDip * o.Dpi;
            int column = (int)Math.Round(x0 + OverlayLayout.CardPadX / 2 * u);
            var border = C(OverlayPalette.PanelBorder);
            bool top = Enumerable.Range((int)y0 - 2, 5).Any(y => Screen.Distance(bitmap.GetPixel(column, y), border) <= 30);
            if (!top) return null;
            int from = (int)(y0 + (OverlayLayout.CardPadY + OverlayLayout.HeaderH + OverlayLayout.M1 + OverlayLayout.HpBarH) * u);
            int to = Math.Min(bitmap.Height - 1, (int)(y0 + 600 * u));
            for (int y = from; y < to; y++)
                if (Screen.Distance(bitmap.GetPixel(column, y), border) <= 30)
                    return new ComboCard(o, x0, y0, y + o.Dpi);
            return null;
        }

        /// <summary>The card background beside the chip row (the left padding column).</summary>
        public Color Background(Bitmap bitmap) =>
            bitmap.GetPixel((int)Math.Round(X0 + OverlayLayout.CardPadX / 2 * U), (int)Math.Round(ChipTop + ChipSize / 2));

        /// <summary>Cell <paramref name="index"/> of the chip row, as laid out.</summary>
        public Rectangle Chip(int index) => Rectangle.FromLTRB(
            (int)Math.Round(InnerX + index * (OverlayLayout.ChipSize + OverlayLayout.ChipGap) * U), (int)Math.Round(ChipTop),
            (int)Math.Round(InnerX + index * (OverlayLayout.ChipSize + OverlayLayout.ChipGap) * U + ChipSize), (int)Math.Round(ChipTop + ChipSize));

        public Rectangle ChipRow(int count) => Rectangle.FromLTRB(Chip(0).Left - Slack, Chip(0).Top - Slack, Chip(count - 1).Right + Slack, Chip(0).Bottom + Slack);

        /// <summary>The drawn chip: the bounding box of its slot colour around cell <paramref name="index"/>.</summary>
        public Rectangle ChipBox(Bitmap bitmap, int index, uint slotColour)
        {
            var cell = Chip(index);
            cell.Inflate(Slack, Slack);
            return Screen.Bbox(bitmap, C(slotColour), 60, cell)
                ?? throw new InvalidOperationException($"no chip of colour #{slotColour & 0xFFFFFF:X6} in cell {index} {cell}");
        }

        /// <summary>The icon inside chip <paramref name="index"/>, one pixel in from its inset edge.</summary>
        public Rectangle IconRect(Bitmap bitmap, int index, uint slotColour)
        {
            var chip = ChipBox(bitmap, index, slotColour);
            int inset = (int)Math.Ceiling(OverlayLayout.ChipInset * U) + 1;
            chip.Inflate(-inset, -inset);
            return chip;
        }

        public bool ChipEmpty(Bitmap bitmap, int index) => Fraction(bitmap, Chip(index), Background(bitmap), 20) >= 0.9;

        /// <summary>The strip a knob's gold frame (OverlayHost.DrawSequenceChip) shows left of a chip.</summary>
        public Rectangle LeftFrame(Rectangle chip)
        {
            int b = Math.Max(1, (int)Math.Floor(OverlayLayout.KnobFrame * U));
            return new Rectangle(chip.Left - b, chip.Top, b, chip.Height);
        }

        /// <summary>Where the ▲ sits: the chip's top-right corner, inside the frame.</summary>
        public Rectangle TriangleArea(Rectangle chip)
        {
            int b = (int)Math.Ceiling(OverlayLayout.KnobFrame * U);
            return Rectangle.FromLTRB(chip.Right - (int)Math.Ceiling(OverlayLayout.TriangleFromRight * U), chip.Top + b, chip.Right - b, chip.Top + chip.Height / 2);
        }

        /// <summary>Where the ×n sits: the chip's bottom-right corner.</summary>
        public Rectangle CountArea(Rectangle chip) =>
            Rectangle.FromLTRB(chip.Right - (int)Math.Ceiling(OverlayLayout.CountFromRight * U), chip.Bottom - (int)Math.Ceiling(OverlayLayout.CountFromBottom * U), chip.Right, chip.Bottom);

        /// <summary>The gold fill bar under a chip: its length in pixels on the bar's middle row, and its start.</summary>
        public (int Width, int Start) FillBar(Bitmap bitmap, Rectangle chip)
        {
            int y = (int)Math.Round(chip.Bottom + (OverlayLayout.FillBarGap + OverlayLayout.FillBarH / 2) * U);
            var gold = C(OverlayPalette.AccentGold);
            var xs = Enumerable.Range(chip.Left - Slack, chip.Width + 2 * Slack).Where(x => Screen.Distance(bitmap.GetPixel(x, y), gold) <= ColourTolerance).ToList();
            return xs.Count == 0 ? (0, -1) : (xs.Count, xs[0]);
        }

        /// <summary>The 가정 N chip, drawn where chip <paramref name="index"/> would be.</summary>
        public Rectangle AssumptionChip(int index)
        {
            var cell = Chip(index);
            return new Rectangle(cell.Left, cell.Top, (int)Math.Round(OverlayLayout.AssumptionChipW * U), cell.Height);
        }

        /// <summary>The frame of the kill-threshold gauge (HpBarBorder), searched in the gauge's rows.</summary>
        public Rectangle? HpBarBox(Bitmap bitmap)
        {
            double top = Y0 + (OverlayLayout.CardPadY + OverlayLayout.HeaderH + OverlayLayout.M1 - 1) * U;
            var band = Rectangle.FromLTRB(Floor(X0), Floor(top) - 1, Ceil(Right), Ceil(top + (OverlayLayout.HpBarH + 2) * U) + 1);
            return Screen.Bbox(bitmap, C(OverlayPalette.HpBarBorder), 24, band);
        }

        /// <summary>The big target portrait's gold ring, searched right of the 콤보 tag.</summary>
        public Rectangle? PortraitBox(Bitmap bitmap)
        {
            double bigPX = InnerR - OverlayLayout.BigPortrait * U;
            double tagRight = bigPX - OverlayLayout.TagGap * U;
            var area = Rectangle.FromLTRB(Ceil(tagRight) + 1, Floor(Y0), Ceil(Right), Ceil(Y0 + (OverlayLayout.CardPadY + OverlayLayout.BigPortrait + 4) * U));
            return Screen.Bbox(bitmap, C(OverlayPalette.AccentGold), ColourTolerance, area);
        }

        /// <summary>The damage boxes' band: below the gauge (and its note), above the chip row.</summary>
        private Rectangle BoxBand => Rectangle.FromLTRB(Floor(X0), Floor(Y0 + (OverlayLayout.CardPadY + OverlayLayout.HeaderH + OverlayLayout.M1 + OverlayLayout.HpBarH) * U),
            Ceil(Right), Floor(ChipTop));

        public Rectangle? MaxBox(Bitmap bitmap) => Screen.Bbox(bitmap, C(OverlayPalette.MaxBoxBorder), 6, BoxBand);

        public Rectangle? MinBox(Bitmap bitmap) => Screen.Bbox(bitmap, C(OverlayPalette.MinBoxBorder), 6, BoxBand);

        /// <summary>The optional text lines (kill-angle, execute kill-line) between the damage boxes' bottom
        /// and the chip row, inside the padding; null when no damage box is found.</summary>
        public Rectangle? LineBand(Bitmap bitmap) => MaxBox(bitmap) is { } box && Floor(ChipTop) - Slack > box.Bottom + Slack
            ? Rectangle.FromLTRB(Floor(InnerX), box.Bottom + Slack, Ceil(InnerR), Floor(ChipTop) - Slack)
            : null;

        /// <summary>The rightmost pixel of the combo name: in the chip row, right of the first
        /// <paramref name="chips"/> chips, a pixel nearer NameDim than PanelBorder, the card
        /// background or the black around the card.</summary>
        public int? NameRight(Bitmap bitmap, int chips)
        {
            var name = C(OverlayPalette.NameDim);
            var others = new[] { C(OverlayPalette.PanelBorder), Background(bitmap), Color.Black };
            int left = Chip(chips - 1).Right + Slack, right = Math.Min(bitmap.Width, Ceil(Right + 80 * U));
            int? found = null;
            for (int y = Chip(0).Top; y < Chip(0).Bottom; y++)
                for (int x = left; x < right; x++)
                {
                    var p = bitmap.GetPixel(x, y);
                    int d = Screen.Distance(p, name);
                    if (others.All(o => d < Screen.Distance(p, o)) && (found is null || x > found)) found = x;
                }
            return found;
        }

        private static int Floor(double v) => (int)Math.Floor(v);
        private static int Ceil(double v) => (int)Math.Ceiling(v);
    }

    // ── the portrait row ────────────────────────────────────────────────────────────

    /// <summary>The enemy portrait row (OverlayHost.BuildPortraitRow), top-centre, five sample
    /// enemies in <c>PreviewGameState</c> order.</summary>
    private sealed class PortraitRow
    {
        public const int Darius = 0, Jinx = 3;
        private const int Enemies = 5;
        private readonly PreviewOverlay _o;

        public PortraitRow(PreviewOverlay o) => _o = o;

        private double U => _o.U;
        private double RowW => Enemies * OverlayLayout.CellW + (Enemies - 1) * OverlayLayout.CellGap + 2 * OverlayLayout.RowPadH;
        private double X0 => _o.WidthDip / 2 * _o.Dpi - RowW / 2 * U;
        private double Y0 => OverlayLayout.StackTop * _o.HeightDip * _o.Dpi;

        public Rectangle Bounds => Rectangle.FromLTRB((int)X0, (int)Y0, (int)Math.Ceiling(X0 + RowW * U),
            (int)Math.Ceiling(Y0 + (2 * OverlayLayout.RowPadV + OverlayLayout.Portrait) * U));

        public Point Center(int index) => new(
            (int)Math.Round(X0 + (OverlayLayout.RowPadH + index * (OverlayLayout.CellW + OverlayLayout.CellGap) + OverlayLayout.CellW / 2) * U),
            (int)Math.Round(Y0 + (OverlayLayout.RowPadV + OverlayLayout.Portrait / 2) * U));

        /// <summary>Radius of the portrait icon inside its ring.</summary>
        public double InnerRadius => (OverlayLayout.Portrait / 2 - OverlayLayout.PortraitInset) * U;

        /// <summary>Mean saturation and luma std dev of a portrait's icon.</summary>
        /// <remarks>AI-NOTE: a dead enemy's countdown digits are drawn over the centre of its icon,
        /// so the centre disc under them (the largest countdown, "0:00" at 13 DIP) is left out; the
        /// outer two DIP are left out for the ring's anti-aliased edge.</remarks>
        public (double Saturation, double LumaStd) Measure(Bitmap bitmap, int index)
        {
            double outer = InnerRadius - 2 * U, hole = 17 * U;
            return (Screen.MeanSaturation(bitmap, Center(index), outer, hole), Screen.LumaStdDev(bitmap, Center(index), outer, hole));
        }

        public Rectangle Icon(int index)
        {
            var c = Center(index);
            int r = (int)Math.Ceiling(InnerRadius);
            return new Rectangle(c.X - r, c.Y - r, 2 * r, 2 * r);
        }
    }

    // ── the timers ──────────────────────────────────────────────────────────────────

    /// <summary>The three preview timers: dead Jinx's respawn countdown on her portrait and the two
    /// sample minimap labels (OverlayHost.BuildPreviewAlerts), OCR'd from their own colour only.</summary>
    private sealed class TimerLabels
    {
        public static readonly string[] Names = { "Jinx's respawn countdown", "the 252 s minimap timer", "the 125 s minimap timer" };
        private readonly PreviewOverlay _o;
        private readonly PortraitRow _row;

        public TimerLabels(PreviewOverlay o)
        {
            _o = o;
            _row = new PortraitRow(o);
        }

        private (Rectangle Crop, uint Colour)[] Labels
        {
            get
            {
                var mm = MinimapCalibrator.Compute(_o.WidthDip, _o.HeightDip, null).Rect;
                double f = OverlayLayout.MinimapTimerFont * _o.Scale;
                Rectangle Around(double xFrac, double yFrac)
                {
                    double cx = (mm.X + xFrac * mm.Width) * _o.Dpi, cy = (mm.Y + yFrac * mm.Height) * _o.Dpi;
                    return Rectangle.FromLTRB((int)(cx - 2.5 * f * _o.Dpi), (int)(cy - 1.2 * f * _o.Dpi), (int)(cx + 2.5 * f * _o.Dpi), (int)(cy + 1.2 * f * _o.Dpi));
                }
                return new[]
                {
                    (_row.Icon(PortraitRow.Jinx), OverlayPalette.RespawnText),
                    (Around(OverlayLayout.Timer252X, OverlayLayout.Timer252Y), OverlayPalette.AccentGold),
                    (Around(OverlayLayout.Timer125X, OverlayLayout.Timer125Y), OverlayLayout.Timer125Colour),
                };
            }
        }

        public Rectangle[] Crops => Labels.Select(l => l.Crop).ToArray();

        public string[] Read(Bitmap bitmap) => Labels.Select(l =>
        {
            using var mask = Mask(bitmap, l.Crop, C(l.Colour));
            return Squash(Screen.Ocr(mask, new Rectangle(0, 0, mask.Width, mask.Height), DigitsLanguage));
        }).ToArray();

        public Bitmap[] Masks(Bitmap bitmap) => Labels.Select(l => Mask(bitmap, l.Crop, C(l.Colour))).ToArray();

        /// <summary>AI-NOTE: Windows' Korean OCR returned nothing for a lone "4:12" that the English
        /// engine reads; the labels are digits and a colon, so English is used when installed.</summary>
        private static readonly string DigitsLanguage =
            Windows.Media.Ocr.OcrEngine.IsLanguageSupported(new Windows.Globalization.Language("en-US")) ? "en-US" : "ko";

        private const int MaskZoom = 2;

        /// <summary>The label's own pixels, black on white with a margin and ×<see cref="MaskZoom"/>,
        /// for OCR: the digits sit on a portrait or on whatever is under the overlay. A pixel counts
        /// when it is <paramref name="colour"/> at some brightness (the anti-aliased edge of a glyph
        /// over its dark shadow is a dimmer copy), within half of the colour's tint at that brightness.</summary>
        /// <remarks>AI-NOTE: the tint bound is what drops a dead portrait's light parts: that icon is
        /// drawn exactly gray (saturation 0), while RespawnText and the timer golds are tinted.</remarks>
        private static Bitmap Mask(Bitmap bitmap, Rectangle crop, Color colour)
        {
            crop.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            int margin = crop.Height;
            var mask = new Bitmap((crop.Width + 2 * margin) * MaskZoom, (crop.Height + 2 * margin) * MaskZoom, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(mask);
            g.Clear(Color.White);
            int sum = colour.R + colour.G + colour.B, mean = sum / 3;
            int tint = Math.Abs(colour.R - mean) + Math.Abs(colour.G - mean) + Math.Abs(colour.B - mean);
            for (int y = 0; y < crop.Height; y++)
                for (int x = 0; x < crop.Width; x++)
                {
                    var p = bitmap.GetPixel(crop.X + x, crop.Y + y);
                    double k = (p.R + p.G + p.B) / (double)sum;
                    if (k < 0.35 || k > 1.25) continue;
                    var dimmed = Color.FromArgb(Math.Min(255, (int)(colour.R * k)), Math.Min(255, (int)(colour.G * k)), Math.Min(255, (int)(colour.B * k)));
                    if (Screen.Distance(p, dimmed) <= Math.Max(4, tint * k / 2))
                        g.FillRectangle(Brushes.Black, (x + margin) * MaskZoom, (y + margin) * MaskZoom, MaskZoom, MaskZoom);
                }
            return mask;
        }
    }
}

/// <summary>
/// <c>src/Overlay.Client/Hud/OverlayHost.cs</c> layout numbers (authored DIPs, before the HUD
/// scale) the screen checks place their crops with. Like <see cref="OverlayPalette"/>, each cites
/// its source line, and <c>HarnessTests.OverlayLayout_CitedLinesStillHoldTheValues</c> fails when a
/// cited line no longer holds the quoted text.
/// </summary>
public static class OverlayLayout
{
    public const double ReferenceViewportHeight = 2160.0; // OverlayHost.cs:2384
    public const double MinUserScale = 0.3, MaxUserScale = 3.0; // OverlayHost.cs:2387

    /// <summary>The top-centre stack starts at this fraction of the surface height (BuildComboStack).</summary>
    public const double StackTop = 0.05;          // OverlayHost.cs:1245

    // BuildPortraitRow
    public const double Portrait = 56, CellW = 64, CellGap = 8; // OverlayHost.cs:1487
    public const double RowPadH = 10, RowPadV = 8;              // OverlayHost.cs:1488
    public const double PortraitInset = 2;                      // OverlayHost.cs:1490

    // BuildComboCard / DrawSequenceChip
    public const double CardW = 412, CardPadX = 14, CardPadY = 13;   // OverlayHost.cs:1628
    public const double HeaderH = 20, HpBarH = 22, ChipSize = 30;    // OverlayHost.cs:1629
    public const double M1 = 10;                                     // OverlayHost.cs:1630
    public const double BigPortrait = 52;                            // OverlayHost.cs:1657
    public const double TagGap = 8;                                  // OverlayHost.cs:1668
    public const double ChipGap = 6;                                 // OverlayHost.cs:1763
    public const double AssumptionChipW = 44;                        // OverlayHost.cs:1776
    public const double ChipInset = 1.5;                             // OverlayHost.cs:1818
    public const double KnobFrame = 1.5;                             // OverlayHost.cs:1827
    public const double TriangleFromRight = 8;                       // OverlayHost.cs:1861
    public const double FillBarGap = 1, FillBarH = 2;                // OverlayHost.cs:1864-1865
    public const double CountFromRight = 12, CountFromBottom = 11;   // OverlayHost.cs:1869

    // BuildPreviewAlerts: the two sample minimap timers
    public const double MinimapTimerFont = 11;                       // OverlayHost.cs:2618
    public const double Timer252X = 0.782, Timer252Y = 0.219;        // OverlayHost.cs:2619
    public const double Timer125X = 0.153, Timer125Y = 0.872;        // OverlayHost.cs:2620
    public const uint Timer125Colour = 0xFFF0C674;                   // OverlayHost.cs:2620

    /// <summary><c>OverlayHost.ReadScale</c>'s auto-fit factor (OverlayHost.cs:2409-2411).</summary>
    public static double AutoFit(double surfaceDipHeight) => Math.Clamp(surfaceDipHeight / ReferenceViewportHeight, 0.2, 2.0);

    /// <summary>Every cited value: its name, the source line and a verbatim piece of that line.</summary>
    public static IReadOnlyList<(string Name, int Line, string Text)> Citations { get; } = new[]
    {
        (nameof(ReferenceViewportHeight), 2384, "ReferenceViewportHeight = 2160.0"),
        (nameof(MinUserScale), 2387, "MinUserScale = 0.3, MaxUserScale = 3.0"),
        (nameof(AutoFit), 2409, "surfaceHeight / ReferenceViewportHeight"),
        (nameof(AutoFit), 2411, "Math.Clamp(autoFit, 0.2, 2.0)"),
        (nameof(StackTop), 1245, "y = h * 0.05"),
        (nameof(Portrait), 1487, "portrait = 56 * scale, cellW = 64 * scale, cellGap = 8 * scale"),
        (nameof(RowPadH), 1488, "padH = 10 * scale, padV = 8 * scale"),
        (nameof(PortraitInset), 1490, "inset = 2 * scale"),
        (nameof(CardW), 1628, "cardW = 412 * scale, padX = 14 * scale, padY = 13 * scale"),
        (nameof(HeaderH), 1629, "headerH = 20 * scale, hpBarH = 22 * scale, boxH = 48 * scale, chipSize = 30 * scale"),
        (nameof(M1), 1630, "m1 = 10 * scale"),
        (nameof(BigPortrait), 1657, "bigP = 52 * scale, bigPX = innerR - bigP"),
        (nameof(TagGap), 1668, "hpBarW = (bigPX - 8 * scale) - innerX"),
        (nameof(ChipGap), 1763, "chipGap = 6 * scale"),
        (nameof(AssumptionChipW), 1776, "gW = 44 * scale"),
        (nameof(ChipInset), 1818, "chipInset = 1.5 * scale"),
        (nameof(KnobFrame), 1827, "b = 1.5 * scale"),
        (nameof(TriangleFromRight), 1861, "chipX + chipSize - 8 * scale"),
        (nameof(FillBarGap), 1864, "cy + chipSize + 1 * scale"),
        (nameof(FillBarH), 1865, "2 * scale, AccentGold"),
        (nameof(CountFromRight), 1869, "chipX + chipSize - 12 * scale, cy + chipSize - 11 * scale"),
        (nameof(MinimapTimerFont), 2618, "f = 11 * scale"),
        (nameof(Timer252X), 2619, "mm.X + 0.782 * mm.Width, mm.Y + 0.219 * mm.Height, FormatTimerSeconds(252)"),
        (nameof(Timer125X), 2620, "mm.X + 0.153 * mm.Width, mm.Y + 0.872 * mm.Height, FormatTimerSeconds(125)"),
        (nameof(Timer125Colour), 2620, "0xFFF0C674"),
    };
}
