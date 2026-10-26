using System.Text;

namespace Overlay.Client.UiTests;

/// <summary>One test's row: both run outcomes (a run that did not happen is null).</summary>
public sealed record ReportRow(
    string Test, IReadOnlyList<string> ChecklistIds, string? Run1, string? Run2,
    IReadOnlyList<string> Screenshots, IReadOnlyList<string> Notes);

/// <summary>
/// <c>artifacts/uitest/runs/&lt;runId&gt;/REPORT.md</c>: one table row per test, rewritten after every
/// test so a run that is cut short still leaves a report.
/// </summary>
public sealed class RunReport
{
    private readonly List<ReportRow> _rows = new();

    public RunReport(string path) => Path = path;

    public string Path { get; }

    public IReadOnlyList<ReportRow> Rows
    {
        get { lock (_rows) return _rows.ToList(); }
    }

    public void Add(ReportRow row)
    {
        lock (_rows)
        {
            _rows.Add(row);
            Flush();
        }
    }

    private void Flush()
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        var sb = new StringBuilder();
        sb.AppendLine($"# UI test run {System.IO.Path.GetFileName(folder)}");
        sb.AppendLine();
        sb.AppendLine("| test | checklist ids | run1 | run2 | screenshots | notes |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in _rows)
        {
            string shots = string.Join("<br>", r.Screenshots.Select(s => System.IO.Path.GetRelativePath(folder, s).Replace('\\', '/')));
            sb.AppendLine($"| {Cell(r.Test)} | {Cell(string.Join(", ", r.ChecklistIds))} | {Cell(r.Run1 ?? "—")} | "
                + $"{Cell(r.Run2 ?? "—")} | {shots} | {Cell(string.Join("; ", r.Notes))} |");
        }
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path, sb.ToString());
    }

    private static string Cell(string text) =>
        text.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
}
