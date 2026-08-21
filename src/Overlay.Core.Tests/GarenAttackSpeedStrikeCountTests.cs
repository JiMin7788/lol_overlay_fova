using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode exists in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #3 round 3 — Garen E (Judgment) attack-speed-scaled strike count
/// (docs/reports/golden/GOLDEN_03_GAREN.md §5, CLAUDE_CODE_TODO §18 third item). Confirms
/// <c>SkillHit.AttackSpeedStrikeStep</c>'s formula: <c>strikes = 7 + floor(countedAS / 25)</c>
/// (inclusive boundary), where <c>countedAS = itemAS (Data Dragon) + levelGrowthAS (champion
/// growth curve)</c> — deliberately EXCLUDING rune/stat-shard attack speed.
///
/// THE critical regression this file guards (the sheet's own L1 case, recorder-confirmed
/// 2026-07-14): the panel's TOTAL bonus AS bundles item + growth + rune/shard together with no way
/// to decompose it. At L1 with a Recurve Bow (15% item AS) and an Attack Speed shard (10%), the
/// PANEL reads 25% — which, fed naively into <c>floor(x/25)</c>, gives 8 strikes. The REAL,
/// recorder-confirmed strike count at that exact setup was 7 — proving the shard's 10% must NOT
/// count. Any implementation that reads a "total AS" stat instead of reconstructing item+growth
/// independently will silently regress this exact case.
/// </summary>
public class GarenAttackSpeedStrikeCountTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    private const string RecurveBowId = "1043";

    public GarenAttackSpeedStrikeCountTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemRepository.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "GarenAsStrikeCountTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────

    /// <summary>Real BIN-loaded Garen (E's "TotalDamage" calc needs real SpellCalculations/
    /// DataValues), but with a CHOSEN <paramref name="attackSpeedPerLevel"/> so the growth-AS math
    /// is hand-verifiable against a known input rather than whatever the bundled DDragon patch
    /// happens to carry (patch-dependent, per this project's own "always dynamic lookup" rule —
    /// exact real-world numbers are out of scope for a unit test; the FORMULA is what's under test).</summary>
    private static ChampionData LoadGarenFromBin(double attackSpeedPerLevel)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "garen.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Garen", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Garen" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Garen", Name = "Garen", Skills = skills,
            BaseStats = new ChampionBaseStats(),
            StatsPerLevel = new ChampionStatsPerLevel { AttackSpeed = attackSpeedPerLevel },
        };
    }

    private static ChampionData Dummy(string id, double hp = 5000, double armor = 0, double mr = 0) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static GameSnapshot Snapshot(int level, int[]? itemIds = null)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = level, PlayerCount = 2,
            Stats = new ActivePlayerStats { AttackDamage = 100, AbilityE = 5 },
        };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Garen";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = level;
        foreach (var id in itemIds ?? Array.Empty<int>()) snap.Players[0].TryAddItem(id);
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = level;
        return snap;
    }

    private ComboResult RunGarenE(ChampionData garen, GameSnapshot snap)
    {
        ChampionRepository.Initialize(new[] { garen, Dummy("Target") });
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Garen", "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: "E_0", NodeType: ComboNodeType.Skill, Name: "E", Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT",
            evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    // ── THE critical regression: L1, item 15% + a hypothetical 10% shard (panel would read 25%, ──
    // ── giving 8 if used directly) — correct countedAS is 15% (growth=0 at L1), so 7 strikes. ──────

    [Fact]
    public void GarenE_Level1_RecurveBowPlusHypotheticalShard_UsesItemOnly_Strikes7NotPanel8()
    {
        ItemRepository.Initialize(new[]
        {
            new ItemData { Id = RecurveBowId, Name = "Recurve Bow", Stats = new ItemStats { AttackSpeedPercent = 0.15 } },
        });
        // attackSpeedPerLevel is irrelevant at level 1 (growth = 0 regardless — the curve's n=level-1=0
        // term always zeroes out), so any value here proves the SAME point; 3.65 mirrors a real
        // champion's DDragon value for realism.
        var garen = LoadGarenFromBin(attackSpeedPerLevel: 3.65);
        var snap = Snapshot(level: 1, itemIds: new[] { int.Parse(RecurveBowId) });

        var result = RunGarenE(garen, snap);

        // NOT the panel's rune-inclusive 25% (item 15 + a hypothetical 10% AS shard, which this
        // fixture deliberately does NOT feed anywhere — ActivePlayerStats has no "total AS" field at
        // all, so there is no code path that could even see the shard). countedAS = 15 (item) + 0
        // (growth) = 15 -> floor(15/25) = 0 -> 7 strikes, matching the sheet's recorder-confirmed L1
        // reading exactly (NOT the 8 a panel-total bug would produce).
        int eStrikes = result.NodeBreakdown.Count(n => n.NodeId.StartsWith("E_0", StringComparison.Ordinal));
        Assert.Equal(7, eStrikes);
    }

    // ── Boundary: exactly 25% countedAS -> 8 (inclusive), 24.99% -> 7 (not yet). ────────────────

    [Fact]
    public void GarenE_CountedAsExactly25_IsInclusive_Strikes8()
    {
        ItemRepository.Initialize(new[]
        {
            new ItemData { Id = RecurveBowId, Name = "Synthetic 25% AS Item", Stats = new ItemStats { AttackSpeedPercent = 0.25 } },
        });
        var garen = LoadGarenFromBin(attackSpeedPerLevel: 0); // isolate: item-only, no growth contribution
        var snap = Snapshot(level: 1, itemIds: new[] { int.Parse(RecurveBowId) });

        var result = RunGarenE(garen, snap);

        int eStrikes = result.NodeBreakdown.Count(n => n.NodeId.StartsWith("E_0", StringComparison.Ordinal));
        Assert.Equal(8, eStrikes); // floor(25/25) = 1 -> 7+1
    }

    [Fact]
    public void GarenE_CountedAsJustUnder25_Strikes7()
    {
        ItemRepository.Initialize(new[]
        {
            new ItemData { Id = RecurveBowId, Name = "Synthetic 24.99% AS Item", Stats = new ItemStats { AttackSpeedPercent = 0.2499 } },
        });
        var garen = LoadGarenFromBin(attackSpeedPerLevel: 0);
        var snap = Snapshot(level: 1, itemIds: new[] { int.Parse(RecurveBowId) });

        var result = RunGarenE(garen, snap);

        int eStrikes = result.NodeBreakdown.Count(n => n.NodeId.StartsWith("E_0", StringComparison.Ordinal));
        Assert.Equal(7, eStrikes); // floor(24.99/25) = 0 -> still the floor
    }

    // ── Growth AS alone (no items) correctly contributes via the SAME per-level curve LevelGrowth ──
    // ── already applies to HP/AD/Armor/MR — proves this isn't item-only. ──────────────────────────

    [Fact]
    public void GarenE_GrowthAsAlone_NoItems_CrossesBoundaryAtHigherLevel()
    {
        // attackSpeedPerLevel=10 (a clean, hand-computable value): at level 6 (n=5),
        // growth = 10*5*(0.7025+0.0175*5) = 10*5*0.79 = 39.5 -> well past 25 -> 8 strikes, with ZERO
        // items built (proving growth alone can cross the boundary, not just items).
        var garen = LoadGarenFromBin(attackSpeedPerLevel: 10);
        var snap = Snapshot(level: 6);

        var result = RunGarenE(garen, snap);

        int eStrikes = result.NodeBreakdown.Count(n => n.NodeId.StartsWith("E_0", StringComparison.Ordinal));
        Assert.Equal(8, eStrikes);
    }

    [Fact]
    public void GarenE_GrowthAsAlone_LowLevel_StaysAtFloor()
    {
        // Same attackSpeedPerLevel=10, but level 2 (n=1): growth = 10*1*(0.7025+0.0175*1) = 10*0.72
        // = 7.2 -> floor(7.2/25) = 0 -> stays at the 7-strike floor.
        var garen = LoadGarenFromBin(attackSpeedPerLevel: 10);
        var snap = Snapshot(level: 2);

        var result = RunGarenE(garen, snap);

        int eStrikes = result.NodeBreakdown.Count(n => n.NodeId.StartsWith("E_0", StringComparison.Ordinal));
        Assert.Equal(7, eStrikes);
    }
}
