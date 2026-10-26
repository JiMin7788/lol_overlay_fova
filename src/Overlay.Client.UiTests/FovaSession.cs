using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Overlay.Core;

namespace Overlay.Client.UiTests;

/// <summary>
/// One running Fova copy in test mode: its own data dir (<c>FOVA_USER_DATA_DIR</c>), seed config,
/// loopback telemetry listener and UI Automation handle on the HOME window.
/// </summary>
public sealed class FovaSession : IDisposable
{
    private readonly AppCopyFixture _app;
    private readonly IReadOnlyDictionary<string, object?> _overrides;
    private UIA3Automation? _automation;

    /// <param name="sessionDir"><c>artifacts/uitest/runs/&lt;runId&gt;/&lt;test&gt;-run&lt;n&gt;</c>; the
    /// data dir is its <c>data</c> folder and must not exist yet.</param>
    public FovaSession(AppCopyFixture app, string sessionDir, IReadOnlyDictionary<string, object?>? overrides = null)
    {
        _app = app;
        _overrides = overrides ?? new Dictionary<string, object?>();
        SessionDir = sessionDir;
        DataDir = Path.Combine(sessionDir, "data");
        Listener = new LoopbackListener();
        Input = new SafeInput(this);
    }

    public string SessionDir { get; }

    /// <summary>The <c>FOVA_USER_DATA_DIR</c> of this session.</summary>
    public string DataDir { get; }

    public string ConfigPath => Path.Combine(DataDir, "config", "user_config.json");

    public string AppLogPath => Path.Combine(DataDir, "logs", "app.log");

    public LoopbackListener Listener { get; }

    public SafeInput Input { get; }

    public Process? Process { get; private set; }

    /// <summary>The HOME window (the one holding <c>PreviewToggle</c>).</summary>
    public Window Home { get; private set; } = null!;

    public UIA3Automation Automation => _automation ?? throw new InvalidOperationException("Session not started");

    public bool IsAlive => Process is { HasExited: false };

    /// <summary>Seeds the config, starts the copy and waits up to 30 s for HOME, then checks the
    /// config read back (spec R18).</summary>
    public FovaSession Start()
    {
        if (Directory.Exists(DataDir)) throw new InvalidOperationException($"Data dir is not fresh: {DataDir}");
        ConfigSeed.Write(ConfigPath, Listener.Url, _overrides);
        Process = StartFova(_app.ExePath, DataDir);
        _automation = new UIA3Automation();
        Home = WaitForHome(TimeSpan.FromSeconds(30));
        CheckIsolatedConfig();
        return this;
    }

