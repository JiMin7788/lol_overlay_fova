using System.Diagnostics;
using System.Globalization;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Overlay;
using Overlay.Core.Runes;
using Xunit;
using Xunit.Abstractions;

namespace Overlay.Core.Tests;

/// <summary>Whether perf tests may run: only when <c>FOVA_PERF_TESTS=1</c>, because wall-clock
/// timings depend on the machine and its load.</summary>
public static class PerfGate
{
    public const string EnvVar = "FOVA_PERF_TESTS";

    public const string SkipReason = "Perf test: set FOVA_PERF_TESTS=1 to run (timing-sensitive)";

    public static bool Enabled => Environment.GetEnvironmentVariable(EnvVar) == "1";
}

/// <summary>A perf test: skipped unless <see cref="PerfGate.Enabled"/>.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (!PerfGate.Enabled) Skip = PerfGate.SkipReason;
    }
}

/// <summary>
/// TODO:44 — the always-on skill panel recomputes <see cref="ComboRunner.ComputeSkillPanel"/> on the
/// render thread about four times a second (a handful of <c>ComboEngine.Execute</c> +
/// <c>ApplyBinDamage</c> calls each). The budget is p95 &lt;= 5 ms per recompute, measured with the
/// real champion/item/skill data on the preview snapshot.
/// </summary>
public class SkillPanelPerfTests : IDisposable
{
    private static readonly string[] Casters = { "Garen", "Ahri", "Akali", "Aatrox", "Jinx" };
    private const int WarmUp = 20;
    private const int Timed = 200;

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public SkillPanelPerfTests(ITestOutputHelper output)
    {
        _output = output;
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "SkillPanelPerf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ItemRepository.ResetForTests();
        EventBus.EventBus.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Same loading as <c>ComboRunnerConcurrencyTests.InitRepository</c>, plus the items
    /// from the same Data Dragon directory so the preview enemies' items resolve.</summary>
    private static void InitRepositories()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var summary = Directory.GetFiles(Path.Combine(dataDir, "ddragon"), "champion.json",
            SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Last();
        var ddragonDir = Path.GetDirectoryName(summary)!;
        ChampionRepository.InitializeFromCache(ddragonDir, Path.Combine(dataDir, "communitydragon"));
        ItemRepository.Initialize(
            DDragonParser.ParseItems(File.ReadAllText(Path.Combine(ddragonDir, "item.json"))));
    }

    [PerfFact]
    public void SkillPanelRecompute_P95_IsWithinFiveMilliseconds()
    {
        InitRepositories();
        using var config = new ConfigManager(Path.Combine(_dir, "user_config.json"));
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        GameSnapshot? current = null;
        using var runner = new ComboRunner(engine, config, () => current);

        var samplesMs = new List<double>(Casters.Length * Timed);
        foreach (var caster in Casters)
        {
            var snap = PreviewGameState.Build(caster);
            Assert.NotNull(snap);
            current = snap;
            // A null panel, or one whose abilities all came out 0 (SlotDamage swallows failures as 0),
            // would time an early bail-out, not the recompute — the run would prove nothing.
            var panel = runner.ComputeSkillPanel(snap);
            Assert.NotNull(panel);
            Assert.Contains(panel!.Slots, s => s.Slot != "A" && s.Damage > 0);

            for (int i = 0; i < WarmUp; i++) runner.ComputeSkillPanel(snap);
            for (int i = 0; i < Timed; i++)
            {
                long start = Stopwatch.GetTimestamp();
                runner.ComputeSkillPanel(snap);
                long end = Stopwatch.GetTimestamp();
                samplesMs.Add((end - start) * 1000.0 / Stopwatch.Frequency);
            }
        }

        samplesMs.Sort();
        double p50 = Percentile(samplesMs, 0.50);
        double p95 = Percentile(samplesMs, 0.95);
        double max = samplesMs[^1];
        string stats = string.Format(CultureInfo.InvariantCulture,
            "n={0} p50={1:F2} p95={2:F2} max={3:F2} ms", samplesMs.Count, p50, p95, max);

        Assert.True(p95 <= 5.00, "skill panel recompute p95 over the 5 ms budget: " + stats);
        // AI-NOTE: printed only after the assertion passed — the verify gate greps this line, so a
        // skipped or failing run cannot satisfy it.
        _output.WriteLine("skill panel recompute: " + stats);
    }

    /// <summary>Nearest-rank percentile of an ascending-sorted list.</summary>
    private static double Percentile(List<double> sorted, double p)
        => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];
}
