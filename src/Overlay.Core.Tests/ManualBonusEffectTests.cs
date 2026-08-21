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
/// Proves T3.3/T8: MANUAL bonus effects the user attaches under a combo node (the editor's sub-icon
/// affordance) — the core model + persistence + ComboRunner merge, plus the newly-curated Mordekaiser
/// passive and the picker-discovery query. WPF is out of scope (verified only by build); everything
/// here is headless.
///  (a) A user-attached on-hit bonus on an AA node adds the extra hit; the total rises by exactly the
///      BIN-resolved amount, mitigated by the bonus's OWN type.
///  (b) A user-defined bonus AND the champion's curated bonus on the same champ BOTH apply (curated
///      counted once, user not dropped).
///  (c) A saved combo carrying user bonus effects round-trips through serialize→deserialize AND a real
///      ConfigManager save→reload, preserving them.
///  (d) The newly-curated Mordekaiser passive resolves to its expected BIN number and is discoverable
///      via GetAttachableBonusEffects (as is the already-curated Sylas passive).
///  (e) A combo with NO user effects produces the same total as before (backward-compat).
/// </summary>
public class ManualBonusEffectTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ManualBonusEffectTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "ManualBonusEffectTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── fixtures (mirror PassiveBonusEffectTests: BIN skills, zeroed base so bonus AD == total AD) ──

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
                EffectAmounts = bin.EffectAmounts,
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

    private static AttachableBonusEffect Fx(string slot, BonusTrigger trigger, HitDamageType type, string calc)
        => new(slot, new SkillBonusEffect
        {
            Trigger = trigger,
            Hits = new[] { new SkillHit { Type = type, Calc = calc, Count = 1 } },
        });

    private static ComboNode AaNode(params AttachableBonusEffect[] userEffects) => new(
        Id: "AA_0",
        NodeType: ComboNodeType.Aa,
        Name: "AA",
        Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0,
        UserBonusEffects: userEffects.Length > 0 ? userEffects : null);

    private static GameSnapshot Snapshot(string activeChampion, ActivePlayerStats stats,
        int level = 6, string? enemyChampion = null, int enemyLevel = 6)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = level,
            PlayerCount = enemyChampion is null ? 1 : 2,
            Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = activeChampion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = level;

        if (enemyChampion is not null)
        {
            snap.Players[1].SummonerName = "Foe";
            snap.Players[1].ChampionName = enemyChampion;
            snap.Players[1].Team = "CHAOS";
            snap.Players[1].Level = enemyLevel;
        }
        return snap;
    }

    private ComboResult RunCombo(string championId, ComboNode[] nodes, GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo(championId, "c");
        foreach (var n in nodes) editor.AddNode(draft.Id, n);
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

    // ── (a) a user-attached on-hit bonus adds the extra hit, mitigated by its OWN type ─────────

    [Fact]
    public void UserBonusEffect_OnAaNode_AddsExtraHit_MitigatedByOwnType()
    {
        // Sylas has NO curated on-hit bonus (its P is a direct-damage passive), so attaching one
        // manually is a clean isolated test of the MANUAL path (no auto effect to interfere).
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Sylas"),
            Dummy("Dummy", hp: 3000, armor: 100, mr: 25),
        });

        // Attach Sylas's P (PassiveDamage, magic) as a manual bonus under the AA node.
        var manual = Fx("P", BonusTrigger.OnHit, HitDamageType.Magic, "PassiveDamage");
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100 };
        var snap = Snapshot("Sylas", stats, level: 6, enemyChampion: "Dummy");

        // Derive the expected number from the BIN (never a typed-in constant).
        double bonusRaw = SkillDamage.ComputeCalcDamage(
            LoadChampionFromBin("Sylas"), "P", "PassiveDamage", stats, level: 6)!.Value;
        double expected = Math.Round(
            100.0 * (100.0 / (100.0 + 100.0)) +   // AA physical vs armor 100
            bonusRaw * (100.0 / (100.0 + 25.0)),  // manual magic bonus vs MR 25
            2);

        var withManual = RunCombo("Sylas", new[] { AaNode(manual) }, snap);
        var withoutManual = RunCombo("Sylas", new[] { AaNode() }, snap);

        Assert.Equal(2, withManual.NodeBreakdown.Count);      // AA + one manual on-hit bonus
        Assert.Equal(expected, withManual.TotalDamage, precision: 2);
        // The delta over the bare AA equals exactly the manual bonus's mitigated amount.
        Assert.Equal(
            Math.Round(bonusRaw * (100.0 / 125.0), 2),
            Math.Round(withManual.TotalDamage - withoutManual.TotalDamage, 2),
            precision: 2);
    }

    // ── (b) user-defined + curated on the same champ BOTH apply ────────────────────────────────

    [Fact]
    public void UserBonus_AndCuratedBonus_BothApply_NoCuratedDoubleCount()
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Warwick"),   // curated P on-hit (OnHitDamage, magic) auto-applies
            Dummy("Dummy", hp: 3000, armor: 100, mr: 25),
        });

        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100 };
        var snap = Snapshot("Warwick", stats, level: 6, enemyChampion: "Dummy");

        double onHitRaw = SkillDamage.ComputeCalcDamage(
            LoadChampionFromBin("Warwick"), "P", "OnHitDamage", stats, level: 6)!.Value;
        double aaMit = 100.0 * (100.0 / 200.0);        // AA physical vs armor 100 = 50
        double bonusMit = onHitRaw * (100.0 / 125.0);  // each magic on-hit vs MR 25

        // Curated-only (no manual): AA + curated on-hit = 2 hits (matches PassiveBonusEffectTests 86.33).
        var curatedOnly = RunCombo("Warwick", new[] { AaNode() }, snap);
        Assert.Equal(2, curatedOnly.NodeBreakdown.Count);
        Assert.Equal(Math.Round(aaMit + bonusMit, 2), curatedOnly.TotalDamage, precision: 2);

        // Add the SAME on-hit effect manually: curated (1) + user (1) BOTH apply → 3 hits, and the
        // curated contribution is still counted exactly once (total = AA + 2×bonus).
        var manual = Fx("P", BonusTrigger.OnHit, HitDamageType.Magic, "OnHitDamage");
        var both = RunCombo("Warwick", new[] { AaNode(manual) }, snap);

        Assert.Equal(3, both.NodeBreakdown.Count);
        Assert.Equal(Math.Round(aaMit + 2 * bonusMit, 2), both.TotalDamage, precision: 2);
        // Delta over curated-only is exactly one more bonus hit (curated not dropped, user added once).
        Assert.Equal(Math.Round(bonusMit, 2), Math.Round(both.TotalDamage - curatedOnly.TotalDamage, 2), precision: 2);
    }

    // ── (c) a combo with user bonus effects round-trips (serialize AND ConfigManager reload) ───

    [Fact]
    public void UserBonusEffects_RoundTrip_ThroughSerializeAndConfigReload()
    {
        var manual = Fx("P", BonusTrigger.OnHit, HitDamageType.Magic, "PassiveDamage");
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());

        // 1) Engine serialize → deserialize preserves the node's UserBonusEffects.
        var graph = engine.BuildGraph(new[] { AaNode(manual) });
        var reloaded = engine.Deserialize(engine.Serialize(graph));
        var node = Assert.Single(reloaded.Nodes);
        var eff = Assert.Single(node.UserBonusEffects!);
        Assert.Equal("P", eff.Slot);
        Assert.Equal(BonusTrigger.OnHit, eff.Effect.Trigger);
        var hit = Assert.Single(eff.Effect.Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("PassiveDamage", hit.Calc);

        // 2) Real ConfigManager save (instance A) → dispose → load (instance B) preserves them.
        string comboId;
        using (var configA = new ConfigManager(_configPath))
        {
            var editorA = new ComboEditor(engine, configA);
            var draft = editorA.CreateCombo("Sylas", "persisted");
            editorA.AddNode(draft.Id, AaNode(manual));
            editorA.SaveCombo(draft.Id);
            comboId = draft.Id;
        }

        using var configB = new ConfigManager(_configPath);
        var editorB = new ComboEditor(engine, configB);
        var loaded = editorB.LoadCombo(comboId);
        var loadedNode = Assert.Single(loaded.Graph.Nodes);
        var loadedEff = Assert.Single(loadedNode.UserBonusEffects!);
        Assert.Equal("P", loadedEff.Slot);
        Assert.Equal("PassiveDamage", Assert.Single(loadedEff.Effect.Hits).Calc);
    }

    // ── (d) newly-curated Mordekaiser passive resolves to its BIN number + is discoverable ─────

    [Fact]
    public void MordekaiserPassive_ResolvesBinNumber_AndIsDiscoverable()
    {
        var morde = LoadChampionFromBin("Mordekaiser");
        Assert.True(morde.Skills.ContainsKey("P"), "Mordekaiser passive was not extracted as slot P");

        // Curated: P (Darkness Rise) = on-hit magic aura via AuraDamagePerStack.
        var bonus = SkillDamageDb.GetBonusEffects("Mordekaiser", "P");
        Assert.NotNull(bonus);
        var effect = Assert.Single(bonus!);
        Assert.Equal(BonusTrigger.OnHit, effect.Trigger);
        var hit = Assert.Single(effect.Hits);
        Assert.Equal(HitDamageType.Magic, hit.Type);
        Assert.Equal("AuraDamagePerStack", hit.Calc);

        // AuraDamagePerStack = 5 (flat) + 0.30 * AP, BIN-sourced. AP 100 → 5 + 30 = 35 (no per-rank
        // array, so the champion-level rank convention leaves it constant).
        var stats = new ActivePlayerStats { AbilityPower = 100 };
        double value = SkillDamage.ComputeCalcDamage(morde, "P", "AuraDamagePerStack", stats, level: 6)!.Value;
        Assert.Equal(35.0, value, precision: 3);

        // Discoverable by the picker query as an attachable on-hit effect.
        var attachable = SkillDamageDb.GetAttachableBonusEffects("Mordekaiser");
        Assert.Contains(attachable, a =>
            a.Slot == "P" && a.Effect.Trigger == BonusTrigger.OnHit
            && a.Effect.Hits.Any(h => h.Calc == "AuraDamagePerStack"));

        // Sylas (already curated in T3.1) is also discoverable — its P direct-damage passive is
        // surfaced as an attachable Self effect.
        var sylas = SkillDamageDb.GetAttachableBonusEffects("Sylas");
        Assert.Contains(sylas, a =>
            a.Slot == "P" && a.Effect.Trigger == BonusTrigger.Self
            && a.Effect.Hits.Any(h => h.Calc == "PassiveDamage"));
    }

    // ── (e) backward-compat: NO user effects → same total as before ────────────────────────────

    [Fact]
    public void NoUserBonusEffects_ProducesSameTotalAsBefore()
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampionFromBin("Sylas"),
            Dummy("Dummy", hp: 3000, armor: 100, mr: 25),
        });

        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100 };
        var snap = Snapshot("Sylas", stats, level: 6, enemyChampion: "Dummy");

        // A bare Sylas AA (no manual effect, Sylas has no auto on-hit) = total AD mitigated by armor.
        var result = RunCombo("Sylas", new[] { AaNode() }, snap);

        Assert.Single(result.NodeBreakdown);
        Assert.Equal(Math.Round(100.0 * (100.0 / 200.0), 2), result.TotalDamage, precision: 2); // 50
    }
}
