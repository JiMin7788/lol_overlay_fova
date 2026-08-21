using System.Text.Json;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// Guards the hand-curated skill_damage files authored in task T2c: the 4 formerly
/// zero-slot champions (Khazix, Ryze, Taric, Zoe) and the priority type-fix set
/// (Lux, Garen, Darius, Yasuo, Yone, Caitlyn, Ezreal, Vayne, Kaisa, Lucian, Jhin,
/// Katarina, Fizz, Syndra, Veigar). Curated files must no longer carry the "auto" marker
/// and must still load; Zoe must now resolve real damage; and the three interpreter-limited
/// zero-slot champions are asserted to still resolve nothing so the limitation is documented
/// rather than silently regressed.
/// </summary>
public class SkillDataCurationTests : IDisposable
{
    private static readonly string[] Slots = { "Q", "W", "E", "R" };

    // Priority type-fix set (Jinx is excluded: it is one of the original hand-curated 5).
    private static readonly string[] PriorityChampions =
    {
        "Lux", "Garen", "Darius", "Yasuo", "Yone", "Caitlyn", "Ezreal", "Vayne", "Kaisa",
        "Lucian", "Jhin", "Katarina", "Fizz", "Syndra", "Veigar",
    };

    // Formerly zero-slot champions, now curated.
    private static readonly string[] ZeroSlotChampions = { "Khazix", "Ryze", "Taric", "Zoe" };

    // Cowork session 2026-07-10: top/jungle/mid/ADC/support batch curated from "auto": true.
    private static readonly string[] July10Batch =
    {
        "Camille", "Renekton", "LeeSin", "Malzahar", "Draven", "Tristana", "Nautilus", "Thresh",
    };

    // Cowork session 2026-07-10, 2nd batch: a different top/jungle/mid/support/ADC spread.
    private static readonly string[] July10SecondBatch =
    {
        "Sett", "Irelia", "Viego", "Amumu", "Rakan", "Blitzcrank", "Morgana", "Ashe",
    };

    // Cowork session 2026-07-10, 3rd batch: mid/top/support/jungle spread.
    private static readonly string[] July10ThirdBatch =
    {
        "Akali", "Fiora", "Nami", "Diana", "Volibear", "XinZhao", "Shen", "MonkeyKing",
    };

    public SkillDataCurationTests()
    {
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
    }

    public void Dispose()
    {
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
    }

    private static string SkillDamageDir => Path.Combine(AppContext.BaseDirectory, "data", "skill_damage");

    private static void InitRepositoryFromCache()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var ddragonRoot = Path.Combine(dataDir, "ddragon");
        var summary = Directory.GetFiles(ddragonRoot, "champion.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).Last();
        ChampionRepository.InitializeFromCache(
            Path.GetDirectoryName(summary)!, Path.Combine(dataDir, "communitydragon"));
    }

    private static ActivePlayerStats SampleStats() => new()
    {
        AttackDamage = 200,
        AbilityPower = 200,
        // Max mana for the mana-scaling AbilityResourceByCoefficient parts (Ryze Q/W/E).
        ResourceMax = 1000,
        AbilityQ = 5,
        AbilityW = 5,
        AbilityE = 5,
        AbilityR = 3,
    };

    // ── every curated file dropped the "auto" marker and still loads ───────────────────

    [Theory]
    [InlineData("Khazix")]
    [InlineData("Ryze")]
    [InlineData("Taric")]
    [InlineData("Zoe")]
    [InlineData("Lux")]
    [InlineData("Garen")]
    [InlineData("Darius")]
    [InlineData("Yasuo")]
    [InlineData("Yone")]
    [InlineData("Caitlyn")]
    [InlineData("Ezreal")]
    [InlineData("Vayne")]
    [InlineData("Kaisa")]
    [InlineData("Lucian")]
    [InlineData("Jhin")]
    [InlineData("Katarina")]
    [InlineData("Fizz")]
    [InlineData("Syndra")]
    [InlineData("Veigar")]
    [InlineData("Camille")]
    [InlineData("Renekton")]
    [InlineData("LeeSin")]
    [InlineData("Malzahar")]
    [InlineData("Draven")]
    [InlineData("Tristana")]
    [InlineData("Nautilus")]
    [InlineData("Thresh")]
    [InlineData("Sett")]
    [InlineData("Irelia")]
    [InlineData("Viego")]
    [InlineData("Amumu")]
    [InlineData("Rakan")]
    [InlineData("Blitzcrank")]
    [InlineData("Morgana")]
    [InlineData("Ashe")]
    [InlineData("Akali")]
    [InlineData("Fiora")]
    [InlineData("Nami")]
    [InlineData("Diana")]
    [InlineData("Volibear")]
    [InlineData("XinZhao")]
    [InlineData("Shen")]
    [InlineData("MonkeyKing")]
    public void CuratedFile_HasNoAutoMarker_AndLoads(string championId)
    {
        var path = Path.Combine(SkillDamageDir, championId + ".json");
        Assert.True(File.Exists(path), $"curated file missing: {championId}.json");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(
            doc.RootElement.TryGetProperty("auto", out _),
            $"{championId}.json must not carry the \"auto\" marker after curation");

        foreach (var slot in Slots)
        {
            var ex = Record.Exception(() => SkillDamageDb.GetHits(championId, slot));
            Assert.Null(ex);
        }
    }

