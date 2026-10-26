using System.Diagnostics;
using Xunit.Sdk;

namespace Overlay.Client.UiTests;

/// <summary>
/// The frame every UI test body runs in: preconditions, cursor save/restore, at most two runs with
/// fresh sessions, and the report row (spec R5-R7).
/// </summary>
public static class UiTest
{
    public const string LeagueRunningMessage = "Close League of Legends before running the UI tests";
    public const string DesktopNotInteractiveMessage = "The desktop is locked or not interactive";

    private static readonly string[] LeagueProcesses = { "LeagueClientUx", "League of Legends" };

    /// <summary>Runs <paramref name="body"/>; a failing run is retried once with a fresh context
    /// (fresh sessions, fresh data dirs). Passes when either run passes; both outcomes are reported.</summary>
    public static void Run(AppCopyFixture app, string name, string[] checklistIds, Action<UiContext> body)
    {
        string? run1 = null, run2 = null;
        var screenshots = new List<string>();
        var notes = new List<string>();
        Exception? failure = null;
        bool cursorSaved = false;
        NativeMethods.POINT cursor = default;
        try
        {
            app.EnsureBuilt();
            CheckPreconditions();
            cursorSaved = NativeMethods.GetCursorPos(out cursor);
            for (int run = 1; run <= 2; run++)
            {
                if (run == 2) CheckPreconditions();
                string outcome;
                using (var ctx = new UiContext(app, name, run))
                {
                    try
                    {
                        body(ctx);
                        outcome = "pass";
                        failure = null;
                    }
                    catch (Exception ex)
                    {
                        outcome = "FAIL: " + ex.Message;
                        failure = ex;
                        ctx.CaptureAllWindows();
                    }
                    screenshots.AddRange(ctx.Screenshots);
                    notes.AddRange(ctx.Notes.Select(n => $"run{run}: {n}"));
                }
                if (run == 1) run1 = outcome; else run2 = outcome;
                if (failure is null) break;
            }
        }
        catch (Exception ex)
        {
            // Build, precondition or harness failure outside a run: no retry.
            failure = ex;
            if (run1 is null) run1 = "FAIL: " + ex.Message; else run2 ??= "FAIL: " + ex.Message;
        }
        finally
        {
            if (cursorSaved) NativeMethods.SetCursorPos(cursor.X, cursor.Y);
            app.Report.Add(new ReportRow(name, checklistIds, run1, run2, screenshots, notes));
        }

        if (failure is not null)
            throw new XunitException($"{name} failed. run1: {run1}; run2: {run2 ?? "not run"}", failure);
    }

    /// <summary>Fails at once when League runs (no key or click may reach it) or input cannot be sent.</summary>
    public static void CheckPreconditions()
    {
        foreach (var processName in LeagueProcesses)
        {
            var found = Process.GetProcessesByName(processName);
            foreach (var p in found) p.Dispose();
            if (found.Length > 0) throw new InvalidOperationException(LeagueRunningMessage);
        }

        var desktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_SWITCHDESKTOP);
        if (desktop == IntPtr.Zero) throw new InvalidOperationException(DesktopNotInteractiveMessage);
        NativeMethods.CloseDesktop(desktop);
        if (NativeMethods.GetForegroundWindow() == IntPtr.Zero)
            throw new InvalidOperationException(DesktopNotInteractiveMessage);
    }
}

/// <summary>One run of a UI test body: the sessions it starts, its folders, notes and screenshots.</summary>
public sealed class UiContext : IDisposable
{
    private readonly List<FovaSession> _sessions = new();
    private readonly List<string> _screenshots = new();
    private readonly List<string> _notes = new();

    internal UiContext(AppCopyFixture app, string testName, int run)
    {
        App = app;
        TestName = testName;
        Run = run;
        RunFolder = Path.Combine(app.RunDir, $"{Safe(testName)}-run{run}");
    }

    public AppCopyFixture App { get; }

    public string TestName { get; }

    /// <summary>1 or 2.</summary>
    public int Run { get; }

    /// <summary><c>artifacts/uitest/runs/&lt;runId&gt;/&lt;test&gt;-run&lt;n&gt;</c>, the first session's folder.</summary>
    public string RunFolder { get; }

    public IReadOnlyList<FovaSession> Sessions => _sessions;

    public IReadOnlyList<string> Screenshots => _screenshots;

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Starts a new session with a fresh data dir: the first in <see cref="RunFolder"/>, a
    /// k-th one in <c>&lt;RunFolder&gt;-s&lt;k&gt;</c>.</summary>
    public FovaSession Start(IReadOnlyDictionary<string, object?>? overrides = null)
    {
        string dir = _sessions.Count == 0 ? RunFolder : $"{RunFolder}-s{_sessions.Count + 1}";
        var session = new FovaSession(App, dir, overrides);
        _sessions.Add(session);
        return session.Start();
    }

    /// <summary>A scratch folder of this run (created).</summary>
    public string ScratchDir(string name)
    {
        string dir = Path.Combine(RunFolder, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Note(string text) => _notes.Add(text);

    /// <summary>Saves a capture of every window of every session, next to REPORT.md.</summary>
    public void CaptureAllWindows()
    {
        for (int i = 0; i < _sessions.Count; i++)
            _screenshots.AddRange(_sessions[i].CaptureWindows(App.RunDir, $"{Safe(TestName)}-run{Run}-s{i + 1}"));
    }

    public void AddScreenshot(string path) => _screenshots.Add(path);

    public void Dispose()
    {
        foreach (var session in _sessions) session.Dispose();
    }

    private static string Safe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
