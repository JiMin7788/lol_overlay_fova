using Overlay.Core.ChampionDb;

namespace Overlay.Core.Tests;

/// <summary>
/// Exercises ChampionBinParser/FormulaInterpreter against the real, checked-in
/// CommunityDragon BIN fixtures (Data/communitydragon/*.bin.json) — verifying the M11
/// AD/AP-ratio gap (see docs/reports/reviewer/M11_review.md) is actually closed, not
/// just that parsing doesn't throw.
/// </summary>
public class ChampionBinTests
{
    private static string FixturePath(string champion)
        => Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{champion}.bin.json");

    [Fact]
    public void ParseChampion_Aatrox_ExtractsQDamageDataValuesAndFormula()
    {
        var json = File.ReadAllText(FixturePath("aatrox"));
        var skills = ChampionBinParser.ParseChampion("Aatrox", json);

        Assert.True(skills.ContainsKey("Q"));
        var q = skills["Q"];
        Assert.Equal(new double[] { -5, 10, 25, 40, 55, 70, 85 }, q.DataValues["QBaseDamage"]);
        Assert.True(q.SpellCalculations.ContainsKey("QDamage"));
    }

    [Fact]
    public void ParseChampion_ExposesExtraFormSpells_ForMultiFormChampions()
    {
        // M22 Phase 1: transform/stance/weapon/sub-spell objects that are NOT the CharacterRecord's
        // Q/W/E/R spellNames must still be exposed (keyed by their BIN leaf name) so a curated slot's
        // SkillHit.BinSpell can resolve calcs against them. Jayce's cannon Q lives in the separate
        // spell object "JayceShockBlast" (path .../JayceShockBlastAbility/JayceShockBlast).
        var json = File.ReadAllText(FixturePath("jayce"));
        var skills = ChampionBinParser.ParseChampion("Jayce", json);

        // Canonical slots still present and unchanged (additive change).
        Assert.True(skills.ContainsKey("Q"));
        Assert.True(skills.ContainsKey("R"));

        // The cannon-form ability is now exposed under its leaf name with its own damage calc.
        Assert.True(skills.ContainsKey("JayceShockBlast"),
            "extra (non-QWER) form spell should be exposed for binSpell resolution");
        Assert.True(skills["JayceShockBlast"].SpellCalculations.ContainsKey("Damage"));

        // And that calc evaluates to a positive number (a real base+ratio line), proving the
        // exposed spell is actually usable by the damage engine, not just present.
        var cannonQ = new SkillData
        {
            Key = "JayceShockBlast",
            DataValues = skills["JayceShockBlast"].DataValues,
            SpellCalculations = skills["JayceShockBlast"].SpellCalculations,
        };
        var dmg = FormulaInterpreter.Evaluate(cannonQ, "Damage", rank: 1, statResolver: (_, _) => 100.0);
        Assert.True(dmg > 0, "cannon Q 'Damage' should resolve to a positive number");

        // Lean invariant: every EXTRA (non-Q/W/E/R/P) spell key carries at least one calc — a
        // pure-utility spell with no mSpellCalculations is skipped by ParseExtraSpells.
        var canonical = new HashSet<string> { "P", "Q", "W", "E", "R" };
        foreach (var (key, data) in skills)
            if (!canonical.Contains(key))
                Assert.NotEmpty(data.SpellCalculations);
    }