    // ── Zoe now resolves real damage (its calcs were all excluded '...Tooltip' names) ──

    [Fact]
    public void Zoe_ResolvesPositiveDamage_AfterCuration()
    {
        InitRepositoryFromCache();
        var zoe = ChampionRepository.Get("Zoe");
        Assert.NotNull(zoe);

        var stats = SampleStats();
        bool anyPositive = false;
        foreach (var slot in Slots)
        {
            foreach (var hit in SkillDamageDb.GetHits("Zoe", slot) ?? Array.Empty<SkillHit>())
            {
                if (SkillDamage.ComputeCalcDamage(zoe!, slot, hit.Calc, stats, level: 18) is > 0)
                    anyPositive = true;
            }
        }
        Assert.True(anyPositive, "Zoe: no curated slot resolved to a positive damage number");
    }

    // ── Khazix / Ryze / Taric now resolve real damage (T2d interpreter extension) ──────
    //
    // Their damage bases live in CalculationParts the interpreter previously could not
    // evaluate, which task T2d added:
    //   - Khazix Q/W/E and Taric E: EffectValueCalculationPart (mEffectIndex into the spell's
    //     effect-amount table, SkillData.EffectAmounts), and
    //   - Ryze Q/W/E: AbilityResourceByCoefficientCalculationPart (max-mana scaling; the
    //     ResourceMax in SampleStats() drives it).
    // Before T2d any calc containing such a part threw inside ComputeCalcDamage and was caught
    // as null, so this test asserted NO slot resolved (documenting the limitation). T2d closed
    // that gap: each champion now has at least one curated slot that resolves to a positive
    // number. This is the sanctioned contract flip from the earlier "still resolves nothing".

    [Theory]
    [InlineData("Khazix")]
    [InlineData("Ryze")]
    [InlineData("Taric")]
    public void FormerlyInterpreterLimitedChampion_NowResolvesPositiveDamage(string championId)
    {
        InitRepositoryFromCache();
        var champion = ChampionRepository.Get(championId);
        Assert.NotNull(champion);

        var stats = SampleStats();
        bool anyPositive = false;
        foreach (var slot in Slots)
        {
            foreach (var hit in SkillDamageDb.GetHits(championId, slot) ?? Array.Empty<SkillHit>())
            {
                if (SkillDamage.ComputeCalcDamage(champion!, slot, hit.Calc, stats, level: 18) is > 0)
                    anyPositive = true;
            }
        }
        Assert.True(anyPositive,
            $"{championId}: no curated slot resolved to positive damage after T2d interpreter extension");
    }

    // ── priority-set type fixes actually loaded as the intended enum ───────────────────

    [Fact]
    public void PriorityTypeFixes_AreLoadedAsCurated()
    {
        // Spot-check the deliberate type corrections made in T2c so a bad edit is caught.
        Assert.Equal(HitDamageType.Magic, Single("Yasuo", "E").Type);   // Sweeping Blade: magic
        // Spirit Cleave: EQUAL PARTS physical and magic (loop 497). This row used to assert "magic",
        // which was the defect §41 recorded against itself and never fixed — half the ability was
        // meeting magic resist when it should have met armour. Both hits now carry the split, and
        // asserting BOTH halves is the point: typing it wholly as either one is the bug.
        Assert.All(SkillDamageDb.GetHits("Yone", "W")!, h =>
        {
            Assert.Equal(HitDamageType.Physical, h.Type);
            Assert.Equal(HitDamageType.Magic, h.SplitType);
        });
        Assert.Equal(HitDamageType.Magic, Single("Ezreal", "W").Type);  // Essence Flux: magic
        Assert.Equal(HitDamageType.Magic, Single("Ezreal", "E").Type);  // Arcane Shift: magic
        Assert.Equal(HitDamageType.Magic, Single("Ezreal", "R").Type);  // Trueshot Barrage: magic
        Assert.Equal(HitDamageType.Magic, Single("Katarina", "E").Type);// Shunpo: magic
        // Urchin Strike (re-curated loop 221): the single Magic-typed hit was self-contradictory
        // (TotalDamage is a pure 1.0x AD calc). Now two wiki-verified components: the triggered
        // attack (TotalDamage, Physical) + the ability's own AP-scaled bonus (QDamage, Magic).
        var fizzQ = SkillDamageDb.GetHits("Fizz", "Q");
        Assert.NotNull(fizzQ);
        Assert.Equal(2, fizzQ!.Length);
        Assert.Contains(fizzQ, h => h.Calc == "TotalDamage" && h.Type == HitDamageType.Physical);
        Assert.Contains(fizzQ, h => h.Calc == "QDamage" && h.Type == HitDamageType.Magic);
        Assert.Equal(HitDamageType.True, Single("Darius", "R").Type);   // Noxian Guillotine: true
        Assert.Equal(HitDamageType.True, Single("Vayne", "W").Type);    // Silver Bolts: true
        // Dauntless Instinct: physical, not true (§11.E fix loop 106). §16 P-round re-curated P into
        // three stance-split hits (All-Out rider + base-form flat 12 + base-form level-%), all physical.
        Assert.All(SkillDamageDb.GetHits("Ksante", "P")!, h => Assert.Equal(HitDamageType.Physical, h.Type));
        Assert.Equal(7, Single("Garen", "E").Count);                    // Judgment: 7 strikes
    }

