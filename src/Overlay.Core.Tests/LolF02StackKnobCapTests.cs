using System.IO;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// (lolF02 t5, HC:108) The cap the combo editor's "몇 스택" picker offers is the one the engine clamps the knob to:
/// a per-stack term's BIN DataValue (Yasuo E, Kassadin R: MaxStacks 4) or its literal (Darius R: 5). An ordinary
/// BuffCounter stack (Cho'Gath E's Feast) states none and keeps the uncapped picker.
/// </summary>
public class LolF02StackKnobCapTests
{
    public LolF02StackKnobCapTests() => SkillDamageDb.ResetForTests();

    [Theory]
    [InlineData("Yasuo", "E", 4)]
    [InlineData("Kassadin", "R", 4)]
    [InlineData("Darius", "R", 5)]
    public void PickerCap_IsTheEngineClamp(string champ, string slot, int cap)
    {
        var hit = SkillDamageDb.GetStackScaledHit(champ, slot)!;
        Assert.Equal(cap, ComboRunner.StackKnobCap(LoadChampionFromBin(champ), slot, hit));
    }

    [Fact]
    public void AnUncappedStack_HasNoPickerCap()
    {
        var hit = SkillDamageDb.GetStackScaledHit("Chogath", "E")!;
        Assert.Null(ComboRunner.StackKnobCap(LoadChampionFromBin("Chogath"), "E", hit));
    }

    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{championId.ToLowerInvariant()}.bin.json"));
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
}