    /// <summary>Starts <paramref name="exe"/> with <c>FOVA_USER_DATA_DIR</c> set in the child's
    /// environment only; the test process's own environment is not changed.</summary>
    public static Process StartFova(string exe, string dataDir)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.Environment[TestDataDir.EnvVar] = dataDir;
        return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
    }

    private Window WaitForHome(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process!.HasExited)
                throw new InvalidOperationException(
                    $"Fova exited with code {Process.ExitCode} before its HOME window appeared.\n{LogTail()}");
            try
            {
                // AI-NOTE: not FlaUI Application.Attach, which reads Process.MainModule and throws
                // while the just-started process has not loaded its modules yet.
                foreach (var element in Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(Process.Id)))
                    if (element.FindFirstDescendant(cf => cf.ByAutomationId("PreviewToggle")) is not null)
                        return element.AsWindow();
            }
            catch
            {
                // The process is still creating its windows.
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException($"No HOME window within {timeout.TotalSeconds:0} s.\n{LogTail()}");
    }

    /// <summary>Spec R18: the running copy must point telemetry at this session's listener and have
    /// ads and the LCU connector off, unless the test overrode that key itself.</summary>
    private void CheckIsolatedConfig()
    {
        var config = Config();
        Expect("telemetry.endpoint", v => v?.GetValue<string>() == Listener.Url, Listener.Url);
        Expect("ads.enabled", v => v?.GetValue<bool>() == false, "false");
        Expect("champSelect.enabled", v => v?.GetValue<bool>() == false, "false");

        void Expect(string key, Func<JsonNode?, bool> ok, string expected)
        {
            if (_overrides.ContainsKey(key)) return;
            var value = ConfigSeed.Get(config, key);
            bool passed;
            try { passed = ok(value); } catch { passed = false; }
            if (!passed)
                throw new InvalidOperationException(
                    $"Spec R18: session config {key} = {value?.ToJsonString() ?? "missing"}, expected {expected}");
        }
    }

    /// <summary>The session's <c>config/user_config.json</c> as written by the app.</summary>
    public JsonObject Config()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            }
            catch (IOException) when (attempt < 10)
            {
                // AI-NOTE: ConfigManager swaps the file in with File.Replace; a read can hit the swap.
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The session's <c>logs/app.log</c> lines (empty when not written yet).</summary>
    public string[] AppLogLines()
    {
        if (!File.Exists(AppLogPath)) return Array.Empty<string>();
        using var stream = new FileStream(AppLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
    }

    private string LogTail() => "app.log tail:\n" + string.Join("\n", AppLogLines().TakeLast(20));

    /// <summary>A second launch of the copy with the same data dir (show-event and restart cases).</summary>
    public Process Relaunch() => StartFova(_app.ExePath, DataDir);

    /// <summary>A restart: closes the copy (it must exit on its own), starts it again with the same data
    /// dir and listener, waits up to 30 s for the new HOME and checks the config read back (spec R18).</summary>
    public FovaSession Restart()
    {
        if (!Close()) throw new InvalidOperationException("Fova did not exit within 10 s of closing HOME (it was killed)");
        Process!.Dispose();
        Process = StartFova(_app.ExePath, DataDir);
        Home = WaitForHome(TimeSpan.FromSeconds(30));
        CheckIsolatedConfig();
        return this;
    }

    /// <summary>Closes HOME through the UIA WindowPattern and waits 10 s for the process to exit,
    /// then kills it. True when it exited on its own.</summary>
    public bool Close()
    {
        if (Process is null || Process.HasExited) return true;
        try { Home.Patterns.Window.Pattern.Close(); } catch { /* window already gone */ }
        if (Process.WaitForExit(10_000)) return true;
        Kill();
        return false;
    }

    private void Kill()
    {
        try
        {
            if (Process is { HasExited: false })
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(5000);
            }
        }
        catch
        {
            // Exited between the check and the kill.
        }
    }

    /// <summary>Visible top-level windows of the session process.</summary>
    public List<IntPtr> Windows() =>
        Process is { HasExited: false } p ? NativeMethods.VisibleTopLevelWindows(p.Id) : new List<IntPtr>();

    /// <summary>Saves a capture of every visible session window as <c>&lt;prefix&gt;-&lt;k&gt;.png</c> in
    /// <paramref name="folder"/> and returns the paths.</summary>
    public List<string> CaptureWindows(string folder, string prefix)
    {
        var paths = new List<string>();
        int k = 0;
        foreach (var hwnd in Windows())
        {
            try
            {
                using var bitmap = Screen.Capture(hwnd);
                string path = Path.Combine(folder, $"{prefix}-{++k}.png");
                Directory.CreateDirectory(folder);
                bitmap.Save(path, ImageFormat.Png);
                paths.Add(path);
            }
            catch
            {
                // A window that closed or has no size; the other captures still count.
            }
        }
        return paths;
    }

    public void Dispose()
    {
        Kill();
        try
        {
            if (File.Exists(AppLogPath))
            {
                Directory.CreateDirectory(SessionDir);
                File.Copy(AppLogPath, Path.Combine(SessionDir, "app.log"), overwrite: true);
            }
        }
        catch
        {
            // The log copy is evidence, not a result.
        }
        _automation?.Dispose();
        Process?.Dispose();
        Listener.Dispose();
    }
}