    private static SkillHit Single(string championId, string slot)
    {
        var hits = SkillDamageDb.GetHits(championId, slot);
        Assert.NotNull(hits);
        return Assert.Single(hits!);
    }

    // ── newly curated P-slot bonus effects (Janna MS mapping + StatBySubPart fix) ──────
    //
    // Janna's Tailwind needed the new mStat=7 (Move Speed) resolver mapping; Riven/XinZhao/
    // Urgot/Samira/MissFortune's passives needed the new CalculationPart.Kind.StatBySubPart.
    // Both were previously unresolvable (KeyNotFoundException / NotSupportedException, caught
    // by ComputeCalcDamage and turned into null) so GetBonusEffects would have loaded the
    // curated JSON structure fine but every resolve attempt returned null/0.

    [Theory]
    [InlineData("Riven")]
    [InlineData("XinZhao")]
    [InlineData("Urgot")]
    [InlineData("Samira")]
    [InlineData("MissFortune")]
    public void StatBySubPartPassive_ResolvesPositiveDamage(string championId)
    {
        InitRepositoryFromCache();
        var champion = ChampionRepository.Get(championId);
        Assert.NotNull(champion);

        var bonusEffects = SkillDamageDb.GetBonusEffects(championId, "P");
        Assert.NotNull(bonusEffects);
        // Exactly one hit per passive carries a named calc. Urgot's P additionally carries a second
        // hit for Echoing Flames' %maxHP term, which resolves via hpPercentCalc and so has no Calc
        // of its own — select by non-empty Calc rather than assuming a single hit. See Urgot._noteP.
        var hit = Assert.Single(
            Assert.Single(bonusEffects!).Hits.Where(h => !string.IsNullOrEmpty(h.Calc)));

        var stats = SampleStats();
        double? damage = SkillDamage.ComputeCalcDamage(champion!, "P", hit.Calc, stats, level: 18);

        Assert.True(damage is > 0, $"{championId} P/{hit.Calc}: expected positive damage, got {damage}");
    }

    [Fact]
    public void JannaPassiveBonusDamage_ResolvesPositiveDamage_ViaBonusMoveSpeedMapping()
    {
        InitRepositoryFromCache();
        var janna = ChampionRepository.Get("Janna");
        Assert.NotNull(janna);

        var bonusEffects = SkillDamageDb.GetBonusEffects("Janna", "P");
        Assert.NotNull(bonusEffects);
        var hit = Assert.Single(Assert.Single(bonusEffects!).Hits);
        Assert.Equal("BonusDamage", hit.Calc);

        // MoveSpeed above the champion's own base MS is required to see the bonus-MS mapping
        // actually contribute (a stats object with MoveSpeed == 0 would floor to 0 either way).
        var stats = SampleStats();
        stats.MoveSpeed = 500;

        double? damage = SkillDamage.ComputeCalcDamage(janna!, "P", hit.Calc, stats, level: 18);

        Assert.True(damage is > 0, $"Janna P/BonusDamage: expected positive damage, got {damage}");
    }

    // ── loop 38: castCount schema — Ahri R is NOT a same-target recast ─────────────────
    //
    // The castCount/maxCasts schema field expands a slot into N independently-mitigated casts,
    // but only for a genuinely SAME-target-recastable skill. Ahri R ("Spirit Rush") was briefly
    // modeled as castCount:3 and then corrected per the user: each of its up-to-3 dashes fires
    // ONE orb that damages only the FIRST enemy it passes through — one orb PER target, not the
    // same target hit 3× — so a single-target combo shows exactly one orb. Ahri R therefore
    // carries NO castCount and must default to 1, like every other uncurated slot.

