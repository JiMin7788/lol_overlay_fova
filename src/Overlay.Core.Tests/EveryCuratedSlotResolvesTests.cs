using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// (loop 488) One sweep over EVERY curated slot of EVERY curated champion, asking the only question
/// that matters end to end: with every knob turned on, does this slot put damage on the board?
///
/// <para>Why now. Loops 479-487 added five schema fields (metHpPercentCalc,
/// missingHpBonusDataValue, stackTierBonusDataValues, appliesTo/maxProcs riders) and three
/// conditions, and rewrote the resolver's conditional branch, its heuristic fallback and the runner's
/// cast bookkeeping. The existing silent-zero guard covers a hand-picked list of slots; that list is
/// exactly the set of mistakes someone already thought of. This covers all 631.</para>
///
/// <para>The expectation is derived from the curation, not from a list: a slot with direct HITS must
/// score on its own; a slot carrying only RIDERS must raise the attack that follows it; a slot with
/// neither is a utility cast and must score nothing. That third rule is not a loophole — it is how
/// Hwei's two boons and Ivern's brush are honestly represented, and pinning it stops a real hit from
/// quietly becoming one of them.</para>
/// </summary>
public class EveryCuratedSlotResolvesTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public EveryCuratedSlotResolvesTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "AllSlots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>A REAL champion as the target, not a synthetic dummy: the runner only credits the
    /// missing-health tracker against a target it actually resolved, so a fallback defender would
    /// leave every execute-shaped hit reading zero for a reason that is about this harness.</summary>
    private const string TargetChampion = "Garen";

    /// <summary>Loads the corpus the way the APP does — Data Dragon for the slot layout, Community
    /// Dragon for the formulas — not by parsing a BIN alone. That distinction is not pedantic: a
    /// champion whose BIN spell objects carry hashed names (K'Sante) has no Q/W/E/R at all under a
    /// bin-only parse, and a sweep built that way reports its own blind spot as six dead slots.</summary>
    private static void InitRepositoryFromCache()
    {
        var ddragonRoot = Path.Combine(DataDir, "ddragon");
        var summary = Directory.GetFiles(ddragonRoot, "champion.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).Last();
        ChampionRepository.InitializeFromCache(
            Path.GetDirectoryName(summary)!, Path.Combine(DataDir, "communitydragon"));
    }

    /// <summary>Deliberately generous: real AD, AP, mana and health so no scaling term is zero for a
    /// reason that has nothing to do with curation. Ability ranks are LEFT UNSET so ChooseRank takes
    /// the max-rank path (the documented no-ability-data preview behaviour) — a rank-0 slot would
    /// otherwise score nothing and say nothing about the curation.</summary>
    private static ActivePlayerStats Stats() => new()
    {
        AttackDamage = 300, AbilityPower = 200, MaxHealth = 3000, CurrentHealth = 3000,
        ResourceValue = 1000, ResourceMax = 1000,
        // Armour, magic resist and movement speed too: Rammus and Rell scale their on-hit off their
        // own resistances and Janna off her bonus movement speed, and a sweep that left those at zero
        // would report three perfectly good curations as dead because of its own fixture.
        Armor = 100, MagicResist = 100, MoveSpeed = 400,
    };

    private static GameSnapshot Snap(string championId)
    {
        var snap = new GameSnapshot
        {
            HasData = true, ActivePlayerSummonerName = "Me", Level = 18, PlayerCount = 2, Stats = Stats(),
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = championId;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = 18;
        snap.Players[1].SummonerName = "Foe";
        snap.Players[1].ChampionName = TargetChampion;
        snap.Players[1].Team = "CHAOS";
        snap.Players[1].Level = 18;
        return snap;
    }

    /// <summary>Every knob on: the condition asserted, the zone stood in for its whole duration, the
    /// summon landing its hits, the stacks full. "Can this slot produce damage AT ALL" is the
    /// question, so nothing is left at a conservative floor.</summary>
    private static ComboNode Node(string slot, int index) => new(
        Id: $"{slot}_{index}",
        NodeType: slot == "AA" ? ComboNodeType.Aa : ComboNodeType.Skill,
        Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
        DamageType: ComboDamageType.Physical, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
        CastTime: 0, Delay: 0, TravelTime: 0)
    {
        UserConditionMet = slot == "AA" ? null : true,
        UserHitDurationSeconds = 30,
        UserAttackCount = 5,
        UserStackCount = 10,
        UserDistanceFraction = 1.0,
    };

    /// <summary>Goes through <see cref="ComboRunner.ComputePreview"/> — the same resolution a real
    /// trigger runs, minus the event round-trip. An earlier version of this sweep published
    /// COMBO.TRIGGER and waited on UI.COMBO_RESULT for each of 631 slots, and reported a handful of
    /// slots dead that resolve perfectly when asked directly: a sweep whose own harness is racy
    /// cannot distinguish a broken curation from a dropped event.</summary>
    private static double Run(ComboRunner runner, string championId, params string[] slots)
    {
        var nodes = new List<ComboNode>();
        for (int i = 0; i < slots.Length; i++) nodes.Add(Node(slots[i], i));
        return runner.ComputePreview(championId, new ComboGraph(nodes, Array.Empty<ComboEdge>()))
            ?.Resolved ?? 0;
    }

    /// <summary>Does this hit's own formula produce a number? Used for always-on riders, whose
    /// contribution is inseparable from the attack they ride.</summary>
    private static bool Resolves(string championId, string slot, SkillHit hit)
    {
        var champion = ChampionRepository.Get(championId);
        if (champion is null) return false;
        string bin = string.IsNullOrEmpty(hit.BinSpell) ? slot : hit.BinSpell!;
        var stats = Stats();

        double? Try(Func<double?> f) { try { return f(); } catch { return null; } }

        foreach (var calc in new[] { hit.Calc, hit.MetCalc, hit.PerSecondCalc, hit.PerAttackCalc })
            if (!string.IsNullOrEmpty(calc)
                && Try(() => SkillDamage.ComputeCalcDamage(champion, bin, calc!, stats, 18, rankSlot: slot)) is > 0)
                return true;
        foreach (var pct in new[] { hit.HpPercentCalc, hit.MetHpPercentCalc })
            if (!string.IsNullOrEmpty(pct)
                && Try(() => SkillDamage.ResolveHpPercentCalc(champion, bin, pct!, stats, 18, rankSlot: slot)) is > 0)
                return true;
        if (!string.IsNullOrEmpty(hit.HpPercentDataValue)
            && Try(() => SkillDamage.ResolveHpPercent(champion, bin, hit.HpPercentDataValue!, stats, 18, rankSlot: slot)) is > 0)
            return true;
        if (!string.IsNullOrEmpty(hit.FlatDataValue)
            && Try(() => SkillDamage.ComputeFlatDataValue(champion, bin, hit.FlatDataValue!, stats, 18, rankSlot: slot)) is > 0)
            return true;
        return hit.HpPercent > 0;
    }

    [Fact]
    public void EveryCuratedSlotEitherScores_RidesTheNextAttack_OrIsCuratedAsUtility()
    {
        var dir = Path.Combine(DataDir, "skill_damage");
        var champions = Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.True(champions.Count > 150, $"only {champions.Count} curated champions found");

        var deadSlots = new List<string>();
        var deadRiders = new List<string>();
        var unexpectedlyScoring = new List<string>();
        int scored = 0, rode = 0, utility = 0, skippedNoBin = 0;

        InitRepositoryFromCache();

        foreach (var championId in champions)
        {
            SkillDamageDb.ResetForTests();
            EventBus.EventBus.ResetForTests();

            if (ChampionRepository.Get(championId!) is null) { skippedNoBin++; continue; }

            using var config = new ConfigManager(Path.Combine(_dir, championId + ".json"));
            var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
            // The target starts DAMAGED. A hit that scales off missing health (Kayle E's execute,
            // Lee Sin's recast) is legitimately zero against a full-health dummy, and reading that
            // as a dead slot would be the sweep lying about its own setup.
            var tracker = new TargetHealthTracker();
            using var runner = new ComboRunner(engine, config, () => Snap(championId!),
                targetHealthTracker: tracker);
            runner.Start();
            // A LITTLE health already gone, so a missing-health term has something to read. Only a
            // little: total damage is capped by what the target has left — this is a kill calculator —
            // so a nearly-dead target would clamp every slot to nothing and read as 145 dead slots.
            tracker.RecordDamageDealt(TargetChampion, 200);

            double aaAlone = Run(runner, championId!, "AA");

            foreach (var slot in SkillDamageDb.GetCuratedSlotKeys(championId!))
            {
                bool hasHits = SkillDamageDb.GetHits(championId!, slot) is { Length: > 0 };
                bool hasRiders = SkillDamageDb.GetBonusEffects(championId!, slot) is { Length: > 0 };

                double alone = Run(runner, championId!, slot);
                if (hasHits)
                {
                    if (alone > 0.5) scored++;
                    else deadSlots.Add($"{championId}.{slot}");
                    continue;
                }

                if (hasRiders)
                {
                    var effects = SkillDamageDb.GetBonusEffects(championId!, slot)!;
                    // An ALWAYS-ON rider (every passive, and the ability-slot ones flagged as such)
                    // is already inside the bare attack's number, so [slot, AA] cannot exceed [AA] and
                    // comparing them proves nothing. For those the question is whether the rider's own
                    // hit resolves at all; for the rest it is whether casting the slot reaches the
                    // attack after it, which is the sequencing half no direct resolution would catch.
                    bool alwaysOn = slot.Equals("P", StringComparison.OrdinalIgnoreCase)
                                    || effects.Any(e => e.AlwaysOn);
                    bool ok = alwaysOn
                        ? effects.SelectMany(e => e.Hits).Any(h => Resolves(championId!, slot, h))
                        : alone > 0.5 || Run(runner, championId!, slot, "AA") > aaAlone + 0.5;
                    if (ok) rode++;
                    else deadRiders.Add($"{championId}.{slot}");
                    continue;
                }

                // Neither hits nor riders: a curated utility cast (Hwei's boons). It must stay silent —
                // if it scores, something is resolving damage the curation never authorised.
                if (alone > 0.5) unexpectedlyScoring.Add($"{championId}.{slot} = {alone:0.##}");
                else utility++;
            }
        }

        Assert.True(deadSlots.Count == 0,
            $"slots with curated hits that resolved to nothing ({deadSlots.Count}): "
            + string.Join(", ", deadSlots.Take(25)));
        Assert.True(deadRiders.Count == 0,
            $"rider slots that never reached an attack ({deadRiders.Count}): "
            + string.Join(", ", deadRiders.Take(25)));
        Assert.True(unexpectedlyScoring.Count == 0,
            $"utility slots that scored anyway ({unexpectedlyScoring.Count}): "
            + string.Join(", ", unexpectedlyScoring.Take(25)));

        // The sweep is only worth anything if it actually swept: guard against a silent skip of the
        // whole corpus (a moved data folder, a rename) reading as a pass.
        Assert.True(scored > 500, $"only {scored} slots scored — did the sweep actually run?");
        Assert.True(skippedNoBin < 5, $"{skippedNoBin} champions had no BIN to resolve against");
    }
}
