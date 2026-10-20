using System.IO;
using System.Linq;
using System.Text.Json;
using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// M45 §5.5 — the engine side of metric F. Writes every curated hit's resolved number, at the shared
/// stat profiles, as JSON for tools/tooltip_audit/fidelity.py to compare with its independent BIN
/// evaluator. Runs only when M45_DUMP_PATH is set (fidelity.py sets it); otherwise it does nothing, so
/// the ordinary suite stays fast and writes no files.
/// </summary>
public class TooltipFidelityDumpTests
{
    // (M45 loop 50) Base* / *PerLevel: the champion's base stats (ref_eval.FIXTURE_BASE). They were zero, so a bonus part
    // read the total and metric F could not see a bonus / total mix-up.
    private sealed record Profile(string Name, int Level, int Rank, double Ad, double Ap, double Hp, double Armor,
        double Mr, double As, double Ms, double CritChance, double CritDamage, double Resource, int Stacks,
        double BaseAd = 0, double AdPerLevel = 0, double BaseHp = 0, double HpPerLevel = 0, double BaseArmor = 0,
        double ArmorPerLevel = 0, double BaseMr = 0, double MrPerLevel = 0, double BaseMp = 0, double MpPerLevel = 0,
        double BaseAs = 0, double BaseMs = 0);