    [Fact]
    public void AhriR_IsNotSameTargetRecast_DefaultsToOneCast()
    {
        Assert.Equal(1, SkillDamageDb.GetCastCount("Ahri", "R"));
    }

    [Theory]
    [InlineData("Ahri", "Q")]
    [InlineData("Ahri", "W")]
    [InlineData("Ahri", "E")]
    [InlineData("Zed", "R")]
    public void UncuratedCastCount_DefaultsToOne(string championId, string slot)
    {
        Assert.Equal(1, SkillDamageDb.GetCastCount(championId, slot));
    }

    // ── Cowork session 2026-07-10 batch: multi-hit / blocked-slot / mixed-type shapes ──

    [Fact]
    public void RenektonW_RuthlessPredator_IsTwoStrikes()
    {
        var hit = Single("Renekton", "W");
        Assert.Equal("HitDamage", hit.Calc);
        Assert.Equal(2, hit.Count);
    }

    [Fact]
    public void DravenR_WhirlingDeath_IsTwoAxeThrows()
    {
        var hit = Single("Draven", "R");
        Assert.Equal("RCalculatedDamage", hit.Calc);
        Assert.Equal(2, hit.Count);
    }

    /// <summary>(loop 482) The mark and the recast are two CASTS, so they are two slots. They used to
    /// be two hits on Q, which meant a Q node always scored the dash as well — there was no way to say
    /// Sonic Wave hit and Resonating Strike was never fired.</summary>
    [Fact]
    public void LeeSinQ_IsTheMark_AndQ2IsTheRecast()
    {
        var q = SkillDamageDb.GetHits("LeeSin", "Q");
        Assert.NotNull(q);
        Assert.Equal("InitialDamage", Assert.Single(q!).Calc);

        var q2 = Assert.Single(SkillDamageDb.GetHits("LeeSin", "Q2")!);
        Assert.Equal("RecastDamage", q2.Calc);
        // …and the recast carries the wiki's 0-100% missing-health scaling, which the folded
        // curation had no way to express.
        Assert.Equal("Q2MaxMissingHealthMod", q2.MissingHpBonusDataValue);
    }

    [Fact]
    public void CamilleR_HasNoUsableCalc_GenuinelyBlocked()
    {
        // The Hextech Ultimatum has no resolvable damage calc in the BIN (see Camille.json _noteR) —
        // R must carry no hits rather than a fabricated one.
        Assert.Null(SkillDamageDb.GetHits("Camille", "R"));
    }

    [Fact]
    public void TristanaE_HasMainHitAndOnHitBonusEffect()
    {
        var hits = SkillDamageDb.GetHits("Tristana", "E");
        Assert.NotNull(hits);
        var mainHit = Assert.Single(hits!);
        Assert.Equal(HitDamageType.Physical, mainHit.Type);
        Assert.Equal("ActiveDamage", mainHit.Calc);

        var bonusEffects = SkillDamageDb.GetBonusEffects("Tristana", "E");
        Assert.NotNull(bonusEffects);
        var bonusHit = Assert.Single(Assert.Single(bonusEffects!).Hits);
        Assert.Equal(HitDamageType.Magic, bonusHit.Type);
        Assert.Equal("PassiveDamage", bonusHit.Calc);
    }

    [Theory]
    [InlineData("Malzahar", "Q")]
    [InlineData("Malzahar", "E")]
    // Malzahar R moved to its own fact below: the loop-442 audit added the Null Zone %maxHP
    // hit, so the slot is no longer single-hit.
    [InlineData("Nautilus", "Q")]
    [InlineData("Nautilus", "W")]
    [InlineData("Nautilus", "E")]
    [InlineData("Nautilus", "R")]
    [InlineData("Thresh", "Q")]
    [InlineData("Thresh", "E")]
    [InlineData("Thresh", "R")]
    public void July10Batch_DirectDamageSlots_AreMagic(string championId, string slot)
    {
        var hit = Single(championId, slot);
        Assert.Equal(HitDamageType.Magic, hit.Type);
    }

    [Fact]
    public void MalzaharR_BeamPlusNullZone_BothMagic()
    {
        // Loop-442 audit + loop-446 fix: beam total + the Null Zone FULL-DURATION %maxHP total
        // (ZoneDamageTooltip = wiki 10/15/20% +2.5%/100AP, a single count-1 hit — the calc is the
        // whole-zone total, not per-tick). Both magic.
        var hits = SkillDamageDb.GetHits("Malzahar", "R");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.All(hits, h => Assert.Equal(HitDamageType.Magic, h.Type));
        var zone = hits.Single(h => h.HpPercentCalc == "ZoneDamageTooltip");
        Assert.Equal(1, zone.Count);
    }

