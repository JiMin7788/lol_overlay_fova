using System.IO;
using System.Linq;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (M45 loop 51) A condition is the condition the engine checks: the audit's condition rules accepted any condition field,
/// and three clauses named a fact their hit's box did not carry - Briar E's wall damage needs a full charge, Udyr's awakened
/// lightning an isolated target, Kled R's ram a distance (a form box could say only one end).
/// </summary>
public class M45Loop51ConditionKindsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public M45Loop51ConditionKindsTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "M45L51_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly ActivePlayerStats Stats = new()
    {
        AttackDamage = 200, AbilityPower = 100, AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 2,
    };

    private static bool TryResolve(string champ, string slot, SkillHit hit, out double damage, bool? met = null, double? charge = null)
        => ComboRunner.ResolveHitForAudit(hit, LoadChampionFromBin(champ), slot, Stats, 11, 1000.0,
            null, met, 0, allOut: false, out damage, out _, out _, distanceFraction: charge);

    [Fact]
    public void BriarE_WallDamage_OnlyAtFullCharge()
    {
        var wall = SkillDamageDb.GetHits("Briar", "E")![1];
        Assert.True(wall.FullChargeOnly);
        double full = SkillDamage.ComputeCalcDamage(LoadChampionFromBin("Briar"), "E", "WallHitDamage", Stats, 11)!.Value;

        Assert.True(TryResolve("Briar", "E", wall, out double unset, met: true));
        Assert.Equal(full, unset, 6);                                             // unset = the full-charge anchor
        Assert.True(TryResolve("Briar", "E", wall, out double atFull, met: true, charge: 1.0));
        Assert.Equal(full, atFull, 6);
        Assert.False(TryResolve("Briar", "E", wall, out _, met: true, charge: 0.5)); // half a charge knocks nothing back
        // the scream itself still scales with the charge
        Assert.True(TryResolve("Briar", "E", SkillDamageDb.GetHits("Briar", "E")![0], out double half, charge: 0.5));
        Assert.True(half > 0);
    }

    [Fact]
    public void KledR_RamScalesByDistance_BetweenItsTwoEnds()
    {
        var ram = SkillDamageDb.GetHits("Kled", "R")!.Single();
        var kled = LoadChampionFromBin("Kled");
        double min = SkillDamage.ResolveHpPercentCalc(kled, "R", "MinimumDamageTooltip", Stats, 11)!.Value;
        double max = SkillDamage.ResolveHpPercentCalc(kled, "R", "MaximumChargeDamage", Stats, 11)!.Value;
        Assert.True(max > min);

        Assert.True(TryResolve("Kled", "R", ram, out double unset));
        Assert.Equal(min * 1000, unset, 6);                                       // unset = the minimum, as before
        Assert.True(TryResolve("Kled", "R", ram, out double end, charge: 1.0));
        Assert.Equal(max * 1000, end, 6);
        Assert.True(TryResolve("Kled", "R", ram, out double mid, charge: 0.5));
        Assert.Equal((min + max) / 2 * 1000, mid, 6);                              // every distance between, not two ends

        Assert.NotNull(SkillDamageDb.GetDistanceScaledHit("Kled", "R"));         // the slider is offered
        Assert.Null(SkillDamageDb.GetConditionalHit("Kled", "R"));               // the form box is gone
    }

    [Fact]
    public void KledR_UnsetCombo_SpansMinimumToMaximum()
    {
        var kled = LoadChampionFromBin("Kled");
        double min = SkillDamage.ResolveHpPercentCalc(kled, "R", "MinimumDamageTooltip", Stats, 11)!.Value;
        double max = SkillDamage.ResolveHpPercentCalc(kled, "R", "MaximumChargeDamage", Stats, 11)!.Value;
        var r = RunResult("Kled", "R");
        Assert.True(r.TotalDamage > 0);
        Assert.Equal(max / min, r.RangeMax / r.TotalDamage, 3);
    }

    [Fact]
    public void UdyrQ_AwakenedBox_AlsoAssertsAnIsolatedTarget()
    {
        var box = SkillDamageDb.GetConditionalHit("Udyr", "Q")!;
        Assert.Equal("Upgraded", box.ConditionType);
        Assert.Equal("EmpoweredLightningBonusMax", box.MetHpPercentCalc);
        Assert.Equal(new[] { "VsIsolated" }, box.AlsoAssumes);
    }

    /// <summary>Every fact a box also asserts is a condition the player states (the badge names it); a live-resolved one
    /// would be a claim the click cannot make, and a hit with no box of its own has nothing to assert it with.</summary>
    [Fact]
    public void EveryAlsoAssumes_IsAUserAssumedCondition_OnAConditionalHit()
    {
        int seen = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "data", "skill_damage"), "*.json"))
        {
            string champ = Path.GetFileNameWithoutExtension(file);
            foreach (var slot in SkillDamageDb.GetCuratedSlotKeys(champ))
            {
                var hits = (SkillDamageDb.GetHits(champ, slot) ?? Array.Empty<SkillHit>())
                    .Concat((SkillDamageDb.GetBonusEffects(champ, slot) ?? Array.Empty<SkillBonusEffect>()).SelectMany(e => e.Hits));
                foreach (var hit in hits.Where(h => h.AlsoAssumes is { Length: > 0 }))
                {
                    Assert.True(Enum.TryParse<ConditionType>(hit.ConditionType, out _), $"{champ} {slot}: alsoAssumes without a box");
                    foreach (var fact in hit.AlsoAssumes!)
                    {
                        Assert.True(Enum.TryParse<ConditionType>(fact, out var c), $"{champ} {slot}: {fact} is not a ConditionType");
                        Assert.True(ConditionResolution.IsUserAssumed(c), $"{champ} {slot}: {fact} resolves live");
                        seen++;
                    }
                }
            }
        }
        Assert.True(seen >= 1);
    }

    private ComboResult RunResult(string champ, params string[] slots)
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin(champ) });
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 11, PlayerCount = 1, Stats = Stats };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champ; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 11;
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "t" + string.Join("", slots));
        int i = 0;
        foreach (var slot in slots)
            editor.AddNode(draft.Id, new ComboNode($"{slot}_{i++}", ComboNodeType.Skill, slot, 0, 0, 0, ComboDamageType.Magic, 0, 0, 0, 0, 0, 0));
        editor.SaveCombo(draft.Id);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!;
    }

    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{championId.ToLowerInvariant()}.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
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
            BaseStats = new ChampionBaseStats { Ad = 60 }, StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }
}