    [Fact]
    public void DumpEngineHits_WhenRequested()
    {
        string? outPath = Environment.GetEnvironmentVariable("M45_DUMP_PATH");
        string? profilesPath = Environment.GetEnvironmentVariable("M45_PROFILES");
        if (string.IsNullOrEmpty(outPath) || string.IsNullOrEmpty(profilesPath)) return;

        var profiles = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(profilesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        var rows = new List<Dictionary<string, object?>>();

        foreach (var file in Directory.GetFiles(Path.Combine(dataDir, "skill_damage"), "*.json").OrderBy(f => f))
        {
            string champ = Path.GetFileNameWithoutExtension(file);
            string bin = Path.Combine(dataDir, "communitydragon", champ.ToLowerInvariant() + ".bin.json");
            if (!File.Exists(bin)) continue;
            ChampionData champion;
            try { champion = LoadChampionFromBin(champ, bin, profiles[0]); }
            catch { continue; }

            foreach (var slot in SkillDamageDb.GetCuratedSlotKeys(champ))
            {
                var hits = (SkillDamageDb.GetHits(champ, slot) ?? Array.Empty<SkillHit>())
                    .Select((h, i) => (Hit: h, Origin: "hit", Index: i)).ToList();
                var effects = SkillDamageDb.GetBonusEffects(champ, slot) ?? Array.Empty<SkillBonusEffect>();
                int b = 0;
                foreach (var effect in effects)
                    foreach (var h in effect.Hits)
                        hits.Add((h, "bonus", b++));

                foreach (var (hit, origin, index) in hits)
                    foreach (var p in profiles)
                        // (M45 loop 40) a condition-gated growth is dumped both ways too (Hwei QW)
                        // (M45 loop 42) ...and an Nth-attack landing behind its condition (Ivern R's shockwave)
                        foreach (bool? met in hit.IsConditional || hit.MissingHpBonusWhenMet || (hit.LandsOnNthAttack is > 0 && hit.ConditionType is not null)
                                     || hit.MetDamageAmp is > 0 // (M45 loop 42) ...and a literal amp, both ways (Nidalee's Hunted Takedown)
                                     ? new bool?[] { false, true } : new bool?[] { null })
                        foreach (bool allOut in hit.AllOutOnly || !string.IsNullOrEmpty(hit.AllOutCalc) ? new[] { false, true } : new[] { false })
                        // a min/max range hit also at its two ends (the knob at 0 and 1)
                        foreach (double? dist in hit.IsDistanceScaled ? new double?[] { null, 0.0, 1.0 } : new double?[] { null })
                        {
                            bool ok = ComboRunner.ResolveHitForAudit(hit, champion, slot, Stats(p, slot), p.Level, 1000.0,
                                // (M45 loop 25) a finale at its own seconds; (loop 28) a whole-duration total over the whole duration
                                exposureSeconds: hit.RequiresExposureSeconds ?? (hit.TotalOverDurationCalc is null ? 1.0 : hit.MaxDurationSeconds),
                                // (M45 loop 42) a per-stack hit at ONE stack = its per-stack number (Varus W's Blight)
                                conditionMet: met, stacks: hit.PerStack ? 1 : p.Stacks, allOut: allOut,
                                out double damage, out ComboExecuteType exec, out double execPercent,
                                attackCount: hit.LandsOnNthAttack ?? 1, // a per-attack hit at ONE landing = its per-hit number
                                distanceFraction: dist);
                            rows.Add(new Dictionary<string, object?>
                            {
                                ["champion"] = champ, ["slot"] = slot, ["origin"] = origin, ["index"] = index,
                                ["profile"] = p.Name, ["met"] = met, ["allOut"] = allOut, ["dist"] = dist, ["ok"] = ok,
                                ["damage"] = ok ? Math.Round(damage, 4) : null,
                                ["critScalar"] = ok && hit.CanCrit
                                    ? Math.Round(ComboRunner.CritScalarForAudit(hit, champion, slot, Stats(p, slot), p.Level, damage, p.Stacks), 6)
                                    : null,
                                ["critDamage"] = p.CritDamage > 0 ? p.CritDamage : 2.0,
                                ["execType"] = ok ? exec.ToString() : null,
                                ["execPercent"] = ok ? Math.Round(execPercent, 6) : null,
                                ["mods"] = ComboRunner.ModifiersForAudit(hit, champion, slot, Stats(p, slot), p.Level),
                            });
                        }

                // (M45 loop 37) a stored-damage execute (Mel P): the store after the profile's stacks
                if (slot == "P")
                    foreach (var p in profiles)
                        if (ComboRunner.StoredExecuteThreshold(champion, Stats(p, slot), p.Level, p.Stacks) is { } stored)
                            rows.Add(new Dictionary<string, object?>
                            {
                                ["champion"] = champ, ["slot"] = slot, ["origin"] = "exec", ["index"] = 0,
                                ["profile"] = p.Name, ["met"] = null, ["allOut"] = false, ["dist"] = null, ["ok"] = true,
                                ["damage"] = Math.Round(stored.Threshold, 4), ["execType"] = "None", ["execPercent"] = 0.0,
                                ["mods"] = new Dictionary<string, double> { ["perStackCalc"] = stored.PerStack },
                            });

                // (M45 loop 44) a crit reduction (Jhin P): its fraction, as a mods-only row
                if (slot == "P")
                    foreach (var p in profiles)
                        if (ComboRunner.CritReductionForAudit(champion, Stats(p, slot), p.Level) is double red)
                            rows.Add(new Dictionary<string, object?>
                            {
                                ["champion"] = champ, ["slot"] = slot, ["origin"] = "critmod", ["index"] = 0,
                                ["profile"] = p.Name, ["met"] = null, ["allOut"] = false, ["dist"] = null, ["ok"] = true,
                                ["damage"] = 0.0, ["execType"] = "None", ["execPercent"] = 0.0,
                                ["mods"] = new Dictionary<string, double> { ["critReductionDataValue"] = red },
                            });

                // (M45 loop 33) a slot-level curse amp (Amumu P): its fraction, as a mods-only row
                if (slot == "P")
                    foreach (var p in profiles)
                        if (ComboRunner.MagicTrueAmpForAudit(champion, Stats(p, slot), p.Level) is double amp)
                            rows.Add(new Dictionary<string, object?>
                            {
                                ["champion"] = champ, ["slot"] = slot, ["origin"] = "amp", ["index"] = 0,
                                ["profile"] = p.Name, ["met"] = null, ["allOut"] = false, ["dist"] = null, ["ok"] = true,
                                ["damage"] = 0.0, ["execType"] = "None", ["execPercent"] = 0.0,
                                ["mods"] = new Dictionary<string, double> { ["magicTrueAmpDataValue"] = amp },
                            });

                // The slot's own hits summed (x count) with every knob unset (index 0), with each capped
                // repeat-hit count and exposure at its cap (index 1), and with every condition met (index 2): the
                // tooltip's "total" / "max" / empowered-total numbers.
                var own = SkillDamageDb.GetHits(champ, slot) ?? Array.Empty<SkillHit>();
                foreach (var p in profiles)
                    for (int variant = 0; variant < 3; variant++)
                    {
                        double total = 0;
                        // ...and per damage type (a split hit half to each), for "X magic and Y physical"
                        var byType = new Dictionary<string, double>();
                        bool flat = own.Any();
                        foreach (var hit in own)
                        {
                            if (!ComboRunner.ResolveHitForAudit(hit, champion, slot, Stats(p, slot), p.Level, 1000.0,
                                    exposureSeconds: variant == 1 ? hit.MaxDurationSeconds : null,
                                    conditionMet: variant == 2 ? true : null, stacks: variant == 1 ? 99 : p.Stacks, allOut: false,
                                    out double d, out ComboExecuteType ex, out _,
                                    attackCount: variant == 1 ? hit.MaxAttackCount : null))
                                continue;
                            if (ex != ComboExecuteType.None) { flat = false; break; }
                            total += d * hit.Count;
                            // (M45 loop 23) the engine's own shares: split, met type, true conversion
                            foreach (var (share, type) in ComboRunner.TypeSharesForAudit(hit, d * hit.Count, champion, slot,
                                         Stats(p, slot), p.Level, variant == 2 ? true : null))
                                byType[type.ToString()] = byType.GetValueOrDefault(type.ToString()) + share;
                        }
                        if (!flat || (variant == 1 && !own.Any(h => h.MaxAttackCount is > 0 || h.IsDurationScaled
                                || !string.IsNullOrEmpty(h.PerStackCalc) || !string.IsNullOrEmpty(h.PerStackMultiplierDataValue)))
                            || (variant == 2 && !own.Any(h => h.IsConditional))) continue;
                        rows.Add(new Dictionary<string, object?>
                        {
                            ["champion"] = champ, ["slot"] = slot, ["origin"] = "total", ["index"] = variant,
                            ["profile"] = p.Name, ["met"] = null, ["allOut"] = false, ["ok"] = true,
                            ["damage"] = Math.Round(total, 4), ["execType"] = "None", ["execPercent"] = 0.0,
                        });
                        if (byType.Count > 1)
                            foreach (var (type, sum) in byType)
                                rows.Add(new Dictionary<string, object?>
                                {
                                    ["champion"] = champ, ["slot"] = slot, ["origin"] = "total:" + type, ["index"] = variant,
                                    ["profile"] = p.Name, ["met"] = null, ["allOut"] = false, ["ok"] = true,
                                    ["damage"] = Math.Round(sum, 4), ["execType"] = "None", ["execPercent"] = 0.0,
                                });
                    }
            }
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(rows));
    }

    private static ActivePlayerStats Stats(Profile p, string slot)
    {
        int rank = p.Rank, rRank = Math.Min(p.Rank, 3);
        return new ActivePlayerStats
        {
            AttackDamage = p.Ad, AbilityPower = p.Ap, MaxHealth = p.Hp, CurrentHealth = p.Hp, Armor = p.Armor,
            MagicResist = p.Mr, AttackSpeed = p.As, MoveSpeed = p.Ms, CritChance = p.CritChance,
            CritDamage = p.CritDamage, ResourceMax = p.Resource,
            AbilityQ = rank, AbilityW = rank, AbilityE = rank, AbilityR = rRank,
        };
    }

    private static ChampionData LoadChampionFromBin(string championId, string binPath, Profile fixture)
    {
        var binSkills = ChampionBinParser.ParseChampion(championId, File.ReadAllText(binPath));
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
            BaseStats = new ChampionBaseStats
            {
                Ad = fixture.BaseAd, Hp = fixture.BaseHp, Armor = fixture.BaseArmor, Mr = fixture.BaseMr, Mp = fixture.BaseMp,
                AttackSpeed = fixture.BaseAs, Ms = fixture.BaseMs,
            },
            StatsPerLevel = new ChampionStatsPerLevel
            {
                Ad = fixture.AdPerLevel, Hp = fixture.HpPerLevel, Armor = fixture.ArmorPerLevel, Mr = fixture.MrPerLevel,
                Mp = fixture.MpPerLevel,
            },
        };
    }
}
