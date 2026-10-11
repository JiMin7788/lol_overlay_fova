using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 550) Elise spider-form slots, added via the M22 extra-spell enabler (Jayce QCannon
/// precedent). QSpider (Venomous Bite) = flat BaseDamage DV + (8% + 3% per 100 AP) of the
/// target's MISSING health (MissingHPDamage, ×0.01 multiplier tree), both resolved from the
/// non-canonical <c>EliseSpiderQCast</c> spell object; the "QSpider" key rank-falls-back to the
/// Q ability rank. WSpider/ESpider are damage-less Utility slots and RSpiderling / the R onHit
/// rider stay unchanged.
/// </summary>
public class EliseSpiderFormTests
{
    private static ChampionData Elise()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "elise.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Elise", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Elise" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Elise", Name = "Elise", Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ActivePlayerStats Stats(double ap = 0) => new()
    {
        AbilityPower = ap, AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    [Fact]
    public void SpiderQ_FlatBase_ResolvesFromTheExtraSpell()
    {
        // BaseDamage values [20, 50, 80, 110, 140, 170, 200] — index==rank, rank 1 = 50.
        var flat = SkillDamage.ComputeFlatDataValue(Elise(), "EliseSpiderQCast", "BaseDamage",
            Stats(), level: 4, rankSlot: "QSpider");
        Assert.NotNull(flat);
        Assert.Equal(50.0, flat!.Value, 1);
    }

    [Theory]
    [InlineData(0, 0.08)]     // 8% of missing health at 0 AP
    [InlineData(100, 0.11)]   // +3% per 100 AP
    public void SpiderQ_MissingHpFraction_ScalesWithAp(double ap, double expected)
    {
        var fraction = SkillDamage.ResolveHpPercentCalc(Elise(), "EliseSpiderQCast", "MissingHPDamage",
            Stats(ap), level: 4, 0, rankSlot: "QSpider");
        Assert.NotNull(fraction);
        Assert.Equal(expected, fraction!.Value, 3);
    }

    [Fact]
    public void SpiderSlots_AreCurated_AndSpiderQHasTheMissingBasis()
    {
        SkillDamageDb.ResetForTests();
        Assert.True(SkillDamageDb.IsSlotCurated("Elise", "QSpider"));
        Assert.True(SkillDamageDb.IsSlotCurated("Elise", "WSpider"));
        Assert.True(SkillDamageDb.IsSlotCurated("Elise", "ESpider"));

        var hits = SkillDamageDb.GetHits("Elise", "QSpider");
        Assert.NotNull(hits);
        Assert.Equal(2, hits!.Length);
        Assert.Contains(hits, h => h.HpBasis == HpBasis.Missing);
        // The utility forms honestly carry no hits at all.
        Assert.Null(SkillDamageDb.GetHits("Elise", "WSpider"));
        Assert.Null(SkillDamageDb.GetHits("Elise", "ESpider"));
    }
}
