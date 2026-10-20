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
/// (M45 loop 5) Stack-threshold passives fire on their threshold, not on every trigger: Braum's
/// Concussive Blows on the 4th attack (procEvery 4), Alistar's Trample on ONE attack after E
/// (maxProcs 1). Both used to ride every attack, so a 4-attack combo counted Braum's stun damage 4 times.
/// </summary>
public class StackProcGateTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public StackProcGateTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "StackProc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void BraumPassive_ProcsOnTheFourthAttackOnly()
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Braum") });
        var ids = RunCombo("Braum", "AA", "AA", "AA", "AA");

        var procs = ids.Where(id => id.Contains("#bonus")).ToList();
        Assert.Single(procs);
        Assert.StartsWith("AA_3", procs[0]);
    }

    [Fact]
    public void AlistarTrample_RidesOneAttackAfterE()
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Alistar") });
        var ids = RunCombo("Alistar", "E", "AA", "AA");

        Assert.Single(ids, id => id.StartsWith("AA_") && id.Contains("#bonus"));
    }

    [Fact]
    public void LucianSecondShot_RidesOnlyTheAttackAfterASpell()
    {
        // (M45 loop 8) Lightslinger: Q arms one second shot; the second AA has no fresh cast. Vigilance
        // is manual-only now, so no rider fires on its own.
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Lucian") });
        var ids = RunCombo("Lucian", "Q", "AA", "AA", "W", "AA");

        var procs = ids.Where(id => id.Contains("#bonus")).ToList();
        Assert.Equal(2, procs.Count);
        Assert.StartsWith("AA_1", procs[0]);
        Assert.StartsWith("AA_4", procs[1]);
    }

    [Fact]
    public void LeblancSigil_DetonatesOnTheNextAbility_NotOnItsOwnCast()
    {
        // (M45 loop 10) Q places the sigil; W detonates it once; the E after that finds no mark.
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Leblanc") });
        var ids = RunCombo("Leblanc", "Q", "W", "E");

        var procs = ids.Where(id => id.Contains("#bonus")).ToList();
        Assert.Single(procs);
        Assert.StartsWith("W_1", procs[0]);
    }

    [Fact]
    public void LuxIllumination_RidesTheAttackAfterASpell_NotEveryAttack()
    {
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Lux") });
        var ids = RunCombo("Lux", "AA", "Q", "AA", "AA");

        Assert.Single(ids, id => id.StartsWith("AA_") && id.Contains("#bonus"));
        Assert.Contains(ids, id => id.StartsWith("AA_2") && id.Contains("#bonus"));
    }

    [Fact]
    public void GangplankTrialByFire_ProcsOncePerCombo_AndParrrleySkipsIt()
    {
        // (M45 loop 20) a P rider's maxProcs is honored (Trial by Fire is on a cooldown), and Parrrley
        // applies on-hit effects other than Trial by Fire.
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Gangplank") });
        var ids = RunCombo("Gangplank", "Q", "AA", "AA");

        var procs = ids.Where(id => id.Contains("#bonus")).ToList();
        Assert.Single(procs);
        Assert.StartsWith("AA_1", procs[0]);
    }

    [Fact]
    public void FizzBleed_RefreshesOnce_TwitchVenom_StacksSixTimes()
    {
        // (M45 loop 23) an always-on ability-slot rider honors a stated cap (Fizz W's bleed refreshes);
        // Twitch's Deadly Venom adds a stack per attack up to MaxStacks 6.
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Fizz") });
        Assert.Single(RunCombo("Fizz", "AA", "AA", "AA").Where(id => id.Contains("#bonus")));

        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Twitch") });
        var venom = RunCombo("Twitch", "AA", "AA", "AA", "AA", "AA", "AA", "AA", "AA").Where(id => id.Contains("#bonus")).ToList();
        Assert.Equal(6, venom.Count);
        Assert.DoesNotContain(venom, id => id.StartsWith("AA_6") || id.StartsWith("AA_7"));
    }

    [Fact]
    public void AnOnHitCast_CarriesTheAlwaysOnRiders_OncePerStrike()
    {
        // (M45 loop 23) a cast that applies on-hit effects carries them once per strike (Bel'Veth E's slashes):
        // a three-shot Parrrley fixture with an uncapped Trial by Fire procs it three times.
        SkillDamageDb.LoadForTests("Gangplank", """
            {
              "Q": { "hits": [ { "type": "Physical", "calc": "ShotDamage", "count": 3, "appliesOnHit": true } ] },
              "P": { "bonusEffects": [ { "trigger": "onHit", "hits": [ { "type": "True", "calc": "TotalDamage", "count": 1 } ] } ] }
            }
            """);
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Gangplank") });
        var procs = RunCombo("Gangplank", "Q").Where(id => id.Contains("#bonus")).ToList();
        Assert.Equal(3, procs.Count);
        Assert.Equal(3, procs.Distinct().Count());
        Assert.True(SkillDamageDb.GetHits("Belveth", "E")![0].AppliesOnHit);
    }

    [Fact]
    public void SonaChord_NeedsThreeBasicCasts_SeraphineNotes_OnePerCast()
    {
        // (M45 loop 25) Power Chord: the attack after three BASIC-ability casts (R does not stack it)
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Sona") });
        Assert.Empty(RunCombo("Sona", "AA", "Q", "W", "R", "AA").Where(id => id.Contains("#bonus")));
        var chord = RunCombo("Sona", "Q", "W", "E", "AA", "AA").Where(id => id.Contains("#bonus")).ToList();
        Assert.Single(chord);
        Assert.StartsWith("AA_3", chord[0]);
        // (loop 29) Q last: the Staccato chord INSTEAD of the plain one - still one proc
        Assert.Single(RunCombo("Sona", "W", "E", "Q", "AA").Where(id => id.Contains("#bonus")));

        // Seraphine: two casts store two notes, the next attack spends both, the one after has none
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Seraphine") });
        var notes = RunCombo("Seraphine", "Q", "W", "AA", "AA").Where(id => id.Contains("#bonus")).ToList();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, id => Assert.StartsWith("AA_2", id));
    }

    [Fact]
    public void SennaMist_ConsumesTheMarkOnTheSecondAttack_OnceACombo()
    {
        // (M45 loop 25) the siphon rides attack 2 only; the 20% AD rider rides all four
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Senna") });
        var procs = RunCombo("Senna", "AA", "AA", "AA", "AA").Where(id => id.Contains("#bonus")).ToList();
        Assert.Equal(5, procs.Count);
        Assert.Equal(2, procs.Count(id => id.StartsWith("AA_1")));
    }

    [Fact]
    public void MelBolts_ThreePerCast_SpentByTheNextAttack()
    {
        // (M45 loop 31) Q then two attacks: the first attack fires the cast's 3 bolts, the second none
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Mel") });
        var bolts = RunCombo("Mel", "Q", "AA", "AA").Where(id => id.Contains("#bonus")).ToList();
        Assert.Equal(3, bolts.Count);
        Assert.All(bolts, id => Assert.StartsWith("AA_1", id));
        Assert.Empty(RunCombo("Mel", "AA", "AA").Where(id => id.Contains("#bonus")));
        Assert.True(SkillDamageDb.GetHits("Mel", "R")![0].StacksFromPriorHits);
    }

    private List<string> RunCombo(string champ, params string[] slots)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 9, PlayerCount = 1,
            Stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 50, AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 1 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champ; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 9;

        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "c");
        int i = 0;
        foreach (var slot in slots)
        {
            bool aa = slot == "AA";
            editor.AddNode(draft.Id, new ComboNode($"{slot}_{i++}", aa ? ComboNodeType.Aa : ComboNodeType.Skill, slot,
                0, 0, 0, aa ? ComboDamageType.Physical : ComboDamageType.Magic, aa ? 1.0 : 0, 0, 0, 0, 0, 0));
        }
        editor.SaveCombo(draft.Id);

        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT not delivered");
        return received!.NodeBreakdown.Select(n => n.NodeId).ToList();
    }

    [Fact]
    public void MelEclipse_CountsTheStacksTheEarlierHitsApplied()
    {
        // (M45 loop 31) each earlier attack applies an Overwhelm stack: R after two attacks deals more than R alone
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Mel") });
        double alone = RunTotal("Mel", "R");
        double afterTwo = RunTotal("Mel", "AA", "AA", "R") - RunTotal("Mel", "AA", "AA");
        Assert.True(afterTwo > alone + 1, $"R after two attacks {afterTwo:0.##} vs alone {alone:0.##}");
    }

    [Fact]
    public void AmumuCurse_AddsATrueShare_ToMagicHitsAfterAnAttack()
    {
        // (M45 loop 33) Q before the cursing attack: no share; Q after it: a true companion
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Amumu") });
        Assert.DoesNotContain(RunCombo("Amumu", "Q", "AA"), id => id.Contains("#curse"));
        var cursed = RunCombo("Amumu", "AA", "Q");
        Assert.Contains(cursed, id => id.StartsWith("Q_1") && id.EndsWith("#curse"));
        Assert.DoesNotContain(cursed, id => id.StartsWith("AA_0") && id.EndsWith("#curse"));
    }

    [Fact]
    public void WukongClone_JoinsTheQAttack_OnlyAfterW()
    {
        // (M45 loop 38) Q alone: his own hit only; W then Q: the clone's swing and bonus join it
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("MonkeyKing") });
        int alone = RunCombo("MonkeyKing", "Q").Count(id => id.StartsWith("Q_0"));
        int afterW = RunCombo("MonkeyKing", "W", "Q").Count(id => id.StartsWith("Q_1"));
        Assert.Equal(1, alone);
        Assert.Equal(3, afterW);
    }

    [Fact]
    public void YoneSecondAttack_IsHalfMagic()
    {
        // (M45 loop 32) attack 1 one physical node; attack 2 splits into the physical half and a magic half
        ChampionRepository.Initialize(new[] { LoadChampionFromBin("Yone") });
        var ids = RunCombo("Yone", "AA", "AA", "AA");
        Assert.DoesNotContain(ids, id => id.StartsWith("AA_0#shape"));
        Assert.Contains(ids, id => id.StartsWith("AA_1#shape"));
        Assert.DoesNotContain(ids, id => id.StartsWith("AA_2#shape"));
    }

    [Fact]
    public void MelOverwhelm_KillLineIsTheStoreOfTheCombosHits()
    {
        // (M45 loop 37) two attacks store two stacks: PassiveFlatDamage + 2 x PassiveStackDamage
        var mel = LoadChampionFromBin("Mel");
        ChampionRepository.Initialize(new[] { mel });
        var stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 50, AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 1 };
        double flat = SkillDamage.ComputeCalcDamage(mel, "R", "PassiveFlatDamage", stats, 9)!.Value;
        double perStack = SkillDamage.ComputeCalcDamage(mel, "R", "PassiveStackDamage", stats, 9)!.Value;
        var result = RunResult("Mel", "AA", "AA");
        Assert.Equal(flat + 2 * perStack, result.ExecuteThresholdHP, 3);
        Assert.Equal("멜 P(압도)", result.ExecuteRuleLabel);
    }

    private double RunTotal(string champ, params string[] slots) => RunResult(champ, slots).TotalDamage;

    private ComboResult RunResult(string champ, params string[] slots)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 9, PlayerCount = 1,
            Stats = new ActivePlayerStats { AttackDamage = 100, AbilityPower = 50, AbilityQ = 3, AbilityW = 3, AbilityE = 3, AbilityR = 1 } };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = champ; snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 9;
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo(champ, "t" + string.Join("", slots));
        int i = 0;
        foreach (var slot in slots)
        {
            bool aa = slot == "AA";
            editor.AddNode(draft.Id, new ComboNode($"{slot}_{i++}", aa ? ComboNodeType.Aa : ComboNodeType.Skill, slot,
                0, 0, 0, aa ? ComboDamageType.Physical : ComboDamageType.Magic, aa ? 1.0 : 0, 0, 0, 0, 0, 0));
        }
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