    [Fact]
    public void ParseChampion_ExposesHashedWeaponSpells_Aphelios()
    {
        // M22 Phase 5: Aphelios' per-weapon Q spells live at TOP-LEVEL HASHED keys (not under
        // Characters/Aphelios/Spells/). ParseExtraSpells must expose them by hash so a curated slot's
        // BinSpell can resolve them — Calibrum's Q is at "{9501e989}" with a "SpellDamage" calc.
        var json = File.ReadAllText(FixturePath("aphelios"));
        var skills = ChampionBinParser.ParseChampion("Aphelios", json);

        Assert.True(skills.ContainsKey("{9501e989}"), "Calibrum Q hashed spell should be exposed");
        Assert.True(skills["{9501e989}"].SpellCalculations.ContainsKey("SpellDamage"));

        var calibrumQ = new SkillData
        {
            Key = "{9501e989}",
            DataValues = skills["{9501e989}"].DataValues,
            SpellCalculations = skills["{9501e989}"].SpellCalculations,
        };
        var dmg = FormulaInterpreter.Evaluate(calibrumQ, "SpellDamage", rank: 1, statResolver: (_, _) => 100.0);
        Assert.True(dmg > 0, "Calibrum Q 'SpellDamage' should resolve to a positive number");
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_AatroxQDamage_MatchesRealBinData()
    {
        var json = File.ReadAllText(FixturePath("aatrox"));
        var binSkills = ChampionBinParser.ParseChampion("Aatrox", json);
        var skill = new SkillData
        {
            Key = "Q",
            DataValues = binSkills["Q"].DataValues,
            SpellCalculations = binSkills["Q"].SpellCalculations,
        };

        // Rank 1: QBaseDamage[1] + QTotalADRatio[1] * statResolver(stat id for the AD
        // ratio term). The exact game-stat meaning of the raw BIN stat id is not
        // published by Riot (see CalculationPart.Stat), so this test supplies a fixed
        // stand-in AD value to prove the formula tree evaluates arithmetically correctly
        // end-to-end (DataValue lookup + stat multiplication + summation), not that the
        // stat-id mapping itself is semantically resolved.
        var result = FormulaInterpreter.Evaluate(skill, "QDamage", rank: 1, statResolver: (_, _) => 100.0);

        // QBaseDamage[1] = 10, QTotalADRatio[1] = 0.6 (60%) -> 10 + 0.6 * 100 = 70
        Assert.Equal(70.0, result, precision: 5);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_GameCalculationModified_AppliesMultiplier()
    {
        var json = File.ReadAllText(FixturePath("aatrox"));
        var binSkills = ChampionBinParser.ParseChampion("Aatrox", json);
        var skill = new SkillData
        {
            Key = "Q",
            DataValues = binSkills["Q"].DataValues,
            SpellCalculations = binSkills["Q"].SpellCalculations,
        };

        // QEdgeDamage is a GameCalculationModified wrapping QDamage, SCALED by its mMultiplier
        // = SumOfSubParts(1 + QSweetSpotBonus). QSweetSpotBonus = 0.75, so the edge (sweet-spot)
        // hit deals 1.75× the base Q — the multiplier must be applied, not dropped. (A prior
        // version of this test asserted equality, which only held because the interpreter
        // silently ignored mMultiplier — the same under-counting class the combo fixes target.)
        var direct = FormulaInterpreter.Evaluate(skill, "QDamage", rank: 1, statResolver: (_, _) => 100.0);
        var modified = FormulaInterpreter.Evaluate(skill, "QEdgeDamage", rank: 1, statResolver: (_, _) => 100.0);

        Assert.Equal(direct * 1.75, modified, precision: 5);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_KhazixE_ResolvesEffectValueCalculationPart()
    {
        // Khazix E "TotalDamage" = EffectValueCalculationPart(mEffectIndex 1) + 0.4×(AD stat).
        // The base lives in the spell's effect-amount table (mSpell.mEffectAmount), NOT in
        // DataValues — the EffectValueCalculationPart path T2d added. mEffectAmount[0].value =
        // [30, 65, 100, 135, 170, 205, 240]; mEffectIndex is 1-based, so index 1 → array 0,
        // and the rank clamps like a DataValue (rank 5 → element [5] = 205).
        var json = File.ReadAllText(FixturePath("khazix"));
        var binSkills = ChampionBinParser.ParseChampion("Khazix", json);
        var skill = new SkillData
        {
            Key = "E",
            DataValues = binSkills["E"].DataValues,
            SpellCalculations = binSkills["E"].SpellCalculations,
            EffectAmounts = binSkills["E"].EffectAmounts,
        };

        // Fixed stand-in stat value (100) for the 0.4× AD term; the raw stat-id meaning is
        // unpublished (see CalculationPart.Stat) so the resolver supplies a constant.
        var result = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 5, statResolver: (_, _) => 100.0);

        // effectAmount[0][5] = 205, plus 0.4 × 100 = 40 → 245.
        Assert.Equal(245.0, result, precision: 5);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_RyzeQ_ResolvesAbilityResourceByCoefficient()
    {
        // Ryze Q "QDamageCalc" = BaseDamage + 0.55×AP + AbilityResourceByCoefficient(mCoefficient
        // ≈ 0.02) × caster max mana. The mana term is the AbilityResourceByCoefficientCalculationPart
        // path T2d added; casterResource threads in ActivePlayerStats.ResourceMax.
        var json = File.ReadAllText(FixturePath("ryze"));
        var binSkills = ChampionBinParser.ParseChampion("Ryze", json);
        var q = binSkills["Q"];
        var skill = new SkillData
        {
            Key = "Q",
            DataValues = q.DataValues,
            SpellCalculations = q.SpellCalculations,
        };

        // Read the exact BIN coefficient (float32 ≈ 0.02) to assert the exact product.
        var manaPart = q.SpellCalculations["QDamageCalc"].FormulaParts
            .Single(p => p.PartKind == CalculationPart.Kind.AbilityResourceByCoefficient);
        double coeff = manaPart.Coefficient!.Value;
        const double mana = 1000.0;

        // AP resolver returns 0 to isolate BaseDamage + the mana term. Rank 1 → BaseDamage[1] = 75.
        var result = FormulaInterpreter.Evaluate(
            skill, "QDamageCalc", rank: 1, statResolver: (_, _) => 0.0, casterResource: mana);

        Assert.Equal(75.0 + coeff * mana, result, precision: 6);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_NasusQ_ResolvesBuffCounterByStackCount()
    {
        // (M25 §11.G) Nasus Q "TotalDamage" = BonusDamage + 1.0×(total AD) +
        // BuffCounterByCoefficient(mCoefficient 1.0) × Siphoning-Strike stacks. The stack term is
        // the BuffCounterByCoefficient path this pass added; stackCount threads the user's "몇 스택"
        // knob and defaults to 0 = the un-stacked floor (so the formula resolves to its base when no
        // stack count is supplied). Previously this whole calc threw (unrecognized part kind).
        var json = File.ReadAllText(FixturePath("nasus"));
        var binSkills = ChampionBinParser.ParseChampion("Nasus", json);
        var q = binSkills["Q"];
        var skill = new SkillData { Key = "Q", DataValues = q.DataValues, SpellCalculations = q.SpellCalculations };

        // Read the exact BIN per-stack coefficient (1.0) to assert the exact product.
        var stackPart = q.SpellCalculations["TotalDamage"].FormulaParts
            .Single(p => p.PartKind == CalculationPart.Kind.BuffCounterByCoefficient);
        double perStack = stackPart.Coefficient!.Value;

        // AD resolver returns 0 to isolate BonusDamage + the stack term. Rank 5 → BonusDamage[5] = 120.
        double floor = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 5, statResolver: (_, _) => 0.0, stackCount: 0);
        double stacked = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 5, statResolver: (_, _) => 0.0, stackCount: 12);

        Assert.Equal(120.0, floor, precision: 5);                       // 0 stacks = the un-stacked floor
        Assert.Equal(120.0 + 12 * perStack, stacked, precision: 5);     // +12 × per-stack coefficient
    }

    [Fact]
    public void FormulaInterpreter_ByCharLevel_IndexesByChampionLevel_NotAbilityRank_PantheonMortalWill()
    {
        // (M25 §12③) Pantheon Q "EmpoweredDamageCalc" (Mortal Will) = ByCharLevelInterpolation(20->240)
        // + 1.15 bonus AD. It is "based on LEVEL" (wiki "Mortal Will: 20-240"), so it must scale with
        // CHAMPION level, not the Q ability rank. Before the fix a Q-slot calc was indexed by ability
        // rank (1-5), so the interpolation never passed ~24% of the way -> a large under-count at high
        // levels (rank 5 resolved ~72, not 240).
        var json = File.ReadAllText(FixturePath("pantheon"));
        var q = ChampionBinParser.ParseChampion("Pantheon", json)["Q"];
        var skill = new SkillData { Key = "Q", DataValues = q.DataValues, SpellCalculations = q.SpellCalculations };

        // bonus-AD resolver returns 0 to isolate the ByCharLevel interpolation term.
        double atL1 = FormulaInterpreter.Evaluate(skill, "EmpoweredDamageCalc", rank: 5, statResolver: (_, _) => 0.0, championLevel: 1);
        double atL18 = FormulaInterpreter.Evaluate(skill, "EmpoweredDamageCalc", rank: 5, statResolver: (_, _) => 0.0, championLevel: 18);
        Assert.Equal(20.0, atL1, precision: 3);    // level 1 = start of the 20->240 range
        Assert.Equal(240.0, atL18, precision: 3);  // level 18 = end

        // Backward compat: with NO championLevel, ByCharLevel* still indexes by `rank` (rank 5 -> ~24%).
        double byRank = FormulaInterpreter.Evaluate(skill, "EmpoweredDamageCalc", rank: 5, statResolver: (_, _) => 0.0);
        Assert.Equal(20.0 + 220.0 * (4.0 / 17.0), byRank, precision: 3);
    }

    [Fact]
    public void FormulaInterpreter_ByCharLevel_IndexesByChampionLevel_PykeRExecuteBase()
    {
        // (M25 §12③) Pyke R "RBaseDamage" = ByCharLevelBreakpoints(250 @L1; +40/lvl@7, +30@10, +20@12,
        // +10@17) -> 250 at L6 (R unlock) up to 550 at L18. It is level-scaled; the old rank-indexing
        // pinned it at 250 (R rank 1-3 never reaches the L7 breakpoint). The fix also resolves the prior
        // inconsistency with the execute_effects baseByLevel [250..550] curve for the same R.
        var json = File.ReadAllText(FixturePath("pyke"));
        var r = ChampionBinParser.ParseChampion("Pyke", json)["R"];
        var skill = new SkillData { Key = "R", DataValues = r.DataValues, SpellCalculations = r.SpellCalculations };

        Assert.Equal(250.0, FormulaInterpreter.Evaluate(skill, "RBaseDamage", rank: 3, statResolver: (_, _) => 0.0, championLevel: 6), precision: 3);
        Assert.Equal(550.0, FormulaInterpreter.Evaluate(skill, "RBaseDamage", rank: 3, statResolver: (_, _) => 0.0, championLevel: 18), precision: 3);
        // Backward compat: without championLevel, rank 3 pins it at the L1 value (250).
        Assert.Equal(250.0, FormulaInterpreter.Evaluate(skill, "RBaseDamage", rank: 3, statResolver: (_, _) => 0.0), precision: 3);
    }

    [Fact]
    public void ParseChampion_AllFiveSampleChampions_ExtractAllFourAbilitySlotsPlusPassive()
    {
        // T3.1: in addition to Q/W/E/R the parser now extracts the champion PASSIVE ("P") from
        // the CharacterRecord's mCharacterPassiveSpell. All five sample champions have a parseable
        // passive spell, so the extracted slot set is exactly {P, Q, W, E, R}.
        foreach (var champion in new[] { "Aatrox", "Ahri", "Annie", "Zed", "Jinx" })
        {
            var json = File.ReadAllText(FixturePath(champion.ToLowerInvariant()));
            var skills = ChampionBinParser.ParseChampion(champion, json);
            Assert.Equal(new[] { "E", "P", "Q", "R", "W" }, skills.Keys.OrderBy(k => k));
        }
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_LissandraPassive_AppliesInitialAndPerLevelBreakpointGrowth()
    {
        // Lissandra passive (Iceborn Subjugation) "TotalDamage" is a ByCharLevelBreakpoints
        // part: mLevel1Value=120, mInitialBonusPerLevel=20, one breakpoint @ level 13 with
        // mBonusPerLevelAtAndAfter=30. Before this fix, ChampionBinParser dropped both the
        // flat mAdditionalBonusAtThisLevel/mBonusPerLevelAtAndAfter merge AND
        // mInitialBonusPerLevel entirely, so growth after level 1 was never applied at all.
        // Hand-computed per the champion's own published tooltip range "120 - 520 (based on
        // level)" (wiki.leagueoflegends.com/en-us/Lissandra, Iceborn Subjugation): the
        // per-level rate is 20 for levels 2-12 (11 steps) then 30 for levels 13-18 (6 steps
        // after the level-13 breakpoint takes effect on the step reaching level 13):
        //   value(18) = 120 + 20*11 + 30*6 = 520.
        var json = File.ReadAllText(FixturePath("lissandra"));
        var binSkills = ChampionBinParser.ParseChampion("Lissandra", json);
        var passive = binSkills["P"];
        var skill = new SkillData
        {
            Key = "P",
            DataValues = passive.DataValues,
            SpellCalculations = passive.SpellCalculations,
        };

        var atLevel1 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 1, statResolver: (_, _) => 0.0);
        var atLevel18 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 18, statResolver: (_, _) => 0.0);

        Assert.Equal(120.0, atLevel1, precision: 5);
        Assert.Equal(520.0, atLevel18, precision: 5);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_SkarnerPassivePercentHealthDamage_AppliesPlainMultiplier()
    {
        // Skarner passive "PercentHealthDamage" is a PLAIN GameCalculation (mFormulaParts =
        // ByCharLevelInterpolation 5.0..9.0) that ALSO carries mMultiplier = 0.01 (converts
        // the tooltip percent-points value into a fraction). Before this fix, ChampionBinParser
        // only captured mMultiplier on the GameCalculationModified branch, so this plain
        // calculation silently evaluated to 5..9 instead of 0.05..0.09 -- a 100x overshoot.
        var json = File.ReadAllText(FixturePath("skarner"));
        var binSkills = ChampionBinParser.ParseChampion("Skarner", json);
        var passive = binSkills["P"];
        var skill = new SkillData
        {
            Key = "P",
            DataValues = passive.DataValues,
            SpellCalculations = passive.SpellCalculations,
        };

        // ByCharLevelInterpolation clamps rank-1 to [0,17]/17.0; rank 1 -> t=0 -> start=5.0,
        // scaled by the 0.01 multiplier -> 0.05.
        var result = FormulaInterpreter.Evaluate(skill, "PercentHealthDamage", rank: 1, statResolver: (_, _) => 0.0);

        Assert.Equal(0.05, result, precision: 5);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_TaricPassive_ResolvesProductOfSubPartsMPart1MPart2Shape()
    {
        // Taric passive (Bravado) "TotalDamage" = ProductOfSubPartsCalculationPart(
        //   ByCharLevelInterpolationCalculationPart(25.0 -> 93.0) x
        //   NamedDataValueCalculationPart("BaseDamageMultiplierForModesBalance"))
        // + StatByNamedDataValueCalculationPart(Armor, dataValue="ArmorDamageValue").
        // The product part's BIN shape is "mPart1"/"mPart2", not the "mSubparts" array
        // ParseSubParts previously required exclusively -- before this fix the product
        // parsed to zero subparts and Aggregate's 1.0 seed made the whole product term
        // silently evaluate to a bogus 1.0 instead of the real interpolated*multiplier value.
        var json = File.ReadAllText(FixturePath("taric"));
        var binSkills = ChampionBinParser.ParseChampion("Taric", json);
        var passive = binSkills["P"];
        var skill = new SkillData
        {
            Key = "P",
            DataValues = passive.DataValues,
            SpellCalculations = passive.SpellCalculations,
        };

        // BaseDamageMultiplierForModesBalance is a game-mode balance knob; in Summoner's
        // Rift BIN data it is 1.0 at every rank (confirmed via the parsed DataValues below),
        // so the product collapses to just the ByCharLevelInterpolation(25..93) term.
        Assert.All(
            passive.DataValues["BaseDamageMultiplierForModesBalance"],
            v => Assert.Equal(1.0, v, precision: 5));

        // Armor stat resolver fixed at 0 to isolate the product term.
        var atLevel1 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 1, statResolver: (_, _) => 0.0);
        var atLevel18 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 18, statResolver: (_, _) => 0.0);

        // ByCharLevelInterpolation: start=25, end=93, t=(rank-1)/17.
        // Level 1: t=0 -> 25 * 1.0 = 25. Level 18: t=1 -> 93 * 1.0 = 93.
        Assert.Equal(25.0, atLevel1, precision: 5);
        Assert.Equal(93.0, atLevel18, precision: 5);

        // Sanity check the pre-fix bug is actually gone: the old code path returned 1.0
        // (the empty-product Aggregate seed) regardless of level.
        Assert.NotEqual(1.0, atLevel1);
        Assert.NotEqual(1.0, atLevel18);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_RivenPassive_ResolvesStatBySubPartWithInterpolation()
    {
        // Riven passive (Runic Blade) "TotalDamage" = StatBySubPartCalculationPart(mStat=2, no
        // mStatFormula = TOTAL AD, mSubpart=ByCharLevelInterpolationCalculationPart(0.30 ->
        // 0.45)). Matches the live tooltip "bonus physical damage equal to 30 - 45% (based on
        // level) of total attack damage" (wiki.leagueoflegends.com/en-us/Riven, Runic Blade).
        // Before this fix, StatBySubPartCalculationPart parsed to Kind.Unknown and evaluating
        // it threw NotSupportedException, blocking Riven's passive entirely.
        var json = File.ReadAllText(FixturePath("riven"));
        var binSkills = ChampionBinParser.ParseChampion("Riven", json);
        var passive = binSkills["P"];
        var skill = new SkillData
        {
            Key = "P",
            DataValues = passive.DataValues,
            SpellCalculations = passive.SpellCalculations,
        };

        const double totalAd = 200.0;
        var atLevel1 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 1, statResolver: (_, _) => totalAd);
        var atLevel18 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 18, statResolver: (_, _) => totalAd);

        // Level 1: t=0 -> 0.30 * 200 = 60. Level 18: t=1 -> 0.45 * 200 = 90.
        Assert.Equal(60.0, atLevel1, precision: 5);
        Assert.Equal(90.0, atLevel18, precision: 5);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_UrgotPassive_ResolvesStatBySubPartWithBreakpoints()
    {
        // Urgot passive "ADDamage" = StatBySubPartCalculationPart(mStat=2, no mStatFormula =
        // TOTAL AD, mSubpart=ByCharLevelBreakpointsCalculationPart(Level1Value=0.40, one-off
        // +0.12 bumps at levels 6/9/11/13/15)). Confirms StatBySubPart nests correctly with a
        // DIFFERENT sub-part kind (breakpoints, not interpolation) than the Riven case above.
        var json = File.ReadAllText(FixturePath("urgot"));
        var binSkills = ChampionBinParser.ParseChampion("Urgot", json);
        var passive = binSkills["P"];
        var skill = new SkillData
        {
            Key = "P",
            DataValues = passive.DataValues,
            SpellCalculations = passive.SpellCalculations,
        };

        const double totalAd = 100.0;
        // Level 6: the level-6 breakpoint's one-off bump has just applied -> 0.40+0.12=0.52.
        var atLevel6 = FormulaInterpreter.Evaluate(skill, "ADDamage", rank: 6, statResolver: (_, _) => totalAd);
        // Level 18: all 5 breakpoints (6/9/11/13/15) have applied -> 0.40+0.12*5=1.00 (capped).
        var atLevel18 = FormulaInterpreter.Evaluate(skill, "ADDamage", rank: 18, statResolver: (_, _) => totalAd);

        Assert.Equal(52.0, atLevel6, precision: 5);
        Assert.Equal(100.0, atLevel18, precision: 5);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_LeonaPassive_ResolvesByCharLevelFormulaCalculationPart()
    {
        // Leona passive (Sunlight) "TotalDamage" = a single ByCharLevelFormulaCalculationPart
        // whose raw BIN "values" array is [25,32,39,...,235] (31 entries, step +7). Confirmed
        // against the live tooltip "32 - 151 (based on level) bonus magic damage"
        // (wiki.leagueoflegends.com/en-us/Leona, Sunlight): values[1]=32 (level 1),
        // values[18]=151 (level 18) -- i.e. index = level directly (index 0 unused
        // placeholder), the same convention FormulaInterpreter.LookupDataValue/
        // LookupEffectAmount already use for their per-rank arrays.
        var json = File.ReadAllText(FixturePath("leona"));
        var binSkills = ChampionBinParser.ParseChampion("Leona", json);
        var passive = binSkills["P"];
        var skill = new SkillData
        {
            Key = "P",
            DataValues = passive.DataValues,
            SpellCalculations = passive.SpellCalculations,
        };

        var atLevel1 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 1, statResolver: (_, _) => 0.0);
        var atLevel18 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 18, statResolver: (_, _) => 0.0);

        Assert.Equal(32.0, atLevel1, precision: 5);
        Assert.Equal(151.0, atLevel18, precision: 5);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_KhazixPassive_ResolvesByCharLevelFormulaPlusBonusADRatio()
    {
        // Khazix passive (Taste Their Fear's isolation bonus) "TotalDamage" =
        // ByCharLevelFormulaCalculationPart(values=[10,17,...,220], step +7) +
        // StatByNamedDataValueCalculationPart(mStat=2/mStatFormula=2, dataValue="BonusADRatio").
        // values[1]=17 (level 1), values[18]=10+7*18=136 (level 18), same direct-index-by-level
        // convention verified against Leona above.
        var json = File.ReadAllText(FixturePath("khazix"));
        var binSkills = ChampionBinParser.ParseChampion("Khazix", json);
        var passive = binSkills["P"];
        var skill = new SkillData
        {
            Key = "P",
            DataValues = passive.DataValues,
            SpellCalculations = passive.SpellCalculations,
        };

        // BonusADRatio stand-in stat value of 100 for the AD term (real mapping unpublished --
        // see CalculationPart.Stat), so this proves the by-level base + AD-ratio sum, not the
        // stat-id resolution itself.
        var bonusAdRatio = FormulaInterpreter.EvaluateDataValue(skill, "BonusADRatio", rank: 1);
        var atLevel1 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 1, statResolver: (_, _) => 100.0);

        // 17 (values[1]) + bonusAdRatio * 100.
        Assert.Equal(17.0 + bonusAdRatio * 100.0, atLevel1, precision: 5);
    }