    // ── Cowork session 2026-07-10, 2nd batch: multi-hit / blocked-slot / hpPercentCalc shapes ──

    [Fact]
    public void SettQ_KnuckleDown_IsFlatPlusHpPercentBothCountTwo()
    {
        var hits = SkillDamageDb.GetHits("Sett", "Q");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.All(hits, h => Assert.Equal(2, h.Count));
        Assert.Contains(hits, h => h.FlatDataValue == "BaseDamage");
        Assert.Contains(hits, h => h.HpPercentCalc == "MaxHealthDamageCalc" && h.HpBasis == HpBasis.Max);
    }

    [Fact]
    public void SettR_HasNoTargetBonusHealthTerm_OnlyBaseHit()
    {
        // The Show Stopper's "+% of target's BONUS health" term has no HpBasis support (only
        // target Max/Current/Missing are modeled) -- R must carry exactly the flat/AD base hit.
        var hit = Single("Sett", "R");
        Assert.Equal("DamageCalc", hit.Calc);
    }

    [Fact]
    public void IreliaP_HasNoHits_HashedCalculationPartBlocked()
    {
        Assert.Null(SkillDamageDb.GetHits("Irelia", "P"));
    }

    [Fact]
    public void ViegoQ_DirectStabHit_PlusOnHitBonusEffects()
    {
        // Re-pinned (loop 221): TotalDamage's crit-scaling mMultiplier resolves to exactly 1 now
        // that mStat 8/9 map to the documented 0.0 no-crit floor (§26, SkillDamage.BuildStatResolver)
        // -- so Q carries its direct active-cast stab again (wiki 25-85 +70% AD), alongside the two
        // cleanly-resolvable on-hit procs as bonusEffects.
        var hits = SkillDamageDb.GetHits("Viego", "Q");
        Assert.NotNull(hits);
        var stab = Assert.Single(hits!);
        Assert.Equal("TotalDamage", stab.Calc);
        Assert.Equal(HitDamageType.Physical, stab.Type);
        var bonusEffects = SkillDamageDb.GetBonusEffects("Viego", "Q");
        Assert.NotNull(bonusEffects);
        Assert.Equal(2, bonusEffects!.Length);
        Assert.All(bonusEffects, e => Assert.Equal(BonusTrigger.OnHit, e.Trigger));
    }

    [Fact]
    public void ViegoR_MissingHealthHit_ResolvesPositiveDamage()
    {
        InitRepositoryFromCache();
        var viego = ChampionRepository.Get("Viego");
        Assert.NotNull(viego);

        // Re-pinned (loop 221): R now carries TWO hits -- the %missing-health bonus asserted below
        // plus the AoE arrival hit (TotalDamage, 120% AD; its crit mMultiplier collapses to 1 under
        // the §26 no-crit floor). Select the %HP hit rather than assuming it is the only one.
        var rHits = SkillDamageDb.GetHits("Viego", "R");
        Assert.NotNull(rHits);
        Assert.Equal(2, rHits!.Length);
        Assert.Contains(rHits, h => h.Calc == "TotalDamage" && h.Type == HitDamageType.Physical);
        var hit = Assert.Single(rHits, h => h.HpPercentCalc is not null);
        Assert.Equal(HitDamageType.Physical, hit.Type);
        Assert.Equal("TotalPercentHealth", hit.HpPercentCalc);
        Assert.Equal(HpBasis.Missing, hit.HpBasis);

        var stats = SampleStats();
        double? damage = SkillDamage.ComputeCalcDamage(viego!, "R", hit.HpPercentCalc!, stats, level: 18);
        // TotalPercentHealth's raw evaluated value is percentage-POINTS (e.g. ~12-20), not a
        // fraction -- ComputeCalcDamage alone (no %HP multiply) should still return that positive
        // raw number, confirming the calc itself resolves rather than throwing.
        Assert.True(damage is > 0, $"Viego R/TotalPercentHealth: expected positive raw value, got {damage}");
    }

    [Fact]
    public void AmumuW_UsesHashedCalcNames_BothResolvePositiveDamage()
    {
        InitRepositoryFromCache();
        var amumu = ChampionRepository.Get("Amumu");
        Assert.NotNull(amumu);

        var hits = SkillDamageDb.GetHits("Amumu", "W");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.Contains(hits, h => h.Calc == "{c8e45bc3}");
        Assert.Contains(hits, h => h.HpPercentCalc == "{8a96509c}" && h.HpBasis == HpBasis.Max);

        var stats = SampleStats();
        var flatHit = hits.Single(h => h.Calc == "{c8e45bc3}");
        double? flat = SkillDamage.ComputeCalcDamage(amumu!, "W", flatHit.Calc, stats, level: 18);
        Assert.True(flat is > 0, $"Amumu W flat term: expected positive damage, got {flat}");
    }

