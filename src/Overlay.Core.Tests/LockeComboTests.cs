using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104 (ComboNode exists in .Combo and .Damage)

namespace Overlay.Core.Tests;

/// <summary>
/// Full-kit combo coverage for Locke (release_checklist.md open item — one of the 4 champions the
/// existing suite doesn't yet exercise with realistic combos), against the REAL curated
/// <c>data/skill_damage/Locke.json</c> + live <c>locke.bin.json</c> BIN numbers, run through the
/// actual ComboEngine/ComboEditor/ComboRunner via the EventBus (same harness as
/// <see cref="GoldenKsanteTests"/>/<see cref="AbilitySlotOnHitGatingTests"/>).
///
/// This file is DELIBERATELY separate from <see cref="AbilitySlotOnHitGatingTests"/>'s
/// Locke_SingleAutoAttack_IsAdPlusPassiveOnHitOnly_NotAbilityNail /
/// Locke_NailEmpowersOnlyTheAutoThatFollowsQ_OrderSensitive (the §10 92→243 regression, AD 73/AP
/// 18/Lv 7): it uses DIFFERENT stats (AD 80/AP 40/Lv 18) so it is not a duplicate of that pinned
/// case, and it adds the coverage that file doesn't have — Q/E/R each fired alone, a full 4-node
/// combo, and the R execute/burst-ceiling tag. Every expected number below is hand-computed from
/// locke.bin.json's own DataValues arrays and formula shapes (shown inline), not read back from
/// the engine.
///
/// No enemy is placed on the board (<c>PlayerCount = 1</c>), so ComboRunner's FallbackDefender
/// (Armor 0 / MR 0) applies — every hit lands with NO mitigation (k = 100/(100+0) = 1), and a
/// combo's total is the exact raw sum of its hits (same convention as
/// AbilitySlotOnHitGatingTests/ComboDamageModelTests.AhriQ_MultiHit).
///
/// KEY DATA FACT used throughout: every one of Locke's StatByCoefficient ratio terms (P's 0.1, Q's
/// 0.2, E's 0.4/0.4, R's 0.6) omits an explicit BIN <c>mStat</c> id, which <see
/// cref="FormulaInterpreter"/> defaults to 0 (Ability Power) — Locke's whole kit scales off AP, not
/// AD; AD only matters for the raw auto-attack itself. Confirmed by reading locke.bin.json directly
/// (Characters/Locke/Spells/{LockeQAbility/LockeQ,LockeEAbility/LockeE,LockeRAbility/LockeR} and
/// the {7531bb00} passive object).
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs; see
/// CLAUDE_CODE_TODO.md's build+test entry for the exact `dotnet test --filter`.
/// </summary>
public class LockeComboTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public LockeComboTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "LockeComboTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── fixtures (mirrors AbilitySlotOnHitGatingTests' LoadChampionFromBin) ──────────────

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
            BaseStats = new ChampionBaseStats(),        // 0 base -> "bonus" stat terms equal the raw live stat
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ComboNode SkillNode(string slot) => new(
        Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot,
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Magic,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    private static ComboNode AaNode() => new(
        Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA",
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    /// <summary>No enemy on board -> FallbackDefender (0 armor / 0 MR): every hit lands unmitigated.</summary>
    private static GameSnapshot Snapshot(string activeChampion, ActivePlayerStats stats, int level)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = level,
            PlayerCount = 1,
            Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = activeChampion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = level;
        return snap;
    }

    /// <param name="stackCount">(loop 484) Fills every node's stack knob — the nail count for Locke's
    /// tiered consumption. 0 (default) leaves it unset, which resolves as one nail.</param>
    private ComboResult RunCombo(string championId, string[] slots, GameSnapshot snap, int stackCount = 0)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo(championId, "c");
        foreach (var s in slots)
        {
            var node = s == "AA" ? AaNode() : SkillNode(s);
            editor.AddNode(draft.Id, stackCount > 0 ? node with { UserStackCount = stackCount } : node);
        }
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    // ── shared fixture stats: AD 80 / AP 40, level 18, Q5/E5/R3 (deliberately different from the
    // §10 regression's AD 73/AP 18/Lv 7, so this is a fresh case, not a re-run of that one) ──────

    private const double Ad = 80;
    private const double Ap = 40;
    private const int Level = 18; // ByCharLevelInterpolation t = (18-1)/17 = 1.0 -> the max-tier P value
    private static ActivePlayerStats Stats() => new() { AttackDamage = Ad, AbilityPower = Ap, AbilityQ = 5, AbilityE = 5, AbilityR = 3 };

    // ── 1. base AA alone (the floor): AD + P's always-on on-hit, no ability cast ────────────────

    [Fact]
    public void Locke_AaAlone_IsAdPlusPassiveOnHit()
    {
        // P "Silver Stake" MinOnHitDamage = ByCharLevelInterpolation(5 -> 40) + 0.1 x AP (see class
        // remarks: the coefficient has no mStat -> AP). At level 18, t = (18-1)/17 = 1.0 exactly, so
        // the interpolation is pinned at its END value: 5 + (40-5)*1.0 = 40. Plus 0.1*40 (AP) = 4.
        // P = 44. AA (physical, AD 80, unmitigated) = 80. Total = 80 + 44 = 124.
        var locke = LoadChampionFromBin("Locke");
        ChampionRepository.Initialize(new[] { locke });

        var result = RunCombo("Locke", new[] { "AA" }, Snapshot("Locke", Stats(), Level));

        Assert.Equal(80.0 + 44.0, result.TotalDamage, precision: 2);
        Assert.Equal(2, result.NodeBreakdown.Count); // AA hit + P on-hit rider
    }

    // ── 2. one skill alone per damage-dealing slot (Q castCount 3, E's 2 hits, R) ────────────────

    /// <summary>(loop 483) Ritual Nails is three casts and they are three NODES now — castCount 3
    /// folded them into one, so a Q node always claimed all three nails and one or two landing was
    /// unsayable. The per-cast number and the three-cast total are both unchanged.</summary>
    [Fact]
    public void Locke_QAlone_IsOneNail_AndTheThreeCastsAreThreeNodes()
    {
        // MissileDamage = NamedDataValue(BaseMissileDamage, rank 5) + 0.2 x AP (no mStat on the
        // coefficient -> AP, per class remarks).
        //
        // (loop 495) Numbers re-derived for the 16.16 BIN refresh. BaseMissileDamage went
        // [40,50,60,70,80,90,100] -> [32,40,48,56,64,72,80], so rank 5 is 72 and a cast is
        // 72 + 0.2*40 = 80. These are BIN-derived fixtures, not a measured golden: the arithmetic
        // is the assertion, so it follows the data it was always reading. No AA follows, so the
        // nail bonusEffect never attaches to anything.
        var locke = LoadChampionFromBin("Locke");
        ChampionRepository.Initialize(new[] { locke });

        var one = RunCombo("Locke", new[] { "Q" }, Snapshot("Locke", Stats(), Level));
        Assert.Equal(80.0, one.TotalDamage, precision: 2);
        Assert.Single(one.NodeBreakdown);

        // …and all three recasts still total what the folded node used to.
        var all = RunCombo("Locke", new[] { "Q", "Q2", "Q3" }, Snapshot("Locke", Stats(), Level));
        Assert.Equal(240.0, all.TotalDamage, precision: 2);
        Assert.Equal(3, all.NodeBreakdown.Count);
    }

    [Fact]
    public void Locke_EAlone_BlinkPlusDashMagicHits()
    {
        // E "Ashen Pursuit" curates TWO hits: OnHitDamage (blink-arrival AoE) and DashDamage
        // (empowered-attack dash-arrival AoE), both NamedDataValue + 0.4xAP (no mStat -> AP).
        // BaseOnHitDamage[5] = 80 (DataValues [30,40,50,60,70,80,90]); BaseDashDamage[5] = 120
        // (DataValues [20,40,60,80,100,120,140]). OnHit = 80 + 0.4*40 = 96. Dash = 120 + 0.4*40 = 136.
        // E total = 96 + 136 = 232.
        var locke = LoadChampionFromBin("Locke");
        ChampionRepository.Initialize(new[] { locke });

        var result = RunCombo("Locke", new[] { "E" }, Snapshot("Locke", Stats(), Level));

        Assert.Equal(232.0, result.TotalDamage, precision: 2);
        Assert.Equal(2, result.NodeBreakdown.Count); // OnHitDamage + DashDamage
    }

    [Fact]
    public void Locke_R_ExecuteTaggedBurst_DirectHitAndTagsExposed()
    {
        // R "Purgatory" Damage = NamedDataValue(BaseDamage, rank 3) + 0.6 x AP (no mStat -> AP).
        // BaseDamage[3] = 300 (locke.bin.json R DataValues: [75,150,225,300,375,450,525], index =
        // rank). 300 + 0.6*40 = 324. Curated with tags ["Execute","BurstCeiling","AoeUltimate"] (the
        // hard EXECUTE mechanic itself is a threshold-kill, not a bonus number, so it is documented via
        // the tag rather than fabricated as a hit — see Locke.json's own _note).
        var locke = LoadChampionFromBin("Locke");
        ChampionRepository.Initialize(new[] { locke });

        var result = RunCombo("Locke", new[] { "R" }, Snapshot("Locke", Stats(), Level));

        Assert.Equal(324.0, result.TotalDamage, precision: 2);
        Assert.Single(result.NodeBreakdown);

        var tags = SkillDamageDb.GetSlotTags("Locke", "R");
        Assert.Contains("Execute", tags);
        Assert.Contains("BurstCeiling", tags);
        Assert.Contains("AoeUltimate", tags);
    }

    // ── 3. full combo: Q(x3) + E + R + a trailing AA (nail attaches since Q was cast earlier) ────

    [Fact]
    public void Locke_FullCombo_QEThenR_ThenEmpoweredAA_SumsEveryHit()
    {
        // Q+Q2+Q3(240) + E(232) + R(324) as computed above, then a trailing AA. The AA gets BOTH riders:
        // P's always-on on-hit (44, computed in test 1) AND Q's nail bonusEffect, because
        // ComboRunner's castSlots set remembers Q was cast earlier in the sequence (nail is not
        // gated to immediate adjacency -- E and R casting in between does not clear it; see
        // AbilitySlotOnHitGatingTests' class remarks on castSlots).
        // Nail = StatByNamedDataValue(MarkRatio, no mStat -> AP) + NamedDataValue(MarkDamage), both at
        // rank 5: MarkRatio[5] = 0.35 (DataValues [.225,.25,.275,.30,.325,.35,.375], index = rank),
        // MarkDamage[5] = 50 after the 16.16 refresh. Nail = 0.35*40 + 50 = 14 + 50 = 64.
        // AA node total = 80 (AD) + 44 (P) + 64 (nail) = 188.
        // Combo total = 240 + 232 + 324 + 188 = 984.
        var locke = LoadChampionFromBin("Locke");
        ChampionRepository.Initialize(new[] { locke });

        var result = RunCombo("Locke", new[] { "Q", "Q2", "Q3", "E", "R", "AA" },
            Snapshot("Locke", Stats(), Level));

        // (loop 483) Same total as before the split — three nail nodes where there was one node
        // replayed three times. What changed is that two nails is now a combo you can write.
        Assert.Equal(984.0, result.TotalDamage, precision: 2);
    }

    // ── 4. (loop 484) the nail tiers — the user stated the shape and the BIN carries every number ──

    /// <summary>Soul Nails are consumed ALL AT ONCE by the next attack, and saving them is worth more
    /// than spending them one at a time: base, then (base x 2) x 1.2 at two nails and (base x 3) x 1.4
    /// at three. Both percentages are BIN DataValues, so nothing here is a curated constant — which is
    /// exactly why loop 483 left this open and loop 484 could close it.</summary>
    [Theory]
    [InlineData(0, 1.0)]    // knob untouched → one nail, the floor this rider always had
    [InlineData(1, 1.0)]
    [InlineData(2, 2.4)]    // 2 × (1 + TwoMarkBonusPercent 20%)
    [InlineData(3, 4.2)]    // 3 × (1 + ThreeMarkBonusPercent 40%)
    [InlineData(9, 4.2)]    // clamped: there is no fourth nail
    public void Locke_NailConsumption_ScalesByTheTierTable(int nails, double multiplier)
    {
        var locke = LoadChampionFromBin("Locke");
        ChampionRepository.Initialize(new[] { locke });

        // Nail = StatByNamedDataValue(MarkRatio[5] 0.35, no mStat → AP 40) + NamedDataValue(MarkDamage[5])
        // (loop 495, 16.16) MarkDamage went [10,20,30,40,50,60,70] -> [10,18,26,34,42,50,58], so
        // rank 5 is 50 and one nail is 0.35*40 + 50 = 64 (was 74). The tier multipliers are
        // untouched — they come from TwoMarkBonusPercent 20 and ThreeMarkBonusPercent 40, which
        // did not move, which is the point of curating them as percentages rather than as 2.4/4.2.
        const double aaWithPassive = 80.0 + 44.0;
        var result = RunCombo("Locke", new[] { "Q", "AA" }, Snapshot("Locke", Stats(), Level),
            stackCount: nails);

        // Q itself is 98 (one nail cast); the rest is the attack plus the consumed nails.
        Assert.Equal(80.0 + aaWithPassive + 64.0 * multiplier, result.TotalDamage, precision: 2);
    }

    /// <summary>The knob bounds itself: three nails, stated by the tier list's own length rather than
    /// a curated number, so the editor offers 1-3 instead of the 600 an uncapped stack counter has.</summary>
    [Fact]
    public void Locke_NailKnob_IsOfferedOnTheAttack_AndCapsAtThree()
    {
        var locke = LoadChampionFromBin("Locke");
        ChampionRepository.Initialize(new[] { locke });

        // The rider has no hit on any Q/W/E/R slot — it lands on the ATTACK, which is where the knob
        // has to appear or the tiers are curated and unreachable.
        var onAttack = SkillDamageDb.GetStackScaledHit("Locke", "AA");
        Assert.NotNull(onAttack);
        Assert.True(onAttack!.IsStackTiered);
        Assert.Equal(3, onAttack.MaxStackTier);

        // Ashen Pursuit consumes them too, per the wiki; W does not.
        var effect = Assert.Single(SkillDamageDb.GetBonusEffects("Locke", "Q")!);
        Assert.True(effect.AppliesToSlot("E"));
        Assert.False(effect.AppliesToSlot("W"));
    }
}

