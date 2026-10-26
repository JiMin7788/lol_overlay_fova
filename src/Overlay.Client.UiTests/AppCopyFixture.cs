using System.Diagnostics;

namespace Overlay.Client.UiTests;

/// <summary>The xUnit collection every UI test class joins, so they share one app copy and one
/// report.</summary>
[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<AppCopyFixture>
{
    public const string Name = "Fova UI";
}

/// <summary>
/// One Fova build copy per test run, in <c>artifacts/uitest/app</c>. Built with <c>-o</c> so it never
/// touches the Debug bundle (the Client project's own output folder), which the user's running Fova locks.
/// </summary>
public sealed class AppCopyFixture : IDisposable
{
    private readonly Lazy<string?> _buildError;

    public AppCopyFixture()
    {
        RepoRoot = FindRepoRoot(AppContext.BaseDirectory);
        AppDir = Path.Combine(RepoRoot, "artifacts", "uitest", "app");
        RunId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        RunDir = Path.Combine(RepoRoot, "artifacts", "uitest", "runs", RunId);
        Report = new RunReport(Path.Combine(RunDir, "REPORT.md"));
        // AI-NOTE: xUnit creates collection fixtures even when every test in the collection is
        // skipped, so the 10-minute build waits for the first UI test that actually runs.
        _buildError = new Lazy<string?>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string RepoRoot { get; }

    /// <summary>The build copy: <c>&lt;root&gt;/artifacts/uitest/app</c>.</summary>
    public string AppDir { get; }

    public string ExePath => Path.Combine(AppDir, "fova.exe");

    public string RunId { get; }

    /// <summary><c>&lt;root&gt;/artifacts/uitest/runs/&lt;runId&gt;</c>.</summary>
    public string RunDir { get; }

    public RunReport Report { get; }

    /// <summary>Builds the copy once (later calls return at once). Throws with the build output's
    /// tail when the build failed, so every test fails with it.</summary>
    public void EnsureBuilt()
    {
        if (_buildError.Value is { } error) throw new InvalidOperationException(error);
    }

    public static string FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "LolOverlay.sln"))) return dir.FullName;
        throw new InvalidOperationException($"No LolOverlay.sln above {start}");
    }

    /// <summary>Timestamped fixture steps in <c>runs/&lt;runId&gt;/fixture.log</c> (where a slow start went).</summary>
    private void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(RunDir);
            File.AppendAllText(Path.Combine(RunDir, "fixture.log"), $"{DateTime.Now:HH:mm:ss.fff} {text}\n");
        }
        catch (IOException)
        {
            // A progress aid, not a result.
        }
    }

    private string? Build()
    {
        Log("killing leftover copies");
        KillLeftoverCopies();
        Log("building the copy");
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "build", Path.Combine("src", "Overlay.Client", "Overlay.Client.csproj"),
                     "-c", "Debug", "-o", AppDir, "--nologo",
                 })
            psi.ArgumentList.Add(arg);

        var output = new List<string>();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return "Building the Fova copy timed out after 10 minutes.\n" + Tail(output);
        }
        process.WaitForExit(); // flush the async output readers
        Log($"build exited {process.ExitCode}");
        if (process.ExitCode != 0)
            return $"Building the Fova copy failed (exit {process.ExitCode}):\n" + Tail(output);
        if (!File.Exists(ExePath)) return $"The build succeeded but {ExePath} is missing.\n" + Tail(output);
        return null;
    }

    private static string Tail(List<string> lines)
    {
        lock (lines) return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 40)));
    }

    /// <summary>Kills Fova processes left by an earlier run of this copy — only those whose
    /// executable is under <see cref="AppDir"/>, never the user's own Fova.</summary>
    public void KillLeftoverCopies()
    {
        string prefix = Path.GetFullPath(AppDir).TrimEnd('\\') + "\\";
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainModule?.FileName is { } file
                    && Path.GetFullPath(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // Access denied (system or elevated process) or already exited: not ours.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_buildError.IsValueCreated) KillLeftoverCopies();
    }
}
