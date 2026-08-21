using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 488) Two CalculationPart kinds the interpreter did not recognise, and one curation that
/// claimed to be a percentage without being one. All three were found by the same thing: a sweep
/// that asks every curated slot of every champion whether it produces damage.
///
/// <para>The failure mode they share is silence. A calc whose formula contains an unknown part
/// THROWS, the hit is dropped, and a slot with no surviving hits falls through to the heuristic —
/// so Ambessa's Q showed a plausible number that came from nowhere near her curation, and her Q2
/// showed nothing. Nothing logs, nothing fails, and the number on screen is wrong.</para>
/// </summary>
public class FormulaPartKindTests
{
    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 300, AbilityPower = 200, MaxHealth = 3000,
        Armor = 100, MagicResist = 100, MoveSpeed = 400, ResourceMax = 1000,
    };

    private static ChampionData Champion(string championId)
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var summary = Directory.GetFiles(Path.Combine(dataDir, "ddragon"), "champion.json",
            SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Last();
        ChampionRepository.ResetForTests();
        ChampionRepository.InitializeFromCache(
            Path.GetDirectoryName(summary)!, Path.Combine(dataDir, "communitydragon"));
        var champion = ChampionRepository.Get(championId);
        Assert.NotNull(champion);
        return champion!;
    }

    /// <summary>A multiplier that IS another calculation on the same spell ({f3cbe7b2}). Ambessa's Q
    /// is built entirely out of these — each curated calc multiplies a doubled base by a separately
    /// named half-ratio — so every one of her four Q hits threw, and both her Q casts were wrong in
    /// different directions.</summary>
    [Fact]
    public void ACalculationCanBeMultipliedByAnotherCalculation()
    {
        var ambessa = Champion("Ambessa");

        double doubled = SkillDamage.ComputeCalcDamage(ambessa, "Q", "Calc_Damage_2_Max", Stats(), 18)!.Value;
        double halved = SkillDamage.ComputeCalcDamage(ambessa, "Q", "{3ce89b9e}", Stats(), 18)!.Value;
        Assert.True(doubled > 0);

        // {3ce89b9e} = Calc_Damage_2_Max x Calc_Damage_2_Min_Ratio, and that ratio is a half — which
        // is the whole reason the curation points at the hashed calc rather than the _Max one.
        Assert.Equal(0.5, halved / doubled, 3);
    }

    [Fact]
    public void AmbessaBothQCastsNowResolve()
    {
        var ambessa = Champion("Ambessa");
        foreach (var slot in new[] { "Q", "Q2" })
            foreach (var hit in SkillDamageDb.GetHits("Ambessa", slot)!)
            {
                string bin = string.IsNullOrEmpty(hit.BinSpell) ? slot : hit.BinSpell!;
                double? v = hit.Calc.Length > 0
                    ? SkillDamage.ComputeCalcDamage(ambessa, bin, hit.Calc, Stats(), 18, rankSlot: slot)
                    : SkillDamage.ResolveHpPercentCalc(ambessa, bin, hit.HpPercentCalc!, Stats(), 18, rankSlot: slot);
                Assert.True(v is > 0, $"Ambessa {slot} hit resolved to {v?.ToString() ?? "null"}");
            }
    }

    /// <summary>A base DataValue plus a per-CHAMPION-LEVEL one ({b22609db}), both named by hashed
    /// fields. One champion in the corpus uses it, and it is the passive on-hit this project curated
    /// in loop 483 — which therefore resolved to nothing from the day it was written.</summary>
    [Fact]
    public void ADataValuePairCanScaleByChampionLevel()
    {
        var irelia = Champion("Irelia");

        // OnHitBaseDamage 10 + OnHitPerLevel 3: the wiki's "10 - 61 (based on level)" is 10 + 3x17,
        // and the AD term adds 20% of bonus AD on top (300 total AD against a base the repository
        // reports, so the assertion is on the level curve, not the exact total).
        double atOne = SkillDamage.ComputeCalcDamage(irelia, "P", "OnHitBonus", Stats(), 1)!.Value;
        double atEighteen = SkillDamage.ComputeCalcDamage(irelia, "P", "OnHitBonus", Stats(), 18)!.Value;
        Assert.Equal(51, atEighteen - atOne, 1);
    }

    /// <summary>Subjugate is a fraction of the target's maximum health, and it was wired through
    /// "calc" — which reads a fraction as a flat damage number. Trundle's ultimate was contributing
    /// about a third of one point of damage.</summary>
    [Fact]
    public void TrundleR_IsAPercentageOfTheTarget_NotAThirdOfAPoint()
    {
        var trundle = Champion("Trundle");
        var hit = Assert.Single(SkillDamageDb.GetHits("Trundle", "R")!);

        Assert.Equal("TotalPercentHPDamage", hit.HpPercentCalc);
        Assert.Equal(HpBasis.Max, hit.HpBasis);
        Assert.Equal(string.Empty, hit.Calc);

        double fraction = SkillDamage.ResolveHpPercentCalc(trundle, "R", hit.HpPercentCalc!, Stats(), 18)!.Value;
        // 35% at max rank plus 2% per 100 AP: a fraction, which is exactly why reading it as damage
        // was so quiet — 0.34 is not zero, so no silent-zero guard would ever have fired.
        Assert.InRange(fraction, 0.3, 0.5);
    }
}