    [Fact]
    public void RakanE_AndPassive_HaveNoHits_ShieldOnly()
    {
        Assert.Null(SkillDamageDb.GetHits("Rakan", "E"));
        Assert.Null(SkillDamageDb.GetHits("Rakan", "P"));
    }

    [Fact]
    public void BlitzcrankR_HasMainHitAndOnHitBonusEffect()
    {
        var hits = SkillDamageDb.GetHits("Blitzcrank", "R");
        Assert.NotNull(hits);
        var mainHit = Assert.Single(hits!);
        Assert.Equal("ActiveDamage", mainHit.Calc);

        var bonusEffects = SkillDamageDb.GetBonusEffects("Blitzcrank", "R");
        Assert.NotNull(bonusEffects);
        var bonusHit = Assert.Single(Assert.Single(bonusEffects!).Hits);
        Assert.Equal("PassiveDamage", bonusHit.Calc);
    }

    [Fact]
    public void BlitzcrankW_HasNoHits_NoDamageAbility()
    {
        Assert.Null(SkillDamageDb.GetHits("Blitzcrank", "W"));
    }

    [Fact]
    public void MorganaW_UsesMinDamage_NotTheMissingHealthAmplifiedMax()
    {
        // TotalMaxDamage amplifies TotalMinDamage by up to 2x based on the target's missing
        // health -- a live multiplier this schema can't express, so W must curate only the
        // conservative unamplified TotalMinDamage baseline.
        var hit = Single("Morgana", "W");
        Assert.Equal("TotalMinDamage", hit.Calc);
    }

    [Fact]
    public void AsheQ_EmpoweredDamage_IsSingleHitNotMultipliedByShotCount()
    {
        // DamagePerStrike already equals the wiki's combined "Total Damage Per Flurry" value, so
        // Q must be curated as ONE hit, not five (which would 5x-overstate the real number).
        var hit = Single("Ashe", "Q");
        Assert.Equal("EmpoweredDamage", hit.Calc);
        Assert.Equal(1, hit.Count);
    }

    [Fact]
    public void AshePassive_HasNoHits_StatConversionNotADamageInstance()
    {
        Assert.Null(SkillDamageDb.GetHits("Ashe", "P"));
    }

    [Theory]
    [InlineData("Sett", "W")]
    [InlineData("Sett", "E")]
    [InlineData("Irelia", "Q")]
    [InlineData("Irelia", "W")]
    [InlineData("Viego", "W")]
    [InlineData("Amumu", "Q")]
    [InlineData("Amumu", "E")]
    [InlineData("Amumu", "R")]
    [InlineData("Rakan", "Q")]
    [InlineData("Rakan", "W")]
    [InlineData("Blitzcrank", "Q")]
    [InlineData("Morgana", "Q")]
    [InlineData("Morgana", "R")]
    [InlineData("Ashe", "R")]
    public void July10SecondBatch_DirectDamageSlots_ResolvePositiveDamage(string championId, string slot)
    {
        InitRepositoryFromCache();
        var champion = ChampionRepository.Get(championId);
        Assert.NotNull(champion);

        var hits = SkillDamageDb.GetHits(championId, slot);
        Assert.NotNull(hits);
        var stats = SampleStats();
        bool anyPositive = hits!.Any(hit =>
            SkillDamage.ComputeCalcDamage(champion!, slot, hit.Calc, stats, level: 18) is > 0);
        Assert.True(anyPositive, $"{championId} {slot}: no hit resolved to positive damage");
    }

    // ── Cowork session 2026-07-10, 3rd batch: mid/top/support/jungle shapes ────────────

    /// <summary>(loop 471) Shuriken Flip's throw and its recast dash are two casts, so they are two
    /// SLOTS a combo can use independently — the recast only happens if the shuriken hit. This test
    /// followed that split, and pins the property that makes the split safe: the pair still describes
    /// the same ability, so E and E2 together are exactly what the single E slot used to hold.</summary>
    [Fact]
    public void AkaliE_ShurikenFlip_IsTwoSeparatelySelectableCasts()
    {
        var throwHits = SkillDamageDb.GetHits("Akali", "E");
        var recastHits = SkillDamageDb.GetHits("Akali", "E2");
        Assert.NotNull(throwHits);
        Assert.NotNull(recastHits);

        var both = throwHits!.Concat(recastHits!).ToArray();
        Assert.Equal(2, both.Length);
        Assert.All(both, h => Assert.Equal(HitDamageType.Magic, h.Type));
        Assert.Contains(throwHits!, h => h.Calc == "E1Damage");
        Assert.Contains(recastHits!, h => h.Calc == "E2DamageCalc");

        // The recast's calc still lives in the canonical E spell object.
        Assert.Equal("E", Assert.Single(recastHits!).BinSpell);
    }

