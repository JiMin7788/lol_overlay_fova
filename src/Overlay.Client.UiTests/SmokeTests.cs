using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using Microsoft.Win32;

namespace Overlay.Client.UiTests;

/// <summary>
/// The harness and test mode end to end on the real app copy (spec R2, R5, R7): launch and close,
/// two sessions at once, the exit code for an uncreatable data dir, the files test mode writes, and
/// the HKCU Run value left alone. Plus the engine oracle's initialisation.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SmokeTests
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly AppCopyFixture _app;

    public SmokeTests(AppCopyFixture app) => _app = app;

    [UiFact]
    public void LaunchFindsHomeAndCloseExits() => UiTest.Run(_app, nameof(LaunchFindsHomeAndCloseExits), new[] { "R7" }, ctx =>
    {
        var session = ctx.Start();

        var toggle = session.Home.FindFirstDescendant(cf => cf.ByAutomationId("PreviewToggle"))?.AsButton();
        Assert.NotNull(toggle);
        Assert.Equal("오버레이 미리보기", toggle!.Name);

        // Evidence for the README: which capture path works on HOME, its DPI scale, and whether
        // Korean OCR reads the button (noted, not asserted; the screen checks assert it).
        var hwnd = session.Home.Properties.NativeWindowHandle.Value;
        using (var capture = Screen.Capture(hwnd, out string method))
        {
            Assert.False(Screen.IsBlank(capture), "the HOME capture is blank");
            string path = Path.Combine(_app.RunDir, $"{ctx.TestName}-run{ctx.Run}-home.png");
            capture.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            ctx.AddScreenshot(path);
            NativeMethods.GetWindowRect(hwnd, out var win);
            var b = toggle.BoundingRectangle;
            var crop = new System.Drawing.Rectangle(b.Left - win.Left, b.Top - win.Top, b.Width, b.Height);
            string ocr;
            try { ocr = Screen.Ocr(capture, crop, "ko").Replace(" ", ""); }
            catch (InvalidOperationException ex) { ocr = ex.Message; }
            ctx.Note($"HOME capture via {method}, DPI scale {Screen.Dpi(hwnd):0.##}, OCR of PreviewToggle: '{ocr}'");
        }

        Assert.True(session.Close(), "Fova did not exit within 10 s of closing HOME (it was killed)");
        Assert.True(session.Process!.HasExited);
    });

    [UiFact]
    public void TwoSessionsRunAtOnce() => UiTest.Run(_app, nameof(TwoSessionsRunAtOnce), new[] { "R2", "R7" }, ctx =>
    {
        var first = ctx.Start();
        var second = ctx.Start();

        Assert.NotEqual(first.DataDir, second.DataDir);
        Assert.NotEqual(first.Process!.Id, second.Process!.Id);
        Assert.True(first.IsAlive, "the first session exited when the second started");
        Assert.True(second.IsAlive);
        Assert.NotNull(first.Home);
        Assert.NotNull(second.Home);
    });

    [UiFact]
    public void UncreatableDataDirExitsWithCode2() => UiTest.Run(_app, nameof(UncreatableDataDirExitsWithCode2), new[] { "R2", "R7" }, ctx =>
    {
        // A directory cannot be created under an existing file.
        string blocker = Path.Combine(ctx.ScratchDir("uncreatable"), "a-file");
        File.WriteAllText(blocker, "not a directory");
        string dataDir = Path.Combine(blocker, "data");

        using var process = FovaSession.StartFova(_app.ExePath, dataDir);
        bool sawWindow = false;
        var watch = Stopwatch.StartNew();
        try
        {
            while (!process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (NativeMethods.VisibleTopLevelWindows(process.Id).Count > 0) sawWindow = true;
                Thread.Sleep(50);
            }
            Assert.True(process.HasExited, "Fova did not exit within 10 s");
            Assert.Equal(2, process.ExitCode);
            Assert.False(sawWindow, "Fova showed a window before exiting");
            Assert.False(Directory.Exists(dataDir));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    });

    [UiFact]
    public void TestModeWritesConfigAndLogUnderDataDir() => UiTest.Run(_app, nameof(TestModeWritesConfigAndLogUnderDataDir), new[] { "R1", "R2", "R7" }, ctx =>
    {
        string copyLog = Path.Combine(_app.AppDir, "logs", "app.log");
        var before = FileStamp(copyLog);

        var session = ctx.Start();
        string expected = $"test mode: user data dir = {session.DataDir}";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!session.AppLogLines().Any(l => l.EndsWith(expected, StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
            Thread.Sleep(100);

        Assert.True(File.Exists(Path.Combine(session.DataDir, "config", "user_config.json")));
        Assert.True(File.Exists(Path.Combine(session.DataDir, "logs", "app.log")));

        var lines = session.AppLogLines();
        int header = Array.FindIndex(lines, l => l.StartsWith("=== BUILD", StringComparison.Ordinal));
        Assert.True(header >= 0, "app.log has no '=== BUILD' header");
        Assert.True(header + 1 < lines.Length, "app.log has no line after the header");
        Assert.EndsWith(expected, lines[header + 1]);

        Assert.Equal(before, FileStamp(copyLog));
    });

    [UiFact]
    public void HkcuRunValueIsUntouched() => UiTest.Run(_app, nameof(HkcuRunValueIsUntouched), new[] { "R2", "R5", "R7" }, ctx =>
    {
        string before = RunValue();
        ctx.Note($"HKCU Run 'Fova' before: {before}");

        var session = ctx.Start(new Dictionary<string, object?> { ["general.autoStartWithGame"] = true });
        Assert.True(ConfigSeed.Get(session.Config(), "general.autoStartWithGame")?.GetValue<bool>(),
            "the session did not start with general.autoStartWithGame=true");
        // HomeWindow applies the autostart setting in its constructor, before HOME is found; give a
        // late write time to land anyway.
        Thread.Sleep(2000);
        string during = RunValue();
        session.Close();
        string after = RunValue();

        Assert.Equal(before, during);
        Assert.Equal(before, after);
    });

    [UiFact]
    public void OracleInitialises() => UiTest.Run(_app, nameof(OracleInitialises), new[] { "R19" }, ctx =>
    {
        string dir = ctx.ScratchDir("oracle");
        string seedPath = Path.Combine(dir, "seed", "user_config.json");
        using (var listener = new LoopbackListener()) ConfigSeed.Write(seedPath, listener.Url, null);

        using var oracle = new EngineOracle(_app.AppDir, seedPath, Path.Combine(dir, "runner"));
        var preview = oracle.Preview("Annie", EngineOracle.SkillSequence("Q"));

        Assert.NotNull(preview);
        Assert.True(preview!.Resolved > 0, $"Annie Q resolved to {preview.Resolved}");
        ctx.Note($"Annie [Q] reference preview: {preview.Resolved:0.#} ({preview.Min:0.#}-{preview.Max:0.#})");
    });

    /// <summary>The Run value 'Fova' as kind + exact data, or "absent".</summary>
    internal static string RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue("Fova", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return "absent";
        string data = value is byte[] bytes ? Convert.ToBase64String(bytes)
            : value is string[] parts ? string.Join("\0", parts)
            : value.ToString() ?? "";
        return $"{key!.GetValueKind("Fova")}:{data}";
    }

    private static string FileStamp(string path) =>
        File.Exists(path) ? $"{new FileInfo(path).Length}@{File.GetLastWriteTimeUtc(path):O}" : "absent";
}
