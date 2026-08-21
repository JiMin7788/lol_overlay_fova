using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 478) Where a variant cast sits in the palette. Until now the canonical P/Q/W/E/R came
/// first and every extra slot was appended after R, so Aatrox read P Q W R Q2 Q3 and a reader had to
/// hunt the far end of the palette for the other forms of a cast they were already looking at.
///
/// <para>A variant's slot key always begins with the letter of the ability it varies — RWall is
/// still R, QCalibrum is still Q — which is the same convention the icon fallback uses.</para>
/// </summary>
public class PaletteVariantOrderTests : IDisposable
{
    public PaletteVariantOrderTests()
    {
        ChampionRepository.ResetForTests();
        SkillDamageDb.ResetForTests();
    }

    public void Dispose()
    {
        ChampionRepository.ResetForTests();
        SkillDamageDb.ResetForTests();
    }

    private static ChampionData FromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon",
            $"{championId.ToLowerInvariant()}.bin.json"));
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in ChampionBinParser.ParseChampion(championId, json))
            skills[slot] = new SkillData
            {
                Key = slot, Name = championId + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
                MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = championId, Name = championId, Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static List<string> PaletteIds(string championId)
    {
        ChampionRepository.Initialize(new[] { FromBin(championId) });
        return ComboEditor.LoadPalette(championId).AvailableNodes.Select(n => n.Id).ToList();
    }

    [Fact]
    public void AatroxThreeQCastsSitTogether()
    {
        var ids = PaletteIds("Aatrox");
        int q = ids.IndexOf("Q"), q2 = ids.IndexOf("Q2"), q3 = ids.IndexOf("Q3"), w = ids.IndexOf("W");

        Assert.True(q >= 0 && q2 >= 0 && q3 >= 0 && w >= 0);
        // The canonical cast leads its own group, the variants follow in curation order, and W is
        // only reached after all of them.
        Assert.Equal(q + 1, q2);
        Assert.Equal(q2 + 1, q3);
        Assert.True(w > q3);
    }

    [Fact]
    public void KaynRhaastFormsFollowTheAbilitiesTheyReplace()
    {
        var ids = PaletteIds("Kayn");
        Assert.Equal(ids.IndexOf("Q") + 1, ids.IndexOf("QRhaast"));
        Assert.Equal(ids.IndexOf("R") + 1, ids.IndexOf("RRhaast"));
        // …and the Rhaast Q is no longer stranded after the ultimate.
        Assert.True(ids.IndexOf("QRhaast") < ids.IndexOf("R"));
    }

    [Fact]
    public void ApheliosFiveGunsAllFollowQ()
    {
        var ids = PaletteIds("Aphelios");
        int q = ids.IndexOf("Q");
        Assert.True(q >= 0);
        foreach (var gun in new[] { "QCalibrum", "QSeverum", "QGravitum", "QInfernum", "QCrescendum" })
        {
            int at = ids.IndexOf(gun);
            Assert.True(at > q, $"{gun} should come after Q");
            Assert.True(at < ids.IndexOf("W"), $"{gun} should come before W");
        }
        // The gun-specific ultimate sits with R, not with the Q block.
        Assert.True(ids.IndexOf("RInfernum") > ids.IndexOf("R"));
    }

    [Fact]
    public void TheAutoAttackStaysLast()
    {
        // AA is appended after the abilities and must not be pulled into a letter group.
        var ids = PaletteIds("Aatrox");
        Assert.Equal(ids.Count - 1, ids.IndexOf("AA"));
    }
}