    [Fact]
    public void FormulaInterpreter_Evaluate_LuxPassive_ResolvesByCharLevelFormulaPlusAPRatio()
    {
        // Lux passive (Illumination) "TotalDamage" = ByCharLevelFormulaCalculationPart(
        // values=[20,30,...,320], step +10) + StatByNamedDataValueCalculationPart(dataValue=
        // "APRatio"). values[1]=30 (level 1), values[18]=20+10*18=200 (level 18).
        var json = File.ReadAllText(FixturePath("lux"));
        var binSkills = ChampionBinParser.ParseChampion("Lux", json);
        var passive = binSkills["P"];
        var skill = new SkillData
        {
            Key = "P",
            DataValues = passive.DataValues,
            SpellCalculations = passive.SpellCalculations,
        };

        var apRatio = FormulaInterpreter.EvaluateDataValue(skill, "APRatio", rank: 1);
        var atLevel1 = FormulaInterpreter.Evaluate(skill, "TotalDamage", rank: 1, statResolver: (_, _) => 100.0);

        Assert.Equal(30.0 + apRatio * 100.0, atLevel1, precision: 5);
    }

    [Fact]
    public void ChampionRepository_MergedSkill_NoLongerDefaultsRatiosToZero()
    {
        // Regression guard for the reviewer-flagged gap: SkillData used to hardcode
        // RatioAD/RatioBonusAD/RatioAP = 0 (fields have since been removed entirely —
        // see M11 spec Data Model note). Assert the replacement fields actually carry
        // real BIN data instead of being empty defaults.
        var json = File.ReadAllText(FixturePath("annie"));
        var binSkills = ChampionBinParser.ParseChampion("Annie", json);

        var q = binSkills["Q"];
        Assert.Contains("BaseDamage", q.DataValues.Keys);
        Assert.Contains(q.SpellCalculations["TotalDamage"].FormulaParts,
            p => p.PartKind == CalculationPart.Kind.StatByNamedDataValue && p.DataValue == "APRatio");
    }
}
