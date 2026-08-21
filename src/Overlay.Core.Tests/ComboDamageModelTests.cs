using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// Proves the accurate combo-damage model (curated per-skill JSON + live BIN numbers +
/// per-hit-type mitigation + all-champion enemy resistances):
///  1. A multi-hit skill (Ahri Q = magic-out + true-return) sums BOTH hits — strictly
///     greater than one hit — hand-checked against the BIN DataValues.
///  2. Mixed-type hits (Jinx Q physical + E magic) each mitigate by their OWN resistance
///     (armor vs MR) and sum, matching the 100/(100+r) formula.
///  3. BuildDefender resolves real resistances for a NON-cached champion (Lux) straight
///     from the Data Dragon summary — not the zeroed fallback.
/// </summary>
public class ComboDamageModelTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ComboDamageModelTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "ComboDamageModelTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────

    private static string FixturePath(string champion)
        => Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{champion}.bin.json");

    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(FixturePath(championId.ToLowerInvariant()));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);

        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
        {
            skills[slot] = new SkillData
            {
                Key = slot,
                Name = championId + slot,
                DataValues = bin.DataValues,
                SpellCalculations = bin.SpellCalculations,
                MaxRank = slot == "R" ? 3 : 5,
            };
        }

        return new ChampionData
        {
            Id = championId,
            Name = championId,
            Skills = skills,
            BaseStats = new ChampionBaseStats(),
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy(string id, double hp, double armor, double mr) => new()
    {
        Id = id,
        Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ComboNode SkillNode(string slot) => new(
        Id: $"{slot}_0",
        NodeType: ComboNodeType.Skill,
        Name: slot,
        Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Magic,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0);

    private static GameSnapshot Snapshot(string activeChampion, ActivePlayerStats stats,
        string? enemyChampion = null, int enemyLevel = 1)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = 6,
            PlayerCount = enemyChampion is null ? 1 : 2,
            Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = activeChampion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;

        if (enemyChampion is not null)
        {
            snap.Players[1].SummonerName = "Foe";
            snap.Players[1].ChampionName = enemyChampion;
            snap.Players[1].Team = "CHAOS";
            snap.Players[1].Level = enemyLevel;
        }
        return snap;
    }

    private ComboResult RunCombo(string championId, string[] slots, GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo(championId, "c");
        foreach (var s in slots) editor.AddNode(draft.Id, SkillNode(s));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        // UI.COMBO_RESULT now carries a ComboHudResult wrapper (T4); unwrap its .Result.
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    // ── 1. multi-hit skill sums BOTH hits ────────────────────────────────────────────

    [Fact]
    public void AhriQ_MultiHit_SumsMagicOutAndTrueReturn_GreaterThanSingleHit()
    {
        var ahri = LoadChampionFromBin("Ahri");
        ChampionRepository.Initialize(new[] { ahri });

        // Ahri Q "TotalDamage" = BaseDamage[rank] + 0.5*AP. Rank 5: BaseDamage[5] = 135.
        // AP 100 -> 185 per pass. Curated as TWO hits (magic out + true return), count 1 each.
        var stats = new ActivePlayerStats { AbilityPower = 100, AbilityQ = 5 };
        double singleHit = SkillDamage.ComputeCalcDamage(ahri, "Q", "TotalDamage", stats)!.Value;
        Assert.Equal(135.0 + 0.5 * 100.0, singleHit, precision: 4); // 185

        // No enemy on board -> FallbackDefender (0 armor/MR). Magic and True both unmitigated,
        // so the combo total is exactly two passes.
        var snap = Snapshot("Ahri", stats);
        var result = RunCombo("Ahri", new[] { "Q" }, snap);

        Assert.Equal(2 * singleHit, result.TotalDamage, precision: 2);      // 370
        Assert.True(result.TotalDamage > singleHit + 0.01, "multi-hit total must exceed one hit");
        Assert.Equal(2, result.NodeBreakdown.Count);                        // one Q expanded into two hits
    }

    // ── 1a. Ahri W = 3 flames (1 full + 2 reduced), not a single reduced flame ────────

    [Fact]
    public void AhriW_ThreeFlames_FullPlusTwoReduced_OnOneTarget()
    {
        var ahri = LoadChampionFromBin("Ahri");
        ChampionRepository.Initialize(new[] { ahri });

        // Ahri W fires 3 flames: the first deals SingleFireDamage, the next two each deal
        // MultiFireDamage (= SingleFireDamage * RepeatDamageMod, a reduced subsequent flame).
        // Regression: the curated map previously used MultiFireDamage*1 only (one reduced
        // flame) — LESS than a single hit — which is the "W 1타만 계산" bug.
        var stats = new ActivePlayerStats { AbilityPower = 100, AbilityW = 5 };
        double single = SkillDamage.ComputeCalcDamage(ahri, "W", "SingleFireDamage", stats)!.Value;
        double multi = SkillDamage.ComputeCalcDamage(ahri, "W", "MultiFireDamage", stats)!.Value;

        Assert.True(single > 0, "SingleFireDamage must resolve to a real number");
        Assert.True(multi > 0 && multi < single, "a subsequent flame is a reduced fraction of the first");

        // No enemy on board -> FallbackDefender (0 MR): magic unmitigated, so the total is the
        // raw sum of all three flames.
        var snap = Snapshot("Ahri", stats);
        var result = RunCombo("Ahri", new[] { "W" }, snap);

        Assert.Equal(single + 2 * multi, result.TotalDamage, precision: 2);
        Assert.Equal(3, result.NodeBreakdown.Count); // one W expanded into three flames
    }

    // ── 1b. Ahri R evaluates despite Riot's BIN casing quirk ─────────────────────────

    [Fact]
    public void AhriR_ResolvesDespiteDataValueCasingMismatch()
    {
        var ahri = LoadChampionFromBin("Ahri");
        // Ahri R "RCalculatedDamage" references DataValue "rAPCoefficient" but the value is
        // defined as "RAPCoefficient" (Riot's own casing mismatch). The case-insensitive
        // DataValue fallback recovers it so R yields a real number instead of throwing.
        // Rank 3 (R max): RBaseDamage[3] = 175, RAPCoefficient = 0.35. AP 100 -> 175 + 35 = 210.
        var stats = new ActivePlayerStats { AbilityPower = 100, AbilityR = 3 };

        double? r = SkillDamage.ComputeCalcDamage(ahri, "R", "RCalculatedDamage", stats);

        Assert.NotNull(r);
        Assert.Equal(175.0 + 0.35 * 100.0, r!.Value, precision: 4); // 210
    }

    // ── 2. mixed-type hits each mitigate by their own resistance ──────────────────────

    [Fact]
    public void JinxQE_MixedTypes_PhysicalMitigatedByArmor_MagicByMr()
    {
        var jinx = LoadChampionFromBin("Jinx");
        // Enemy "Dummy" injected with known armor/MR (repository-first resolution).
        ChampionRepository.Initialize(new[] { jinx, Dummy("Dummy", hp: 2000, armor: 50, mr: 25) });

        // Jinx Q RocketDamage = RocketTAD[5](1.1) * TOTAL AD(100) = 110 (physical -> armor 50).
        // Jinx E TotalDamage   = Damage[5](290) + 1.0*AP(100) = 390 (magic -> MR 25).
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100, AbilityQ = 5, AbilityE = 5 };

        double qRaw = SkillDamage.ComputeCalcDamage(jinx, "Q", "RocketDamage", stats)!.Value;
        double eRaw = SkillDamage.ComputeCalcDamage(jinx, "E", "TotalDamage", stats)!.Value;
        Assert.Equal(110.0, qRaw, precision: 4);
        Assert.Equal(390.0, eRaw, precision: 4);

        double expected = Math.Round(
            qRaw * (100.0 / (100.0 + 50.0)) +   // physical vs armor 50
            eRaw * (100.0 / (100.0 + 25.0)),    // magic vs MR 25
            2);

        var snap = Snapshot("Jinx", stats, enemyChampion: "Dummy");
        var result = RunCombo("Jinx", new[] { "Q", "E" }, snap);

        Assert.Equal(expected, result.TotalDamage, precision: 2); // 73.33 + 312.00 = 385.33
        Assert.Equal(2, result.NodeBreakdown.Count);
    }

    // ── 3. non-cached enemy gets real resistances from the summary ────────────────────

    [Fact]
    public void BuildDefender_NonCachedChampion_ResolvesResistancesFromSummary()
    {
        // Only Ahri is cached; Lux is deliberately NOT in the repository, so the old code
        // would have returned the zeroed FallbackDefender. The summary (all ~170 champions)
        // must now supply Lux's real base + per-level resistances.
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Ahri") });

        var active = new ScoreboardEntry { SummonerName = "Me", ChampionName = "Ahri", Team = "ORDER" };
        int enemyLevel = 5;
        var snap = Snapshot("Ahri", new ActivePlayerStats { AbilityPower = 100 },
            enemyChampion: "Lux", enemyLevel: enemyLevel);

        var defender = ComboRunner.BuildDefender(snap, active);

        // Data Dragon summary Lux: armor 21 (+5.2/lvl), MR 30 (+1.3/lvl), HP 580 (+99/lvl).
        // BuildDefender scales per-level stats with Riot's real super-linear growth curve
        // (LevelGrowth.Stat: base + perLevel*n*(0.7025+0.0175*n), n=level-1), NOT naive linear —
        // at level 5 (n=4) that factor is 0.7725, so e.g. armor = 21 + 5.2*4*0.7725 = 37.068.
        double expectedArmor = LevelGrowth.Stat(21, 5.2, enemyLevel); // 37.068
        double expectedMr = LevelGrowth.Stat(30, 1.3, enemyLevel);    // 34.017
        double expectedHp = LevelGrowth.Stat(580, 99, enemyLevel);    // 885.91

        Assert.Equal(expectedArmor, defender.Armor, precision: 3);
        Assert.Equal(expectedMr, defender.Mr, precision: 3);
        Assert.Equal(expectedHp, defender.MaxHP, precision: 3);
        Assert.True(defender.Armor > 0 && defender.Mr > 0, "non-cached enemy must not fall back to 0 resistances");
    }
}