    [Fact]
    public void AkaliR_UsesGuaranteedRecastFloor_NotMissingHealthMax()
    {
        // Cast2DamageMax bakes in only the theoretical 200%-missing-HP cap as a flat x3, not a
        // live scaling formula this schema can express -- R must curate the guaranteed
        // Cast2DamageMin floor instead.
        var hits = SkillDamageDb.GetHits("Akali", "R");
        Assert.NotNull(hits);
        Assert.Contains(hits, h => h.Calc == "Cast1Damage");
        Assert.Contains(hits, h => h.Calc == "Cast2DamageMin");
    }

    [Fact]
    public void FioraP_DuelistsDance_IsTrueDamage_NotPhysical()
    {
        // Live wiki: "deals bonus TRUE damage equal to 3% (+4% per 100 bonus AD)" -- the prior
        // curation pass had mistagged this Physical.
        var hit = Single("Fiora", "P");
        Assert.Equal(HitDamageType.True, hit.Type);
        Assert.Equal("PassiveDamageTotal", hit.HpPercentCalc);
    }

    [Fact]
    public void FioraQ_WasMissing_NowResolvesPositiveDamage()
    {
        InitRepositoryFromCache();
        var fiora = ChampionRepository.Get("Fiora");
        Assert.NotNull(fiora);

        var hit = Single("Fiora", "Q");
        Assert.Equal(HitDamageType.Physical, hit.Type);

        var stats = SampleStats();
        double? damage = SkillDamage.ComputeCalcDamage(fiora!, "Q", hit.Calc, stats, level: 18);
        Assert.True(damage is > 0, $"Fiora Q/{hit.Calc}: expected positive damage, got {damage}");
    }

    [Fact]
    public void NamiE_TidecallersBlessing_IsOnHitBonusEffect()
    {
        var bonusEffects = SkillDamageDb.GetBonusEffects("Nami", "E");
        Assert.NotNull(bonusEffects);
        var hit = Assert.Single(Assert.Single(bonusEffects!).Hits);
        Assert.Equal(BonusTrigger.OnHit, bonusEffects![0].Trigger);
        Assert.Equal(HitDamageType.Magic, hit.Type);
    }

    [Fact]
    public void DianaW_PaleCascade_IsThreeOrbsCountThree()
    {
        var hit = Single("Diana", "W");
        Assert.Equal("TotalDamage", hit.Calc);
        Assert.Equal(3, hit.Count);
    }

    [Fact]
    public void VolibearE_SkySplitter_IsFlatPlusMaxHpPercentDataValue()
    {
        var hits = SkillDamageDb.GetHits("Volibear", "E");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.Contains(hits, h => h.Calc == "CalculatedDamage");
        Assert.Contains(hits, h => h.HpPercentDataValue == "PercentDamage" && h.HpBasis == HpBasis.Max);
    }

    [Fact]
    public void XinZhaoQ_ThreeTalonStrike_IsThreeEmpoweredAttacks()
    {
        var hit = Single("XinZhao", "Q");
        Assert.Equal("BonusDamage", hit.Calc);
        Assert.Equal(3, hit.Count);
    }

    [Fact]
    public void XinZhaoW_SlashPlusThrust_BothPhasesCurated()
    {
        // Re-pinned (loop 221): ThrustDamage's mMultiplier (1 + mStat8 x CritChanceAmp) resolves to
        // exactly 1 under the §26 no-crit floor (mStat 8 -> 0.0), so W carries both phases:
        // 4x Slash + 1 Thrust (wiki 'Thrust: 50-190 (+90% AD)(+65% AP)').
        var hits = SkillDamageDb.GetHits("XinZhao", "W");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.Contains(hits, h => h.Calc == "SlashDamage" && h.Count == 4);
        Assert.Contains(hits, h => h.Calc == "ThrustDamage" && h.Count == 1);
    }

    // ── §48: mStat=4 (attack speed) mapping un-deadens Akshan E ─────────────────────────

