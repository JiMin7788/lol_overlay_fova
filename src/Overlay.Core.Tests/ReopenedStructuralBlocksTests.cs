using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 499) Three curations closed as "structurally impossible" that were not, and one that half
/// was. Section 27 recorded them as permanent limits; two turned out to be stale notes describing an
/// engine that has since changed, and the third was two mechanics filed under one verdict.
///
/// <para>The lesson is the same one trap 2.7 records for checkboxes: a note saying something cannot
/// be done is a claim about the code AT THE TIME. Re-read it when the code moves.</para>
/// </summary>
public class ReopenedStructuralBlocksTests
{
    private static ChampionData Champion(string championId)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var summary = Directory.GetFiles(Path.Combine(dataDir, "ddragon"), "champion.json",
            SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Last();
        ChampionRepository.ResetForTests();
        SkillDamageDb.ResetForTests();
        ChampionRepository.InitializeFromCache(
            Path.GetDirectoryName(summary)!, Path.Combine(dataDir, "communitydragon"));
        var champion = ChampionRepository.Get(championId);
        Assert.NotNull(champion);
        return champion!;
    }

    private static ActivePlayerStats Stats(double ap) => new()
    {
        AttackDamage = 200, AbilityPower = ap, MaxHealth = 3000, ResourceMax = 1000,
    };

    /// <summary>The empowered casts live on R's BIN object while belonging to Q, W and E — which the
    /// old note called unplaceable and which binSpell has expressed since the multi-form work.</summary>
    [Fact]
    public void HeimerdingerUpgradeIsThreeCastsResolvingAgainstR()
    {
        var heim = Champion("Heimerdinger");

        foreach (var slot in new[] { "QUlt", "WUlt", "EUlt" })
        {
            var hit = Assert.Single(SkillDamageDb.GetHits("Heimerdinger", slot)!);
            Assert.Equal("R", hit.BinSpell);
        }

        // CH-3X Lightning Grenade: 100/200/300 (+60% AP) a bounce. At R rank 3 and 300 AP that is
        // 300 + 180 = 480 — the wiki number, resolved from the BIN rather than typed in.
        Assert.Equal(480, SkillDamage.ComputeCalcDamage(heim, "R", "EUltDamage", Stats(300), 18, rankSlot: "EUlt")!.Value, 1);

        // Rocket Swarm's initial rocket: 135/180/225 (+45% AP) -> 225 + 135 = 360.
        Assert.Equal(360, SkillDamage.ComputeCalcDamage(heim, "R", "WUltDamage", Stats(300), 18, rankSlot: "WUlt")!.Value, 1);

        // R itself still deals nothing — it is a toggle, and that half of the old note was right.
        Assert.Null(SkillDamageDb.GetHits("Heimerdinger", "R"));
    }

    /// <summary>The "unmapped mStat 15" was in a wrapper the engine never needed: it multiplies the
    /// fraction by the target's missing health, which is what hpBasis Missing already means.</summary>
    [Fact]
    public void EkkoWIsAMissingHealthFractionBelowAThreshold()
    {
        var ekko = Champion("Ekko");

        var effect = Assert.Single(SkillDamageDb.GetBonusEffects("Ekko", "W")!);
        Assert.Equal(BonusTrigger.OnHit, effect.Trigger);
        Assert.True(effect.AlwaysOn, "it is W's passive — it needs no cast");

        var hit = Assert.Single(effect.Hits);
        Assert.Equal("HpBelow", hit.ConditionType);
        Assert.Equal(0.3, hit.ConditionValue, 3);
        Assert.Equal("MissingHealthPercent", hit.MetHpPercentCalc);
        Assert.Equal(HpBasis.Missing, hit.HpBasis);

        // 3% (+3% per 100 AP): 3% with no AP, 12% at 300.
        Assert.Equal(0.03, SkillDamage.ResolveHpPercentCalc(ekko, "W", "MissingHealthPercent", Stats(0), 18)!.Value, 4);
        Assert.Equal(0.12, SkillDamage.ResolveHpPercentCalc(ekko, "W", "MissingHealthPercent", Stats(300), 18)!.Value, 4);
    }

    /// <summary>Half of Fired Up! is Milio's and half is the ally's. Filing both under one "impossible"
    /// verdict cost the half that was always computable.</summary>
    [Fact]
    public void MilioBurnIsHisOwn_AndTheBurstStillIsNot()
    {
        var milio = Champion("Milio");

        var hit = Assert.Single(Assert.Single(SkillDamageDb.GetBonusEffects("Milio", "P")!).Hits);
        Assert.Equal("Upgraded", hit.ConditionType);      // he has to be enchanted, and nothing reports that
        Assert.Equal("BurnDamage", hit.MetCalc);

        // 10-50 by level (+20% of MILIO's AP): at level 18 with 300 AP that is 50 + 60 = 110.
        Assert.Equal(110, SkillDamage.ComputeCalcDamage(milio, "P", "BurnDamage", Stats(300), 18)!.Value, 1);
        // …and at level 1 it is the bottom of that range, which is what makes it a level curve and not
        // a rank one — the passive has no rank.
        Assert.Equal(10 + 60, SkillDamage.ComputeCalcDamage(milio, "P", "BurnDamage", Stats(300), 1)!.Value, 1);

        // The burst is a share of the ENCHANTED ALLY's attack damage. It resolves to a RATIO, not a
        // number, and there is no ally-stat resolver to multiply it by — so it stays uncurated.
        double ratio = SkillDamage.ComputeCalcDamage(milio, "P", "ADBurstRatio", Stats(300), 18)!.Value;
        Assert.InRange(ratio, 0.05, 0.20);
        Assert.DoesNotContain(SkillDamageDb.GetBonusEffects("Milio", "P")!.SelectMany(e => e.Hits),
            h => h.Calc == "ADBurstRatio" || h.MetCalc == "ADBurstRatio");
    }
}
