using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 498) Three threads reach <see cref="ComboRunner.BuildContext"/>, and the lock only ever
/// covered two of them. <c>_computeGate</c>'s own doc comment names the hotkey thread and the poll
/// thread; the always-on skill panel came later and calls <see cref="ComboRunner.ComputeSkillPanel"/>
/// from the render thread about four times a second, taking no lock at all — while BuildContext
/// WRITES the last-seen-target cache.
///
/// <para>The symptom would not have been a wrong number. Concurrent writes to a plain Dictionary
/// corrupt its bucket chain, which shows up as an intermittent hang or an IndexOutOfRangeException
/// from inside the BCL.</para>
///
/// <para>WHAT THIS TEST IS AND IS NOT. It hammers both entry points from eight threads, and it does
/// NOT reproduce the defect: run against the original plain Dictionary it passes every time. That is
/// a fact about the cache rather than about the test — a dictionary corrupts most readily while
/// INSERTING, and this one is keyed by the pinned target, so it overwrites one entry and never
/// resizes. So the race is real by inspection and was unlikely to have ever fired. Kept as a
/// REGRESSION GUARD — it would catch someone reintroducing shared mutable state that throws under
/// contention — and paired with a behavioural test below that does verify what the cache is for.</para>
/// </summary>
public class ComboRunnerConcurrencyTests : IDisposable
{
    private readonly string _dir;
    private const string ChampId = "Garen";
    private const string Target = "Ashe";

    /// <summary>Pinned-target names cycled through so the cache INSERTS rather than overwriting one
    /// key forever. Only "Ashe" is in the snapshot, so the rest exercise the absent-target branch.</summary>
    private static readonly string[] Names =
        { "Ashe", "Teemo", "Zed", "Lux", "Sett", "Nami", "Jhin", "Vi", "Ekko", "Rell", "Kayle", "Olaf" };

    public ComboRunnerConcurrencyTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "ComboRace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static void InitRepository()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var summary = Directory.GetFiles(Path.Combine(dataDir, "ddragon"), "champion.json",
            SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Last();
        ChampionRepository.InitializeFromCache(
            Path.GetDirectoryName(summary)!, Path.Combine(dataDir, "communitydragon"));
    }

    /// <summary>Snapshots that keep CHANGING which target is pinned, so the cache is written on nearly
    /// every call rather than settling after the first.</summary>
    private static GameSnapshot Snap(int i)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 2,
            Stats = new ActivePlayerStats
            {
                AttackDamage = 200 + i % 7, AbilityPower = 100,
                AbilityQ = 5, AbilityW = 5, AbilityE = 5, AbilityR = 3,
            },
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = ChampId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 11;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = Target;
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 1 + i % 18;   // level changes → cache rewritten each time
        return snap;
    }

    [Fact]
    public void TheSkillPanelAndTheComboPathCanRunAtOnce()
    {
        InitRepository();
        using var config = new ConfigManager(Path.Combine(_dir, "user_config.json"));
        // Pin a manual target: that is the branch that writes the last-seen cache. The pin CHANGES
        // constantly here, because a cache that only ever overwrites one key never resizes, and a
        // Dictionary corrupts on concurrent INSERT far more readily than on overwrite.
        config.Set("targeting.mode", "Manual");
        config.Set("targeting.manualTarget", Target);

        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        int tick = 0;
        using var runner = new ComboRunner(engine, config, () => Snap(Interlocked.Increment(ref tick)));
        runner.Start();

        var graph = new ComboGraph(new[]
        {
            new ComboNode("Q_0", ComboNodeType.Skill, "Q", 0, 0, 0, ComboDamageType.Physical, 0, 0, 0, 0, 0, 0),
            new ComboNode("E_1", ComboNodeType.Skill, "E", 0, 0, 0, ComboDamageType.Physical, 0, 0, 0, 0, 0, 0),
        }, Array.Empty<ComboEdge>());

        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        int panels = 0, previews = 0;

        // Eight threads: half acting as the render thread, half as the hotkey/poll thread.
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            try
            {
                for (int i = 0; i < 250; i++)
                {
                    // A different pinned name most iterations -> inserts, and eventually resizes.
                    config.Set("targeting.manualTarget", Names[(t * 251 + i) % Names.Length]);
                    if (t % 2 == 0)
                    {
                        if (runner.ComputeSkillPanel(Snap(i)) is not null) Interlocked.Increment(ref panels);
                    }
                    else
                    {
                        if (runner.ComputePreview(ChampId, graph) is not null) Interlocked.Increment(ref previews);
                    }
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToList();

        foreach (var th in threads) th.Start();
        foreach (var th in threads)
            Assert.True(th.Join(TimeSpan.FromSeconds(30)),
                "a worker did not finish — a corrupted Dictionary bucket chain spins forever, which is "
                + "exactly the hang this guards against");

        Assert.True(errors.IsEmpty, "threw under concurrent access: "
            + string.Join(" | ", errors.Select(e => e.GetType().Name + ": " + e.Message).Distinct()));

        // …and it did real work rather than bailing out early on every call.
        Assert.True(panels > 0, "no skill panel resolved — the test proved nothing");
        Assert.True(previews > 0, "no preview resolved — the test proved nothing");
    }

    [Fact]
    public void TheLastSeenCacheStillAnswersAfterTheTargetLeaves()
    {
        // The cache exists so a pinned target that has left the snapshot keeps its last-known stats
        // instead of collapsing to a 0/0 fallback. Making it concurrent must not change that.
        InitRepository();
        using var config = new ConfigManager(Path.Combine(_dir, "leave.json"));
        config.Set("targeting.mode", "Manual");
        config.Set("targeting.manualTarget", Target);

        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        bool present = true;
        using var runner = new ComboRunner(engine, config, () =>
        {
            var snap = Snap(5);
            if (!present) snap.Players[1].ChampionName = "Teemo";  // pinned champion gone
            return snap;
        });
        runner.Start();

        var graph = new ComboGraph(new[]
        {
            new ComboNode("Q_0", ComboNodeType.Skill, "Q", 0, 0, 0, ComboDamageType.Physical, 0, 0, 0, 0, 0, 0),
        }, Array.Empty<ComboEdge>());

        double seen = runner.ComputePreview(ChampId, graph)!.Resolved;
        present = false;
        double remembered = runner.ComputePreview(ChampId, graph)!.Resolved;

        Assert.True(seen > 0);
        Assert.Equal(seen, remembered, 1);
    }
}