    [Fact]
    public void AkshanE_DamageToDeal_ScalesWithBonusAttackSpeedRatio()
    {
        // DamageToDeal = [BaseDamage(rank5 = 40) + 0.25 x total AD] x (1 + 0.3 x bonusAS), where
        // bonusAS is the RATIO total/base - 1 (the §48 StatAttackSpeed=4 mapping). Before that
        // mapping the mMultiplier's mStat=4 part threw KeyNotFoundException and E silently dealt 0
        // for as long as it was "curated" (TODO#48).
        InitRepositoryFromCache();
        var akshan = ChampionRepository.Get("Akshan");
        Assert.NotNull(akshan);
        double baseAs = akshan!.BaseStats.AttackSpeed;
        Assert.True(baseAs > 0, "cached Akshan base AS missing");

        // Live AS = 1.5x base -> bonusAS ratio = 0.5 -> multiplier = 1 + 0.3*0.5 = 1.15.
        // (precision 2: the BIN coefficient is float32 noise, 0.30000001..., not exactly 0.3.)
        var stats = SampleStats();
        stats.AttackSpeed = baseAs * 1.5;
        double? damage = SkillDamage.ComputeCalcDamage(akshan, "E", "DamageToDeal", stats, level: 18);
        Assert.NotNull(damage);
        Assert.Equal((40.0 + 0.25 * 200.0) * 1.15, damage!.Value, precision: 2);

        // No live AS reported (0): bonus floors to 0 -> multiplier exactly 1, and it never throws.
        double? floor = SkillDamage.ComputeCalcDamage(akshan, "E", "DamageToDeal", SampleStats(), level: 18);
        Assert.NotNull(floor);
        Assert.Equal(40.0 + 0.25 * 200.0, floor!.Value, precision: 2);
    }

    [Fact]
    public void XinZhaoR_HasCurrentHealthTerm()
    {
        var hits = SkillDamageDb.GetHits("XinZhao", "R");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.Contains(hits, h => h.Calc == "TotalDamage");
        Assert.Contains(hits, h => h.HpPercentDataValue == "PercentCurrentHealthDamage" && h.HpBasis == HpBasis.Current);
    }

    [Fact]
    public void ShenQ_TwilightAssault_IsFlatPlusHpPercentBothCountThree()
    {
        var hits = SkillDamageDb.GetHits("Shen", "Q");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.All(hits, h => Assert.Equal(3, h.Count));
        Assert.Contains(hits, h => h.Calc == "BaseFlatDamage");
        Assert.Contains(hits, h => h.HpPercentCalc == "BasePercentHealth" && h.HpBasis == HpBasis.Max);
    }

    [Fact]
    public void ShenE_ShadowDash_IsPhysical_NotMagic()
    {
        // Shadow Dash was reworked off magic damage; the prior auto-pick had it Magic.
        var hit = Single("Shen", "E");
        Assert.Equal(HitDamageType.Physical, hit.Type);
    }

    [Fact]
    public void ShenP_W_R_HaveNoHits_PureUtility()
    {
        Assert.Null(SkillDamageDb.GetHits("Shen", "P"));
        Assert.Null(SkillDamageDb.GetHits("Shen", "W"));
        Assert.Null(SkillDamageDb.GetHits("Shen", "R"));
    }

    [Fact]
    public void MonkeyKingR_Cyclone_UsesTotalCalcs_NotTheNonexistentDamageCalc()
    {
        // The prior auto-pick referenced a calc named "Damage" that does not exist anywhere in
        // Cyclone's BIN spell data (silently resolved null already) -- replaced with the real
        // full-duration total calcs.
        var hits = SkillDamageDb.GetHits("MonkeyKing", "R");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.Contains(hits, h => h.Calc == "TotalDamageTT");
        Assert.Contains(hits, h => h.HpPercentCalc == "PercentHPDamageTT" && h.HpBasis == HpBasis.Max);
    }

    [Theory]
    [InlineData("Akali", "Q")]
    [InlineData("Akali", "E")]
    [InlineData("Akali", "R")]
    [InlineData("Fiora", "Q")]
    [InlineData("Fiora", "W")]
    [InlineData("Nami", "Q")]
    [InlineData("Nami", "W")]
    [InlineData("Nami", "R")]
    [InlineData("Diana", "Q")]
    [InlineData("Diana", "W")]
    [InlineData("Diana", "E")]
    [InlineData("Diana", "R")]
    [InlineData("Volibear", "Q")]
    [InlineData("Volibear", "W")]
    [InlineData("Volibear", "E")]
    [InlineData("Volibear", "R")]
    [InlineData("XinZhao", "Q")]
    [InlineData("XinZhao", "W")]
    [InlineData("XinZhao", "E")]
    [InlineData("XinZhao", "R")]
    [InlineData("Shen", "Q")]
    [InlineData("Shen", "E")]
    [InlineData("MonkeyKing", "Q")]
    [InlineData("MonkeyKing", "E")]
    [InlineData("MonkeyKing", "R")]
    public void July10ThirdBatch_DirectDamageSlots_ResolvePositiveDamage(string championId, string slot)
    {
        InitRepositoryFromCache();
        var champion = ChampionRepository.Get(championId);
        Assert.NotNull(champion);

        var hits = SkillDamageDb.GetHits(championId, slot);
        Assert.NotNull(hits);
        var stats = SampleStats();
        bool anyPositive = hits!.Any(hit =>
            SkillDamage.ComputeCalcDamage(champion!, slot, hit.Calc, stats, level: 18) is > 0);
        Assert.True(anyPositive, $"{championId} {slot}: no hit resolved to positive damage");
    }
}
