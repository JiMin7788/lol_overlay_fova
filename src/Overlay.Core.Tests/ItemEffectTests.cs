using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Items;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// Proves T3.2: item on-hit (Nashor 3115 / Guinsoo 3124 / Wit's End 3091) and spellblade
/// (Sheen 3057 / Trinity 3078 / Lich Bane 3100) proc auto-apply. Nashor/Guinsoo/Sheen/Trinity/
/// Lich Bane resolve LIVE from CommunityDragon's items BIN via <see cref="ItemEffectDb"/> +
/// <see cref="FormulaInterpreter"/> — <c>data/item_effects.json</c> stores only the raw BIN
/// base/ratio values + structure (trigger, curated damage type, calc), nothing pre-computed.
/// Wit's End (3091) is the one EXCEPTION: its raw values were hand-authored from a live
/// wiki.leagueoflegends.com fetch (raw.communitydragon.org was unreachable this session) —
/// see item_effects.json's per-item "_note" for the exact quoted passive text/patch — but it
/// still resolves through the same <see cref="ItemEffectDb"/>/<see cref="FormulaInterpreter"/>
/// path, so its test coverage is identical in shape to the BIN-sourced items.
///  (d) ItemEffectDb loads every covered item and each proc resolves to its hand-verified number.
///  (a) an on-hit item adds a MAGIC hit to an AA, mitigated by MR independently of the AA's armor.
///  (b) a spellblade item adds ONE proc on an ability→AA transition (and shared items don't stack).
///  (c) a combo whose build has NO proc item produces the SAME total (backward-compat).
/// </summary>
public class ItemEffectTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public ItemEffectTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        ItemEffectDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "ItemEffectTests_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Loads a champion straight from its cached BIN, with a TEST-CONTROLLED base AD so
    /// spellblade (which scales on BASE AD) has a clean, hand-verifiable value. All other base
    /// stats are zeroed (bonus AD == total AD − baseAD stays exact).</summary>
    private static ChampionData LoadChampion(string championId, double baseAd = 0)
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
            BaseStats = new ChampionBaseStats { Ad = baseAd },
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

    /// <summary>A minimal COMBO CHAMPION (attacker) fixture carrying only what the melee/ranged
    /// item-proc classification (<see cref="ComboRunner.IsMelee"/>) needs: a specific
    /// <c>BaseStats.AttackRange</c>. No skills/BIN data — these tests only run AA nodes.</summary>
    private static ChampionData AttackerChampion(string id, double attackRange) => new()
    {
        Id = id,
        Name = id,
        BaseStats = new ChampionBaseStats { AttackRange = attackRange },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ComboNode SkillNode(string slot) => new(
        Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot,
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Magic,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    private static ComboNode AaNode() => new(
        Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA",
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    private static GameSnapshot Snapshot(string activeChampion, ActivePlayerStats stats,
        int level = 6, string? enemyChampion = null, int enemyLevel = 6, int[]? activeItems = null)
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
        foreach (var id in activeItems ?? Array.Empty<int>())
            snap.Players[0].TryAddItem(id);

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

    // ── (d) loader + interpreter resolve every covered item to its hand-verified number ─

    [Fact]
    public void ItemEffectDb_LoadsCoveredItems_AndFormulaInterpreterResolvesThem()
    {
        // Synthetic live stats: AP 100, total AD 100, base AD 60 (=> bonus AD 40).
        const double ap = 100, baseAd = 60, totalAd = 100;
        Func<int, int?, double> resolver = (statId, statFormula) => statId switch
        {
            0 => ap,
            2 => statFormula == 1 ? baseAd : statFormula == 2 ? totalAd - baseAd : totalAd,
            _ => 0.0,
        };

        double Proc(string itemId, out ItemEffect e)
        {
            e = ItemEffectDb.Get(itemId) ?? throw new Xunit.Sdk.XunitException($"item {itemId} not loaded");
            return FormulaInterpreter.Evaluate(e.Skill, e.Calc, rank: 1, resolver, casterResource: 0);
        }

        // Nashor's Tooth 3115 — on-hit magic = NashorsBaseValue(15) + NashorsAPValue(0.15)*AP(100) = 30.
        Assert.Equal(30.0, Proc("3115", out var nashor), precision: 3);
        Assert.Equal(ItemTrigger.OnHit, nashor.Trigger);
        Assert.Equal(HitDamageType.Magic, nashor.DamageType);

        // Guinsoo's Rageblade 3124 — on-hit magic = OnHitDamage(30) flat.
        Assert.Equal(30.0, Proc("3124", out var guinsoo), precision: 3);
        Assert.Equal(ItemTrigger.OnHit, guinsoo.Trigger);
        Assert.Equal(HitDamageType.Magic, guinsoo.DamageType);

        // Sheen 3057 — spellblade physical = 1.0 * BASE AD (mStatFormula==1) = 60.
        Assert.Equal(60.0, Proc("3057", out var sheen), precision: 3);
        Assert.Equal(ItemTrigger.Spellblade, sheen.Trigger);
        Assert.Equal(HitDamageType.Physical, sheen.DamageType);

        // Trinity Force 3078 — spellblade physical = SpellbladeMultiplier(2.0) * BASE AD = 120.
        Assert.Equal(120.0, Proc("3078", out var trinity), precision: 3);
        Assert.Equal(ItemTrigger.Spellblade, trinity.Trigger);
        Assert.Equal(HitDamageType.Physical, trinity.DamageType);

        // Lich Bane 3100 — spellblade MAGIC = SpellbladeADRatio(0.75)*baseAD(60) + LichBaneAPValue(0.45)*AP(100) = 90.
        Assert.Equal(90.0, Proc("3100", out var lich), precision: 3);
        Assert.Equal(ItemTrigger.Spellblade, lich.Trigger);
        Assert.Equal(HitDamageType.Magic, lich.DamageType);

        // Wit's End 3091 (hand-authored from a live wiki.leagueoflegends.com fetch, not BIN —
        // see item_effects.json's per-item _note) — on-hit magic = FrayDamage(45) flat, per the
        // wiki's current passive text "Basic attacks deal 45 bonus magic damage on-hit."
        Assert.Equal(45.0, Proc("3091", out var witsEnd), precision: 3);
        Assert.Equal(ItemTrigger.OnHit, witsEnd.Trigger);
        Assert.Equal(HitDamageType.Magic, witsEnd.DamageType);

        // Blade of the Ruined King 3153 (hand-authored from a live wiki fetch, patch V25.14) —
        // melee/ranged %-current-target-HP item: no BIN calc, so it's read straight off
        // ItemEffectDb rather than through FormulaInterpreter.
        var botk = ItemEffectDb.Get("3153") ?? throw new Xunit.Sdk.XunitException("item 3153 not loaded");
        Assert.Equal(ItemTrigger.OnHit, botk.Trigger);
        Assert.Equal(HitDamageType.Physical, botk.DamageType);
        Assert.Equal(ItemHpPercentBasis.TargetCurrent, botk.HpPercentBasis);
        Assert.NotNull(botk.MeleeHpPercent);
        Assert.NotNull(botk.RangedHpPercent);
        Assert.Equal(0.09, botk.MeleeHpPercent!.Value, precision: 4);
        Assert.Equal(0.06, botk.RangedHpPercent!.Value, precision: 4);

        // Titanic Hydra 3748 (hand-authored, patch V14.19; ID confirmed 3748, NOT 3181) —
        // melee/ranged %-caster-max-HP item.
        var hydra = ItemEffectDb.Get("3748") ?? throw new Xunit.Sdk.XunitException("item 3748 not loaded");
        Assert.Equal(ItemTrigger.OnHit, hydra.Trigger);
        Assert.Equal(HitDamageType.Physical, hydra.DamageType);
        Assert.Equal(ItemHpPercentBasis.CasterMax, hydra.HpPercentBasis);
        Assert.NotNull(hydra.MeleeHpPercent);
        Assert.NotNull(hydra.RangedHpPercent);
        Assert.Equal(0.01, hydra.MeleeHpPercent!.Value, precision: 4);
        Assert.Equal(0.005, hydra.RangedHpPercent!.Value, precision: 4);

        // Kraken Slayer 3095 (hand-authored, patch V14.19 base / V25.14 missing-health scaling,
        // current through V25.16) — stack-then-consume item: no BIN calc, so it's read straight
        // off ItemEffectDb like the two %-HP items above. Both the BASE damage AND the real
        // missing-health scaling term (0.75, wired into Damage.ExecuteType.BaseWithMissingHpBonus
        // — see the KrakenSlayer_* integration tests below) are modeled.
        var kraken = ItemEffectDb.Get("3095") ?? throw new Xunit.Sdk.XunitException("item 3095 not loaded");
        Assert.Equal(ItemTrigger.StackThenConsume, kraken.Trigger);
        Assert.Equal(HitDamageType.Physical, kraken.DamageType);
        Assert.Equal(2, kraken.StacksRequired);
        Assert.Equal(150.0, kraken.MeleeDamageAtLevel1!.Value, precision: 3);
        Assert.Equal(200.0, kraken.MeleeDamageAtLevel18!.Value, precision: 3);
        Assert.Equal(120.0, kraken.RangedDamageAtLevel1!.Value, precision: 3);
        Assert.Equal(160.0, kraken.RangedDamageAtLevel18!.Value, precision: 3);
        Assert.Equal(0.75, kraken.MissingHpBonusScalar!.Value, precision: 3);
    }

    // ── (a) on-hit item adds a magic hit to an AA, mitigated by MR (not the AA's armor) ─

    [Fact]
    public void OnHitItem_AddsMagicHitToEveryAa_MitigatedByMrIndependently()
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampion("Ahri"),
            Dummy("Dummy", hp: 2000, armor: 100, mr: 25),
        });

        // One AA + Nashor's Tooth (3115) built. Against Dummy(armor 100, MR 25):
        //   AA physical      100        * 100/(100+100) = 50.00   (mitigated by ARMOR)
        //   Nashor magic     15+0.15*AP(100)=30 * 100/(100+25) = 24.00 (mitigated by MR, INDEPENDENTLY)
        //   total = 74.00
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100 };
        var snap = Snapshot("Ahri", stats, level: 6, enemyChampion: "Dummy", enemyLevel: 6,
            activeItems: new[] { 3115 });

        var result = RunCombo("Ahri", new[] { AaNode() }, snap);

        Assert.Equal(2, result.NodeBreakdown.Count);   // AA + one appended on-hit item hit
        Assert.Equal(74.0, result.TotalDamage, precision: 2);
        Assert.True(result.TotalDamage > 50.0 + 0.01, "on-hit item must add to the AA total");
    }

    // ── Wit's End (3091, hand-authored from a live wiki fetch — see item_effects.json _note):
    // a flat magic on-hit item adds a magic hit to every AA, mitigated by MR independently ────

    [Fact]
    public void OnHitItem_WitsEnd_AddsFlatMagicHitToEveryAa_MitigatedByMrIndependently()
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampion("Ahri"),
            Dummy("Dummy", hp: 2000, armor: 100, mr: 25),
        });

        // One AA + Wit's End (3091) built. Against Dummy(armor 100, MR 25):
        //   AA physical   100                     * 100/(100+100) = 50.00  (mitigated by ARMOR)
        //   Fray magic    45 (flat, per wiki cite) * 100/(100+25) = 36.00  (mitigated by MR, INDEPENDENTLY)
        //   total = 86.00
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100 };
        var snap = Snapshot("Ahri", stats, level: 6, enemyChampion: "Dummy", enemyLevel: 6,
            activeItems: new[] { 3091 });

        var result = RunCombo("Ahri", new[] { AaNode() }, snap);

        Assert.Equal(2, result.NodeBreakdown.Count);   // AA + one appended on-hit item hit
        Assert.Equal(86.0, result.TotalDamage, precision: 2);
    }

    // ── (b) spellblade adds ONE proc on an ability→AA transition; shared items don't stack ─

    [Fact]
    public void Spellblade_AddsOneProc_OnAbilityThenAaTransition_AndSharedItemsDoNotStack()
    {
        // Ahri combo champion with a TEST base AD of 60 so Sheen's 100%-base-AD proc is exact.
        ChampionRepository.Initialize(new[]
        {
            LoadChampion("Ahri", baseAd: 60),
            Dummy("Dummy", hp: 4000, armor: 100, mr: 25),
        });
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100, AbilityQ = 5 };

        // Baseline: [Q, AA] with NO items.
        var noItems = Snapshot("Ahri", stats, level: 6, enemyChampion: "Dummy");
        var baseResult = RunCombo("Ahri", new[] { SkillNode("Q"), AaNode() }, noItems);

        // With Sheen (3057): exactly ONE extra hit on the AA that follows Q.
        //   Sheen proc = 1.0 * baseAD(60) = 60 physical, mitigated by armor 100 = 60*100/200 = 30.
        var withSheen = Snapshot("Ahri", stats, level: 6, enemyChampion: "Dummy", activeItems: new[] { 3057 });
        var sheenResult = RunCombo("Ahri", new[] { SkillNode("Q"), AaNode() }, withSheen);

        Assert.Equal(baseResult.NodeBreakdown.Count + 1, sheenResult.NodeBreakdown.Count);
        Assert.Equal(baseResult.TotalDamage + 30.0, sheenResult.TotalDamage, precision: 2);

        // Sheen + Trinity built together: spellblade is ONE shared passive → still only ONE proc,
        // and it's the largest (Trinity 2.0*baseAD(60)=120 phys, mitigated 120*100/200 = 60).
        var withBoth = Snapshot("Ahri", stats, level: 6, enemyChampion: "Dummy", activeItems: new[] { 3057, 3078 });
        var bothResult = RunCombo("Ahri", new[] { SkillNode("Q"), AaNode() }, withBoth);

        Assert.Equal(baseResult.NodeBreakdown.Count + 1, bothResult.NodeBreakdown.Count); // NOT +2
        Assert.Equal(baseResult.TotalDamage + 60.0, bothResult.TotalDamage, precision: 2);
    }

    // ── (c) a build with NO proc item is unchanged (backward-compat) ────────────────────

    [Fact]
    public void BuildWithNoProcItem_ProducesSameTotalAsBefore()
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampion("Ahri", baseAd: 60),
            Dummy("Dummy", hp: 4000, armor: 100, mr: 25),
        });
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100, AbilityQ = 5 };

        var noItems = Snapshot("Ahri", stats, level: 6, enemyChampion: "Dummy");
        var baseResult = RunCombo("Ahri", new[] { SkillNode("Q"), AaNode() }, noItems);

        // A build carrying only a non-proc item id (1001 Boots) — no covered effect — must not
        // change the node count or the total.
        var bootsOnly = Snapshot("Ahri", stats, level: 6, enemyChampion: "Dummy", activeItems: new[] { 1001 });
        var bootsResult = RunCombo("Ahri", new[] { SkillNode("Q"), AaNode() }, bootsOnly);

        Assert.Equal(baseResult.NodeBreakdown.Count, bootsResult.NodeBreakdown.Count);
        Assert.Equal(baseResult.TotalDamage, bootsResult.TotalDamage, precision: 4);
    }

    // ── Blade of the Ruined King (3153): the critical LIVE vs SNAPSHOT correctness property ─
    // ── — two BOTK-triggering AAs against a target whose HP drops between them must yield ──
    // ── TWO DIFFERENT proc damage values (re-evaluated against CURRENT hp each time), not ───
    // ── the same "9% of pre-combo HP" number twice. ─────────────────────────────────────────

    [Fact]
    public void BladeOfTheRuinedKing_ReEvaluatesAgainstTargetsCurrentHp_AcrossMultiHitCombo()
    {
        // Melee attacker (AttackRange 175, same cluster as Darius/Garen/Aatrox verified this
        // session) with 100 total AD, vs a Dummy defender: MaxHP 1000, Armor 100, MR 0.
        ChampionRepository.Initialize(new[]
        {
            AttackerChampion("MeleeAttacker", attackRange: 175),
            Dummy("Dummy", hp: 1000, armor: 100, mr: 0),
        });
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        var snap = Snapshot("MeleeAttacker", stats, level: 6, enemyChampion: "Dummy", enemyLevel: 6,
            activeItems: new[] { 3153 });

        var result = RunCombo("MeleeAttacker", new[] { AaNode() with { Id = "AA_0" }, AaNode() with { Id = "AA_1" } }, snap);

        // 4 breakdown entries: AA1, BOTK-proc-1, AA2, BOTK-proc-2.
        Assert.Equal(4, result.NodeBreakdown.Count);

        // AA1: 100 AD physical vs 100 armor -> mitigated 100*100/(100+100) = 50.00. remainingHp: 1000 -> 950.
        Assert.Equal(50.0, result.NodeBreakdown[0].Damage, precision: 2);
        // BOTK proc 1: 9% melee of the target's CURRENT hp (950, not the pre-combo 1000) = 85.5 raw,
        // mitigated by armor 100 (independently, but armor is the same 100 here) = 85.5*100/200 = 42.75.
        // remainingHp: 950 -> 907.25.
        Assert.Equal(42.75, result.NodeBreakdown[1].Damage, precision: 2);

        // AA2: another 100 AD physical, mitigated 50.00. remainingHp: 907.25 -> 857.25.
        Assert.Equal(50.0, result.NodeBreakdown[2].Damage, precision: 2);
        // BOTK proc 2: 9% melee of the NEW current hp (857.25) = 77.1525 raw, mitigated
        // 77.1525*100/200 = 38.57625 -> displayed rounded to 38.58. THIS MUST DIFFER FROM
        // proc 1 (42.75) — the whole point (internal Simulate math is full-precision, only the
        // final displayed breakdown value is rounded to 2dp — see DamageEngine.Calculate).
        Assert.Equal(38.58, result.NodeBreakdown[3].Damage, precision: 2);

        // The critical correctness property, stated directly: two BOTK procs in the same combo
        // against a target whose HP dropped between them are NOT equal (a frozen/snapshot
        // implementation would incorrectly compute 9%*1000 = 90 raw -> 45.00 mitigated, TWICE).
        Assert.NotEqual(result.NodeBreakdown[1].Damage, result.NodeBreakdown[3].Damage);

        Assert.Equal(181.33, result.TotalDamage, precision: 2);
    }

    // ── Titanic Hydra (3748): melee vs ranged split, scaled off the CASTER's own max HP (no ──
    // ── live-target-HP mechanism needed — the value is constant across the combo). ──────────

    [Fact]
    public void TitanicHydra_MeleeAndRangedCasters_GetDifferentCleavePercentOfOwnMaxHealth()
    {
        ChampionRepository.Initialize(new[]
        {
            AttackerChampion("MeleeAttacker2", attackRange: 175),   // e.g. Darius/Garen/Aatrox cluster
            AttackerChampion("RangedAttacker", attackRange: 550),   // e.g. Ashe/Caitlyn/Jinx cluster
            Dummy("Dummy2", hp: 5000, armor: 0, mr: 0),             // 0 armor -> no mitigation, clean numbers
        });

        // Same AD (100) and same own MaxHealth (2000) for both — only AttackRange differs, so any
        // damage difference is attributable purely to the melee/ranged classification.
        var stats = new ActivePlayerStats { AttackDamage = 100, MaxHealth = 2000 };

        var meleeSnap = Snapshot("MeleeAttacker2", stats, level: 6, enemyChampion: "Dummy2", enemyLevel: 6,
            activeItems: new[] { 3748 });
        var meleeResult = RunCombo("MeleeAttacker2", new[] { AaNode() }, meleeSnap);

        var rangedSnap = Snapshot("RangedAttacker", stats, level: 6, enemyChampion: "Dummy2", enemyLevel: 6,
            activeItems: new[] { 3748 });
        var rangedResult = RunCombo("RangedAttacker", new[] { AaNode() }, rangedSnap);

        // Melee: Cleave on-hit = 1% of caster's own 2000 max HP = 20 physical (0 armor -> unmitigated).
        // Total = 100 (AA) + 20 (Cleave) = 120.
        Assert.Equal(2, meleeResult.NodeBreakdown.Count);
        Assert.Equal(120.0, meleeResult.TotalDamage, precision: 2);

        // Ranged: Cleave on-hit = 0.5% of the same 2000 max HP = 10 physical.
        // Total = 100 (AA) + 10 (Cleave) = 110.
        Assert.Equal(2, rangedResult.NodeBreakdown.Count);
        Assert.Equal(110.0, rangedResult.TotalDamage, precision: 2);

        // The melee/ranged split is the whole point: the two Cleave procs must differ even though
        // AD, target, and caster max HP are all identical between the two runs.
        Assert.NotEqual(meleeResult.NodeBreakdown[1].Damage, rangedResult.NodeBreakdown[1].Damage);
        Assert.Equal(20.0, meleeResult.NodeBreakdown[1].Damage, precision: 2);
        Assert.Equal(10.0, rangedResult.NodeBreakdown[1].Damage, precision: 2);
    }

    // ── Kraken Slayer (3095): "stack then consume, repeatable" — the critical correctness ──
    // ── properties of the new StackThenConsume trigger, counted over the combo's own AA ─────
    // ── sequence (COMBO-INTERNAL, no live game-state needed — see item_effects.json _note). ─
    // Level 1 is used throughout so the level-interpolated base damage is the exact endpoint
    // (Melee 150 flat), and the Dummy defender carries 0 armor so the AA/proc numbers stay
    // unmitigated and easy to hand-verify. The proc's damage is additionally scaled by the
    // TARGET's missing-health fraction AT THE MOMENT it lands (Damage.ExecuteType.
    // BaseWithMissingHpBonus, scalar 0.75) — these tests construct their ComboRunner with no
    // TargetHealthTracker, so the defender still starts each combo at full HP exactly as before
    // (see ComboRunner.BuildDefenderFor's tracker-optional param), and that missing HP can only
    // come from damage already dealt earlier IN THIS SAME combo (the preceding AAs), which these
    // tests' hand-computed numbers account for. PARTIALLY ADDRESSED ELSEWHERE (not by these
    // tests): when a TargetHealthTracker IS wired in (see AppComposition, TargetHealthTrackerTests,
    // ComboRunnerMissingHpTests), the defender's starting CurrentHP can be below MaxHP too — an
    // honest LOWER-BOUND estimate anchored to the target's last respawn, still narrowed, never
    // "perfectly" closed (potion/elixir/buff healing and non-combo damage remain invisible to us).

    private static ComboNode[] AaSequence(int count)
    {
        var nodes = new ComboNode[count];
        for (int i = 0; i < count; i++)
            nodes[i] = AaNode() with { Id = $"AA_{i}" };
        return nodes;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void KrakenSlayer_ComboWithFewerThanThreeAas_NeverTriggers(int aaCount)
    {
        ChampionRepository.Initialize(new[]
        {
            AttackerChampion("KrakenMeleeAttacker1", attackRange: 175),
            Dummy("KrakenDummy1", hp: 5000, armor: 0, mr: 0),
        });
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        var snap = Snapshot("KrakenMeleeAttacker1", stats, level: 1, enemyChampion: "KrakenDummy1", enemyLevel: 6,
            activeItems: new[] { 3095 });

        var result = RunCombo("KrakenMeleeAttacker1", AaSequence(aaCount), snap);

        // No stack ever reaches 2, so no extra Kraken Slayer hit is appended: node count == AA
        // count exactly, and total damage is pure unmitigated AA damage (100 each, 0 armor).
        Assert.Equal(aaCount, result.NodeBreakdown.Count);
        Assert.Equal(100.0 * aaCount, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void KrakenSlayer_ComboWithExactlyThreeAas_TriggersExactlyOnce_OnTheThird()
    {
        ChampionRepository.Initialize(new[]
        {
            AttackerChampion("KrakenMeleeAttacker2", attackRange: 175),
            Dummy("KrakenDummy2", hp: 5000, armor: 0, mr: 0),
        });
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        var snap = Snapshot("KrakenMeleeAttacker2", stats, level: 1, enemyChampion: "KrakenDummy2", enemyLevel: 6,
            activeItems: new[] { 3095 });

        var result = RunCombo("KrakenMeleeAttacker2", AaSequence(3), snap);

        // 3 AAs + exactly ONE Kraken Slayer proc (on the 3rd AA, which reaches 2 stacks).
        Assert.Equal(4, result.NodeBreakdown.Count);
        // AA1, AA2, AA3: 100 physical each, unmitigated (0 armor) -> remainingHP = 5000-300 = 4700
        // by the time the Kraken proc (appended right after AA3) evaluates. Melee base at level 1
        // = 150, scaled by missing-health: missingFrac = 300/5000 = 0.06, multiplier = 1+0.75*0.06
        // = 1.045 -> proc = 150*1.045 = 156.75, unmitigated.
        Assert.Equal(100.0, result.NodeBreakdown[0].Damage, precision: 2);
        Assert.Equal(100.0, result.NodeBreakdown[1].Damage, precision: 2);
        Assert.Equal(100.0, result.NodeBreakdown[2].Damage, precision: 2);
        Assert.Equal(156.75, result.NodeBreakdown[3].Damage, precision: 2);
        Assert.Equal(3 * 100.0 + 156.75, result.TotalDamage, precision: 2);
    }

    [Fact]
    public void KrakenSlayer_ComboWithSixAas_TriggersExactlyTwice_OnThirdAndSixth()
    {
        ChampionRepository.Initialize(new[]
        {
            AttackerChampion("KrakenMeleeAttacker3", attackRange: 175),
            Dummy("KrakenDummy3", hp: 5000, armor: 0, mr: 0),
        });
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        var snap = Snapshot("KrakenMeleeAttacker3", stats, level: 1, enemyChampion: "KrakenDummy3", enemyLevel: 6,
            activeItems: new[] { 3095 });

        var result = RunCombo("KrakenMeleeAttacker3", AaSequence(6), snap);

        // 6 AAs + exactly TWO Kraken Slayer procs: "stack then consume" repeats — 2 stacks build,
        // consume on AA3, then 2 MORE stacks build (AA4/AA5), consume again on AA6. Each proc's
        // missing-health scaling term grows across the combo since the FIRST proc itself deals
        // extra damage that counts toward the second proc's missing-HP fraction (dynamic live
        // re-evaluation, same convention CurrentHp/MissingHp already use):
        //   proc1 (after AA3): remainingHP=5000-300=4700, missingFrac=0.06 -> 150*1.045=156.75
        //   proc2 (after AA6): remainingHP=4700-156.75-300=4243.25, missingFrac=756.75/5000=
        //     0.15135 -> 150*(1+0.75*0.15135)=150*1.1135125=167.026875 (rounds to 167.03)
        Assert.Equal(8, result.NodeBreakdown.Count);
        Assert.Equal(923.78, result.TotalDamage, precision: 2);

        // Pinpoint exactly which breakdown entries are the Kraken procs: index 3 (right after
        // AA3) and index 7 (right after AA6).
        Assert.Equal(156.75, result.NodeBreakdown[3].Damage, precision: 2);
        Assert.Equal(167.03, result.NodeBreakdown[7].Damage, precision: 2);
        // Every other entry is a plain 100-damage AA.
        foreach (int i in new[] { 0, 1, 2, 4, 5, 6 })
            Assert.Equal(100.0, result.NodeBreakdown[i].Damage, precision: 2);
    }

    // ── Kraken Slayer's missing-health scaling term approaches its confirmed 75% cap against a ──
    // ── near-dead target (see DamageEngineTests for the same shape tested in isolation). ────────

    [Fact]
    public void KrakenSlayer_NearDeadTarget_MissingHpBonusApproachesCap()
    {
        ChampionRepository.Initialize(new[]
        {
            AttackerChampion("KrakenMeleeAttacker4", attackRange: 175),
            // hp=310: 3 AAs of 100 each leave exactly 10 HP remaining by the time the Kraken
            // proc lands, i.e. the target is nearly dead (missingFrac ~= 0.968).
            Dummy("KrakenDummy6", hp: 310, armor: 0, mr: 0),
        });
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        var snap = Snapshot("KrakenMeleeAttacker4", stats, level: 1, enemyChampion: "KrakenDummy6", enemyLevel: 6,
            activeItems: new[] { 3095 });

        var result = RunCombo("KrakenMeleeAttacker4", AaSequence(3), snap);

        // remainingHP before the proc = 310-300 = 10; missingFrac = 300/310 = 0.9677419...;
        // multiplier = 1+0.75*0.9677419... = 1.7258064...; proc = 150*1.7258064... = 258.8709...
        // — close to, but never exceeding, the theoretical 100%-missing cap of 150*1.75=262.5.
        Assert.Equal(258.87, result.NodeBreakdown[3].Damage, precision: 2);
        Assert.True(result.NodeBreakdown[3].Damage < 150.0 * 1.75);
    }

    // ── Kraken Slayer's ranged base damage: confirms the melee/ranged split (like Titanic ────
    // ── Hydra/Blade of the Ruined King) applies to the new StackThenConsume trigger too. ──────

    [Fact]
    public void KrakenSlayer_RangedCaster_UsesRangedBaseDamage()
    {
        ChampionRepository.Initialize(new[]
        {
            AttackerChampion("KrakenRangedAttacker", attackRange: 550),
            Dummy("KrakenDummy4", hp: 5000, armor: 0, mr: 0),
        });
        var stats = new ActivePlayerStats { AttackDamage = 100 };
        var snap = Snapshot("KrakenRangedAttacker", stats, level: 1, enemyChampion: "KrakenDummy4", enemyLevel: 6,
            activeItems: new[] { 3095 });

        var result = RunCombo("KrakenRangedAttacker", AaSequence(3), snap);

        Assert.Equal(4, result.NodeBreakdown.Count);
        // Ranged base at level 1 = 120 (vs melee's 150), same missing-health scaling as the melee
        // test (remainingHP=4700/5000, missingFrac=0.06, multiplier=1.045): 120*1.045=125.4.
        Assert.Equal(125.4, result.NodeBreakdown[3].Damage, precision: 2);
        Assert.Equal(3 * 100.0 + 125.4, result.TotalDamage, precision: 2);
    }

    // ── Backward-compat: a build with Kraken Slayer alone (no other proc item) must not change
    // ── behavior for a combo that never reaches 3 AAs — same guarantee (c) already covers for
    // ── the other 8 items, restated for the new trigger kind. ───────────────────────────────

    [Fact]
    public void KrakenSlayer_DoesNotAffectOtherItemsOrNonAaNodes()
    {
        ChampionRepository.Initialize(new[]
        {
            LoadChampion("Ahri", baseAd: 60),
            Dummy("KrakenDummy5", hp: 4000, armor: 100, mr: 25),
        });
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 100, AbilityQ = 5 };

        // [Q, AA] with Kraken Slayer built: the ability node and the single AA (ordinal 1, never
        // reaches 3) must be completely unaffected — same total as the existing no-items baseline.
        var noItems = Snapshot("Ahri", stats, level: 6, enemyChampion: "KrakenDummy5");
        var baseResult = RunCombo("Ahri", new[] { SkillNode("Q"), AaNode() }, noItems);

        var withKraken = Snapshot("Ahri", stats, level: 6, enemyChampion: "KrakenDummy5", activeItems: new[] { 3095 });
        var krakenResult = RunCombo("Ahri", new[] { SkillNode("Q"), AaNode() }, withKraken);

        Assert.Equal(baseResult.NodeBreakdown.Count, krakenResult.NodeBreakdown.Count);
        Assert.Equal(baseResult.TotalDamage, krakenResult.TotalDamage, precision: 4);
    }
}
