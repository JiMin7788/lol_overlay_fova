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
/// Proves the flagship "총 데미지: 0" fix: combo skill nodes now compute REAL damage from
/// the champion's CommunityDragon BIN spell formula (M11) + live stats, via
/// <see cref="SkillDamage"/> and <see cref="ComboRunner"/>.
///
/// Two layers of proof:
///  1. Hand-checked BIN math for two champions with two DIFFERENT stat mappings — Aatrox Q
///     (mStat id 2 = total AD) and Annie Q (id 0 = AP) — read the base+ratio DataValue
///     arrays, compute base[rank]+ratio[rank]*stat by hand, assert the resolver+interpreter
///     reproduce it. This proves the mStat mapping is derived, not guessed.
///  2. An end-to-end ComboRunner run publishing UI.COMBO_RESULT with a NON-ZERO TotalDamage
///     equal to the summed per-skill BIN computation.
/// </summary>
public class SkillDamageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public SkillDamageTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "SkillDamageTests_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Builds a ChampionData whose Q/W/E/R skills carry the REAL parsed BIN
    /// DataValues/SpellCalculations, with standard max ranks (R=3, others=5).</summary>
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

        return new ChampionData { Id = championId, Name = championId, Skills = skills };
    }

    // ── 1. resolver mapping ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildStatResolver_MapsApArmorAdMrMsHealth_ElseThrows()
    {
        var stats = new ActivePlayerStats
        {
            AbilityPower = 250, AttackDamage = 175, Armor = 80, MagicResist = 60, MoveSpeed = 400,
            MaxHealth = 2500,
        };
        // baseAD = LevelGrowth.Stat(60, 5, 5) = 75.45 (real growth curve, not naive linear), so
        // bonus AD = 175 - 75.45 = 99.55. MS has no per-level base growth (ChampionStatsPerLevel
        // carries no Ms field), so bonus MS = 400 - 335 = 65.
        // baseHp = LevelGrowth.Stat(600, 90, 5) = 878.1, so bonus HP = 2500 - 878.1 = 1621.9
        // (Tahm Kench passive stat mapping, see
        // BuildStatResolver_TahmKenchPassive_MatchesHandComputedBonusHpFormula).
        var champion = new ChampionData
        {
            BaseStats = new ChampionBaseStats { Ad = 60, Ms = 335, Hp = 600 },
            StatsPerLevel = new ChampionStatsPerLevel { Ad = 5, Hp = 90 },
        };
        var resolve = SkillDamage.BuildStatResolver(stats, champion, level: 5);
        double baseAd = LevelGrowth.Stat(60, 5, 5);
        double baseHp = LevelGrowth.Stat(600, 90, 5);

        Assert.Equal(250, resolve(0, null));  // id 0 = AP (mStat omitted in BIN AP parts)
        Assert.Equal(80, resolve(1, null));   // id 1 = Armor
        Assert.Equal(175, resolve(2, null));  // id 2, no mStatFormula = total AD
        Assert.Equal(175 - baseAd, resolve(2, 2), precision: 6); // id 2, mStatFormula 2 = bonus AD
        Assert.Equal(60, resolve(6, null));   // id 6 = Magic Resist
        Assert.Equal(400, resolve(7, null));  // id 7, no mStatFormula = total MS
        Assert.Equal(65, resolve(7, 2));       // id 7, mStatFormula 2 = bonus MS (400-335)
        Assert.Equal(2500, resolve(12, null)); // id 12, no mStatFormula = total Health (Tahm Kench E GreyHealthMaximum)
        Assert.Equal(2500 - baseHp, resolve(12, 2), precision: 6); // id 12, mStatFormula 2 = bonus Health (Tahm Kench passive)
        // Unmapped ids (no confirmed mapping and/or no live stat source) still fail loudly
        // instead of silently resolving to 0 (T-stat-resolver-gap fix).
        Assert.Throws<KeyNotFoundException>(() => resolve(99, null)); // any other unmapped id
    }

    [Fact]
    public void ComputeCalcDamage_JannaPassiveBonusDamage_UsesBonusMoveSpeed()
    {
        // Janna passive (Tailwind) TailwindSelf.BonusDamage = StatByNamedDataValueCalculationPart
        // (mStat=7, mStatFormula=2 -> BONUS move speed, mDataValue="MSBonusMagicDamage"=0.30),
        // matching the live tooltip "bonus magic damage equal to 30% of her bonus movement
        // speed" (wiki.leagueoflegends.com/en-us/Janna, Tailwind).
        var loaded = LoadChampionFromBin("Janna");
        var janna = new ChampionData
        {
            Id = "Janna",
            Name = "Janna",
            Skills = loaded.Skills,
            BaseStats = new ChampionBaseStats { Ms = 335 }, // Janna's published base MS
        };
        var stats = new ActivePlayerStats { MoveSpeed = 400 }; // bonus MS = 400 - 335 = 65

        double? damage = SkillDamage.ComputeCalcDamage(janna, "P", "BonusDamage", stats, level: 1);

        Assert.NotNull(damage);
        Assert.Equal(65.0 * 0.30, damage!.Value, precision: 5); // 19.5
    }

    // ── 2. hand-checked BIN math (two champions, two stat mappings) ──────────────────

    [Fact]
    public void ComputeNodeDamage_AatroxQ_MatchesHandComputedTotalAdFormula()
    {
        var aatrox = LoadChampionFromBin("Aatrox");
        // QDamage = QBaseDamage[rank] + QTotalADRatio[rank] * totalAD (mStat 2 -> total AD).
        // Max rank Q = 5: QBaseDamage[5] = 70, QTotalADRatio[5] = 0.90.
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 0 };

        double? damage = SkillDamage.ComputeNodeDamage(aatrox, "Q", stats);

        Assert.NotNull(damage);
        Assert.Equal(70.0 + 0.90 * 100.0, damage!.Value, precision: 4); // 160
    }

    [Fact]
    public void ComputeNodeDamage_AnnieQ_MatchesHandComputedApFormula()
    {
        var annie = LoadChampionFromBin("Annie");
        // TotalDamage = BaseDamage[rank] + APRatio[rank] * AP (mStat omitted -> id 0 = AP).
        // Max rank Q = 5: BaseDamage[5] = 260, APRatio[5] = 0.80.
        var stats = new ActivePlayerStats { AbilityPower = 200, AttackDamage = 0 };

        double? damage = SkillDamage.ComputeNodeDamage(annie, "Q", stats);

        Assert.NotNull(damage);
        Assert.Equal(260.0 + 0.80 * 200.0, damage!.Value, precision: 4); // 420
    }

    [Fact]
    public void ComputeNodeDamage_ZedQ_UsesBonusAd_NotTotalAd()
    {
        // Zed Q "TotalDamage" = BaseDamage[rank] + BonusADRatio[rank] * BONUS AD
        // (mStat 2 + mStatFormula 2 -> bonus AD). Max rank Q = 5: BaseDamage[5] = 240,
        // BonusADRatio[5] = 1.0. Give Zed a known base AD so bonus AD < total AD and the
        // split is observable: baseAD = LevelGrowth.Stat(60, 5, 6) = 79.75 (real growth
        // curve), bonusAD = 175 - 79.75 = 95.25.
        var loaded = LoadChampionFromBin("Zed");
        var zed = new ChampionData
        {
            Id = "Zed",
            Name = "Zed",
            Skills = loaded.Skills,
            BaseStats = new ChampionBaseStats { Ad = 60 },
            StatsPerLevel = new ChampionStatsPerLevel { Ad = 5 },
        };
        var stats = new ActivePlayerStats { AttackDamage = 175, AbilityPower = 0 };

        double? damage = SkillDamage.ComputeNodeDamage(zed, "Q", stats, level: 6);

        Assert.NotNull(damage);
        double baseAd = LevelGrowth.Stat(60, 5, 6);  // 79.75
        double bonusAd = 175 - baseAd;               // 95.25
        Assert.Equal(240.0 + 1.0 * bonusAd, damage!.Value, precision: 4);  // 335.25, NOT 415 (total AD)
        // Guard: the old total-AD behaviour would have over-estimated at 240 + 175 = 415.
        Assert.NotEqual(240.0 + 175.0, damage!.Value, precision: 4);
    }

    [Fact]
    public void ComputeCalcDamage_TahmKenchPassive_MatchesHandComputedBonusHpFormula()
    {
        // TahmKenchPassive.TotalDamage (mStat id 12 = bonus/total Health, added this session —
        // see SkillDamage.BuildStatResolver remarks and Data/skill_damage/TahmKench.json's "P"
        // citation). Rank for a passive ("P") is the champion's LEVEL, not a skill rank.
        //
        // mFormulaParts[0] ByCharLevelBreakpointsCalculationPart(Level1Value=5,
        //   InitialBonusPerLevel=5, breakpoint @level12 AdditionalBonusAtThisLevel=5):
        //   at level 12 = 5 + 5*10 (levels 2-11) + (5 bump + 5 rate at level 12) = 65.
        // mFormulaParts[1] StatByCoefficientCalculationPart(mStat=12, mStatFormula=2, coeff=0.04)
        //   = 0.04 * bonusHp.
        // mFormulaParts[2] StatBySubPartCalculationPart — the BIN part has NO mStat, so the outer
        //   stat defaults to AP (id 0, per StatBySubPart's `part.Stat ?? 0`). Its subpart is
        //   ProductOfSubParts(StatByNamedDataValue(mStat=12, mStatFormula=2,
        //   "APRatioPer100BonusHP"=0.0125), Number(0.01)) = (bonusHp * 0.0125) * 0.01, which is an
        //   AP RATIO (~0.17625 at 1410 bonusHp), NOT flat damage. So part[2] = AP * (bonusHp *
        //   0.000125) — it only contributes when the caster has AP (the data value's own name says
        //   "APRatio"). Total = 65 + 0.04*bonusHp + AP*(bonusHp*0.000125).
        var loaded = LoadChampionFromBin("TahmKench");
        var tahmKench = new ChampionData
        {
            Id = "TahmKench",
            Name = "TahmKench",
            Skills = loaded.Skills,
            BaseStats = new ChampionBaseStats { Hp = 600 },
            StatsPerLevel = new ChampionStatsPerLevel { Hp = 90 },
        };
        // baseHp = LevelGrowth.Stat(600, 90, 12) = 1486.05 (real growth curve), bonusHp = 3000 -
        // 1486.05 = 1513.95. AP=100 so the AP-ratio part[2] resolves to a non-zero, verifiable
        // value (proves it isn't silently dropped).
        var stats = new ActivePlayerStats { MaxHealth = 3000, AbilityPower = 100 };

        double? damage = SkillDamage.ComputeCalcDamage(tahmKench, "P", "TotalDamage", stats, level: 12);

        Assert.NotNull(damage);
        double baseHp = LevelGrowth.Stat(600, 90, 12);   // 1486.05
        double bonusHp = 3000 - baseHp;                  // 1513.95
        double apRatio = bonusHp * 0.0125 * 0.01;      // ~0.18924
        double expected = 65.0 + bonusHp * 0.04 + 100.0 * apRatio; // 65 + 60.558 + 18.924 = 144.482
        Assert.Equal(expected, damage!.Value, precision: 3);
        // Guard: before this session's mStat=12 mapping, this calc threw KeyNotFoundException
        // (caught by ComputeCalcDamage) and returned null — never a phantom 0 or the wrong
        // (total-HP) number. Confirm it is neither of those now that it resolves.
        Assert.NotEqual(65.0, damage!.Value, precision: 3);            // not the level-only term alone
        Assert.NotEqual(65.0 + bonusHp * 0.04, damage!.Value, precision: 3); // AP-ratio part[2] DID resolve
        double totalHpWouldGive = 65.0 + 3000.0 * 0.04 + 100.0 * (3000.0 * 0.000125);
        Assert.NotEqual(totalHpWouldGive, damage!.Value, precision: 3); // not mistakenly using TOTAL health
    }

    [Fact]
    public void ResolveHpPercentCalc_KSanteP_UsesBonusArmorAndBonusMr_NotTotal()
    {
        // KSanteP.mSpellCalculations "MaxHealthDamagePercent" mFormulaParts =
        //   NamedDataValue(MaxHealthDamagePercentBase=0.01)
        //   + StatByNamedDataValue(mStat=1 Armor, mStatFormula=2, MaxHealthDamageResistRatio=0.0001)
        //   + StatByNamedDataValue(mStat=6 MR,    mStatFormula=2, MaxHealthDamageResistRatio=0.0001)
        // -- a %maxHP fraction sourced via the new SkillHit.hpPercentCalc mechanism (see
        // PercentHpDamageTests' Skarner P tests), whose Armor/MR terms need BONUS (not total)
        // Armor/MR (SkillDamage.BuildStatResolver additive fix). Give K'Sante known base
        // Armor/MR so bonus < total and the split is observable (real growth curve, not
        // naive linear):
        //   baseArmor = LevelGrowth.Stat(30, 4, 6) = 45.8, bonusArmor = 130 - 45.8 = 84.2
        //   baseMr    = LevelGrowth.Stat(30, 3, 6) = 41.85, bonusMr    = 95 - 41.85 = 53.15
        var loaded = LoadChampionFromBin("KSante");
        var ksante = new ChampionData
        {
            Id = "KSante",
            Name = "KSante",
            Skills = loaded.Skills,
            BaseStats = new ChampionBaseStats { Armor = 30, Mr = 30 },
            StatsPerLevel = new ChampionStatsPerLevel { Armor = 4, Mr = 3 },
        };
        var stats = new ActivePlayerStats { Armor = 130, MagicResist = 95 };

        double? fraction = SkillDamage.ResolveHpPercentCalc(ksante, "P", "MaxHealthDamagePercent", stats, level: 6);

        Assert.NotNull(fraction);
        // Read bonusArmor/bonusMr via the SAME BuildStatResolver ResolveHpPercentCalc uses
        // internally, so the expected value is bit-identical (not just precision-close) to
        // production's floating-point arithmetic order.
        var resolve = SkillDamage.BuildStatResolver(stats, ksante, level: 6);
        double bonusArmor = resolve(1, 2); // ~84.2 (130 - LevelGrowth.Stat(30, 4, 6))
        double bonusMr = resolve(6, 2);    // ~53.15 (95 - LevelGrowth.Stat(30, 3, 6))
        double expected = 0.01 + 0.0001 * bonusArmor + 0.0001 * bonusMr; // ~0.023735
        // precision 4, not 5: the BIN's real MaxHealthDamageResistRatio is a float32-truncated
        // 9.999999747378752e-5 (not exactly 0.0001), which lands this expected value within a
        // few×1e-10 of the 5th-decimal rounding boundary — precision 5 flips on that noise.
        Assert.Equal(expected, fraction!.Value, precision: 4);
        // Guard: the old always-TOTAL-Armor/MR behaviour would have over-estimated:
        // 0.01 + 0.0001*130 + 0.0001*95 = 0.0325, NOT 0.023735.
        Assert.NotEqual(0.01 + 0.0001 * 130 + 0.0001 * 95, fraction!.Value, precision: 4);
    }

    // ── 2b. REAL ability rank is used (not always max rank) ──────────────────────────

    [Fact]
    public void ComputeNodeDamage_AatroxQ_UsesRealRank_LowerRankLowerDamage()
    {
        var aatrox = LoadChampionFromBin("Aatrox");
        // QDamage = QBaseDamage[rank] + QTotalADRatio[rank] * totalAD (rank indexes the arrays).
        // QBaseDamage   = [-5,10,25,40,55,70,...]  -> rank1=10,  rank5=70
        // QTotalADRatio = [0.525,0.60,0.675,0.75,0.825,0.90,...] -> rank1=0.60, rank5=0.90
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityQ = 1 }; // real rank 1

        double? rank1 = SkillDamage.ComputeNodeDamage(aatrox, "Q", stats);

        Assert.NotNull(rank1);
        Assert.Equal(10.0 + 0.60 * 100.0, rank1!.Value, precision: 4); // 70, the rank-1 value
        // Guard: the old always-max-rank behaviour returned the rank-5 value (160).
        Assert.NotEqual(70.0 + 0.90 * 100.0, rank1!.Value, precision: 4);
    }

    [Fact]
    public void ComputeNodeDamage_NotYetLeveledSlot_ReturnsNull_WhenAbilityDataPresent()
    {
        var aatrox = LoadChampionFromBin("Aatrox");
        // Abilities parsed (Q leveled) but W not yet leveled (AbilityW == 0). Per the rank-0
        // guideline (2026-07-16): a canonical Q/W/E/R at real rank 0 can't be cast, so it deals
        // no damage — the node contributes nothing rather than flooring to a rank-1 preview.
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityQ = 3, AbilityW = 0 };

        double? wDamage = SkillDamage.ComputeNodeDamage(aatrox, "W", stats);

        Assert.Null(wDamage);
        // Sanity: explicitly leveling W to 1 does produce a value (the rule is rank-0-specific,
        // not "W never computes").
        double? wAtRank1 = SkillDamage.ComputeNodeDamage(
            aatrox, "W", stats with { AbilityW = 1 });
        Assert.NotNull(wAtRank1);
    }

    [Fact]
    public void ComputeNodeDamage_NoAbilityData_FallsBackToMaxRank()
    {
        var aatrox = LoadChampionFromBin("Aatrox");
        // Every ability rank 0 (e.g. abilities block absent / synthetic stats) -> MaxRank
        // fallback (Aatrox Q MaxRank 5): 70 + 0.90*100 = 160.
        var stats = new ActivePlayerStats { AttackDamage = 100 };

        double? damage = SkillDamage.ComputeNodeDamage(aatrox, "Q", stats);

        Assert.NotNull(damage);
        Assert.Equal(70.0 + 0.90 * 100.0, damage!.Value, precision: 4); // 160, the max-rank value
    }

    // ── 3. primary-damage-calc heuristic ─────────────────────────────────────────────

    [Fact]
    public void FindPrimaryDamageCalc_PicksExpectedCalculationPerSkill()
    {
        var aatrox = LoadChampionFromBin("Aatrox");
        var annie = LoadChampionFromBin("Annie");
        var zed = LoadChampionFromBin("Zed");

        Assert.Equal("QDamage", SkillDamage.FindPrimaryDamageCalc(aatrox.Skills["Q"], "Q"));
        Assert.Equal("TotalDamage", SkillDamage.FindPrimaryDamageCalc(annie.Skills["Q"], "Q"));
        Assert.Equal("RCalculatedDamage", SkillDamage.FindPrimaryDamageCalc(zed.Skills["R"], "R"));
        // Aatrox E has only a heal calc ("TotalEVamp") — no primary damage.
        Assert.Null(SkillDamage.FindPrimaryDamageCalc(aatrox.Skills["E"], "E"));
    }

    // ── 4. end-to-end ComboRunner: non-zero total = summed per-skill BIN damage ───────

    [Fact]
    public void ComboTrigger_SkillNodes_PublishesNonZeroTotal_MatchingSummedBinDamage()
    {
        var annie = LoadChampionFromBin("Annie");
        ChampionRepository.Initialize(new[] { annie });

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        // Annie combo: Q then W, both skill nodes with zero-damage templates (as the
        // palette stores them). AP = 100, no enemy on the board -> FallbackDefender has
        // 0 armor/MR, so post-mitigation total == raw summed BIN damage.
        var draft = editor.CreateCombo("Annie", "burst");
        editor.AddNode(draft.Id, SkillNode("Q"));
        editor.AddNode(draft.Id, SkillNode("W"));
        editor.SaveCombo(draft.Id);

        var stats = new ActivePlayerStats { AbilityPower = 100, AttackDamage = 0 };
        double expected = Math.Round(
            SkillDamage.ComputeNodeDamage(annie, "Q", stats)!.Value
            + SkillDamage.ComputeNodeDamage(annie, "W", stats)!.Value, 2);

        var snap = BuildActiveOnlySnapshot("Annie", stats);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();

        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        // UI.COMBO_RESULT now carries a ComboHudResult wrapper (T4); unwrap its .Result.
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });

        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");

        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        Assert.True(received!.TotalDamage > 0, "combo total damage must be non-zero (the flagship fix)");
        Assert.Equal(expected, received.TotalDamage, precision: 2);
        // Hand anchor: Q(260+0.8*100=340) + W(230+0.8*100=310) = 650.
        Assert.Equal(650.0, received.TotalDamage, precision: 2);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    // Use the combo editor's real saved id format "{slot}_{n}" (ComboSettingsView clones palette
    // chips with a per-drop suffix so a skill can repeat) — NOT a bare "Q" — so this exercises the
    // slot-recovery ComboRunner does. A bare-slot node would have hidden the "damage stays 0" bug.
    private static ComboNode SkillNode(string slot) => new(
        Id: $"{slot}_0",
        NodeType: ComboNodeType.Skill,
        Name: slot,
        Cooldown: 0,
        Mana: 0,
        Damage: 0,
        DamageType: ComboDamageType.Magic,
        RatioAD: 0,
        RatioBonusAD: 0,
        RatioAP: 0,
        CastTime: 0,
        Delay: 0,
        TravelTime: 0);

    /// <summary>Snapshot with only the active player (no enemy) so BuildDefender falls back
    /// to a 0-armor/0-MR defender and post-mitigation damage equals raw BIN damage.</summary>
    private static GameSnapshot BuildActiveOnlySnapshot(string championName, ActivePlayerStats stats)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = 6,
            PlayerCount = 1,
            Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = championName;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 6;
        snap.Players[0].IsDead = false;
        return snap;
    }
}
