using System.Drawing;
using System.Net.Http;

namespace Overlay.Client.UiTests;

/// <summary>Harness pieces that need no Fova window: the skip gate, the seed, the input guard's
/// refusal, chord parsing, the listener and the palette citations. Plain facts.</summary>
public sealed class HarnessTests
{
    [Fact]
    public void UiFact_SkipsUnlessTheVariableIsOne()
    {
        Assert.Equal("UI test: set FOVA_UI_TESTS=1 to run (uses the real mouse and keyboard)", UiGate.SkipReason);
        string? expected = Environment.GetEnvironmentVariable("FOVA_UI_TESTS") == "1" ? null : UiGate.SkipReason;
        Assert.Equal(expected, new UiFactAttribute().Skip);
        Assert.Equal(expected, new UiTheoryAttribute().Skip);
    }

    [Fact]
    public void Seed_HoldsTheSpecDefaultsAndTheListenerEndpoint()
    {
        var seed = ConfigSeed.Build("http://127.0.0.1:5555", null);

        Assert.False(ConfigSeed.Get(seed, "ads.enabled")!.GetValue<bool>());
        Assert.False(ConfigSeed.Get(seed, "champSelect.enabled")!.GetValue<bool>());
        Assert.Equal("ko", ConfigSeed.Get(seed, "general.language")!.GetValue<string>());
        Assert.True(ConfigSeed.Get(seed, "general.firstRunGuideShown")!.GetValue<bool>());
        Assert.True(ConfigSeed.Get(seed, "telemetry.noticeShown")!.GetValue<bool>());
        Assert.Equal("http://127.0.0.1:5555", ConfigSeed.Get(seed, "telemetry.endpoint")!.GetValue<string>());
    }

    [Fact]
    public void Seed_OverridesSchemaKeysAndRejectsOthers()
    {
        var seed = ConfigSeed.Build("http://127.0.0.1:5555", new Dictionary<string, object?>
        {
            ["general.autoStartWithGame"] = true,
            ["ads.enabled"] = true,
        });
        Assert.True(ConfigSeed.Get(seed, "general.autoStartWithGame")!.GetValue<bool>());
        Assert.True(ConfigSeed.Get(seed, "ads.enabled")!.GetValue<bool>());

        Assert.True(ConfigSeed.IsSchemaKey("combos.saved.some-id"));
        Assert.False(ConfigSeed.IsSchemaKey("general.noSuchKey"));
        Assert.Throws<ArgumentException>(() =>
            ConfigSeed.Build("http://127.0.0.1:5555", new Dictionary<string, object?> { ["general.noSuchKey"] = 1 }));
    }

    [Fact]
    public void SafeInput_RefusesAPointOutsideTheSessionWindows()
    {
        // A session that was never started has no windows, so every point is outside them.
        var app = new AppCopyFixture();
        using var session = new FovaSession(app, Path.Combine(Path.GetTempPath(), "fova-uitest-" + Guid.NewGuid().ToString("N")));

        var refused = Assert.Throws<InvalidOperationException>(() => session.Input.Click(new Point(5, 7)));
        Assert.Equal("Refused to move the mouse outside Fova's windows: (5,7)", refused.Message);
        Assert.Throws<InvalidOperationException>(() => session.Input.Drag(new Point(1, 1), new Point(2, 2)));
        Assert.Throws<InvalidOperationException>(() => session.Input.Wheel(new Point(1, 1), 1));
    }

    [Fact]
    public void ParseChord_MapsFovaChordText()
    {
        var keys = SafeInput.ParseChord("CTRL+ALT+SHIFT+F9").Select(k => (int)k).ToArray();
        Assert.Equal(new[] { 0x11, 0x12, 0x10, 0x78 }, keys);
        Assert.Equal(new[] { 0x51 }, SafeInput.ParseChord("Q").Select(k => (int)k));
        Assert.Throws<ArgumentException>(() => SafeInput.ParseChord("CTRL+NOPE"));
    }

    [Fact]
    public async Task Listener_Answers204AndRecordsThePath()
    {
        using var listener = new LoopbackListener();
        using var http = new HttpClient();
        var response = await http.PostAsync(listener.Url + "/ping", new StringContent("{}"));

        Assert.Equal(204, (int)response.StatusCode);
        var request = Assert.Single(listener.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/ping", request.Path);
    }

    // AI-NOTE: these citation checks run without FOVA_UI_TESTS, so any edit that adds or removes lines
    // above a cited value in OverlayHost.cs turns the plain test run red (QA lolF02 S1). Move the line
    // numbers in OverlayPalette / OverlayLayout to follow; a changed value needs a real look.
    [Fact]
    public void OverlayPalette_CitedLinesStillHoldTheValues()
    {
        string root = AppCopyFixture.FindRepoRoot(AppContext.BaseDirectory);
        var lines = File.ReadAllLines(Path.Combine(root, "src", "Overlay.Client", "Hud", "OverlayHost.cs"));
        foreach (var (name, argb, line) in OverlayPalette.Citations)
        {
            string text = lines[line - 1];
            Assert.True(text.Contains(name, StringComparison.Ordinal)
                && text.Contains($"0x{argb:X8}", StringComparison.OrdinalIgnoreCase),
                $"OverlayHost.cs:{line} no longer defines {name} = 0x{argb:X8}: {text.Trim()}");
        }
    }

    [Fact]
    public void OverlayLayout_CitedLinesStillHoldTheValues()
    {
        string root = AppCopyFixture.FindRepoRoot(AppContext.BaseDirectory);
        var lines = File.ReadAllLines(Path.Combine(root, "src", "Overlay.Client", "Hud", "OverlayHost.cs"));
        foreach (var (name, line, text) in OverlayLayout.Citations)
            Assert.True(lines[line - 1].Contains(text, StringComparison.Ordinal),
                $"OverlayHost.cs:{line} no longer holds {name} ('{text}'): {lines[line - 1].Trim()}");
    }

    [Fact]
    public void ThemePalette_CitedLinesStillHoldTheValues()
    {
        string root = AppCopyFixture.FindRepoRoot(AppContext.BaseDirectory);
        var lines = File.ReadAllLines(Path.Combine(root, "src", "Overlay.Client", "Theme", "Theme.xaml"));
        foreach (var (key, rgb, line) in ThemePalette.Citations)
        {
            string text = lines[line - 1];
            Assert.True(text.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal)
                && text.Contains($"#{rgb:X6}", StringComparison.OrdinalIgnoreCase),
                $"Theme.xaml:{line} no longer defines {key} = #{rgb:X6}: {text.Trim()}");
        }
    }
}
